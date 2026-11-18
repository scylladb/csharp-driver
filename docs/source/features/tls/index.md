# TLS/SSL

You can secure traffic between the driver and Apache Cassandra with TLS/SSL. There are two aspects to that:

- Client-to-node encryption, where the traffic is encrypted and the client verifies the identity of the nodes it connects to.
- Optional client certificate authentication, where server nodes also verify the identity of the client.

This section describes the driver-side configuration, it assumes that you've already configured SSL encryption in the server. If you're using Apache Cassandra, you can checkout the [documentation that covers the setup with Apache Cassandra][client-to-node].

You can find SSL examples on how to configure the driver for both server and client auth in the driver's [Github repository].

## Driver configuration

Use `Builder.WithSSL()` method to enable client TLS/SSL encryption:

```csharp
var cluster = Cluster.Builder()
    .AddContactPoints(...)
    .WithSSL()
    .Build();
```

`Builder.WithSSL()` and `Builder.WithSSL(new SSLOptions())` enable TLS/SSL with the default configuration. The parameterless `SSLOptions` uses `SslProtocols.None`, which lets the operating system select the best available TLS protocol and apply its security policy. The default configuration also includes a `RemoteCertificateValidationCallback` that logs any errors returned by .NET's SSL API.

To customize this configuration check out the several SSL options available on the `SSLOptions` class:

```csharp
var cluster = Cluster.Builder()
    .AddContactPoints(...)
    .WithSSL(new SSLOptions().SetCertificateRevocationCheck(true))
    .Build();
```

The .NET SSL API will return a `RemoteCertificateNameMismatch` error when the `HostName` does not match the name on the server certificate. By default the driver performs reverse DNS resolution to obtain the `HostName` from the server node's IP address. You can change this by providing a custom hostname resolver in the `SSLOptions.SetHostNameResolver(...)` method:

```csharp
var cluster = Cluster.Builder()
    .AddContactPoints(...)
    .WithSSL(new SSLOptions().SetHostNameResolver(...))
    .Build();
```

Alternatively, you can provide your own `RemoteCertificateValidationCallback` to handle the SSL errors and perform your own validation logic (see below for an example of this). Note that you can introduce security vulnerabilities with this so you should avoid it if you can.

### Enabling server authentication with a custom root certificate

If you have a custom (untrusted) root certificate, then there are three ways to provide it: using the system/user store, loading it manually in code, or a certificate chain policy (see [Other TLS settings](#other-tls-settings)).

#### Certificate store

On Windows, you can add this certificate to the `Trusted Root Certification Authorities` logical store. This can be done with `certmgr.msc` for example, which is a MMC (Microsoft Management Console) snap-in tool to manage certificates on Windows systems. On non Windows platforms, support for system/user certificate stores is limited and the location of this store depends on which SSL library is used so you might prefer loading the certificate manually in code.

#### Loading the certificate in code

An alternative to the certificate store is to load the certificate in code and provide a custom certificate validator using `SSLOptions.SetRemoteCertValidationCallback(RemoteCertificateValidationCallback)` that compares the root certificate of the chain returned by the server with your previously loaded root certificate.

Here is an example (`CustomValidator` here is a class that you would have to implement):

```csharp
// custom validator
var rootCertificate = X509CertificateLoader.LoadCertificateFromFile(@"C:\path\to\ca.crt");
var certificateValidator = new CustomValidator(rootCertificate);

var cluster = Cluster.Builder()
    .AddContactPoints("...")
    .WithSSL(new SSLOptions().SetRemoteCertValidationCallback(
        (sender, certificate, chain, errors) => certificateValidator.Validate(sender, certificate, chain, errors)))
    .Build();
```

The examples on the [driver's repository][Github repository] have a basic custom certificate validator which can be used as a starting point.

### Enabling client authentication

To enable client authentication, you need to provide the driver with the client certificate(s):

```csharp
var clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    @"C:\path\to\client_cert.pfx",
    "cert_password");

// For a PKCS#12 file without a password, pass null instead.
var cluster = Cluster.Builder()
    .AddContactPoints("...")
    .WithSSL(new SSLOptions()
        // set client certificate collection
        .SetCertificateCollection(new X509Certificate2Collection
        {
            clientCertificate
        }))
    .Build();
```

If you prefer to use the certificate store you can use `X509Store` to obtain a collection of all installed certificates (optionally you can filter this collection before providing it to the driver so that it only contains the relevant certificate). On Windows, the following example works if you add the client certificate to the `Personal` logical store (using `certmgr.msc` for example as [described previously][certstore]).

```csharp
X509Certificate2Collection collection;
using (var store = new X509Store(StoreLocation.LocalMachine))
{
    store.Open(OpenFlags.ReadOnly);
    collection = store.Certificates;
}

var cluster = Cluster.Builder()
    .AddContactPoints("...")
    .WithSSL(new SSLOptions().SetCertificateCollection(collection))
    .Build();
```

### Other TLS settings

For TLS settings that the other `SSLOptions` methods don't cover, such as a certificate chain policy, cipher suites or application protocols (ALPN), set a callback with `SSLOptions.SetClientAuthenticationOptionsCallback(...)`. It receives the `SslClientAuthenticationOptions` of each connection, which already hold the server name and the settings of the `SSLOptions`, and can change any of them. For example, to trust only the certificates signed by your own root certificate:

```csharp
var root = X509CertificateLoader.LoadCertificateFromFile("ca.crt");

var cluster = Cluster.Builder()
    .AddContactPoints("...")
    .WithSSL(new SSLOptions().SetClientAuthenticationOptionsCallback(options =>
    {
        options.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            CustomTrustStore = { root },
            // Replaces SetCertificateRevocationCheck. Use X509RevocationMode.Online if your CA publishes revocation lists.
            RevocationMode = X509RevocationMode.NoCheck,
        };
    }))
    .Build();
```

A certificate chain policy replaces the one .NET builds from the `SSLOptions`, so its revocation mode applies instead of `SetCertificateRevocationCheck(...)`. A new `X509ChainPolicy` checks revocation online, which fails for certificates without a revocation endpoint, as is common with a private CA: set `RevocationMode` explicitly.

The driver calls the callback for every connection it opens, possibly concurrently, with new options each time. If the callback throws, the connection fails. The callback must make its changes before it returns, so don't pass an async lambda: the changes it makes once it yields come after the handshake started, and an exception it throws terminates the process. The options hold a new collection of client certificates each time, but the certificates in it are shared: the callback can add or remove certificates, but must not modify or dispose them. After `SetCertificateCollection(null)`, there is no collection, and the callback must create one to add certificates.

[certstore]: #certificate-store
[client-to-node-dse]: https://docs.datastax.com/en/security/6.7/security/encryptClientNodeSSL.html
[client-to-node]: https://docs.datastax.com/en/cassandra/3.0/cassandra/configuration/secureSSLClientToNode.html
[Github repository]: https://github.com/scylladb/csharp-driver/tree/master/examples/Ssl
