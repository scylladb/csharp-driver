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

namespace Cassandra.Responses
{
    internal class EventResponse : Response
    {
        public const byte OpCode = 0x0C;
        private readonly Logger _logger = new Logger(typeof(EventResponse));

        /// <summary>
        /// Information on the actual event
        /// </summary>
        public CassandraEventArgs CassandraEventArgs { get; set; }

        internal EventResponse(Frame frame)
            : base(frame)
        {
            string eventTypeString = Reader.ReadString();
            if (eventTypeString == "TOPOLOGY_CHANGE")
            {
                var ce = new TopologyChangeEventArgs();
                ce.What = Reader.ReadString() == "NEW_NODE"
                              ? TopologyChangeEventArgs.Reason.NewNode
                              : TopologyChangeEventArgs.Reason.RemovedNode;
                ce.Address = Reader.ReadInet();
                CassandraEventArgs = ce;
                return;
            }
            if (eventTypeString == "STATUS_CHANGE")
            {
                var ce = new StatusChangeEventArgs();
                ce.What = Reader.ReadString() == "UP"
                              ? StatusChangeEventArgs.Reason.Up
                              : StatusChangeEventArgs.Reason.Down;
                ce.Address = Reader.ReadInet();
                CassandraEventArgs = ce;
                return;
            }
            if (eventTypeString == "SCHEMA_CHANGE")
            {
                CassandraEventArgs = EventResponse.ParseSchemaChangeBody(frame.Header.Version, Reader);
                return;
            }
            if (eventTypeString == "CLIENT_ROUTES_CHANGE")
            {
                CassandraEventArgs = EventResponse.ParseClientRoutesChangeBody(Reader);
                return;
            }

            var ex = new DriverInternalError("Unknown event type: " + eventTypeString);
            _logger.Error(ex);
            throw ex;
        }

        private static ClientRoutesChangeEventArgs ParseClientRoutesChangeBody(FrameReader reader)
        {
            try
            {
                var changeType = reader.ReadString();
                if (changeType != "UPDATE_NODES")
                {
                    throw new DriverInternalError("Unknown CLIENT_ROUTES_CHANGE change type: " + changeType);
                }

                var connectionIds = reader.ReadStringList();
                var hostIdStrings = reader.ReadStringList();
                if (connectionIds.Length != hostIdStrings.Length)
                {
                    throw new DriverInternalError(
                        "Invalid CLIENT_ROUTES_CHANGE event: connection ID and host ID list lengths differ.");
                }

                var hostIds = new Guid[hostIdStrings.Length];
                for (var i = 0; i < hostIdStrings.Length; i++)
                {
                    try
                    {
                        hostIds[i] = Guid.Parse(hostIdStrings[i]);
                    }
                    catch (FormatException ex)
                    {
                        throw new DriverInternalError(
                            "Invalid CLIENT_ROUTES_CHANGE host ID at index " + i + ": " + hostIdStrings[i], ex);
                    }
                }

                return new ClientRoutesChangeEventArgs
                {
                    What = ClientRoutesChangeEventArgs.Reason.UpdateNodes,
                    ConnectionIds = connectionIds,
                    HostIds = hostIds
                };
            }
            catch (DriverInternalError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DriverInternalError("Invalid CLIENT_ROUTES_CHANGE event payload.", ex);
            }
        }

        public static SchemaChangeEventArgs ParseSchemaChangeBody(ProtocolVersion protocolVersion, FrameReader reader)
        {
            var ce = new SchemaChangeEventArgs();
            var changeTypeText = reader.ReadString();
            SchemaChangeEventArgs.Reason changeType;
            switch (changeTypeText)
            {
                case "UPDATED":
                    changeType = SchemaChangeEventArgs.Reason.Updated;
                    break;

                case "DROPPED":
                    changeType = SchemaChangeEventArgs.Reason.Dropped;
                    break;

                default:
                    changeType = SchemaChangeEventArgs.Reason.Created;
                    break;
            }
            ce.What = changeType;
            if (!protocolVersion.SupportsSchemaChangeFullMetadata())
            {
                //protocol v1 and v2: <change_type><keyspace><table>
                ce.Keyspace = reader.ReadString();
                ce.Table = reader.ReadString();
                return ce;
            }
            //protocol v3+: <change_type><target><options>
            var target = reader.ReadString();
            ce.Keyspace = reader.ReadString();
            switch (target)
            {
                case "TABLE":
                    ce.Table = reader.ReadString();
                    break;

                case "TYPE":
                    ce.Type = reader.ReadString();
                    break;

                case "FUNCTION":
                    ce.FunctionName = reader.ReadString();
                    ce.Signature = reader.ReadStringList();
                    break;

                case "AGGREGATE":
                    ce.AggregateName = reader.ReadString();
                    ce.Signature = reader.ReadStringList();
                    break;
            }

            return ce;
        }

        internal static EventResponse Create(Frame frame)
        {
            return new EventResponse(frame);
        }
    }
}
