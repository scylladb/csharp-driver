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
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace Cassandra.IntegrationTests.ClientRoutes
{
    [TestFixture]
    public sealed class ClientRoutesTestSupportTests
    {
        [Test]
        public void RunAllCleanupActionsExecutesEveryActionAndReportsEveryFailure()
        {
            var calls = new List<int>();
            var firstFailure = new InvalidOperationException("first");
            var secondFailure = new ArgumentException("second");

            var exception = Assert.Throws<AggregateException>(() =>
                ClientRoutesTestSupport.RunAllCleanupActions(
                    () =>
                    {
                        calls.Add(1);
                        throw firstFailure;
                    },
                    () => calls.Add(2),
                    () =>
                    {
                        calls.Add(3);
                        throw secondFailure;
                    },
                    () => calls.Add(4)));

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, calls);
            CollectionAssert.AreEqual(
                new Exception[] { firstFailure, secondFailure },
                exception.InnerExceptions);
        }
    }
}
