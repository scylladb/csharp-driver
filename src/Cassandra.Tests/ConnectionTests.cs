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
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Connections;
using Cassandra.Observers.Null;
using Cassandra.Requests;
using Cassandra.Responses;
using Cassandra.Serialization;
using Moq;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class ConnectionTests
    {
        private static readonly IPEndPoint Address = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000);
        private const int TestFrameLength = 12;

        private static Mock<Connection> GetConnectionMock(Configuration config = null, ISerializerManager serializer = null)
        {
            config = config ?? new Configuration();
            return new Mock<Connection>(
                MockBehavior.Loose,
                serializer?.GetCurrentSerializer() ?? new SerializerManager(ProtocolVersion.MaxSupported).GetCurrentSerializer(),
                new ConnectionEndPoint(ConnectionTests.Address, config.ServerNameResolver, null),
                config,
                new StartupRequestFactory(config.StartupOptionsFactory),
                NullConnectionObserver.Instance);
        }

        [Test]
        public void ReadParse_Handles_Complete_Frames_In_Different_Buffers()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127 }, streamIds);
            buffer = GetResultBuffer(126);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            buffer = GetResultBuffer(125);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 125 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 3);
            Assert.AreEqual(3, responses.Count);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 3), responses.Select(r => ((ResultResponse)r).Kind));
        }

        [Test]
        public void ReadParse_Handles_Complete_Frames_In_A_Single_Frame()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 2);
            Assert.AreEqual(2, responses.Count);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Header()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).Concat(GetResultBuffer(100)).ToArray();
            //first 2 messages and 2 bytes of the third message
            var firstSlice = buffer.Length - TestFrameLength + 2;
            connection.ReadParse(buffer, firstSlice);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            buffer = buffer.Skip(firstSlice).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 3);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 3), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(99);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99 }, streamIds);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Header_In_Multiple_Messages()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).Concat(GetResultBuffer(100)).ToArray();
            //first 2 messages and 2 bytes of the third message
            var length = buffer.Length - TestFrameLength + 2;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            buffer = buffer.Skip(length).ToArray();
            length = buffer.Length - 8;
            //header is still not completed
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            //header and body are completed
            buffer = buffer.Skip(length).ToArray();
            length = buffer.Length;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 3);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 3), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(99);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99 }, streamIds);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Body()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            var exceptions = new ConcurrentBag<Exception>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) =>
                {
                    if (ex != null)
                    {
                        exceptions.Add(ex);
                        return;
                    }
                    responses.Add(r);
                }));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).Concat(GetResultBuffer(100)).ToArray();
            //almost 3 responses, just 1 byte of the body left
            var firstSlice = buffer.Length - 1;
            connection.ReadParse(buffer, firstSlice);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            buffer = buffer.Skip(firstSlice).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count + exceptions.Count == 3, 500, 5);
            CollectionAssert.IsEmpty(exceptions);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 3), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(1);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 1 }, streamIds);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Body_In_Multiple_Messages()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var originalBuffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).Concat(GetResultBuffer(100)).ToArray();
            //almost 3 responses, 3 byte of the body left
            var firstSlice = originalBuffer.Length - 3;
            connection.ReadParse(originalBuffer, firstSlice);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            //2 more bytes, but not enough
            var buffer = originalBuffer.Skip(firstSlice).Take(2).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            //the last byte
            buffer = originalBuffer.Skip(firstSlice + 2).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 3);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 3), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(1);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 1 }, streamIds);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Body_Multiple_Times()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127)
                .Concat(GetResultBuffer(126))
                .Concat(GetResultBuffer(100))
                .ToArray();
            //almost 3 responses, 3 byte of the body left
            var length = buffer.Length - 3;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            //the rest of the last message plus a new message
            buffer = buffer.Skip(length).Concat(GetResultBuffer(99)).ToArray();
            length = buffer.Length - 3;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100 }, streamIds);

            buffer = buffer.Skip(length).Concat(GetResultBuffer(98)).ToArray();
            length = buffer.Length - 3;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99 }, streamIds);

            buffer = buffer.Skip(length).Concat(GetResultBuffer(97)).ToArray();
            length = buffer.Length;
            connection.ReadParse(buffer, length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99, 98, 97 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 6);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 6), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(1);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99, 98, 97, 1 }, streamIds);
        }

        [Test]
        public void ReadParse_Handles_UnComplete_Body_With_Following_Frames()
        {
            var connectionMock = GetConnectionMock();
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                .Callback<short>(id => streamIds.Add(id))
                .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(127).Concat(GetResultBuffer(126)).Concat(GetResultBuffer(100)).ToArray();
            //almost 3 responses, just 1 byte of the body left
            var firstSlice = buffer.Length - 1;
            connection.ReadParse(buffer, firstSlice);
            CollectionAssert.AreEqual(new short[] { 127, 126 }, streamIds);
            buffer = buffer.Skip(firstSlice).Concat(GetResultBuffer(99)).ToArray();
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 4);
            CollectionAssert.AreEqual(Enumerable.Repeat(ResultResponse.ResultResponseKind.Void, 4), responses.Select(r => ((ResultResponse)r).Kind));
            buffer = GetResultBuffer(1);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 127, 126, 100, 99, 1 }, streamIds);
        }

        [Test]
        public async Task ReadParse_While_Disposing_Faults_Tasks_But_Never_Throws()
        {
            var connectionMock = GetConnectionMock();
            var responses = new ConcurrentBag<object>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                          .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add((object)ex ?? r)));
            var connection = connectionMock.Object;
            var bufferBuilder = Enumerable.Empty<byte>();
            const int totalFrames = 63;
            for (var i = 0; i < totalFrames; i++)
            {
                bufferBuilder = bufferBuilder.Concat(GetResultBuffer((byte)i));
            }
            var buffer = bufferBuilder.ToArray();
            var schedulerPair = new ConcurrentExclusiveSchedulerPair();
            var tasks = new List<Task>(buffer.Length);
            for (var i = 0; i < buffer.Length; i++)
            {
                var index = i;
                tasks.Add(
                    Task.Factory.StartNew(() => connection.ReadParse(buffer.Skip(index).ToArray(), 1),
                        CancellationToken.None, TaskCreationOptions.None, schedulerPair.ExclusiveScheduler));
            }
            var random = new Random();
            // Lets wait for some of the read tasks to be completed
            await tasks[random.Next(20, 50)].ConfigureAwait(false);
            await Task.Run(() => connection.Dispose()).ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            // We must await for a short while until operation states are callback (on the TaskScheduler)
            await TestHelper.WaitUntilAsync(() => totalFrames == responses.Count, 100, 30).ConfigureAwait(false);
            Assert.AreEqual(totalFrames, responses.Count);
        }

        [Test]
        public void Should_HandleDifferentProtocolVersionsInDifferentConnections_When_OneConnectionResponseVersionIsDifferentThanSerializer()
        {
            var serializer = new SerializerManager(ProtocolVersion.V4);
            var connectionMock = GetConnectionMock(null, serializer);
            var connectionMock2 = GetConnectionMock(null, serializer);
            var streamIds = new List<short>();
            var responses = new ConcurrentBag<Response>();
            connectionMock.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                          .Callback<short>(id => streamIds.Add(id))
                          .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            connectionMock2.Setup(c => c.RemoveFromPending(It.IsAny<short>()))
                          .Callback<short>(id => streamIds.Add(id))
                          .Returns(() => OperationStateExtensions.CreateMock((ex, r) => responses.Add(r)));
            var connection = connectionMock.Object;
            var buffer = GetResultBuffer(128, ProtocolVersion.V4);
            connection.ReadParse(buffer, buffer.Length);
            buffer = ConnectionTests.GetResultBuffer(100, ProtocolVersion.V2);
            connectionMock2.Object.ReadParse(buffer, buffer.Length);
            buffer = GetResultBuffer(129, ProtocolVersion.V4);
            connection.ReadParse(buffer, buffer.Length);
            CollectionAssert.AreEqual(new short[] { 128, 100, 129 }, streamIds);
            TestHelper.WaitUntil(() => responses.Count == 3);
            Assert.AreEqual(3, responses.Count);
        }

        /// <summary>
        /// Gets a buffer containing 8 bytes for header and 4 bytes for the body.
        /// For result + void response message  (protocol v2)
        /// </summary>
        private static byte[] GetResultBuffer(short streamId, ProtocolVersion version = ProtocolVersion.V2)
        {
            var header = (byte)((int)version | 0x80);
            if (version.Uses2BytesStreamIds())
            {
                var bytes = BeConverter.GetBytes(streamId);
                return new byte[]
                {
                    //header
                    header, 0, bytes[0], bytes[1], ResultResponse.OpCode, 0, 0, 0, 4, 
                    //body
                    0, 0, 0, 1
                };
            }

            return new byte[]
            {
                //header
                header, 0, (byte)streamId, ResultResponse.OpCode, 0, 0, 0, 4, 
                //body
                0, 0, 0, 1
            };
        }
    }
}