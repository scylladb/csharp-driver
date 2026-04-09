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

using System.Security.Authentication;
using Cassandra.Connections;
using NUnit.Framework;

namespace Cassandra.Tests.Connections
{
    [TestFixture]
    public class TlsSessionTicketCacheTests
    {
#if NET8_0_OR_GREATER
        [Test]
        public void GetAuthenticationOptions_Should_SetAllowTlsResume_True_By_Default()
        {
            var cache = new TlsSessionTicketCache();
            var sslOptions = new SSLOptions();
            var authOpts = cache.GetAuthenticationOptions("myhost.example.com", sslOptions);

            Assert.That(authOpts.AllowTlsResume, Is.True);
            Assert.That(authOpts.TargetHost, Is.EqualTo("myhost.example.com"));
            Assert.That(authOpts.EnabledSslProtocols, Is.EqualTo(SslProtocols.Tls));
            Assert.That(authOpts.CertificateRevocationCheckMode,
                Is.EqualTo(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck));
        }

        [Test]
        public void GetAuthenticationOptions_Should_SetAllowTlsResume_False_When_Disabled()
        {
            var cache = new TlsSessionTicketCache();
            var sslOptions = new SSLOptions().SetEnableSessionResumption(false);
            var authOpts = cache.GetAuthenticationOptions("myhost.example.com", sslOptions);

            Assert.That(authOpts.AllowTlsResume, Is.False);
        }

        [Test]
        public void GetAuthenticationOptions_Should_Set_RevocationMode_Online_When_CheckRevocation_True()
        {
            var cache = new TlsSessionTicketCache();
            var sslOptions = new SSLOptions().SetCertificateRevocationCheck(true);
            var authOpts = cache.GetAuthenticationOptions("myhost.example.com", sslOptions);

            Assert.That(authOpts.CertificateRevocationCheckMode,
                Is.EqualTo(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online));
        }

        [Test]
        public void GetAuthenticationOptions_Should_PreserveRemoteCertValidationCallback()
        {
            var cache = new TlsSessionTicketCache();
            System.Net.Security.RemoteCertificateValidationCallback customCallback =
                (sender, cert, chain, errors) => true;
            var sslOptions = new SSLOptions().SetRemoteCertValidationCallback(customCallback);
            var authOpts = cache.GetAuthenticationOptions("myhost.example.com", sslOptions);

            Assert.That(authOpts.RemoteCertificateValidationCallback, Is.SameAs(customCallback));
        }

        [Test]
        public void GetAuthenticationOptions_Should_Return_Same_Instance_For_Same_Host()
        {
            var cache = new TlsSessionTicketCache();
            var sslOptions = new SSLOptions();
            var opts1 = cache.GetAuthenticationOptions("host1.example.com", sslOptions);
            var opts2 = cache.GetAuthenticationOptions("host1.example.com", sslOptions);

            Assert.That(opts1, Is.SameAs(opts2));
        }

        [Test]
        public void GetAuthenticationOptions_Should_Return_Different_Instances_For_Different_Hosts()
        {
            var cache = new TlsSessionTicketCache();
            var sslOptions = new SSLOptions();
            var opts1 = cache.GetAuthenticationOptions("host1.example.com", sslOptions);
            var opts2 = cache.GetAuthenticationOptions("host2.example.com", sslOptions);

            Assert.That(opts1, Is.Not.SameAs(opts2));
        }
#endif
    }
}
