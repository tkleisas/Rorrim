using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Input;
using Rorrim.Client.Streaming;

namespace Rorrim.Client.Tests;

public class MutualTlsTests
{
    private static (X509Certificate2 ca, X509Certificate2 leaf, string caPemPath) MakeCaAndLeaf()
    {
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var caRsa = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=test-ca", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var caSelfSigned = caReq.CreateSelfSigned(notBefore, notAfter);
        // Re-import from PFX: on Windows, a CreateSelfSigned key cannot always sign another cert.
        var ca = X509CertificateLoader.LoadPkcs12(caSelfSigned.Export(X509ContentType.Pkcs12, "pw"), "pw");

        using var leafRsa = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=testhost", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        // Stay strictly inside the issuer's validity window regardless of clock ticks.
        var leaf = leafReq.Create(ca, notBefore, ca.NotAfter.AddSeconds(-1), serial);

        string path = Path.Combine(Path.GetTempPath(), "rorrim-test-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, ca.ExportCertificatePem());
        return (ca, leaf, path);
    }

    [Fact]
    public void ValidateServerCertificate_AcceptsCaIssuedCert()
    {
        var (ca, leaf, pemPath) = MakeCaAndLeaf();
        try
        {
            using var pinned = MutualTls.LoadCaPem(pemPath);
            Assert.True(MutualTls.ValidateServerCertificate(leaf, pinned));
        }
        finally
        {
            File.Delete(pemPath);
        }
    }

    [Fact]
    public void ValidateServerCertificate_RejectsForeignCert()
    {
        var (ca, _, pemPath) = MakeCaAndLeaf();
        try
        {
            using var imposterRsa = RSA.Create(2048);
            var imposterReq = new CertificateRequest("CN=imposter", imposterRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var imposter = imposterReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            using var pinned = MutualTls.LoadCaPem(pemPath);
            Assert.False(MutualTls.ValidateServerCertificate(imposter, pinned));
            Assert.False(MutualTls.ValidateServerCertificate(null, pinned));
        }
        finally
        {
            File.Delete(pemPath);
        }
    }
}

public class MapKeyTests
{
    [Theory]
    [InlineData(Key.A, 0x41u)]
    [InlineData(Key.Z, 0x5Au)]
    [InlineData(Key.D5, 0x35u)]
    [InlineData(Key.NumPad3, 0x63u)]
    [InlineData(Key.F1, 0x70u)]
    [InlineData(Key.F12, 0x7Bu)]
    [InlineData(Key.Space, 0x20u)]
    [InlineData(Key.Enter, 0x0Du)]
    [InlineData(Key.Escape, 0x1Bu)]
    [InlineData(Key.LeftShift, 0xA0u)]
    [InlineData(Key.OemMinus, 0xBDu)]
    public void MapKey_ReturnsVkCodes(Key key, uint expectedVk) =>
        Assert.Equal(expectedVk, InputEncoder.MapKey(key).vk);

    [Fact]
    public void MapKey_MarksNavigationKeysExtended()
    {
        Assert.True(InputEncoder.MapKey(Key.Left).ext);
        Assert.True(InputEncoder.MapKey(Key.Delete).ext);
        Assert.True(InputEncoder.MapKey(Key.Home).ext);
        Assert.False(InputEncoder.MapKey(Key.A).ext);
    }

    [Fact]
    public void MapKey_UnmappedKey_ReturnsZero()
    {
        Assert.Equal(0u, InputEncoder.MapKey(Key.None).vk);
    }
}
