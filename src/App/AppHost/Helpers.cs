using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AppHost;

public static class Helpers
{
    /// <summary>
    /// Generate local TLS certs for NATS websockets
    /// </summary>
    /// <param name="certificatePath"></param>
    /// <param name="keyPath"></param>
    internal static IResourceBuilder<T> WithAuthAuthority<T>(
        this IResourceBuilder<T> resource,
        string name,
        bool singleUserMode,
        AuthentikResources authentik)
        where T : IResourceWithEnvironment
    {
        if (singleUserMode)
            return resource.WithEnvironment(name, "");

        if (!string.IsNullOrWhiteSpace(authentik.ConfiguredAuthority))
            return resource.WithEnvironment(name, authentik.ConfiguredAuthority);

        return authentik.Authority is null
            ? resource.WithEnvironment(name, "")
            : resource.WithEnvironment(name, authentik.Authority);
    }

    internal static void EnsureNatsWebSocketTlsCertificates(string certificatePath, string keyPath)
    {
        if (File.Exists(certificatePath) && File.Exists(keyPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(certificatePath)!);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var subjectAltNames = new SubjectAlternativeNameBuilder();
        subjectAltNames.AddDnsName("localhost");
        subjectAltNames.AddDnsName("nats");
        subjectAltNames.AddIpAddress(IPAddress.Loopback);

        request.CertificateExtensions.Add(subjectAltNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));

        var serverAuthOid = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuthOid, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static string GetEnv(string variable)
    {
        return Environment.GetEnvironmentVariable(variable) ?? "VALUE_NOT_SET";
    }
}