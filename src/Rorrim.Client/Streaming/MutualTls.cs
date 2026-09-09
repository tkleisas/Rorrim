using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Loads mutual-TLS material (client PFX, pinned CA PEM) and validates the server certificate
/// against the pinned CA. The broker's CA is not in any machine trust store, so the server chain is
/// built with <see cref="X509ChainTrustMode.CustomRootTrust"/>.
/// </summary>
public static class MutualTls
{
    public static X509Certificate2 LoadClientPfx(string path, string? password) =>
        string.IsNullOrEmpty(password)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, (string?)null)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);

    public static X509Certificate2 LoadCaPem(string path) =>
        X509CertificateLoader.LoadCertificateFromFile(path);

    public static bool ValidateServerCertificate(X509Certificate2? server, X509Certificate2 trustedCa)
    {
        if (server is null)
            return false;
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(server);
        }
        catch
        {
            return false;
        }
    }
}
