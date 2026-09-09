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

    /// <param name="first">
    /// The first message read from the request stream (typically the Hello). It must be read before
    /// the coordinator runs so <see cref="RequestedDisplayId"/> is known when the agent is launched;
    /// it is re-yielded first so consumers of <see cref="Incoming"/> see the complete stream.
    /// </param>
    public GrpcClientEndpoint(
        IAsyncStreamReader<ClientToServer> requestStream,
        IServerStreamWriter<ServerToClient> responseStream,
        ServerCallContext context,
        ClientToServer? first)
    {
        _response = responseStream;
        _context = context;

        // ClientId from the mTLS peer certificate subject, if present; fallback to peer address.
        string clientId = context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? "client";
        var peerCert = context.GetHttpContext()?.Connection?.GetClientCertificateAsync();
        if (peerCert is { IsCompletedSuccessfully: true } && peerCert.Result is { } cert)
            clientId = cert.Subject;
        ClientId = clientId;

        if (first?.Hello is not null)
            RequestedDisplayId = string.IsNullOrEmpty(first.Hello.DisplayId) ? null : first.Hello.DisplayId;

        Incoming = Enumerate(requestStream, first);
    }

    private async IAsyncEnumerable<ClientToServer> Enumerate(
        IAsyncStreamReader<ClientToServer> stream,
        ClientToServer? first)
    {
        if (first is not null)
        {
            if (first.Hello is not null)
                RequestedDisplayId = string.IsNullOrEmpty(first.Hello.DisplayId) ? null : first.Hello.DisplayId;
            yield return first;
        }
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
