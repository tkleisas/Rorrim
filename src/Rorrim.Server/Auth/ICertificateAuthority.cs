using System.Security.Cryptography.X509Certificates;

namespace Rorrim.Server.Auth;

public sealed record IssuedClientCertificate(
    X509Certificate2 Certificate,
    string Subject,
    string PublicKeyPem);

/// <summary>
/// Owns the server X.509 CA and issues per-client certificates for mTLS authentication on the
/// gRPC channel. Abstracted so the broker's provisioning flow is testable.
/// </summary>
public interface ICertificateAuthority : IDisposable
{
    /// <summary>The CA certificate used to sign server and client certs.</summary>
    X509Certificate2 CaCertificate { get; }

    /// <summary>The TLS server certificate (leaf signed by the CA) for the gRPC listener.</summary>
    X509Certificate2 ServerCertificate { get; }

    /// <summary>Validates a peer certificate for mTLS: must chain to this CA.</summary>
    bool IsValidClientCertificate(X509Certificate2? certificate);

    /// <summary>A subject name string identifying this host (used for the server certificate).</summary>
    string HostName { get; }

    /// <summary>Creates a client certificate for an authenticated remote client.</summary>
    IssuedClientCertificate IssueClientCertificate(string clientId);
}
