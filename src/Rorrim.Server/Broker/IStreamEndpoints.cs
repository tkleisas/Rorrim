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

    /// <summary>Signals that the relay pair no longer needs this agent.</summary>
    void MarkCompleted();

    /// <summary>
    /// Completes when <see cref="MarkCompleted"/> has been called (or immediately after), so the
    /// transport service can release the call. Waiting on the returned task is safe regardless of
    /// whether the signal has already been given.
    /// </summary>
    Task WaitUntilCompletedAsync(CancellationToken ct);
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
