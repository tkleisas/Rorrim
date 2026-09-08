using System.Collections.Concurrent;
using Rorrim.Server.Broker;

namespace Rorrim.Server.Agents;

/// <summary>
/// Thread-safe in-memory <see cref="IAgentRegistry"/> keyed by session id.
/// </summary>
public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<int, IAgentEndpoint> _agents = new();

    public IAgentEndpoint? Register(int sessionId, IAgentEndpoint agent) =>
        _agents.AddOrUpdate(sessionId, agent, (_, prev) => prev);

    public IAgentEndpoint? Get(int sessionId) =>
        _agents.TryGetValue(sessionId, out var agent) ? agent : null;

    public void Unregister(int sessionId, IAgentEndpoint agent) =>
        _agents.TryRemove(new KeyValuePair<int, IAgentEndpoint>(sessionId, agent));
}
