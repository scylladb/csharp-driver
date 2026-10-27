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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cassandra.Benchmarks
{
    /// <summary>
    /// The first page of each RowSet and the following pages, which are only read when a page is fetched and so are
    /// shared by every RowSet. The paging state of a page holds the index of the following page.
    /// </summary>
    internal sealed class PagingData
    {
        private readonly Row[] _firstPageRows;
        private readonly RowSet[] _pages;

        public PagingData(int rowsPerPage, int pages)
        {
            var columns = new[]
            {
                new CqlColumn { Index = 0, Name = "id", TypeCode = ColumnTypeCode.Int, Type = typeof(int) },
                new CqlColumn { Index = 1, Name = "name", TypeCode = ColumnTypeCode.Text, Type = typeof(string) },
            };
            var columnIndexes = columns.ToDictionary(c => c.Name, c => c.Index);
            Row[] CreateRows(int page) => Enumerable.Range(0, rowsPerPage)
                .Select(i => new Row(new object[] { i, "page_" + page }, columns, columnIndexes))
                .ToArray();

            _firstPageRows = CreateRows(0);
            _pages = new RowSet[pages];
            for (var page = 1; page < pages; page++)
            {
                var rs = new RowSet();
                foreach (var row in CreateRows(page))
                {
                    rs.AddRow(row);
                }

                rs.PagingState = page + 1 < pages ? new[] { (byte)(page + 1) } : null;
                _pages[page] = rs;
            }
        }

        public static int Count(RowSet rs)
        {
            var count = 0;
            foreach (var row in rs)
            {
                count++;
            }

            return count;
        }

        public static async Task<int> CountAsync(AsyncRowSet rs)
        {
            var count = 0;
            await foreach (var row in rs)
            {
                count++;
            }

            return count;
        }

        public RowSet CreateRowSet(Func<byte[], Task<RowSet>> fetchNextPage)
        {
            var rs = new RowSet();
            foreach (var row in _firstPageRows)
            {
                rs.AddRow(row);
            }

            rs.PagingState = _pages.Length > 1 ? new byte[] { 1 } : null;
            rs.SetFetchNextPageHandler(fetchNextPage, Timeout.Infinite, null);
            return rs;
        }

        public RowSet GetPage(byte[] pagingState) => _pages[pagingState[0]];
    }
}
