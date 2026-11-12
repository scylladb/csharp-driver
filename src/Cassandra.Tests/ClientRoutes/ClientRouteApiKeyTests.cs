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

using NUnit.Framework;

using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests.ClientRoutes
{
    [TestFixture]
    public sealed class ClientRouteApiKeyTests
    {
        [Test]
        public void EqualityIsOrdinalAndSupportsHashLookup()
        {
            var hostId = Guid.NewGuid();
            var key = new ClientRouteApiKey("connection-a", hostId);
            var equalKey = new ClientRouteApiKey("connection-a", hostId);
            var differentlyCasedKey = new ClientRouteApiKey("CONNECTION-A", hostId);
            var differentHostKey = new ClientRouteApiKey("connection-a", Guid.NewGuid());
            var values = new Dictionary<ClientRouteApiKey, string> { { key, "route" } };

            Assert.IsTrue(key.Equals(equalKey));
            Assert.IsTrue(key.Equals((object)equalKey));
            Assert.AreEqual(key.GetHashCode(), equalKey.GetHashCode());
            Assert.IsFalse(key.Equals(differentlyCasedKey));
            Assert.IsFalse(key.Equals(differentHostKey));
            Assert.IsFalse(key.Equals(null));
            Assert.IsTrue(values.TryGetValue(equalKey, out var value));
            Assert.AreEqual("route", value);
            Assert.IsFalse(values.ContainsKey(differentlyCasedKey));
            Assert.IsFalse(values.ContainsKey(differentHostKey));
        }
    }
}
