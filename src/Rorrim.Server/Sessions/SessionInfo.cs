namespace Rorrim.Server.Sessions;

/// <summary>
/// Describes a Windows logon session as seen from the broker.
/// </summary>
public readonly record struct SessionInfo(
    int SessionId,
    string UserName,
    string DomainName,
    SessionState State)
{
    public bool IsInteractive => State == SessionState.Active || State == SessionState.Connected;
}

/// <summary>
/// Mirrors WTS_CONNECTSTATE_CLASS (wtsapi32). Values are the WTS-defined numeric values.
/// </summary>
public enum SessionState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
    Unknown = -1
}
