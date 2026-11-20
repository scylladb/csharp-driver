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

namespace Cassandra.Tests.Mapping.Pocos
{
    /// <summary>
    /// A POCO with <see cref="DateOnly"/> and <see cref="TimeOnly"/> properties.
    /// </summary>
    public class DateOnlyEvent
    {
        public Guid Id { get; set; }

        public DateOnly Day { get; set; }

        public DateOnly? OptionalDay { get; set; }

        public TimeOnly Start { get; set; }

        public TimeOnly? OptionalStart { get; set; }

        public List<DateOnly> Days { get; set; }
    }
}
