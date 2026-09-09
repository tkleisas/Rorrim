using System.Security.Cryptography.X509Certificates;
using Rorrim.Server.Auth;

namespace Rorrim.Server.Tests;

public class CertificateAuthorityTests
{
    [Fact]
    public void Ca_IssuesClientCert_ValidatedAgainstCa()
    {
        using var ca = new CertificateAuthority(GetTempStore(), "testhost");
        var issued = ca.IssueClientCertificate("client-123");

        Assert.NotNull(issued.Certificate);
        Assert.True(issued.Certificate.HasPrivateKey);

        // Prove the client cert chains to the CA.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca.CaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        bool valid = chain.Build(issued.Certificate);
        Assert.True(valid, string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void Ca_CertificatesPersistAcrossInstances()
    {
        string store = GetTempStore();
        string caThumbprint;
        using (var ca1 = new CertificateAuthority(store, "host"))
        {
            caThumbprint = ca1.CaCertificate.Thumbprint;
            ca1.IssueClientCertificate("c1");
        }
        // A new instance over the same store must reuse the same CA (thumbprint matches).
        using var ca2 = new CertificateAuthority(store, "host");
        Assert.Equal(caThumbprint, ca2.CaCertificate.Thumbprint);
    }

    [Fact]
    public void ServerCertificate_ChainsToCa_HasServerAuthAndSan()
    {
        using var ca = new CertificateAuthority(GetTempStore(), "testhost");
        var server = ca.ServerCertificate;

        Assert.True(server.HasPrivateKey);
        Assert.NotEqual(ca.CaCertificate.Thumbprint, server.Thumbprint);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca.CaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(server), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));

        var eku = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains("1.3.6.1.5.5.7.3.1", eku.Format(false)); // serverAuth
        Assert.Contains("CN=testhost", server.Subject);
    }

    [Fact]
    public void IsValidClientCertificate_AcceptsOnlyCaIssuedCerts()
    {
        using var ca = new CertificateAuthority(GetTempStore(), "testhost");
        var issued = ca.IssueClientCertificate("valid-client");
        Assert.True(ca.IsValidClientCertificate(issued.Certificate));

        Assert.False(ca.IsValidClientCertificate(null));

        // A self-signed cert that was not issued by this CA must be rejected.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=imposter", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var imposter = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(ca.IsValidClientCertificate(imposter));
    }

    [Fact]
    public void CaPem_IsExported_AndMatchesCaThumbprint()
    {
        using var ca = new CertificateAuthority(GetTempStore(), "testhost");

        Assert.True(File.Exists(ca.CaPemPath));
        using var loaded = X509CertificateLoader.LoadCertificateFromFile(ca.CaPemPath);
        Assert.Equal(ca.CaCertificate.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void ClientPfxPath_SanitizesClientId()
    {
        using var ca = new CertificateAuthority(GetTempStore(), "testhost");
        string path = ca.ClientPfxPath("client/../evil id");
        Assert.DoesNotContain("..", path);
        Assert.DoesNotContain("/", path);
        Assert.EndsWith(".pfx", path);
    }

    [Fact]
    public void IssueClientCertificate_IsIdempotentForSameId()
    {
        string store = GetTempStore();
        using var ca = new CertificateAuthority(store, "host");
        var a = ca.IssueClientCertificate("dup");
        var b = ca.IssueClientCertificate("dup");
        Assert.Equal(a.Subject, b.Subject);
    }

    private static string GetTempStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rorrim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
