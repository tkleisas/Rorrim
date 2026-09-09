using System.Collections.Concurrent;
using Rorrim.Server.Broker;


namespace Rorrim.Server.Agents;

/// <summary>
/// Thread-safe in-memory <see cref="IAgentRegistry"/> keyed by session id.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<int, IAgentEndpoint> _agents = new();

    /// <summary>
    /// Registers an attached agent, replacing any previous registration for the session, and
    /// returns the previous one (if any). Newest-wins: a superseded endpoint belongs to a dead
    /// call (the agent re-attaches with a fresh stream after a pairing ends), so keeping the old
    /// entry would pair new clients with a dead stream.
    /// </summary>
    public IAgentEndpoint? Register(int sessionId, IAgentEndpoint agent)
    {
        _agents.TryGetValue(sessionId, out var previous);
        _agents[sessionId] = agent;
        return previous;
    }

    public IAgentEndpoint? Get(int sessionId) =>
        _agents.TryGetValue(sessionId, out var agent) ? agent : null;

    public void Unregister(int sessionId, IAgentEndpoint agent) =>
        _agents.TryRemove(new KeyValuePair<int, IAgentEndpoint>(sessionId, agent));
}
