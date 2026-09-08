using Grpc.Core;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Adapts the gRPC agent transport streams to the <see cref="IAgentEndpoint"/> abstraction. The
/// session id is provided via gRPC request metadata. <see cref="Completed"/> is set by the coordinator
/// when the relay finishes, so the holding service method can return.
/// </summary>
public sealed class GrpcAgentEndpoint : IAgentEndpoint
{
    private readonly IServerStreamWriter<ServerToAgent> _response;
    private readonly ServerCallContext _context;

    public string AgentId { get; }
    public int SessionId { get; }
    public IAsyncEnumerable<AgentToServer> Incoming { get; }

    /// <summary>
    /// A task that completes when the prior client pairing no longer needs this agent. By default it
    /// is a pending task (no client yet) so the transport call stays open while the agent is idle.
    /// The coordinator replaces it (via the interface setter) with a TCS it completes on teardown.
    /// </summary>
    public Task Completed { get; set; } = new TaskCompletionSource().Task;

    public GrpcAgentEndpoint(
        IAsyncStreamReader<AgentToServer> requestStream,
        IServerStreamWriter<ServerToAgent> responseStream,
        ServerCallContext context,
        int sessionId)
    {
        _response = responseStream;
        _context = context;
        SessionId = sessionId;
        AgentId = $"agent-{sessionId}-{context.Peer}";

        Incoming = Enumerate(requestStream);
    }

    private async IAsyncEnumerable<AgentToServer> Enumerate(IAsyncStreamReader<AgentToServer> stream)
    {
        while (await stream.MoveNext(_context.CancellationToken))
            yield return stream.Current;
    }

    public ValueTask SendAsync(ServerToAgent message, CancellationToken ct) =>
        new(_response.WriteAsync(message, ct));
}
