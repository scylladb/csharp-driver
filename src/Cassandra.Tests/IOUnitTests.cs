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
using System.IO;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.CollectionAssert;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Compression;
using Cassandra.Connections;
using Cassandra.Responses;
using Cassandra.Serialization;
using Cassandra.Tasks;
using Microsoft.IO;

namespace Cassandra.Tests
{
    [TestFixture]
    public class IOUnitTests
    {
        [Test]
        public void FrameReader_Should_HandlePartialReads()
        {
            using (var stream = new PartialReadStream(new byte[] { 1, 2, 3, 4 }))
            {
                var reader = new FrameReader(stream, null, false);

                Assert.AreEqual(0x01020304, reader.ReadInt32());
            }
        }

        [Test]
        public void FrameReader_Should_ThrowEndOfStreamException_WhenStreamIsTruncated()
        {
            using (var stream = new PartialReadStream(new byte[] { 1, 2, 3 }))
            {
                var reader = new FrameReader(stream, null, false);

                Assert.Throws<EndOfStreamException>(() => reader.ReadInt32());
            }
        }

        [Test]
        public void OperationState_Can_Concurrently_Get_Timeout_And_Response()
        {
            var counter = 0;
            var timedOutReceived = 0;
            var clientCallbackCounter = 0;
            const int times = 40;
            var expectedTimedout = 0;
            TestHelper.Invoke(() =>
            {
                Action<Exception, Response> clientCallback = (ex, r) =>
                {
                    Interlocked.Increment(ref clientCallbackCounter);
                };
                var state = OperationStateExtensions.CreateMock(clientCallback);
                var actions = new Action[]
                {
                    () =>
                    {
                        var timedout = state.MarkAsTimedOut(
                            new OperationTimedOutException(new IPEndPoint(0, 1), 200), () =>
                            {
                                Interlocked.Increment(ref timedOutReceived);
                                return TaskHelper.Completed;
                            }, 0);
                        Interlocked.Add(ref expectedTimedout, timedout ? 1 : 0);
                    },
                    () =>
                    {
                        state.InvokeCallback(null, 0);
                    }
                };
                if ((counter++) % 2 == 0)
                {
                    //invert order
                    actions = actions.Reverse().ToArray();
                }
                TestHelper.ParallelInvoke(actions);
            }, times);
            //Allow callbacks to be called using the default scheduler
            Thread.Sleep(1000);
            Assert.AreEqual(times, clientCallbackCounter);
            Assert.AreEqual(expectedTimedout, timedOutReceived);
        }

        [Test]
        public void OperationState_Can_Concurrently_Get_Calls_To_SetCompleted()
        {
            var counter = 0;
            var clientCallbackCounter = 0L;
            const int times = 40;
            TestHelper.Invoke(() =>
            {
                Action<Exception, Response> clientCallback = (ex, r) =>
                {
                    // ReSharper disable once AccessToModifiedClosure
                    Interlocked.Increment(ref clientCallbackCounter);
                };
                var state = OperationStateExtensions.CreateMock(clientCallback);
                var actions = Enumerable.Repeat<Action>(() =>
                {
                    var cb = state.SetCompleted();
                    cb(null, null, 0);
                }, 2);
                if ((counter++) % 2 == 0)
                {
                    //invert order
                    actions = actions.Reverse();
                }
                TestHelper.ParallelInvoke(actions.ToArray());
            }, times);
            //Allow callbacks to be called using the default scheduler
            Thread.Sleep(1000);
            Assert.AreEqual(times, Interlocked.Read(ref clientCallbackCounter));
        }

        [Test]
        public void OperationState_Cancel_Should_Never_Callback_Client()
        {
            var clientCallbackCounter = 0;
            Action<Exception, Response> clientCallback = (ex, r) =>
            {
                Interlocked.Increment(ref clientCallbackCounter);
            };
            var state = OperationStateExtensions.CreateMock(clientCallback);
            state.Cancel();
            state.InvokeCallback(null, 0);
            //Allow callbacks to be called using the default scheduler
            Thread.Sleep(20);
            Assert.AreEqual(0, clientCallbackCounter);
        }

