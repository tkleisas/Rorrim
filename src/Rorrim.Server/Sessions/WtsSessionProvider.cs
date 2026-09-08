using System.Runtime.InteropServices;
using System.Text;

namespace Rorrim.Server.Sessions;

/// <summary>
/// <see cref="ISessionProvider"/> backed by the WTS API. Rather than marshaling the whole
/// WTS_SESSION_INFO array (which carries fragile string pointers), this queries the active console
/// session id and reads the user/domain/state for it via WTSQuerySessionInformation. Robust against
/// pointer-range issues when the caller runs outside the interactive session.
/// </summary>
public sealed class WtsSessionProvider : ISessionProvider
{
    public IReadOnlyList<SessionInfo> Enumerate()
    {
        var results = new List<SessionInfo>();
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || sessionId == 0xFFFFFFFF)
            return results;

        string user = QueryString((int)sessionId, WTS_INFO_CLASS.WTSUserName) ?? "";
        string domain = QueryString((int)sessionId, WTS_INFO_CLASS.WTSDomainName) ?? "";
        var state = QueryState((int)sessionId);
        results.Add(new SessionInfo((int)sessionId, user, domain, state));
        return results;
    }

    public SessionInfo? GetActiveInteractiveSession()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue || sessionId == 0xFFFFFFFF)
            return null;

        string user = QueryString((int)sessionId, WTS_INFO_CLASS.WTSUserName) ?? "";
        string domain = QueryString((int)sessionId, WTS_INFO_CLASS.WTSDomainName) ?? "";
        var state = QueryState((int)sessionId);
        var info = new SessionInfo((int)sessionId, user, domain, state);
        return info.IsInteractive ? info : null;
    }

    private static string? QueryString(int sessionId, WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr ptr, out uint bytes))
            return null;
        try
        {
            if (ptr == IntPtr.Zero || bytes == 0)
                return null;
            return Marshal.PtrToStringUni(ptr)?.TrimEnd('\0');
        }
        finally
        {
            if (ptr != IntPtr.Zero) WTSFreeMemory(ptr);
        }
    }

    private static SessionState QueryState(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSConnectState, out IntPtr ptr, out uint bytes))
            return SessionState.Unknown;
        try
        {
            if (ptr == IntPtr.Zero || bytes < 4)
                return SessionState.Unknown;
            return (SessionState)Marshal.ReadInt32(ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) WTSFreeMemory(ptr);
        }
    }

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSConnectState = 8
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
