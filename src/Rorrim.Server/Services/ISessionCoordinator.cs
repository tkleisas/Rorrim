using Rorrim.Server.Broker;

namespace Rorrim.Server.Services;

/// <summary>
/// Coordinates the lifecycle of a remote-control session: given an attached client, find the active
/// interactive session, start (or reuse) the in-session agent, wait for it to attach, then relay.
/// </summary>
public interface ISessionCoordinator
{
    Task HandleClientAsync(IClientEndpoint client, CancellationToken ct);
}
