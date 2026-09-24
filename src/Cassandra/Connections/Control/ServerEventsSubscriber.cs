//
//       Copyright (C) DataStax Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

using System;
using System.Threading.Tasks;
using Cassandra.Requests;
using Cassandra.Responses;

namespace Cassandra.Connections.Control
{
    internal class ServerEventsSubscriber : IServerEventsSubscriber
    {
        private const CassandraEventType DefaultCassandraEventTypes =
            CassandraEventType.TopologyChange | CassandraEventType.StatusChange | CassandraEventType.SchemaChange;

        private readonly CassandraEventType _eventTypes;
        private readonly bool _clientRoutesEnabled;

        internal ServerEventsSubscriber(bool clientRoutesEnabled = false)
        {
            _clientRoutesEnabled = clientRoutesEnabled;
            _eventTypes = ServerEventsSubscriber.DefaultCassandraEventTypes;
            if (clientRoutesEnabled)
            {
                _eventTypes |= CassandraEventType.ClientRoutesChange;
            }
        }

        /// <inheritdoc />
        public async Task SubscribeToServerEvents(IConnection connection, CassandraEventHandler handler)
        {
            connection.CassandraEventResponse += handler;

            // Register to events on the connection
            Response response;
            try
            {
                response = await connection.Send(new RegisterForEventRequest(_eventTypes)).ConfigureAwait(false);
            }
            catch (ProtocolErrorException ex) when (_clientRoutesEnabled)
            {
                throw new NotSupportedException(
                    "The server may not support the CLIENT_ROUTES_CHANGE event required by client routes. " +
                    "Server protocol error: " + ex.Message, ex);
            }
            if (!(response is ReadyResponse))
            {
                throw new DriverInternalError("Expected ReadyResponse, obtained " + response?.GetType().Name);
            }
        }
    }
}
