namespace Rorrim.Server.Sessions;

/// <summary>
/// Provides the list of logon sessions on the host and selects the interactive console session.
/// The WTS API implementation is thin; this abstraction lets broker logic be unit tested.
/// </summary>
public interface ISessionProvider
{
    IReadOnlyList<SessionInfo> Enumerate();

    /// <summary>
    /// Returns the active interactive console session (the desktop the user is logged into), or null.
    /// </summary>
    SessionInfo? GetActiveInteractiveSession();
}
