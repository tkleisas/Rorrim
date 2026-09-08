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
