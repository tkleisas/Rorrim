using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Adapts the gRPC client transport streams to the <see cref="IClientEndpoint"/> abstraction.
/// </summary>
public sealed class GrpcClientEndpoint : IClientEndpoint
{
    private readonly IServerStreamWriter<ServerToClient> _response;
    private readonly ServerCallContext _context;

    public string ClientId { get; }
    public string? RequestedDisplayId { get; private set; }
    public IAsyncEnumerable<ClientToServer> Incoming { get; }

    public GrpcClientEndpoint(
        IAsyncStreamReader<ClientToServer> requestStream,
        IServerStreamWriter<ServerToClient> responseStream,
        ServerCallContext context)
    {
        _response = responseStream;
        _context = context;

        // ClientId from the mTLS peer certificate subject, if present; fallback to peer address.
        string clientId = context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? "client";
        var peerCert = context.GetHttpContext()?.Connection?.GetClientCertificateAsync();
        if (peerCert is not null && peerCert.Result is { } cert)
            clientId = cert.Subject;
        ClientId = clientId;

        Incoming = Enumerate(requestStream);
        RequestedDisplayId = null;
    }

    private async IAsyncEnumerable<ClientToServer> Enumerate(IAsyncStreamReader<ClientToServer> stream)
    {
        while (await stream.MoveNext(_context.CancellationToken))
        {
            var msg = stream.Current;
            if (msg?.Hello is not null)
                RequestedDisplayId = string.IsNullOrEmpty(msg.Hello.DisplayId) ? null : msg.Hello.DisplayId;
            yield return msg!;
        }
    }

    public ValueTask SendAsync(ServerToClient message, CancellationToken ct) =>
        new(VoidAsync(message, ct));

    private async Task VoidAsync(ServerToClient message, CancellationToken ct) =>
        await _response.WriteAsync(message, ct);
}
