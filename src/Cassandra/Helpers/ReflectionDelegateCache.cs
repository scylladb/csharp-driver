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
using System.Reflection;

namespace Cassandra.Helpers
{
    /// <summary>
    /// Caches delegates created from open generic methods via MakeGenericMethod + CreateDelegate.
    /// Used to avoid repeated reflection overhead when invoking generic methods with
    /// runtime-determined type arguments.
    ///
    /// The cache is bounded in practice by the finite set of .NET types used as column types
    /// or converter type pairs, so unbounded growth is not a concern.
    /// </summary>
    internal static class ReflectionDelegateCache<TKey, TDelegate> where TDelegate : Delegate
    {
        private static readonly ConcurrentDictionary<TKey, TDelegate> Cache = new();

        /// <summary>
        /// Returns a cached delegate for the given key, or creates one by calling
        /// MakeGenericMethod on <paramref name="openGenericMethod"/> with type arguments
        /// derived from the key via <paramref name="getTypeArgs"/>.
        /// </summary>
        public static TDelegate GetOrAdd(TKey key, MethodInfo openGenericMethod, Func<TKey, Type[]> getTypeArgs)
        {
            return Cache.GetOrAdd(key, k =>
            {
                var method = openGenericMethod.MakeGenericMethod(getTypeArgs(k));
                return (TDelegate)method.CreateDelegate(typeof(TDelegate));
            });
        }
    }
}
