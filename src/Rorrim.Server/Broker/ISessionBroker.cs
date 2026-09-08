namespace Rorrim.Server.Broker;

/// <summary>
/// Bridges a <see cref="IClientEndpoint"/> and an <see cref="IAgentEndpoint"/>: relays AgentToServer
/// frames down to the client as ServerToClient, and relays client input up to the agent. This is the
/// testable message-multiplexing core; the gRPC service implementations only marshal transport
/// streams into these endpoints.
/// </summary>
public interface ISessionBroker
{
    /// <summary>
    /// Runs the relay between a client and an agent until either side disconnects.
    /// Returns when the pairing ends.
    /// </summary>
    ValueTask RunAsync(IClientEndpoint client, IAgentEndpoint agent, CancellationToken ct);
}
