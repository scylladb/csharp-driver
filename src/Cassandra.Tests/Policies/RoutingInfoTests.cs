//
//      Copyright (C) ScyllaDB
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
using System.Collections.Generic;
using System.Net;
using Cassandra.ExecutionProfiles;
using Cassandra.SessionManagement;
using Moq;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.Policies
{
    [TestFixture]
    public class RoutingInfoTests
    {
        [Test]
        public void ForStatement_Should_Use_The_Statement_Keyspace_Table_And_Routing_Key()
        {
            var routingKey = new RoutingKey { RawRoutingKey = new byte[] { 1 } };
            var statement = new Mock<IStatement>();
            statement.SetupGet(s => s.Keyspace).Returns("ks1");
            statement.SetupGet(s => s.TableName).Returns("tbl");
            statement.SetupGet(s => s.RoutingKey).Returns(routingKey);

            var routing = RoutingInfo.ForStatement(
                statement.Object, "session_ks", GetRequestOptions(ConsistencyLevel.LocalOne));

            Assert.AreEqual("ks1", routing.Keyspace);
            Assert.AreEqual("tbl", routing.Table);
            Assert.AreSame(routingKey, routing.RoutingKey);
            Assert.AreEqual(ConsistencyLevel.LocalOne, routing.Consistency);
            Assert.AreEqual(ConsistencyLevel.LocalSerial, routing.SerialConsistency);
            Assert.IsFalse(routing.IsConfirmedLwt);
            Assert.IsFalse(routing.ShouldRouteAsLwt);
        }

        [Test]
        public void ForStatement_Should_Use_The_Consistency_Levels_Of_The_Statement_Over_The_Profile()
        {
            var statement = new SimpleStatement("SELECT * FROM tbl")
                .SetConsistencyLevel(ConsistencyLevel.Quorum)
                .SetSerialConsistencyLevel(ConsistencyLevel.Serial);

            var routing = RoutingInfo.ForStatement(statement, null, GetRequestOptions(ConsistencyLevel.LocalOne));

            Assert.AreEqual(ConsistencyLevel.Quorum, routing.Consistency);
            Assert.AreEqual(ConsistencyLevel.Serial, routing.SerialConsistency);
            // The serial consistency level alone doesn't make a statement a lightweight transaction
            Assert.IsFalse(routing.ShouldRouteAsLwt);
        }

        [Test]
        public void ForKeyspace_Should_Have_No_Consistency_Levels()
        {
            var routing = RoutingInfo.ForKeyspace("ks1");

            Assert.AreEqual("ks1", routing.Keyspace);
            Assert.IsNull(routing.Consistency);
            Assert.IsNull(routing.SerialConsistency);
            Assert.IsFalse(routing.ShouldRouteAsLwt);
        }

        [Test]
        public void ForStatement_Should_Use_The_Session_Keyspace_When_The_Statement_Has_None()
        {
            var routing = RoutingInfo.ForStatement(
                new SimpleStatement("SELECT * FROM tbl"), "session_ks", GetRequestOptions(ConsistencyLevel.LocalOne));

            Assert.AreEqual("session_ks", routing.Keyspace);
        }

        [TestCase(ConsistencyLevel.Serial)]
        [TestCase(ConsistencyLevel.LocalSerial)]
        public void ForStatement_Should_Route_As_Lwt_When_Only_The_Profile_Sets_A_Serial_Consistency(ConsistencyLevel consistency)
        {
            var routing = RoutingInfo.ForStatement(
                new SimpleStatement("SELECT * FROM tbl"), null, GetRequestOptions(consistency));

            Assert.IsTrue(routing.ShouldRouteAsLwt);
            Assert.IsFalse(routing.IsConfirmedLwt);
        }

        [Test]
        public void ForStatement_Should_Not_Route_As_Lwt_When_The_Statement_Consistency_Overrides_The_Profile()
        {
            var statement = new SimpleStatement("SELECT * FROM tbl").SetConsistencyLevel(ConsistencyLevel.Quorum);

            var routing = RoutingInfo.ForStatement(statement, null, GetRequestOptions(ConsistencyLevel.Serial));

            Assert.IsFalse(routing.ShouldRouteAsLwt);
        }

        [Test]
        public void ForStatement_Should_Route_As_Lwt_When_The_Statement_Is_A_Confirmed_Lwt()
        {
            var statement = new Mock<IStatement>();
            statement.Setup(s => s.IsLwt()).Returns(true);

            var routing = RoutingInfo.ForStatement(statement.Object, null, GetRequestOptions(ConsistencyLevel.LocalOne));

            Assert.IsTrue(routing.IsConfirmedLwt);
            Assert.IsTrue(routing.ShouldRouteAsLwt);
        }

        private static IRequestOptions GetRequestOptions(ConsistencyLevel consistency)
        {
            var options = new Mock<IRequestOptions>();
            options.SetupGet(o => o.ConsistencyLevel).Returns(consistency);
            options.SetupGet(o => o.SerialConsistencyLevel).Returns(ConsistencyLevel.LocalSerial);
            return options.Object;
        }
    }
}
