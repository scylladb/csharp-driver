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
using Cassandra.DataStax.Auth;
using Cassandra.Tests.TestAttributes;
using NUnit.Framework;

namespace Cassandra.Tests.DataStax.Auth
{
    [TestFixture]
    public class DseGssapiAuthProviderTests
    {
#if NETCOREAPP
        [WinOnly]
        [Test]
        public void When_NetStandard20AndWindows_Should_NotThrowException()
        {
            var provider = new DseGssapiAuthProvider();
        }

        [NotWindows]
        [Test]
        public void When_NetStandard20AndNotWindows_Should_ThrowException()
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                var provider = new DseGssapiAuthProvider();
            });
        }
#endif

#if NETFRAMEWORK

        [Test]
        public void When_NetFramework_Should_NotThrowException()
        {
            var provider = new DseGssapiAuthProvider();
        }

#else
        [WinOnly]
        [Test]
        public void When_NotNetFrameworkAndWindows_Should_NotThrowException()
        {
            var provider = new DseGssapiAuthProvider();
        }

        [NotWindows]
        [Test]
        public void When_NotNetFrameworkAndNotWindows_Should_ThrowException()
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                var provider = new DseGssapiAuthProvider();
            });
        }

#endif
    }
}