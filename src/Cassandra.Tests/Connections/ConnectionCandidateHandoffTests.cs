//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using Cassandra.Connections;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class ConnectionCandidateHandoffTests
    {
        [Test]
        public void Replace_ShouldObserveReplacementBeforeReleasingPreviousObservation()
        {
            var events = new List<string>();
            var firstHandlers = new List<Action<IConnection>>();
            var secondHandlers = new List<Action<IConnection>>();
            var first = CreateConnection("first", events, firstHandlers);
            var second = CreateConnection("second", events, secondHandlers);

            using (var handoff = new ConnectionCandidateHandoff(first.Object))
            {
                var previousHandler = firstHandlers[0];

                handoff.Replace(second.Object);
                previousHandler(first.Object);

                Assert.AreSame(second.Object, handoff.Connection);
                Assert.IsFalse(handoff.IsUnavailable);
                CollectionAssert.AreEqual(
                    new[] { "first-add", "second-add", "first-remove" },
                    events);

                secondHandlers[0](second.Object);
                Assert.IsTrue(handoff.IsUnavailable);
            }
        }

        [Test]
        public void Replace_ShouldPreserveCloseObservationWhenReplacementIsSameConnection()
        {
            var events = new List<string>();
            var handlers = new List<Action<IConnection>>();
            var connection = CreateConnection("candidate", events, handlers);

            using (var handoff = new ConnectionCandidateHandoff(connection.Object))
            {
                handlers[0](connection.Object);
                handoff.Replace(connection.Object);

                Assert.IsTrue(handoff.IsUnavailable);
                CollectionAssert.AreEqual(new[] { "candidate-add" }, events);
            }
        }

        [Test]
        public void TryTransfer_ShouldRollbackWhenCandidateClosesDuringPublication()
        {
            var events = new List<string>();
            var handlers = new List<Action<IConnection>>();
            var connection = CreateConnection("candidate", events, handlers);
            var permanentCloseCount = 0;
            Action<IConnection> permanentHandler = _ => permanentCloseCount++;

            using (var handoff = new ConnectionCandidateHandoff(connection.Object))
            {
                var transferred = handoff.TryTransfer(
                    new object(),
                    candidate => candidate.Closing += permanentHandler,
                    candidate =>
                    {
                        events.Add("publish");
                        foreach (var handler in handlers.ToArray())
                        {
                            handler(candidate);
                        }
                        return true;
                    },
                    candidate =>
                    {
                        events.Add("rollback");
                        candidate.Closing -= permanentHandler;
                    });

                Assert.IsFalse(transferred);
                Assert.AreEqual(1, permanentCloseCount);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "candidate-add",
                        "candidate-add",
                        "publish",
                        "rollback",
                        "candidate-remove"
                    },
                    events);
            }
        }

        [Test]
        public void TryTransfer_ShouldKeepPermanentObservationAndReleaseTemporaryObservationAfterPublication()
        {
            var events = new List<string>();
            var handlers = new List<Action<IConnection>>();
            var connection = CreateConnection("candidate", events, handlers);
            var permanentCloseCount = 0;
            Action<IConnection> permanentHandler = _ => permanentCloseCount++;

            using (var handoff = new ConnectionCandidateHandoff(connection.Object))
            {
                var transferred = handoff.TryTransfer(
                    new object(),
                    candidate => candidate.Closing += permanentHandler,
                    candidate =>
                    {
                        Assert.AreEqual(2, handlers.Count);
                        events.Add("publish");
                        return true;
                    },
                    candidate => candidate.Closing -= permanentHandler);

                Assert.IsTrue(transferred);
                Assert.AreEqual(1, handlers.Count);
                handlers[0](connection.Object);
                Assert.AreEqual(1, permanentCloseCount);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "candidate-add",
                        "candidate-add",
                        "publish",
                        "candidate-remove"
                    },
                    events);
            }
        }

        private static Mock<IConnection> CreateConnection(
            string name,
            ICollection<string> events,
            ICollection<Action<IConnection>> handlers)
        {
            var connection = new Mock<IConnection>();
            connection.SetupAdd(value => value.Closing += It.IsAny<Action<IConnection>>())
                      .Callback<Action<IConnection>>(handler =>
                      {
                          events.Add(name + "-add");
                          handlers.Add(handler);
                      });
            connection.SetupRemove(value => value.Closing -= It.IsAny<Action<IConnection>>())
                      .Callback<Action<IConnection>>(handler =>
                      {
                          events.Add(name + "-remove");
                          handlers.Remove(handler);
                      });
            return connection;
        }
    }
}
