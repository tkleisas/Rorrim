namespace Rorrim.Server.Agents;

public enum AgentLaunchMode
{
    /// <summary>Launch elevated (High IL) in the target session, no UAC prompt.</summary>
    Elevated,
    /// <summary>Launch as the session user's normal (Medium IL) token.</summary>
    Standard
}

/// <summary>
/// Launches the in-session Agent process. Production uses
/// WTSQueryUserToken + token elevation + CreateProcessAsUser; this abstraction allows orchestration
/// (and the requested-exit path) to be tested without a real session.
/// </summary>
public interface IAgentProcessLauncher
{
    /// <summary>
    /// Starts the Agent executable inside <paramref name="sessionId"/> as the session user (and
    /// optionally raised to High IL). <paramref name="args"/> are appended to the command line.
    /// </summary>
    Task<IAgentProcess> LaunchAsync(int sessionId, AgentLaunchMode mode, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// A handle to a running Agent process.
/// </summary>
public interface IAgentProcess
{
    int ProcessId { get; }

    /// <summary>Fires when the process exits.</summary>
    ValueTask<bool> WaitForExitAsync(CancellationToken ct);

    /// <summary>Attempts to terminate the process.</summary>
    void Kill();
}
