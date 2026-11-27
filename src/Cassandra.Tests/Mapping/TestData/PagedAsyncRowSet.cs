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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Tests.Mapping.TestData
{
    /// <summary>
    /// Rows in two pages: the following page is read only once the test releases it.
    /// </summary>
    public sealed class PagedAsyncRowSet : AsyncRowSet
    {
        private readonly CqlColumn[] _columns;
        private readonly Row[] _firstPage;
        private readonly Row[] _followingPage;
        private readonly TaskCompletionSource<bool> _waiting =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="firstPage">The rows of the first page, which also give the columns.</param>
        /// <param name="followingPage">The rows of the following page.</param>
        public PagedAsyncRowSet(RowSet firstPage, RowSet followingPage)
        {
            _columns = firstPage.Columns;
            _firstPage = firstPage.ToArray();
            _followingPage = followingPage.ToArray();
        }

        /// <summary>
        /// Completes when an enumeration waits for the following page.
        /// </summary>
        public Task WaitingForFollowingPage => _waiting.Task;

        public override CqlColumn[] Columns => _columns;

        public void ReleaseFollowingPage() => _release.TrySetResult(true);

        public override async IAsyncEnumerator<Row> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var row in _firstPage)
            {
                yield return row;
            }

            _waiting.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in _followingPage)
            {
                yield return row;
            }
        }
    }
}
