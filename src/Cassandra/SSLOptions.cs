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
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Cassandra
{
    /// <summary>
    /// Defines the SSL/TLS options to connect to a ssl enabled Cassandra host
    /// </summary>
    public class SSLOptions
    {
        private readonly static Logger _logger = new Logger(typeof(SSLOptions));

        /// <summary>
        /// The callback installed unless the application provides its own. Held in a field so that
        /// <see cref="VerifiesHostName"/> can recognize it without depending on delegate-to-method-group
        /// comparison semantics.
        /// </summary>
        private readonly static RemoteCertificateValidationCallback DefaultCertValidationCallback = ValidateServerCertificate;

        private RemoteCertificateValidationCallback _remoteCertValidationCallback = SSLOptions.DefaultCertValidationCallback;
        private SslProtocols _sslProtocol = SslProtocols.None;
        private bool _checkCertificateRevocation;
        private X509CertificateCollection _certificateCollection = new X509CertificateCollection();
        private Func<IPAddress, string> _hostNameResolver = GetHostName;
        private Action<SslClientAuthenticationOptions> _clientAuthenticationOptionsCallback;

        /// <summary>
        /// Verifies Cassandra host SSL certificate used for authentication.
        /// </summary>
        public RemoteCertificateValidationCallback RemoteCertValidationCallback
        {
            get { return _remoteCertValidationCallback; }
        }

        /// <summary>
        /// Whether the server host name is verified against its certificate, or <c>null</c> when that cannot be
        /// determined.
        /// <para>
        /// The driver hands the server name to <see cref="System.Net.Security.SslStream"/>, which reports a name
        /// mismatch as <see cref="SslPolicyErrors.RemoteCertificateNameMismatch"/>. Both the callback the driver
        /// installs by default and .NET's own validation (used when the callback is <c>null</c>) reject that, so
        /// the host name is verified in both cases. An application supplied callback decides for itself and what
        /// it decides is not introspectable, so the answer is <c>null</c> rather than <c>false</c>: the driver
        /// cannot vouch for the verification, but neither can it claim the host name goes unchecked. The same
        /// applies to a <see cref="ClientAuthenticationOptionsCallback"/>, which can replace the validation callback
        /// or the target host.
        /// </para>
        /// </summary>
        internal bool? VerifiesHostName =>
            _clientAuthenticationOptionsCallback == null
            && (_remoteCertValidationCallback == null || _remoteCertValidationCallback == SSLOptions.DefaultCertValidationCallback)
                ? true
                : (bool?)null;

        /// <summary>
        /// Ssl Protocol used for communication with Cassandra hosts.
        /// </summary>
        public SslProtocols SslProtocol
        {
            get { return _sslProtocol; }
        }

        /// <summary>
        /// Determines whether the certificate revocation list is checked during connection authentication.
        /// </summary>
        public bool CheckCertificateRevocation
        {
            get { return _checkCertificateRevocation; }
        }

        /// <summary>
        /// Gets the method to be use to determine the host name from the IP address
        /// </summary>
        public Func<IPAddress, string> HostNameResolver
        {
            get { return _hostNameResolver; }
        }

        /// <summary>
        /// Gets the collection that contains the client certificates
        /// </summary>
        public X509CertificateCollection CertificateCollection
        {
            get { return _certificateCollection; }
        }

        /// <summary>
        /// Gets the callback that adjusts the options of each TLS handshake, or <c>null</c> when none is set.
        /// </summary>
        /// <seealso cref="SetClientAuthenticationOptionsCallback"/>
        public Action<SslClientAuthenticationOptions> ClientAuthenticationOptionsCallback
        {
            get { return _clientAuthenticationOptionsCallback; }
        }

        /// <summary>
        /// Creates SSL options with default values, delegating protocol selection to the operating system.
        /// </summary>
        public SSLOptions()
        {
        }

        /// <summary>
        /// Creates SSL options used for SSL connections with Casandra hosts. 
        /// </summary>
        /// <param name="sslProtocol">The SSL protocol to use.</param>
        /// <param name="checkCertificateRevocation">specifies whether the certificate revocation list is checked during connection authentication.</param>
        /// <param name="remoteCertValidationCallback">verifies Cassandra host SSL certificate used for authentication.
        ///     <remarks>
        ///         Default RemoteCertificateValidationCallback won't establish a connection if any error will occur.         
        ///     </remarks> 
        ///     </param>
        public SSLOptions(SslProtocols sslProtocol, bool checkCertificateRevocation, RemoteCertificateValidationCallback remoteCertValidationCallback)
        {
            _sslProtocol = sslProtocol;
            _checkCertificateRevocation = checkCertificateRevocation;
            _remoteCertValidationCallback = remoteCertValidationCallback;
        }

        /// <summary>
        /// Sets the collection that contains the client certificates
        /// </summary>
        public SSLOptions SetCertificateCollection(X509CertificateCollection certificates)
        {
            _certificateCollection = certificates;
            return this;
        }

        /// <summary>
        /// Sets the method to be use to determine the host name from the host IP address
        /// </summary>
        public SSLOptions SetHostNameResolver(Func<IPAddress, string> resolver)
        {
            _hostNameResolver = resolver;
            return this;
        }

        /// <summary>
        /// Determines whether the certificate revocation list is checked during connection authentication.
        /// </summary>
        public SSLOptions SetCertificateRevocationCheck(bool flag)
        {
            _checkCertificateRevocation = flag;
            return this;
        }

        /// <summary>
        /// Sets the callback that validates the certificate of each node during connection authentication. With
        /// <c>null</c>, .NET's own validation applies, which rejects a certificate with any error. By default, the driver
        /// installs a callback that also rejects any error, and logs it.
        /// </summary>
        public SSLOptions SetRemoteCertValidationCallback(RemoteCertificateValidationCallback callback)
        {
            _remoteCertValidationCallback = callback;
            return this;
        }

        /// <summary>
        /// Sets a callback that adjusts the options of each TLS handshake, for settings that the other
        /// methods of these SSL options don't cover, such as
        /// <see cref="SslClientAuthenticationOptions.CipherSuitesPolicy"/>,
        /// <see cref="SslClientAuthenticationOptions.ApplicationProtocols"/>,
        /// <see cref="SslClientAuthenticationOptions.CertificateChainPolicy"/> or
        /// <see cref="SslClientAuthenticationOptions.ClientCertificateContext"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The driver calls the callback for each connection, with new options that already hold the server name
        /// (<see cref="SslClientAuthenticationOptions.TargetHost"/>), and the client certificates, protocols,
        /// revocation check and certificate validation callback of these SSL options. Changes the callback makes
        /// apply to that connection only, and override those values.
        /// </para>
        /// <para>
        /// A certificate chain policy replaces the chain policy that .NET builds from these options, so its
        /// <see cref="X509ChainPolicy.RevocationMode"/> applies instead of the revocation check of these SSL options.
        /// A new <see cref="X509ChainPolicy"/> checks revocation online, which fails for a certificate without a
        /// revocation endpoint.
        /// </para>
        /// <para>
        /// The client certificates are a new collection for each connection, so the callback can add or remove
        /// certificates, but the certificates in it are shared: the callback must not modify or dispose them. After
        /// <see cref="SetCertificateCollection"/> with <c>null</c>, the collection is <c>null</c>, and the callback
        /// must create one to add certificates.
        /// </para>
        /// <para>
        /// The callback must make its changes before it returns, so don't pass an async lambda. It compiles to an
        /// <c>async void</c> method: the changes it makes once it yields come after the handshake started, and an
        /// exception it throws, even before it yields, is raised on the thread pool and terminates the process.
        /// </para>
        /// <para>
        /// The callback can run concurrently for different connections. An exception it throws fails the connection.
        /// </para>
        /// </remarks>
        /// <param name="callback">The callback, or <c>null</c> to remove it.</param>
        /// <returns>This instance.</returns>
        public SSLOptions SetClientAuthenticationOptionsCallback(Action<SslClientAuthenticationOptions> callback)
        {
            _clientAuthenticationOptionsCallback = callback;
            return this;
        }

        /// <summary>
        /// Creates the options of one TLS handshake, with the server name <paramref name="targetHost"/>.
        /// </summary>
        internal SslClientAuthenticationOptions CreateClientAuthenticationOptions(string targetHost)
        {
            // The same options that SslStream.AuthenticateAsClientAsync(string, X509CertificateCollection,
            // SslProtocols, bool) builds, with the validation callback that SslStream's constructor used to take.
            // The collection is copied, so that a callback that adds or removes certificates only changes this
            // connection's. The certificates themselves are shared.
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ClientCertificates = _certificateCollection == null ? null : new X509CertificateCollection(_certificateCollection),
                EnabledSslProtocols = _sslProtocol,
                CertificateRevocationCheckMode = _checkCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = _remoteCertValidationCallback,
            };
            _clientAuthenticationOptionsCallback?.Invoke(options);
            return options;
        }

        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            _logger.Error(string.Format("Cassandra node SSL certificate validation error(s): {0}", sslPolicyErrors));

            // Do not allow this client to communicate with unauthenticated Cassandra hosts.
            return false;
        }

        private static string GetHostName(IPAddress address)
        {
            return Utils.GetPrimaryHostNameInfo(address.ToString());
        }
    }
}
