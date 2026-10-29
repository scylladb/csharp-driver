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
using System.Collections.Generic;

namespace Cassandra
{
    /// <summary>
    /// Specifies the different protocol versions and provides methods (via extension methods) to check whether a
    /// feature is supported in an specific version
    /// </summary>
    public enum ProtocolVersion : byte
    {
        /// <summary>
        /// Cassandra protocol v3, supported in Apache Cassandra 2.1+ and ScyllaDB.
        /// The lowest protocol version supported by the driver: protocol versions 1 and 2 were removed in 4.0.
        /// </summary>
        V3 = 0x03,

        /// <summary>
        /// Cassandra protocol v4, supported in Apache Cassandra 2.2+ and ScyllaDB.
        /// </summary>
        V4 = 0x04,

        /// <summary>
        /// Cassandra protocol v5, in beta from Apache Cassandra 3.x+. Currently not supported by the driver.
        /// </summary>
        V5 = 0x05,

        /// <summary>
        /// The higher protocol version that is supported by this driver.
        /// <para>When acquiring the first connection, it will use this version to start protocol negotiation.</para>
        /// </summary>
        MaxSupported = V4,
        /// <summary>
        /// The lower protocol version that is supported by this driver.
        /// </summary>
        MinSupported = V3
    }

    internal static class ProtocolVersionExtensions
    {
        private static readonly Logger Logger = new Logger(typeof(ProtocolVersion));
        private static readonly Version Version60 = new Version(6, 0);
        private static readonly Version Version40 = new Version(4, 0);
        private static readonly Version Version22 = new Version(2, 2);
        private static readonly Version Version21 = new Version(2, 1);

        /// <summary>
        /// Determines if the protocol version is supported by this driver.
        /// </summary>
        public static bool IsSupported(this ProtocolVersion version, Configuration config)
        {
            if (version.IsBeta())
            {
                return config.AllowBetaProtocolVersions;
            }

            switch (version)
            {
                case ProtocolVersion.V3:
                case ProtocolVersion.V4:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Throws when a version chosen as the maximum is lower than <see cref="ProtocolVersion.MinSupported"/>,
        /// which includes protocol versions 1 and 2, no longer implemented by the driver.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException" />
        public static ProtocolVersion EnsureNotBelowMinSupported(this ProtocolVersion version, string paramName)
        {
            if (version < ProtocolVersion.MinSupported)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    version,
                    $"Protocol version {version:D} is not supported, the driver requires protocol version " +
                    $"{ProtocolVersion.MinSupported:D} or above (Apache Cassandra 2.1+ or any ScyllaDB version).");
            }

            return version;
        }

        /// <summary>
        /// Gets the first version number that is supported, lower than the one provided.
        /// Returns zero when there isn't a lower supported version.
        /// </summary>
        /// <returns></returns>
        public static ProtocolVersion GetLowerSupported(this ProtocolVersion version, Configuration config)
        {
            var lowerVersion = version;
            do
            {
                if (lowerVersion > ProtocolVersion.V5)
                {
                    lowerVersion = ProtocolVersion.V5;
                }
                else if (lowerVersion <= ProtocolVersion.MinSupported)
                {
                    lowerVersion = 0;
                }
                else
                {
                    lowerVersion = lowerVersion - 1;
                }
            } while (lowerVersion > 0 && !lowerVersion.IsSupported(config));

            return lowerVersion;
        }

        /// <summary>
        /// Gets the highest supported protocol version collectively by the given hosts.
        /// </summary>
        public static ProtocolVersion GetHighestCommon(this ProtocolVersion version, Configuration config, IEnumerable<Host> hosts)
        {
            var maxVersion = (byte)version;

            foreach (var host in hosts)
            {
                var cassandraVersion = host.CassandraVersion;

                if (cassandraVersion >= Version40)
                {
                    // Anything 4.0.0+ has a max protocol version of V5.
                    maxVersion = config.AllowBetaProtocolVersions
                        ? Math.Min((byte)ProtocolVersion.V5, maxVersion)
                        : Math.Min((byte)ProtocolVersion.V4, maxVersion);
                }
                else if (cassandraVersion >= Version22)
                {
                    // Cassandra 2.2.x and 3.x have a max protocol version of V4.
                    maxVersion = Math.Min((byte)ProtocolVersion.V4, maxVersion);
                }
                else if (cassandraVersion >= Version21)
                {
                    // Cassandra 2.1.x has a max protocol version of V3.
                    maxVersion = Math.Min((byte)ProtocolVersion.V3, maxVersion);
                }
                else if (cassandraVersion != null)
                {
                    // Anything older only supports protocol versions that the driver no longer implements.
                    Logger.Error($"Detected host {host.Address} with Cassandra version {cassandraVersion}, which " +
                                 $"requires a protocol version lower than {ProtocolVersion.MinSupported:D}. " +
                                 "Will not be able to connect to this host");
                }

                // A host with an unknown Cassandra version doesn't lower the version either
            }

            return (ProtocolVersion)maxVersion;
        }

        /// <summary>
        /// Determines whether the protocol supports partition key indexes in the `prepared` RESULT responses.
        /// </summary>
        /// <param name="version"></param>
        /// <returns></returns>
        public static bool SupportsPreparedPartitionKey(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V4;
        }

        /// <summary>
        /// Determines whether the protocol supports unset parameters.
        /// </summary>
        /// <param name="version"></param>
        /// <returns></returns>
        public static bool SupportsUnset(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V4;
        }

        /// <summary>
        /// Determines if the protocol supports result_metadata_id on PREPARED response and EXECUTE request.
        /// </summary>
        /// <remarks>
        /// Not the right question to ask at a frame encoding or decoding site: the
        /// <c>SCYLLA_USE_METADATA_ID</c> extension backports the field to CQL v4, so its presence is a
        /// property of the connection rather than of the version. Use
        /// <see cref="Connections.IConnection.UseMetadataId"/>, which combines the two.
        /// </remarks>
        public static bool SupportsResultMetadataId(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V5;
        }

        /// <summary>
        /// Determines if the protocol supports to send the Keyspace as part of the PREPARE, QUERY and BATCH.
        /// </summary>
        public static bool SupportsKeyspaceInRequest(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V5;
        }

        /// <summary>
        /// Determines if the protocol supports sending driver information in the STARTUP request.
        /// </summary>
        public static bool SupportsDriverInfoInStartup(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V5;
        }

        /// <summary>
        /// Determines if the protocol provides a map of reasons as part of read_failure and write_failure.
        /// </summary>
        public static bool SupportsFailureReasons(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V5;
        }

        /// <summary>
        /// Determines whether the QUERY, EXECUTE and BATCH flags are encoded using 32 bits.
        /// </summary>
        public static bool Uses4BytesQueryFlags(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V5;
        }

        /// <summary>
        /// Startup responses using protocol v4+ can be a SERVER_ERROR wrapping a ProtocolException,
        /// this method returns true when is possible to receive such error.
        /// </summary>
        public static bool CanStartupResponseErrorBeWrapped(this ProtocolVersion version)
        {
            return version >= ProtocolVersion.V4;
        }

        public static bool IsBeta(this ProtocolVersion version)
        {
            return version == ProtocolVersion.V5;
        }
    }
}
