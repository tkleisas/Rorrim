using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Broker;

/// <summary>
/// A registered Agent stream: the agent's request (frames up) and response (input down) streams.
/// </summary>
public interface IAgentEndpoint
{
    string AgentId { get; }
    int SessionId { get; }

    /// <summary>The messages the agent is sending up (frames, heartbeats).</summary>
    IAsyncEnumerable<AgentToServer> Incoming { get; }

    /// <summary>Sends a command/input message down to the agent.</summary>
    ValueTask SendAsync(ServerToAgent message, CancellationToken ct);

    /// <summary>
    /// A task that completes when the relay pair no longer needs this agent and the transport
    /// service may release the call. Set by the coordinator when the session ends.
    /// </summary>
    Task Completed { get; set; }
}

/// <summary>
/// A connected remote client wanting a desktop stream.
/// </summary>
public interface IClientEndpoint
{
    string ClientId { get; }
    string? RequestedDisplayId { get; }

    /// <summary>The messages the client is sending up (hello, input, grid).</summary>
    IAsyncEnumerable<ClientToServer> Incoming { get; }

    /// <summary>Sends a status/frame message down to the client.</summary>
    ValueTask SendAsync(ServerToClient message, CancellationToken ct);
}
