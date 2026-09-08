using Rorrim.Server.Broker;

namespace Rorrim.Server.Agents;

/// <summary>
/// Tracks agents that have attached to the broker, keyed by logon session. The coordinator finds or
/// creates an agent for a session, then consumes the agent endpoint.
/// </summary>
public interface IAgentRegistry
{
    /// <summary>Registers an attached agent and returns the previous one for the session (if any).</summary>
    IAgentEndpoint? Register(int sessionId, IAgentEndpoint agent);

    /// <summary>Gets the currently attached agent for a session, or null.</summary>
    IAgentEndpoint? Get(int sessionId);

    /// <summary>Removes an agent if it is still the current one for the session.</summary>
    void Unregister(int sessionId, IAgentEndpoint agent);
}
