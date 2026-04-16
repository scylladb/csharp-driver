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

#if NET8_0_OR_GREATER
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
#endif

namespace Cassandra.Connections
{
    /// <summary>
    /// Provides TLS session ticket caching for a Cluster instance.
    /// On .NET 8.0+, this uses <c>SslClientAuthenticationOptions</c> with
    /// <c>AllowTlsResume = true</c> to enable TLS session resumption,
    /// reducing handshake overhead for subsequent connections to the same host.
    /// The same <c>SslClientAuthenticationOptions</c> instance is reused for all
    /// connections to a given server name, which is required by .NET for session
    /// ticket reuse.
    /// On older runtimes (netstandard2.0), this class is a no-op placeholder.
    /// </summary>
    internal class TlsSessionTicketCache
    {
        private static readonly Logger Logger = new Logger(typeof(TlsSessionTicketCache));

#if NET8_0_OR_GREATER
        private readonly ConcurrentDictionary<string, SslClientAuthenticationOptions> _cache =
            new ConcurrentDictionary<string, SslClientAuthenticationOptions>();

        /// <summary>
        /// Returns a cached <see cref="SslClientAuthenticationOptions"/> for the given server name,
        /// creating one on first access. The same instance is returned for subsequent calls with
        /// the same <paramref name="serverName"/>, which is required by .NET for TLS session resumption.
        /// </summary>
        /// <param name="serverName">The target host name for TLS SNI and certificate validation.</param>
        /// <param name="sslOptions">The SSL options configured on the cluster.</param>
        /// <returns>A cached <see cref="SslClientAuthenticationOptions"/> instance.</returns>
        public SslClientAuthenticationOptions GetAuthenticationOptions(string serverName, SSLOptions sslOptions)
        {
            return _cache.GetOrAdd(serverName, key =>
            {
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = key,
                    ClientCertificates = sslOptions.CertificateCollection,
                    EnabledSslProtocols = sslOptions.SslProtocol,
                    CertificateRevocationCheckMode = sslOptions.CheckCertificateRevocation
                        ? X509RevocationMode.Online
                        : X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = sslOptions.RemoteCertValidationCallback,
                    AllowTlsResume = sslOptions.EnableSessionResumption
                };

                Logger.Verbose(
                    "Created SslClientAuthenticationOptions for host '{0}' (AllowTlsResume={1})",
                    key,
                    options.AllowTlsResume);

                return options;
            });
        }
#endif
    }
}
