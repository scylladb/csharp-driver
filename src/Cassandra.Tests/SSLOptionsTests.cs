//
//      Copyright (C) ScyllaDB
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Cassandra.Tests
{
    [TestFixture]
    public class SSLOptionsTests
    {
        [Test]
        public void Should_DelegateProtocolSelectionToTheOperatingSystemByDefault()
        {
            Assert.AreEqual(SslProtocols.None, new SSLOptions().SslProtocol);
        }

        [Test]
        public void Should_HaveNoClientAuthenticationOptionsCallbackByDefault()
        {
            Assert.IsNull(new SSLOptions().ClientAuthenticationOptionsCallback);
        }

        [Test]
        public void SetClientAuthenticationOptionsCallback_Should_SetAndRemoveTheCallback()
        {
            Action<SslClientAuthenticationOptions> callback = _ => { };
            var sslOptions = new SSLOptions();

            Assert.AreSame(sslOptions, sslOptions.SetClientAuthenticationOptionsCallback(callback));
            Assert.AreSame(callback, sslOptions.ClientAuthenticationOptionsCallback);

            sslOptions.SetClientAuthenticationOptionsCallback(null);
            Assert.IsNull(sslOptions.ClientAuthenticationOptionsCallback);
        }

        [Test]
        [TestCase(true, X509RevocationMode.Online)]
        [TestCase(false, X509RevocationMode.NoCheck)]
        public void CreateClientAuthenticationOptions_Should_UseTheSslOptions(
            bool checkCertificateRevocation, X509RevocationMode expectedRevocationMode)
        {
            using var certificate = CreateCertificate();
            RemoteCertificateValidationCallback validation = (sender, cert, chain, errors) => true;
            var certificates = new X509CertificateCollection { certificate };
            var sslOptions = new SSLOptions(SslProtocols.Tls13, checkCertificateRevocation, validation)
                .SetCertificateCollection(certificates);

            var options = sslOptions.CreateClientAuthenticationOptions("node1.example.com");

            Assert.AreEqual("node1.example.com", options.TargetHost);
            Assert.AreEqual(SslProtocols.Tls13, options.EnabledSslProtocols);
            Assert.AreEqual(expectedRevocationMode, options.CertificateRevocationCheckMode);
            Assert.AreSame(validation, options.RemoteCertificateValidationCallback);
            Assert.AreEqual(1, options.ClientCertificates.Count);
            Assert.AreSame(certificate, options.ClientCertificates[0]);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_UseTheDefaultValidationCallback()
        {
            var sslOptions = new SSLOptions();

            var options = sslOptions.CreateClientAuthenticationOptions("node1");

            Assert.IsNotNull(options.RemoteCertificateValidationCallback);
            Assert.AreSame(sslOptions.RemoteCertValidationCallback, options.RemoteCertificateValidationCallback);
            Assert.AreEqual(SslProtocols.None, options.EnabledSslProtocols);
            Assert.AreEqual(X509RevocationMode.NoCheck, options.CertificateRevocationCheckMode);
            Assert.AreEqual(0, options.ClientCertificates.Count);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_KeepNullCertificates()
        {
            var options = new SSLOptions().SetCertificateCollection(null).CreateClientAuthenticationOptions("node1");

            Assert.IsNull(options.ClientCertificates);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_LetTheCallbackOverrideTheOptions()
        {
            SslClientAuthenticationOptions received = null;
            string receivedTargetHost = null;
            RemoteCertificateValidationCallback validation = (sender, cert, chain, errors) => true;
            var sslOptions = new SSLOptions().SetClientAuthenticationOptionsCallback(options =>
            {
                received = options;
                receivedTargetHost = options.TargetHost;
                options.TargetHost = "override.example.com";
                options.EnabledSslProtocols = SslProtocols.Tls13;
                options.RemoteCertificateValidationCallback = validation;
                options.ApplicationProtocols = new() { SslApplicationProtocol.Http11 };
            });

            var result = sslOptions.CreateClientAuthenticationOptions("node1");

            Assert.AreSame(received, result);
            Assert.AreEqual("node1", receivedTargetHost);
            Assert.AreEqual("override.example.com", result.TargetHost);
            Assert.AreEqual(SslProtocols.Tls13, result.EnabledSslProtocols);
            Assert.AreSame(validation, result.RemoteCertificateValidationCallback);
            Assert.AreEqual(SslApplicationProtocol.Http11, result.ApplicationProtocols[0]);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_CreateNewOptionsForEachConnection()
        {
            var calls = 0;
            var sslOptions = new SSLOptions().SetClientAuthenticationOptionsCallback(options =>
            {
                calls++;
                options.TargetHost = "changed";
            });

            var first = sslOptions.CreateClientAuthenticationOptions("node1");
            var second = sslOptions.CreateClientAuthenticationOptions("node2");

            Assert.AreEqual(2, calls);
            Assert.AreNotSame(first, second);
            Assert.AreNotSame(first.ClientCertificates, second.ClientCertificates);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_NotShareTheCertificateCollectionWithTheCallback()
        {
            using var certificate = CreateCertificate();
            var certificates = new X509CertificateCollection();
            var sslOptions = new SSLOptions()
                .SetCertificateCollection(certificates)
                .SetClientAuthenticationOptionsCallback(options => options.ClientCertificates.Add(certificate));

            var options = sslOptions.CreateClientAuthenticationOptions("node1");

            Assert.AreEqual(1, options.ClientCertificates.Count);
            Assert.AreEqual(0, certificates.Count);

            // The next connection starts from the configured certificates again, not from the previous one's.
            Assert.AreEqual(1, sslOptions.CreateClientAuthenticationOptions("node1").ClientCertificates.Count);
        }

        [Test]
        public void CreateClientAuthenticationOptions_Should_ThrowTheCallbackException()
        {
            var error = new InvalidOperationException("Callback failure");
            var sslOptions = new SSLOptions().SetClientAuthenticationOptionsCallback(_ => throw error);

            var ex = Assert.Throws<InvalidOperationException>(() => sslOptions.CreateClientAuthenticationOptions("node1"));
            Assert.AreSame(error, ex);
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }
    }
}
