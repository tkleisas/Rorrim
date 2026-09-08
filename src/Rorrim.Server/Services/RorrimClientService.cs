using Grpc.Core;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Services;

/// <summary>
/// Implements the RorrimClient gRPC service. It adapts the transport request/response streams into
/// an <see cref="IClientEndpoint"/> and asks the <see cref="ISessionCoordinator"/> to pair it with an
/// in-session agent, then relays until either side ends.
/// </summary>
public sealed class RorrimClientService : RorrimClient.RorrimClientBase
{
    private readonly ISessionCoordinator _coordinator;

    public RorrimClientService(ISessionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public override async Task StreamDesktop(
        IAsyncStreamReader<ClientToServer> requestStream,
        IServerStreamWriter<ServerToClient> responseStream,
        ServerCallContext context)
    {
        var endpoint = new GrpcClientEndpoint(requestStream, responseStream, context);

        // The client's first message is expected to be a Hello carrying the requested display.
        // We read it (non-blocking) after the coordinator has a chance to inspect it, or read on demand.
        try
        {
            await _coordinator.HandleClientAsync(endpoint, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client cancelled / left — just end the stream.
        }
    }
}
