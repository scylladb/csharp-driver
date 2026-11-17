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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Cassandra.Connections;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.SessionManagement;
using Cassandra.Tests.Requests;

using Moq;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class BindMarkerTests
    {
        private static readonly SerializerManager Serializers = new SerializerManager(ProtocolVersion.V4);

        [Test]
        public void Should_Send_Simple_Statement_Names_To_The_Coordinator()
        {
            const string cql = "SELECT * FROM ks.tbl WHERE pk = :pk AND ck IN :selected";
            var statement = new SimpleStatement(cql, new { selected = new[] { 11, 12 }, pk = 7 });
            var body = Body(Request(statement));
            var flagsOffset = 4 + System.Text.Encoding.UTF8.GetByteCount(cql) + 2;

            Assert.That(body[flagsOffset] & (byte)QueryProtocolOptions.QueryFlags.WithNameForValues, Is.Not.Zero);
        }

        [TestCase("IN(ck)")]
        [TestCase("in(ck)")]
        [TestCase("value(IN(ck))")]
        [TestCase("value(in(ck))")]
        public void Should_Bind_Anonymous_In_Markers_By_Position(string markerName)
        {
            var wholeList = !markerName.StartsWith("value(", StringComparison.Ordinal);
            var metadata = Variables(markerName, wholeList);
            var cql = wholeList
                ? "SELECT * FROM ks.tbl WHERE pk = ? AND ck IN ?"
                : "SELECT * FROM ks.tbl WHERE pk = ? AND ck IN (?, ?)";
            var prepared = Prepare(cql, metadata);
            var values = wholeList ? new object[] { 7, new[] { 11, 12 } } : new object[] { 7, 11, 12 };
            var bound = prepared.Bind(values);
            var request = Request(bound);
            var body = Body(request);

            Assert.That(bound.QueryValues, Is.EqualTo(values));
            Assert.That(bound.RoutingKey.RawRoutingKey, Is.EqualTo(Serializer().Serialize(7)));
            Assert.That(body[5] & (byte)QueryProtocolOptions.QueryFlags.WithNameForValues, Is.Zero);
            Assert.That(BeConverter.ToUInt16(body.Skip(6).Take(2).ToArray()), Is.EqualTo(values.Length));
            AssertValues(body, 8, values);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Should_Reorder_Explicit_Named_In_Markers_Locally(bool wholeList)
        {
            var metadata = Variables("selected", wholeList);
            if (!wholeList)
            {
                metadata.Columns[2].Name = "other";
            }
            var prepared = Prepare(wholeList
                ? "SELECT * FROM ks.tbl WHERE pk = :pk AND ck IN :selected"
                : "SELECT * FROM ks.tbl WHERE pk = :pk AND ck IN (:selected, :other)", metadata);
            var bound = wholeList
                ? prepared.Bind(new { selected = new[] { 11, 12 }, pk = 7 })
                : prepared.Bind(new { other = 12, selected = 11, pk = 7 });
            var expected = wholeList ? new object[] { 7, new[] { 11, 12 } } : new object[] { 7, 11, 12 };
            var body = Body(Request(bound));

            Assert.That(bound.QueryValues, Is.EqualTo(expected));
            Assert.That(bound.RoutingKey.RawRoutingKey, Is.EqualTo(Serializer().Serialize(7)));
            Assert.That(body[5] & (byte)QueryProtocolOptions.QueryFlags.WithNameForValues, Is.Zero);
            AssertValues(body, 8, expected);
        }

        [TestCase("IN(ck)", "in(ck)", false)]
        [TestCase("in(ck)", "IN(ck)", false)]
        [TestCase("selected", "selected", false)]
        [TestCase("IN(ck)", "in(ck)", true)]
        [TestCase("in(ck)", "IN(ck)", true)]
        [TestCase("selected", "selected", true)]
        public async Task Should_Refresh_Metadata_After_Unprepared_Without_Changing_Bound_Values(
            string originalName, string refreshedName, bool useBatch)
        {
            var named = originalName == "selected";
            var prepared = Prepare(named
                ? "DELETE FROM ks.tbl WHERE pk = :pk AND ck IN :selected"
                : "DELETE FROM ks.tbl WHERE pk = ? AND ck IN ?", Variables(originalName, true));
            var originalVariables = prepared.Variables;
            var originalRoutingIndexes = prepared.RoutingIndexes;
            prepared.SetLwt(true);
            var bound = named
                ? prepared.Bind(new { selected = new[] { 11, 12 }, pk = 7 })
                : prepared.Bind(7, new[] { 11, 12 });
            var another = Prepare(prepared.Cql, Variables(originalName, true));
            var unaffected = new PreparedStatement(Variables("other", true), new byte[] { 2 }, null,
                prepared.Cql, null, Serializers, false)
            { RoutingIndexes = new[] { 0 } };
            var statement = useBatch
                ? (IStatement)new BatchStatement().Add(bound).Add(another.Bind(7, new[] { 13, 14 }))
                    .Add(unaffected.Bind(7, new[] { 15, 16 }))
                : bound;
            var request = Request(statement);
            var before = Body(request);
            var host = new Host(new IPEndPoint(IPAddress.Loopback, 9042), new ConstantReconnectionPolicy(1));
            var validHost = ValidHost.New(host, HostDistance.Local);
            var callbacks = new Queue<Func<IRequestError, Response, Task>>();
            var sentRequests = new List<IRequest>();
            var sentBodies = new List<byte[]>();
            var connection = new Mock<IConnection>();
            var endPoint = new Mock<IConnectionEndPoint>();
            endPoint.SetupGet(value => value.EndpointFriendlyName).Returns("test-node");
            connection.SetupGet(value => value.EndPoint).Returns(endPoint.Object);
            connection.Setup(value => value.SendWithKeyspace(
                    It.IsAny<IRequest>(), It.IsAny<string>(), It.IsAny<Func<IRequestError, Response, Task>>(),
                    It.IsAny<int>(), It.IsAny<bool>()))
                .Returns<IRequest, string, Func<IRequestError, Response, Task>, int, bool>((sent, _, callback, __, ___) =>
                {
                    sentRequests.Add(sent);
                    sentBodies.Add(Body(sent));
                    callbacks.Enqueue(callback);
                    return Task.FromResult<OperationState>(null);
                });
            var parent = new Mock<IRequestHandler>();
            parent.SetupGet(value => value.Serializer).Returns(Serializer());
            parent.SetupGet(value => value.Statement).Returns(statement);
            parent.SetupGet(value => value.RequestOptions).Returns(new TestConfigurationBuilder().Build().DefaultRequestOptions);
            parent.Setup(value => value.GetNextValidHost(It.IsAny<Dictionary<IPEndPoint, Exception>>())).Returns(validHost);
            parent.Setup(value => value.GetConnectionToValidHostAsync(
                    validHost, It.IsAny<IDictionary<IPEndPoint, Exception>>())).ReturnsAsync(connection.Object);
            parent.Setup(value => value.SetNodeExecutionCompleted(It.IsAny<Guid>())).Returns(true);
            parent.Setup(value => value.SetCompletedAsync(It.IsAny<Exception>(), It.IsAny<RowSet>())).ReturnsAsync(true);
            var session = Mock.Of<IInternalSession>();
            var execution = new RequestExecution(parent.Object, session, request, NullRequestObserver.Instance,
                new SessionRequestInfo(statement, null));

            execution.Start(false);
            await callbacks.Dequeue()(RequestError.CreateServerError(
                new PreparedQueryNotFoundException("cache evicted", new byte[] { 1 })), null).ConfigureAwait(false);
            Assert.That(sentRequests[1], Is.TypeOf<InternalPrepareRequest>());
            var refreshed = Variables(refreshedName, true);
            var resultColumns = new RowSetMetadata { Columns = new[] { new CqlColumn { Name = "fresh", TypeCode = ColumnTypeCode.Int } } };
            await callbacks.Dequeue()(null, new ProxyResultResponse(ResultResponse.ResultResponseKind.Prepared,
                new OutputPrepared(new byte[] { 1 }, refreshed, resultColumns))).ConfigureAwait(false);

            Assert.That(sentRequests, Has.Count.EqualTo(3));
            Assert.That(sentRequests[2], Is.SameAs(request));
            Assert.That(sentBodies[0], Is.EqualTo(before));
            Assert.That(sentBodies[2], Is.EqualTo(before));
            Assert.That(prepared.Variables, Is.SameAs(refreshed));
            Assert.That(originalVariables.Columns[1].Name, Is.EqualTo(originalName));
            Assert.That(prepared.RoutingIndexes, Is.SameAs(originalRoutingIndexes));
            Assert.That(prepared.IsLwt, Is.True);
            Assert.That(prepared.Variables.Columns[1].Name, Is.EqualTo(refreshedName));
            Assert.That(prepared.ResultMetadata.RowSetMetadata.Columns[0].Name, Is.EqualTo("fresh"));
            var rebound = named
                ? prepared.Bind(new { selected = new[] { 11, 12 }, pk = 7 })
                : prepared.Bind(7, new[] { 11, 12 });
            Assert.That(rebound.QueryValues, Is.EqualTo(bound.QueryValues));
            Assert.That(rebound.RoutingKey.RawRoutingKey, Is.EqualTo(bound.RoutingKey.RawRoutingKey));
            if (useBatch)
            {
                Assert.That(another.Variables, Is.SameAs(refreshed));
                Assert.That(another.ResultMetadata.RowSetMetadata.Columns[0].Name, Is.EqualTo("fresh"));
                Assert.That(unaffected.Variables.Columns[1].Name, Is.EqualTo("other"));
                Assert.That(unaffected.ResultMetadata, Is.Null);
            }
        }

        private static PreparedStatement Prepare(string cql, RowSetMetadata variables)
        {
            return new PreparedStatement(variables, new byte[] { 1 }, null, cql, null, Serializers, false)
            {
                RoutingIndexes = new[] { 0 }
            };
        }

        private static RowSetMetadata Variables(string name, bool wholeList)
        {
            var columns = new List<CqlColumn>
            {
                new CqlColumn { Name = "pk", TypeCode = ColumnTypeCode.Int, Index = 0 },
                new CqlColumn
                {
                    Name = name, Index = 1,
                    TypeCode = wholeList ? ColumnTypeCode.List : ColumnTypeCode.Int,
                    TypeInfo = wholeList ? new ListColumnInfo { ValueTypeCode = ColumnTypeCode.Int } : null
                }
            };
            if (!wholeList)
            {
                columns.Add(new CqlColumn { Name = name, TypeCode = ColumnTypeCode.Int, Index = 2 });
            }
            return new RowSetMetadata { Columns = columns.ToArray() };
        }

        private static ISerializer Serializer() => Serializers.GetCurrentSerializer();

        private static IRequest Request(IStatement statement) => RequestHandler.GetRequest(
            statement, Serializer(), new TestConfigurationBuilder().Build().DefaultRequestOptions);

        private static byte[] Body(IRequest request)
        {
            using (var stream = new MemoryStream())
            {
                request.WriteFrame(1, stream, Serializer(), false);
                return stream.ToArray().Skip(FrameHeader.Size).ToArray();
            }
        }

        private static void AssertValues(byte[] body, int offset, object[] values)
        {
            foreach (var value in values)
            {
                var length = BeConverter.ToInt32(body, offset);
                offset += 4;
                Assert.That(body.Skip(offset).Take(length), Is.EqualTo(Serializer().Serialize(value)));
                offset += length;
            }
        }
    }
}
