using System.Collections.Generic;

namespace Cassandra.Connections
{
    internal sealed class TabletInfo
    {
        private const string SCYLLA_TABLETS_STARTUP_OPTION_KEY = "TABLETS_ROUTING_V1";
        /// <summary>The experimental V2 protocol extension advertised by ScyllaDB.</summary>
        public const string TABLETS_ROUTING_V2_OPTION = "TABLETS_ROUTING_V2_EXPERIMENTAL";
        /// <summary>The response payload key carrying versioned tablet routing feedback.</summary>
        public const string TABLETS_ROUTING_V2_CUSTOM_PAYLOAD_KEY = "tablets-routing-v2";
        private const string SCYLLA_TABLETS_STARTUP_OPTION_VALUE = "";
        public const string TABLETS_ROUTING_V1_CUSTOM_PAYLOAD_KEY = "tablets-routing-v1";

        private bool enabled;

        /// <summary>Gets whether this connection selected V2 instead of V1 tablet routing.</summary>
        public bool IsV2 { get; }

        private TabletInfo(bool enabled, bool isV2 = false)
        {
            this.enabled = enabled;
            IsV2 = isV2;
        }

        public bool IsEnabled()
        {
            return enabled;
        }

        public static TabletInfo ParseTabletInfo(IDictionary<string, string[]> supported)
        {
            if (supported.ContainsKey(TABLETS_ROUTING_V2_OPTION))
            {
                return new TabletInfo(true, true);
            }

            if (supported.TryGetValue(SCYLLA_TABLETS_STARTUP_OPTION_KEY, out var values))
            {
                return new TabletInfo(
                    values != null &&
                    values.Length == 1 &&
                    values[0] == SCYLLA_TABLETS_STARTUP_OPTION_VALUE
                );
            }

            return new TabletInfo(false);
        }
    }
}
