using Grpc.Core;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Implements the RorrimAgent gRPC service (loopback, only reachable by the in-session agent). The
/// agent announces its logon session id via the "x-rorrim-session" request metadata header. On Attach
/// the agent is registered with the broker registry and the call is held open while the broker's
/// relay consumes the stream (which we read from the same enumerable in the background to keep the
/// wire flowing and to surface disconnects).
/// </summary>
public sealed class RorrimAgentService : RorrimAgent.RorrimAgentBase
{
    private readonly IAgentRegistry _registry;

    public RorrimAgentService(IAgentRegistry registry)
    {
        _registry = registry;
    }

    public override async Task Attach(
        IAsyncStreamReader<AgentToServer> requestStream,
        IServerStreamWriter<ServerToAgent> responseStream,
        ServerCallContext context)
    {
        int sessionId = ParseSessionHeader(context);
        if (sessionId < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing x-rorrim-session metadata."));

        var endpoint = new GrpcAgentEndpoint(requestStream, responseStream, context, sessionId);

        // Drain an initial Hello if present before registering (non-blocking peek is fine; the
        // broker re-reads from the same enumerable). Registration is signal-only here.
        _registry.Register(sessionId, endpoint);

        try
        {
            // Hold the call open until the broker's relay completes.
            await endpoint.Completed.WaitAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _registry.Unregister(sessionId, endpoint);
        }
    }

    private static int ParseSessionHeader(ServerCallContext context)
    {
        var h = context.RequestHeaders.FirstOrDefault(x => x.Key.Equals("x-rorrim-session", StringComparison.OrdinalIgnoreCase));
        return h is not null && int.TryParse(h.Value, out var s) ? s : -1;
    }
}
