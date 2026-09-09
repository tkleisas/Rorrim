using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Rorrim.Server.Auth;

/// <summary>
/// Creates a persistent self-signed CA (in the given directory) and issues per-client certificates
/// for mTLS on the gRPC channel. The server certificate is derived from the same CA so a client that
/// trusts the CA can authenticate the server.
/// </summary>
public sealed class CertificateAuthority : ICertificateAuthority
{
    private const string CaFileName = "rorrim-ca.pfx";
    private const string CaPassword = "rorrim-ca";
    public const string PfxPassword = CaPassword;
    private const string ServerFileName = "rorrim-server.pfx";
    private const string CaPemFileName = "rorrim-ca.pem";
    private const string ClientPfxDir = "clients";

    private readonly string _storePath;
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _server;

    public X509Certificate2 CaCertificate => _ca;
    public X509Certificate2 ServerCertificate => _server;

    /// <summary>Path of the CA public certificate (PEM) that clients pin for server validation.</summary>
    public string CaPemPath => Path.Combine(_storePath, CaPemFileName);

    /// <summary>Path of the PFX (private key) issued for a client id.</summary>
    public string ClientPfxPath(string clientId) =>
        Path.Combine(_storePath, ClientPfxDir, Sanitize(clientId) + ".pfx");

    public string HostName { get; }

    public CertificateAuthority(string storePath, string hostName)
    {
        _storePath = storePath;
        HostName = hostName;
        Directory.CreateDirectory(storePath);
        _ca = LoadOrCreateCa();
        _server = LoadOrCreateServerCertificate();
        TryExportCaPem();
    }

    /// <summary>
    /// Writes the CA's public certificate as PEM so client machines can pin it (the CA is not in
    /// any machine trust store).
    /// </summary>
    private void TryExportCaPem()
    {
        try
        {
            File.WriteAllText(CaPemPath, _ca.ExportCertificatePem());
        }
        catch
        {
            // Best-effort; the PEM is a convenience for provisioning.
        }
    }

    /// <summary>
    /// Validates a client certificate for mTLS: it must chain to this broker's CA. The machine trust
    /// store does not contain the CA, so the chain is built with <see cref="X509ChainTrustMode.CustomRootTrust"/>.
    /// </summary>
    public bool IsValidClientCertificate(X509Certificate2? certificate)
    {
        if (certificate is null)
            return false;
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        }
        catch
        {
            return false;
        }
    }

    public IssuedClientCertificate IssueClientCertificate(string clientId)
    {
        string safeId = Sanitize(clientId);
        string dir = Path.Combine(_storePath, ClientPfxDir);
        Directory.CreateDirectory(dir);
        string pfxPath = ClientPfxPath(clientId);
        string pemPath = Path.Combine(dir, safeId + ".pem");

        if (File.Exists(pfxPath))
        {
            using var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, CaPassword);
            return new IssuedClientCertificate(existing, clientId, File.ReadAllText(pemPath));
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=rorrim-client-{safeId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.2") // clientAuth
        }, critical: false));

        var serial = new byte[8];
        using var rnd = RandomNumberGenerator.Create();
        rnd.GetBytes(serial);
        var cert = req.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1), serial);
        // Re-wrap with the private key for persistence.
        var withKey = cert.CopyWithPrivateKey(rsa);

        File.WriteAllBytes(pfxPath, withKey.Export(X509ContentType.Pkcs12, CaPassword));
        File.WriteAllText(pemPath, withKey.ExportCertificatePem());

        return new IssuedClientCertificate(withKey, clientId, withKey.ExportCertificatePem());
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        string caPath = Path.Combine(_storePath, CaFileName);
        if (File.Exists(caPath))
        {
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(caPath, CaPassword);
            // Reconstruct with private key ownership (on Windows this may require the key restored).
            return loaded;
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=rorrim-ca-{HostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(caPath, ca.Export(X509ContentType.Pkcs12, CaPassword));
        return ca;
    }

    /// <summary>
    /// Issues (once) and persists a TLS server certificate signed by the CA, with serverAuth EKU and
    /// SANs for the host name and localhost. Clients that trust the CA can validate the server.
    /// </summary>
    private X509Certificate2 LoadOrCreateServerCertificate()
    {
        string path = Path.Combine(_storePath, ServerFileName);
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, CaPassword);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={HostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1") // serverAuth
        }, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(HostName);
        if (!string.Equals(HostName, "localhost", StringComparison.OrdinalIgnoreCase))
            san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());

        var serial = new byte[8];
        using var rnd = RandomNumberGenerator.Create();
        rnd.GetBytes(serial);
        var cert = req.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1), serial);
        var withKey = cert.CopyWithPrivateKey(rsa);

        File.WriteAllBytes(path, withKey.Export(X509ContentType.Pkcs12, CaPassword));
        return withKey;
    }

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else if (c == '.' && sb.Length > 0 && sb[^1] != '.')
                sb.Append(c); // single dots ok; dot runs would enable path traversal
            else
                sb.Append('-');
        }
        return sb.ToString();
    }

    public void Dispose() => _ca?.Dispose();
}
