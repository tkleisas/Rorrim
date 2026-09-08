using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Server.Sessions;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Default <see cref="ISessionCoordinator"/>. Finds the active interactive session, ensures an agent
/// is running in it (launching as needed), waits for the agent to attach, and relays the stream.
/// </summary>
public sealed class SessionCoordinator : ISessionCoordinator
{
    private readonly ISessionProvider _sessions;
    private readonly IAgentProcessLauncher _launcher;
    private readonly IAgentRegistry _agents;
    private readonly ISessionBroker _broker;
    private readonly string _agentAttachAddress;
    private readonly TimeSpan _agentAttachTimeout;
    private readonly Action<string>? _log;

    public SessionCoordinator(
        ISessionProvider sessions,
        IAgentProcessLauncher launcher,
        IAgentRegistry agents,
        ISessionBroker broker,
        string agentAttachAddress,
        TimeSpan agentAttachTimeout,
        Action<string>? log = null)
    {
        _sessions = sessions;
        _launcher = launcher;
        _agents = agents;
        _broker = broker;
        _agentAttachAddress = agentAttachAddress;
        _agentAttachTimeout = agentAttachTimeout;
        _log = log;
    }

    public async Task HandleClientAsync(IClientEndpoint client, CancellationToken ct)
    {
        var session = _sessions.GetActiveInteractiveSession()
            ?? throw new InvalidOperationException("No active interactive session found.");

        var agent = _agents.Get(session.SessionId);
        if (agent is null)
        {
            // Launch but don't await — the agent registers via its own Attach call.
            string displayId = client.RequestedDisplayId ?? "0";
            try
            {
                var launched = await _launcher.LaunchAsync(session.SessionId, AgentLaunchMode.Elevated,
                    new[] { "--attach", _agentAttachAddress, "--session", session.SessionId.ToString(), "--display", displayId }, ct);
                _log?.Invoke($"[coordinator] launched agent pid={launched.ProcessId} in session {session.SessionId}");
            }
            catch (Exception ex)
            {
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

        // Set the completion signal up-front so the agent's transport call stays open during the relay.
        var completion = new TaskCompletionSource();
        agent.Completed = completion.Task;

        try
        {
            await _broker.RunAsync(client, agent, ct);
        }
        finally
        {
            completion.TrySetResult();
            if (_agents.Get(session.SessionId) is { } stillCurrent && ReferenceEquals(stillCurrent, agent))
                _agents.Unregister(session.SessionId, agent);
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
