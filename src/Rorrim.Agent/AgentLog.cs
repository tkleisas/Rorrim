namespace Rorrim.Agent;

/// <summary>
/// Minimal file logger for the in-session Agent, which has no stdout when launched by the broker.
/// Writes to a fixed diagnostic path so startup/failure causes are diagnosable.
/// </summary>
public static class AgentLog
{
    private static readonly string Path = "C:\\Windows\\Temp\\rorrim-agent.log";
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
