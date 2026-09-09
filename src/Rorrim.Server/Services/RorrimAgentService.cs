using Grpc.Core;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Implements the RorrimAgent gRPC service (loopback, only reachable by the in-session agent). The
/// agent announces its logon session id and the registration token it was launched with via the
/// "x-rorrim-session" / "x-rorrim-token" request metadata headers; only agents holding the token
/// the coordinator issued are allowed to register. On Attach the agent is registered with the
/// broker registry and the call is held open while the broker's relay consumes the stream.
/// </summary>
public sealed class RorrimAgentService : RorrimAgent.RorrimAgentBase
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentTokenStore _tokens;
    private readonly bool _allowUnauthenticatedAgents;
    private readonly Action<string>? _log;

    public RorrimAgentService(
        IAgentRegistry registry,
        IAgentTokenStore tokens,
        bool allowUnauthenticatedAgents = false,
        Action<string>? log = null)
    {
        _registry = registry;
        _tokens = tokens;
        // Dev-only: --test-mode permits manually-launched agents without a registration token so
        // the full stack can be exercised without running the broker as LocalSystem.
        _allowUnauthenticatedAgents = allowUnauthenticatedAgents;
        _log = log;
    }

    public override async Task Attach(
        IAsyncStreamReader<AgentToServer> requestStream,
        IServerStreamWriter<ServerToAgent> responseStream,
        ServerCallContext context)
    {
        int sessionId = ParseSessionHeader(context);
        if (sessionId < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing x-rorrim-session metadata."));

        string? token = GetHeader(context, "x-rorrim-token");
        if (!_allowUnauthenticatedAgents && !_tokens.TryValidate(sessionId, token))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid or missing agent token."));

        var endpoint = new GrpcAgentEndpoint(requestStream, responseStream, context, sessionId);

        // Registration is signal-only here; the coordinator pairs the endpoint with a client.
        _registry.Register(sessionId, endpoint);
        _log?.Invoke($"[agent-service] agent registered for session {sessionId}: {endpoint.AgentId}");

        try
        {
            // Hold the call open until the broker's relay completes.
            await endpoint.WaitUntilCompletedAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _registry.Unregister(sessionId, endpoint);
            _tokens.Remove(sessionId);
            _log?.Invoke($"[agent-service] agent unregistered for session {sessionId}");
        }
    }

    private static int ParseSessionHeader(ServerCallContext context)
    {
        var h = GetHeader(context, "x-rorrim-session");
        return h is not null && int.TryParse(h, out var s) ? s : -1;
    }

    private static string? GetHeader(ServerCallContext context, string key) =>
        context.RequestHeaders.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
}
