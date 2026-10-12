using System;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TwinCheck.Agent.Gui.Desktop;

public sealed record LocalhostCertificateResult(X509Certificate2 Certificate, bool Trusted);

/// <summary>
/// The per-user HTTPS certificate for https://localhost:3625. The certificate and its key
/// live in CurrentUser\My; its public part goes in CurrentUser\Root so Chrome and Edge trust it.
/// Adding to CurrentUser\Root makes Windows show a one-time confirmation prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LocalhostCertificate
{
    public const string FriendlyName = "TwinCheck Scan Agent Localhost";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public static LocalhostCertificateResult GetOrCreate()
    {
        using var personal = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        personal.Open(OpenFlags.ReadWrite);

        var ours = personal.Certificates.Where(certificate => certificate.FriendlyName == FriendlyName).ToArray();
        var certificate = ours
            .Where(candidate => candidate.HasPrivateKey && candidate.NotAfter - DateTime.Now > RenewBefore)
            .OrderByDescending(candidate => candidate.NotAfter)
            .FirstOrDefault();

        if (certificate is null)
        {
            foreach (var stale in ours) personal.Remove(stale);
            certificate = Create();
            personal.Add(certificate);
        }

        return new LocalhostCertificateResult(certificate, EnsureTrusted(certificate));
    }

    /// <summary>Returns false when the user declined the Windows trust prompt.</summary>
    public static bool EnsureTrusted(X509Certificate2 certificate)
    {
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        if (root.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
        {
            return true;
        }

        try
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            root.Add(publicOnly);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Used by the uninstall hook, which may not show UI. Removing a CurrentUser\Root entry
    /// prompts the user, so only the key-bearing copy in CurrentUser\My is removed.
    /// </summary>
    public static void RemoveFromPersonalStore()
    {
        using var personal = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        personal.Open(OpenFlags.ReadWrite);
        foreach (var certificate in personal.Certificates.Where(certificate => certificate.FriendlyName == FriendlyName).ToArray())
        {
            personal.Remove(certificate);
        }
    }

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], critical: false));

        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        // Round-trip through PFX so Windows persists the private key with the store entry.
        var persisted = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        persisted.FriendlyName = FriendlyName;
        return persisted;
    }
}
