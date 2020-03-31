﻿//
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
using System.IO;
using System.Threading.Tasks;
using Dse.Test.Integration.TestClusterManagement;

namespace Dse.Test.Integration
{
    public abstract class SharedCloudClusterTest : SharedClusterTest
    {
        private const int MaxRetries = 10;

        private readonly bool _sniCertValidation;
        private readonly bool _clientCert;
        protected override string[] SetupQueries => base.SetupQueries;
        
        protected new ICluster Cluster { get; set; }

        protected SharedCloudClusterTest(
            bool createSession = true, bool sniCertValidation = true, bool clientCert = true) :
            base(3, createSession)
        {
            _sniCertValidation = sniCertValidation;
            _clientCert = clientCert;
        }

        public override void OneTimeSetUp()
        {
            base.OneTimeSetUp();
        }

        public override void OneTimeTearDown()
        {
            base.OneTimeTearDown();
            TestCloudClusterManager.TryRemove();
        }

        protected override void CreateCommonSession()
        {
            Exception last = null;
            for (var i = 0; i < SharedCloudClusterTest.MaxRetries; i++)
            {
                try
                {
                    Cluster = CreateCluster();
                    SetBaseSession(Cluster.Connect());
                    return;
                }
                catch (Exception ex)
                {
                    last = ex; 
                    Task.Delay(1000).GetAwaiter().GetResult();
                    if (Cluster != null)
                    {
                        Cluster.Dispose();
                        Cluster = null;
                    }
                }
            }
            throw last;
        }

        private ICluster CreateCluster(string creds = "creds-v1.zip", Action<Builder> act = null)
        {
            var builder = Dse.Cluster.Builder()
                                   .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                                   .WithQueryTimeout(60000);
            act?.Invoke(builder);
            builder = builder
                      .WithCloudSecureConnectionBundle(
                          Path.Combine(((CloudCluster)TestCluster).SniHomeDirectory, "certs", "bundles", creds))
                      .WithPoolingOptions(
                          new PoolingOptions().SetHeartBeatInterval(200))
                      .WithReconnectionPolicy(new ConstantReconnectionPolicy(100));
            return builder.Build();
        }

        protected ICluster CreateTemporaryCluster(string creds = "creds-v1.zip", Action<Builder> act = null)
        {
            var cluster = CreateCluster(creds, act);
            ClusterInstances.Add(cluster);
            return cluster;
        }
        
        private IDseCluster CreateDseCluster(string creds = "creds-v1.zip", Action<DseClusterBuilder> act = null)
        {
            var builder = DseCluster.Builder()
                                   .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(22000).SetConnectTimeoutMillis(60000))
                                   .WithQueryTimeout(60000);
            act?.Invoke(builder);
            builder = builder
                      .WithCloudSecureConnectionBundle(
                          Path.Combine(((CloudCluster)TestCluster).SniHomeDirectory, "certs", "bundles", creds))
                      .WithPoolingOptions(
                          new PoolingOptions().SetHeartBeatInterval(200))
                      .WithReconnectionPolicy(new ConstantReconnectionPolicy(100));
            return builder.Build();
        }
        
        protected IDseCluster CreateTemporaryDseCluster(string creds = "creds-v1.zip", Action<DseClusterBuilder> act = null)
        {
            var cluster = CreateDseCluster(creds, act);
            ClusterInstances.Add(cluster);
            return cluster;
        }

        protected async Task<ISession> CreateSessionAsync(
            string creds = "creds-v1.zip", int retries = SharedCloudClusterTest.MaxRetries, Action<Builder> act = null)
        {
            Exception last = null;
            ICluster cluster = null;
            for (var i = 0; i < SharedCloudClusterTest.MaxRetries; i++)
            {
                try
                {
                    cluster = CreateTemporaryCluster(creds, act);
                    return await cluster.ConnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Task.Delay(1000).GetAwaiter().GetResult();
                    if (cluster != null)
                    {
                        cluster.Dispose();
                        cluster = null;
                    }
                }
            }
            throw last;
        }

        protected Task<IDseSession> CreateDseSessionAsync(string creds = "creds-v1.zip", Action<DseClusterBuilder> act = null)
        {
            return CreateTemporaryDseCluster(creds, act).ConnectAsync();
        }

        protected override ITestCluster CreateNew(int nodeLength, TestClusterOptions options, bool startCluster)
        {
            return TestCloudClusterManager.CreateNew(_sniCertValidation);
        }
    }
}
