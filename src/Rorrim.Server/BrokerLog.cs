using System.IO;

namespace Rorrim.Server;

/// <summary>
/// Simple append-only file logger used because Windows services have no console/stdout. Always writes
/// to a fixed diagnostic path so it is independent of arg parsing and service environment.
/// </summary>
public static class BrokerLog
{
    private static readonly object Lock = new();
    private static string _logPath = "C:\\Windows\\Temp\\rorrim-diag.log";

    public static void Configure(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _logPath = path;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* never throw from logging */ }
    }
}
