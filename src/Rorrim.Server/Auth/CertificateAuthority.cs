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
    private const string ClientPfxDir = "clients";

    private readonly string _storePath;
    private readonly X509Certificate2 _ca;

    public X509Certificate2 CaCertificate => _ca;
    public string HostName { get; }

    public CertificateAuthority(string storePath, string hostName)
    {
        _storePath = storePath;
        HostName = hostName;
        Directory.CreateDirectory(storePath);
        _ca = LoadOrCreateCa();
    }

    public IssuedClientCertificate IssueClientCertificate(string clientId)
    {
        string safeId = Sanitize(clientId);
        string dir = Path.Combine(_storePath, ClientPfxDir);
        Directory.CreateDirectory(dir);
        string pfxPath = Path.Combine(dir, safeId + ".pfx");
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

    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return sb.ToString();
    }

    public void Dispose() => _ca?.Dispose();
}
