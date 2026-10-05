//
//      Copyright (C) DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cassandra.IntegrationTests.TestBase;
using Cassandra.IntegrationTests.TestClusterManagement.Simulacron;
using Cassandra.Tasks;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

namespace Cassandra.IntegrationTests.TestClusterManagement
{
    public class SimulacronManager
    {
        private const string CreateClusterPathFormat = "/cluster?data_centers={0}&cassandra_version={1}&name={2}" +
                                                       "&activity_log={3}&num_tokens={4}";

        private const string CreateClusterPath = "/cluster";

        private volatile Process _simulacronProcess;

        private volatile bool _initialized;

        private readonly object _startLock = new object();

        private readonly TestHttpClient _testHttpClient;

        private static readonly ConcurrentDictionary<string, SimulacronManager> WorkerInstances =
            new ConcurrentDictionary<string, SimulacronManager>();

        private static int _workerSlot;

        public static SimulacronManager InstancePeersV2Tests { get; } =
            new SimulacronManager(8298, "127.0.0.101", 9011);

        public Uri BaseAddress => new Uri($"http://127.0.0.1:{HttpPort}");

        public int? StartPort { get; }

        public string StartIp { get; }

        public int HttpPort { get; }

        /// <summary>
        /// One simulacron JVM per NUnit worker, on its own HTTP port and node IP range, so
        /// parallelizable fixtures neither share a process nor collide on node addresses.
        /// <see cref="TestContext.WorkerId"/> is null for tests on the non-parallel queue,
        /// which all share slot 0.
        /// </summary>
        public static SimulacronManager DefaultInstance =>
            WorkerInstances.GetOrAdd(
                TestContext.CurrentContext?.WorkerId ?? "0",
                _ =>
                {
                    var slot = Interlocked.Increment(ref _workerSlot);
                    return new SimulacronManager(8188 + slot, "127.0." + slot + ".101", null);
                });

        private SimulacronManager(int httpPort, string startIp, int? startPort)
        {
            HttpPort = httpPort;
            StartIp = startIp;
            StartPort = startPort;
            _testHttpClient = new TestHttpClient(BaseAddress);
        }

        public static SimulacronManager GetForPeersTests()
        {
            return SimulacronManager.InstancePeersV2Tests;
        }

        public void Start()
        {
            if (_initialized)
            {
                return;
            }

            lock (_startLock)
            {
                if (_initialized)
                {
                    return;
                }

                var started = false;
                var errorMessage = "Simulacron is taking too long to start. Aborting initialization...";
                _simulacronProcess = new Process();
                var jarPath = Environment.GetEnvironmentVariable("SIMULACRON_PATH");
                if (string.IsNullOrEmpty(jarPath))
                {
                    jarPath = Environment.GetEnvironmentVariable("HOME") + "/simulacron.jar";
                }
                if (!File.Exists(jarPath))
                {
                    throw new Exception("Simulacron: Simulacron jar not found: " + jarPath);
                }

                var args = $"-jar {jarPath} --ip {StartIp} -p {HttpPort}";
                if (StartPort.HasValue)
                {
                    args += $" -s {StartPort}";
                }

                _simulacronProcess.StartInfo.FileName = "java";
                _simulacronProcess.StartInfo.Arguments = args;
                _simulacronProcess.StartInfo.UseShellExecute = false;
                _simulacronProcess.StartInfo.CreateNoWindow = true;
                _simulacronProcess.StartInfo.RedirectStandardOutput = true;
                _simulacronProcess.StartInfo.RedirectStandardError = true;
                _simulacronProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                var eventWaitHandler = new AutoResetEvent(false);
                _simulacronProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null || started) return;
                    Trace.TraceInformation(e.Data);
                    if (e.Data.Contains("Address already in use"))
                    {
                        errorMessage = $"Simulacron start error: {e.Data}";
                        eventWaitHandler.Set();
                        return;
                    }
                    if (e.Data.Contains("Created nodes will start with ip"))
                    {
                        started = true;
                        eventWaitHandler.Set();
                    }
                };
                _simulacronProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    Trace.TraceError(e.Data);
                    errorMessage = $"Simulacron start error: {e.Data}";
                };
                _simulacronProcess.Start();

