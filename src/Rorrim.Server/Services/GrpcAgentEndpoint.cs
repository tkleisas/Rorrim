using Grpc.Core;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Adapts the gRPC agent transport streams to the <see cref="IAgentEndpoint"/> abstraction. The
/// session id is provided via gRPC request metadata. The coordinator signals completion through
/// <see cref="MarkCompleted"/> so the holding service method can return once the relay finishes.
/// </summary>
public sealed class GrpcAgentEndpoint : IAgentEndpoint
{
    private readonly IServerStreamWriter<ServerToAgent> _response;
    private readonly ServerCallContext _context;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string AgentId { get; }
    public int SessionId { get; }
    public IAsyncEnumerable<AgentToServer> Incoming { get; }

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

    public void MarkCompleted() => _completed.TrySetResult();

    public Task WaitUntilCompletedAsync(CancellationToken ct) =>
        _completed.Task.WaitAsync(ct);
}
