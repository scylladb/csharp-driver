﻿#region License

/*
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership.  The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */

#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using Cassandra.DataStax.Graph.Internal;
using Newtonsoft.Json.Linq;

namespace Cassandra.Serialization.Graph.Tinkerpop.Structure.IO.GraphSON
{
    internal class MapSerializer : IGraphSONDeserializer, IGraphSONSerializer
    {
        public dynamic Objectify(JToken graphsonObject, IGraphSONReader reader)
        {
            var jArray = graphsonObject as JArray;
            if (jArray == null)
            {
                return new Dictionary<object, object>(0);
            }
            var result = new Dictionary<object, object>(jArray.Count / 2);
            for (var i = 0; i < jArray.Count; i += 2)
            {
                result[reader.ToObject(jArray[i])] = reader.ToObject(jArray[i + 1]);
            }
            // IDictionary<object, object>
            return result;
        }

        public Dictionary<string, dynamic> Dictify(dynamic objectData, IGraphSONWriter writer)
        {
            var map = objectData as IDictionary;
            if (map == null)
            {
                throw new InvalidOperationException("Object must implement IDictionary");
            }
            var result = new object[map.Count * 2];
            var index = 0;
            foreach (var key in map.Keys)
            {
                result[index++] = writer.ToDict(key);
                result[index++] = writer.ToDict(map[key]);
            }
            return GraphSONUtil.ToTypedValue("Map", result);
        }
    }
}