        [Test]
        public void OperationState_Cancel_Invokes_Cancellation_Handler_Once()
        {
            var state = OperationStateExtensions.CreateMock((ex, response) => { });
            var calls = 0;
            state.SetCancellationHandler(() => Interlocked.Increment(ref calls));

            state.Cancel();
            state.Cancel();

            Assert.AreEqual(1, calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OperationState_Cancel_Should_Keep_Wire_Handler_Until_Operation_Is_Terminal(bool cancelFirst)
        {
            var completionCount = 0;
            var state = OperationStateExtensions.CreateMock((_, __) => { });

            if (cancelFirst)
            {
                state.Cancel();
            }
            state.SetWireCompletionHandler(() => Interlocked.Increment(ref completionCount));
            if (!cancelFirst)
            {
                state.Cancel();
            }
            state.Cancel();

            Assert.AreEqual(0, completionCount);
            state.CompleteWithoutWire();
            state.CompleteWithoutWire();
            Assert.AreEqual(1, completionCount);
        }

        [Test]
        public async Task OperationState_Timeout_Should_Keep_Wire_Handler_Until_Response_Is_Received()
        {
            var clientCallbackCount = 0;
            var lateResponseCount = 0;
            var wireCompletionCount = 0;
            var state = OperationStateExtensions.CreateMock(
                (_, __) => Interlocked.Increment(ref clientCallbackCount));
            state.SetWireCompletionHandler(() => Interlocked.Increment(ref wireCompletionCount));

            Assert.IsTrue(state.MarkAsTimedOut(
                new OperationTimedOutException(new IPEndPoint(0, 1), 200),
                () =>
                {
                    Interlocked.Increment(ref lateResponseCount);
                    return TaskHelper.Completed;
                },
                0));
            TestHelper.WaitUntil(() => Volatile.Read(ref clientCallbackCount) == 1);

            Assert.AreEqual(0, wireCompletionCount);
            var responseCallback = state.SetCompleted();
            await responseCallback(null, null, 0).ConfigureAwait(false);

            Assert.AreEqual(1, wireCompletionCount);
            Assert.AreEqual(1, lateResponseCount);
        }

        [Test]
        public void BeBinaryWriter_Close_Sets_Frame_Body_Length()
        {
            const int frameLength = 10;
            const int iterations = 8;
            const ProtocolVersion protocolVersion = ProtocolVersion.V2;
            var bufferPool = new RecyclableMemoryStreamManager();
            using (var stream = bufferPool.GetStream("test"))
            {
                for (var i = 0; i < iterations; i++)
                {
                    var writer = new FrameWriter(stream, new SerializerManager(protocolVersion).GetCurrentSerializer(), false);
                    writer.WriteFrameHeader(0, 127, 8);
                    writer.WriteUInt16(Convert.ToUInt16(0x0900 + i));
                    var length = writer.Close();
                    Assert.AreEqual(frameLength, length);
                }
                Assert.AreEqual(frameLength * iterations, stream.Length);
                for (byte i = 0; i < iterations; i++)
                {
                    var buffer = new byte[frameLength];
                    stream.Position = i * frameLength;
                    stream.ReadExactly(buffer, 0, frameLength);
                    CollectionAssert.AreEqual(new byte[] { 2, 0, 127, 8, 0, 0, 0, 2, 9, i }, buffer);
                }
            }
        }

        [Test]
        public void RecyclableMemoryStream_GetBufferList_Handles_Multiple_Blocks()
        {
            const int blockSize = 16;
            var buffer = new byte[256];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)i;
            }
            var bufferPool = new RecyclableMemoryStreamManager(new RecyclableMemoryStreamManager.Options
            {
                BlockSize = blockSize,
                LargeBufferMultiple = 1024,
                MaximumBufferSize = 1024 * 1024 * 10
            });
            using (var stream = bufferPool.GetStream())
            {
                stream.Write(buffer, 0, 12);
                CollectionAssert.AreEqual(new[] { new ArraySegment<byte>(buffer, 0, 12) }, stream.GetBufferList());
            }
            using (var stream = bufferPool.GetStream())
            {
                stream.Write(buffer, 0, blockSize);
                var bufferList = stream.GetBufferList();
                Assert.AreEqual(1, bufferList.Count);
                CollectionAssert.AreEqual(new[] { new ArraySegment<byte>(buffer, 0, blockSize) }, bufferList);
            }
            using (var stream = bufferPool.GetStream())
            {
                stream.Write(buffer, 0, blockSize * 2);
                var bufferList = stream.GetBufferList();
                Assert.AreEqual(2, bufferList.Count);
                CollectionAssert.AreEqual(new[] { new ArraySegment<byte>(buffer, 0, blockSize), new ArraySegment<byte>(buffer, blockSize, blockSize) }, bufferList);
            }
            using (var stream = bufferPool.GetStream())
            {
                stream.Write(buffer, 0, blockSize * 2 + 1);
                var bufferList = stream.GetBufferList();
                Assert.AreEqual(3, bufferList.Count);
                CollectionAssert.AreEqual(new[]
                {
                    new ArraySegment<byte>(buffer, 0, blockSize),
                    new ArraySegment<byte>(buffer, blockSize, blockSize),
                    new ArraySegment<byte>(buffer, blockSize * 2, 1)
                }, bufferList);
            }
            using (var stream = bufferPool.GetStream())
            {
                stream.Write(buffer, 0, blockSize * 2 + 1);
                // Converts the blocks into a single large buffer
                stream.GetBuffer();
                CollectionAssert.AreEqual(new[] { new ArraySegment<byte>(buffer, 0, blockSize * 2 + 1) }, stream.GetBufferList());
            }
        }

