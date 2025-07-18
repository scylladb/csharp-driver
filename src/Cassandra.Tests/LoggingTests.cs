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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class LoggingTests
    {
        private TraceLevel _originalTraceLevelSwitch;

        [Test]
        public void FactoryBasedLoggerHandler_Methods_Not_Throw()
        {
            UseAllMethods(new Logger.FactoryBasedLoggerHandler(typeof(int)));
        }

        [SetUp]
        public void TestSetup()
        {
            _originalTraceLevelSwitch = Diagnostics.CassandraTraceSwitch.Level;
        }

        [TearDown]
        public void TestTearDown()
        {
            Diagnostics.CassandraTraceSwitch.Level = _originalTraceLevelSwitch;
        }

        [Test]
        public void Initialization_Should_Log_Driver_Version()
        {
            Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Info;
            var listener = new TestTraceListener();
            Trace.Listeners.Add(listener);
            var builder = Cluster.Builder().AddContactPoint(TestHelper.UnreachableHostAddress).Build();
            // No host available
            Assert.Throws<NoHostAvailableException>(() => builder.Connect());

            // The name and version should be logged
            var message = listener.Messages.Values.First(m => m.Contains("Connecting to cluster using"));
            StringAssert.IsMatch(@"ScyllaDB .*Driver .*v\d+\.\d+\.\d+(-.*)?", message);
        }

        [Test]
        public void FactoryBasedLoggerHandler_Methods_Should_Output_To_Trace()
        {
            var level = Diagnostics.CassandraTraceSwitch.Level;
            try
            {
                Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
                var listener = new TestTraceListener();
                Trace.Listeners.Add(listener);
                var loggerHandler = new Logger.TraceBasedLoggerHandler(typeof(int));
                UseAllMethods(loggerHandler);
                Trace.Listeners.Remove(listener);
                Assert.AreEqual(6, listener.Messages.Count);
                var expectedMessages = new[]
                {
                    "Test exception 1",
                    "Message 1",
                    "Message 2 Param1",
                    "Message 3 Param2",
                    "Message 4 Param3",
                    "Message 5 Param4"
                };
                var messages = listener.Messages.Keys.OrderBy(k => k).Select(k => listener.Messages[k]).ToArray();
                for (var i = 0; i < expectedMessages.Length; i++)
                {
                    StringAssert.Contains(expectedMessages[i], messages[i]);
                }
            }
            finally
            {
                Diagnostics.CassandraTraceSwitch.Level = level;
            }
        }

        [Test]
        public void FactoryBasedLoggerHandler_LogError_Handles_Concurrent_Calls()
        {
            var level = Diagnostics.CassandraTraceSwitch.Level = TraceLevel.Verbose;
            try
            {
                var listener = new TestTraceListener();
                Trace.Listeners.Add(listener);
                var loggerHandler = new Logger.TraceBasedLoggerHandler(typeof(int));
                UseAllMethods(loggerHandler);
                Trace.Listeners.Remove(listener);
                Assert.AreEqual(6, listener.Messages.Count);
                var actions = Enumerable
                              .Repeat(true, 1000)
                              .Select<bool, Action>((_, index) => () =>
                              {
                                  loggerHandler.Error(new ArgumentException("Test exception " + index,
                                      new Exception("Test inner exception")));
                              });
                TestHelper.ParallelInvoke(actions);
            }
            finally
            {
                Diagnostics.CassandraTraceSwitch.Level = level;
            }
        }

        private void UseAllMethods(Logger.ILoggerHandler loggerHandler)
        {
            loggerHandler.Error(new Exception("Test exception 1"));
            loggerHandler.Error("Message 1", new Exception("Test exception 1"));
            loggerHandler.Error("Message 2 {0}", "Param1");
            loggerHandler.Info("Message 3 {0}", "Param2");
            loggerHandler.Verbose("Message 4 {0}", "Param3");
            loggerHandler.Warning("Message 5 {0}", "Param4");
        }

        internal class TestTraceListener : TraceListener
        {
            public readonly ConcurrentDictionary<int, string> Messages = new ConcurrentDictionary<int, string>();

            private int _counter = -1;

            public override void Write(string message)
            {
            }

            public override void WriteLine(string message)
            {
                Messages.AddOrUpdate(Interlocked.Increment(ref _counter), message, (k, v) => message);
            }
        }
    }
}