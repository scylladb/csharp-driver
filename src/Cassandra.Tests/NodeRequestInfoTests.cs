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

using System.Collections.Generic;
using System.Net;
using NUnit.Framework;

namespace Cassandra.Tests
{
    [TestFixture]
    public class NodeRequestInfoTests
    {
        private static readonly Host Host1 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9042), contactPoint: null);
        private static readonly Host Host2 = new Host(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 9042), contactPoint: null);

        [Test]
        public void Equals_Should_Compare_ExecutionId()
        {
            var info = new NodeRequestInfo(Host1, null);
            var other = new NodeRequestInfo(Host1, null);

            Assert.That(info.Equals(info), Is.True);
            Assert.That(info.Equals((object)info), Is.True);
            Assert.That(info.Equals(other), Is.False);
            Assert.That(info.Equals(null), Is.False);
            Assert.That(info.Equals((object)null), Is.False);
        }

        [Test]
        public void EqualityOperators_Should_Handle_Nulls()
        {
            var info = new NodeRequestInfo(Host1, null);
            var other = new NodeRequestInfo(Host2, null);
            NodeRequestInfo none = null;

#pragma warning disable CS1718 // Comparison made to same variable: exercises the operator itself.
            Assert.That(info == info, Is.True);
            Assert.That(info != info, Is.False);
            Assert.That(none == none, Is.True);
#pragma warning restore CS1718
            Assert.That(info == other, Is.False);
            Assert.That(info != other, Is.True);
            Assert.That(info == null, Is.False);
            Assert.That(null == info, Is.False);
            Assert.That(info != null, Is.True);
            Assert.That(null != info, Is.True);
        }

        [Test]
        public void Should_Work_As_A_Hash_Key()
        {
            var info = new NodeRequestInfo(Host1, null);
            var other = new NodeRequestInfo(Host2, null);
            var set = new HashSet<NodeRequestInfo> { info, other };

            Assert.That(set.Add(info), Is.False);
            Assert.That(set, Does.Contain(info));
            Assert.That(set, Does.Contain(other));
            Assert.That(set.Count, Is.EqualTo(2));
        }
    }
}