                _simulacronProcess.BeginOutputReadLine();
                _simulacronProcess.BeginErrorReadLine();

                eventWaitHandler.WaitOne(30000);
                if (!started)
                {
                    Trace.TraceError(errorMessage);
                    Stop();
                    throw new Exception("Simulacron failed to start! " + Environment.NewLine + errorMessage);
                }
                _initialized = true;
                Trace.TraceInformation("Simulacron started");
            }
        }

        /// <summary>Stops every per-worker simulacron JVM (no-op for ones never started).</summary>
        public static void StopAll()
        {
            foreach (var instance in WorkerInstances.Values)
            {
                instance.Stop();
            }
            InstancePeersV2Tests.Stop();
        }

        public void Stop()
        {
            _initialized = false;
            if (_simulacronProcess == null) return;

            try
            {
                _simulacronProcess.Kill();

                if (!_simulacronProcess.WaitForExit(30000))
                {
                    throw new TimeoutException("Simulacron process didn't stop after kill signal.");
                }

                _simulacronProcess.Dispose();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(ex.Message);
            }
            finally
            {
                _simulacronProcess = null;
                Trace.TraceInformation("Simulacron process stopped");
            }
        }

        public Task<SimulacronCluster> CreateNewAsync(int nodeLength)
        {
            return CreateNewAsync(new SimulacronOptions { Nodes = nodeLength.ToString() });
        }

        /// <summary>
        /// Creates a single DC cluster with the amount of nodes provided.
        /// </summary>
        public SimulacronCluster CreateNew(int nodeLength)
        {
            return CreateNew(new SimulacronOptions { Nodes = nodeLength.ToString() });
        }

        public async Task<SimulacronCluster> CreateNewAsync(SimulacronOptions options)
        {
            Start();
            var path = string.Format(CreateClusterPathFormat, options.Nodes, options.GetCassandraVersion(),
                options.Name, options.ActivityLog, options.NumberOfTokens);
            var data = await Post(path, null).ConfigureAwait(false);
            return CreateFromData(data);
        }

        public SimulacronCluster CreateNew(SimulacronOptions options)
        {
            return TaskHelper.WaitToComplete(CreateNewAsync(options));
        }

        /// <summary>
        /// Creates a new cluster with POST body parameters.
        /// </summary>
        public SimulacronCluster CreateNewWithPostBody(dynamic body)
        {
            Start();
            var data = TaskHelper.WaitToComplete(Post(CreateClusterPath, body));
            return CreateFromData(data);
        }

        private SimulacronCluster CreateFromData(dynamic data)
        {
            var cluster = new SimulacronCluster(data["id"].ToString(), this)
            {
                Data = data,
                DataCenters = new List<SimulacronDataCenter>()
            };
            var dcs = (JArray)cluster.Data["data_centers"];
            foreach (var dc in dcs)
            {
                var dataCenter = new SimulacronDataCenter(cluster.Id + "/" + dc["id"], this);
                cluster.DataCenters.Add(dataCenter);
                dataCenter.Nodes = new List<SimulacronNode>();
                var nodes = (JArray)dc["nodes"];
                foreach (var nodeJObject in nodes)
                {
                    var node = new SimulacronNode(dataCenter.Id + "/" + nodeJObject["id"], this);
                    dataCenter.Nodes.Add(node);
                    node.ContactPoint = nodeJObject["address"].ToString();
                }
            }
            return cluster;
        }

        public Task<JObject> Post(string url, object body)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException("Simulacron Process not started.");
            }

            return _testHttpClient.SendWithJsonAsync<JObject>(TestHttpClient.Post, url, body);
        }

        public Task<JObject> PutAsync(string url, object body)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException("Simulacron Process not started.");
            }

            return _testHttpClient.SendWithJsonAsync<JObject>(TestHttpClient.Put, url, body);
        }

        public Task<T> GetAsync<T>(string url)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException("Simulacron Process not started.");
            }

            return _testHttpClient.SendWithJsonAsync<T>(TestHttpClient.Get, url, null);
        }

        public Task DeleteAsync(string url)
        {
            if (!_initialized)
            {
                throw new ObjectDisposedException("Simulacron Process not started.");
            }

            return _testHttpClient.SendWithJsonAsync<JObject>(TestHttpClient.Delete, url, null);
        }
    }
}