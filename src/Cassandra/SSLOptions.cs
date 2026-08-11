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
        private SslProtocols _sslProtocol = SslProtocols.Tls;
        private bool _checkCertificateRevocation;
        private X509CertificateCollection _certificateCollection = new X509CertificateCollection();
        private Func<IPAddress, string> _hostNameResolver = GetHostName;

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
        /// cannot vouch for the verification, but neither can it claim the host name goes unchecked.
        /// </para>
        /// </summary>
        internal bool? VerifiesHostName =>
            _remoteCertValidationCallback == null || _remoteCertValidationCallback == SSLOptions.DefaultCertValidationCallback
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
        ///  Creates SSLOptions with default values.   
        /// </summary>
        public SSLOptions()
        {
        }

        /// <summary>
        /// Creates SSL options used for SSL connections with Casandra hosts. 
        /// </summary>
        /// <param name="sslProtocol">type of SSL protocol, default set to Tls.</param>
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
        /// Determines whether the certificate revocation list is checked during connection authentication.
        /// </summary>
        public SSLOptions SetRemoteCertValidationCallback(RemoteCertificateValidationCallback callback)
        {
            _remoteCertValidationCallback = callback;
            return this;
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