        [Test]
        public void FrameWriter_Writes_Big_Endian_Values_And_Utf8_Strings()
        {
            var expected = new byte[]
            {
                // Header: version, flags, stream id, opcode, body length
                4, 0, 0x01, 0x02, 7, 0, 0, 0, 36,
                0xAB, 0xCD,
                0xFF, 0xFF, 0xFF, 0xFE,
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x00, 0x02, 0xC5, 0xBC,
                0x00, 0x00, 0x00, 0x05, 0x61, 0x62, 0xE2, 0x82, 0xAC,
                0x00, 0x02, 0x00, 0x01, 0x61, 0x00, 0x02, 0x62, 0x63
            };
            // Small blocks, so that values and strings span block boundaries
            var bufferPool = new RecyclableMemoryStreamManager(new RecyclableMemoryStreamManager.Options
            {
                BlockSize = 16,
                LargeBufferMultiple = 1024,
                MaximumBufferSize = 1024 * 1024
            });
            using (var pooledStream = bufferPool.GetStream())
            {
                foreach (var stream in new MemoryStream[] { pooledStream, new MemoryStream() })
                {
                    var writer = new FrameWriter(stream, new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), false);
                    writer.WriteFrameHeader(0, 0x0102, 7);
                    writer.WriteUInt16(0xABCD);
                    writer.WriteInt32(-2);
                    writer.WriteLong(0x0102030405060708);
                    writer.WriteString("\u017C");
                    writer.WriteLongString("ab\u20AC");
                    writer.WriteStringList(new[] { "a", "bc" });
                    Assert.AreEqual(expected.Length, writer.Close());
                    CollectionAssert.AreEqual(expected, stream.ToArray(), stream.GetType().Name);
                }
            }
        }

        [Test]
        public void FrameWriter_Writes_Strings_Across_Pooled_Blocks()
        {
            var text = string.Concat(Enumerable.Repeat("za\u017C\u00F3\u0142\u0107 \U0001F980 ", 20));
            var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            var bufferPool = new RecyclableMemoryStreamManager(new RecyclableMemoryStreamManager.Options
            {
                BlockSize = 16,
                LargeBufferMultiple = 1024,
                MaximumBufferSize = 1024 * 1024
            });
            using (var stream = bufferPool.GetStream())
            {
                var writer = new FrameWriter(stream, new SerializerManager(ProtocolVersion.V4).GetCurrentSerializer(), false);
                writer.WriteByte(1);
                writer.WriteString(text);
                writer.WriteLongString(text);
                var expected = new byte[] { 1 }
                    .Concat(new[] { (byte)(textBytes.Length >> 8), (byte)textBytes.Length }).Concat(textBytes)
                    .Concat(new byte[] { 0, 0, (byte)(textBytes.Length >> 8), (byte)textBytes.Length }).Concat(textBytes);
                CollectionAssert.AreEqual(expected, stream.ToArray());
            }
        }

        [Test]
        public void WrappedStream_Tests()
        {
            var memoryStream = new MemoryStream(Enumerable.Range(0, 128).Select(i => (byte)i).ToArray(), false);
            var buffer = new byte[128];
            memoryStream.Position = 20;
            var wrapper = new WrappedStream(memoryStream, 100);
            var read = wrapper.Read(buffer, 0, 3);
            CollectionAssert.AreEqual(new byte[] { 20, 21, 22 }, buffer.Take(read));
            //do not pass the length of the wrapper
            memoryStream.Position = 10;
            wrapper = new WrappedStream(memoryStream, 2);
            read = wrapper.Read(buffer, 0, 100);
            CollectionAssert.AreEqual(new byte[] { 10, 11 }, buffer.Take(read));
            //do not pass the length of the internal stream
            memoryStream.Position = 126;
            wrapper = new WrappedStream(memoryStream, 2);
            read = wrapper.Read(buffer, 0, 100);
            CollectionAssert.AreEqual(new byte[] { 126, 127 }, buffer.Take(read));

        }

        private sealed class PartialReadStream : Stream
        {
            private readonly MemoryStream _inner;

            public PartialReadStream(byte[] buffer)
            {
                _inner = new MemoryStream(buffer, false);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get { return _inner.Position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, Math.Min(count, 1));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        class LockSynchronisationContext : SynchronizationContext
        {
            private readonly object _postLock = new object();

            public override void Send(SendOrPostCallback codeToRun, object state)
            {
                throw new NotImplementedException();
            }

            public override void Post(SendOrPostCallback codeToRun, object state)
            {
                lock (_postLock)
                {
                    codeToRun(state);
                }
            }

        }
    }
}
