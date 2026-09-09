using System.Collections.Concurrent;
using System.Security.Cryptography;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Server.Sessions;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Default <see cref="ISessionCoordinator"/>. Finds the active interactive session, ensures an agent
/// is running in it (launching as needed), waits for the agent to attach, and relays the stream.
/// Agents launched by this coordinator are stopped when their pairing ends so reconnecting clients
/// don't accumulate zombie agent processes.
/// </summary>
public sealed class SessionCoordinator : ISessionCoordinator
{
    private readonly ISessionProvider _sessions;
    private readonly IAgentProcessLauncher _launcher;
    private readonly IAgentRegistry _agents;
    private readonly ISessionBroker _broker;
    private readonly IAgentTokenStore _tokens;
    private readonly string _agentAttachAddress;
    private readonly TimeSpan _agentAttachTimeout;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _sessionLocks = new();
    private readonly ConcurrentDictionary<int, IAgentProcess> _launchedAgents = new();

    public SessionCoordinator(
        ISessionProvider sessions,
        IAgentProcessLauncher launcher,
        IAgentRegistry agents,
        ISessionBroker broker,
        IAgentTokenStore tokens,
        string agentAttachAddress,
        TimeSpan agentAttachTimeout,
        Action<string>? log = null)
    {
        _sessions = sessions;
        _launcher = launcher;
        _agents = agents;
        _broker = broker;
        _tokens = tokens;
        _agentAttachAddress = agentAttachAddress;
        _agentAttachTimeout = agentAttachTimeout;
        _log = log;
    }

    public async Task HandleClientAsync(IClientEndpoint client, CancellationToken ct)
    {
        var session = _sessions.GetActiveInteractiveSession()
            ?? throw new InvalidOperationException("No active interactive session found.");

        // Serialize find-or-launch per session: two clients connecting at once must not spawn
        // two agents for the same logon session. A second client waits its turn and is told so.
        var sessionLock = _sessionLocks.GetOrAdd(session.SessionId, _ => new SemaphoreSlim(1, 1));
        if (sessionLock.CurrentCount == 0)
        {
            await client.SendAsync(Status(SessionStatus.Types.StatusKind.Unknown,
                "Another client is controlling this session; waiting for it to disconnect..."), ct);
        }
        await sessionLock.WaitAsync(ct);

        IAgentProcess? launched = null;
        IAgentEndpoint? agent;
        try
        {
            agent = _agents.Get(session.SessionId);
            if (agent is null)
            {
                // Launch but don't await the attach — the agent registers via its own Attach call.
                // The agent must present this registration token on Attach, so only the process we
                // launched can act as the session's agent.
                string displayId = client.RequestedDisplayId ?? "0";
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _tokens.Set(session.SessionId, token);
                try
                {
                    launched = await _launcher.LaunchAsync(session.SessionId, AgentLaunchMode.Elevated,
                        new[] { "--attach", _agentAttachAddress, "--session", session.SessionId.ToString(), "--display", displayId, "--token", token }, ct);
                    _launchedAgents[session.SessionId] = launched;
                    _log?.Invoke($"[coordinator] launched agent pid={launched.ProcessId} in session {session.SessionId}");
                }
                catch (Exception ex)
                {
                    _tokens.Remove(session.SessionId);
                    _log?.Invoke($"[coordinator] agent launch failed: {ex}");
                    await client.SendAsync(Status(SessionStatus.Types.StatusKind.Disconnected, $"Agent launch failed: {ex.Message}"), ct);
                    return;
                }

                agent = await WaitForAgentAsync(session.SessionId, client, ct);
                if (agent is null)
                {
                    await client.SendAsync(Status(SessionStatus.Types.StatusKind.Disconnected, "Agent failed to attach"), ct);
                    return;
                }
            }

            try
            {
                _log?.Invoke($"[coordinator] pairing client '{client.ClientId}' with {agent.AgentId} (display '{client.RequestedDisplayId ?? "0"}')");
                await _broker.RunAsync(client, agent, ct);
                _log?.Invoke($"[coordinator] relay ended for {agent.AgentId}");
            }
            finally
            {
                // Release the agent's transport call so its service method can return.
                agent.MarkCompleted();
                if (_agents.Get(session.SessionId) is { } stillCurrent && ReferenceEquals(stillCurrent, agent))
                    _agents.Unregister(session.SessionId, agent);
            }
        }
        finally
        {
            sessionLock.Release();
            _tokens.Remove(session.SessionId);

            // If we launched the agent for this pairing, stop it now that the pairing is over.
            // The agent would otherwise linger and keep reconnecting to the broker.
            if (launched is not null
                && _launchedAgents.TryRemove(session.SessionId, out var tracked)
                && ReferenceEquals(tracked, launched))
            {
                _log?.Invoke($"[coordinator] stopping agent pid={launched.ProcessId}");
                launched.Kill();
            }
        }
    }

    private async Task<IAgentEndpoint?> WaitForAgentAsync(int sessionId, IClientEndpoint client, CancellationToken ct)
    {
        await client.SendAsync(Status(SessionStatus.Types.StatusKind.Locked, "Waiting for agent"), ct);
        var deadline = DateTime.UtcNow + _agentAttachTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var agent = _agents.Get(sessionId);
            if (agent is not null)
                return agent;
            await Task.Delay(200, ct);
        }
        return null;
    }

    private static ServerToClient Status(SessionStatus.Types.StatusKind kind, string message) =>
        new() { Status = new SessionStatus { Kind = kind, Message = message } };
}
