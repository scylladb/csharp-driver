//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Net.Sockets;

using Cassandra.Connections;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class ConnectionFailureTests
    {
        [Test]
        public void Should_PreferLaterNonSocketErrorOverSocketError()
        {
            var socketError = new SocketException((int)SocketError.ConnectionRefused);
            var authenticationError = new AuthenticationException("bad credentials");

            Assert.IsTrue(ConnectionFailure.ShouldReplacePreferred(socketError, authenticationError));
        }

        [Test]
        public void Should_NotPreferLaterSocketErrorOverNonSocketError()
        {
            var authenticationError = new AuthenticationException("bad credentials");
            var socketError = new SocketException((int)SocketError.ConnectionRefused);

            Assert.IsFalse(ConnectionFailure.ShouldReplacePreferred(authenticationError, socketError));
        }

        [Test]
        public void Should_PreferLaterErrorWhenBothErrorsAreInTheSameCategory()
        {
            Assert.IsTrue(ConnectionFailure.ShouldReplacePreferred(
                new SocketException((int)SocketError.ConnectionRefused),
                new SocketException((int)SocketError.TimedOut)));
            Assert.IsTrue(ConnectionFailure.ShouldReplacePreferred(
                new AuthenticationException("first"),
                new InvalidOperationException("second")));
        }

        [Test]
        public void Should_KeepSnapshotsOfEveryFailureCategory()
        {
            var preferred = new AuthenticationException("preferred");
            var superseded = new Exception[] { new SocketException((int)SocketError.ConnectionRefused) };
            var unresolved = new Exception[] { new InvalidOperationException("dns") };

            var target = new ConnectionFailure(preferred, superseded, unresolved);

            Assert.AreSame(preferred, target.PreferredError);
            Assert.AreSame(preferred, target.InnerException);
            CollectionAssert.AreEqual(superseded, target.SupersededConnectionErrors);
            CollectionAssert.AreEqual(unresolved, target.UnresolvedResolutionErrors);
        }
    }
}
