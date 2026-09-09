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
        // Read the client's first message (expected Hello) up-front: the coordinator needs the
        // requested display id before it can launch the agent.
        ClientToServer? first = null;
        try
        {
            if (await requestStream.MoveNext(context.CancellationToken))
                first = requestStream.Current;
        }
        catch (OperationCanceledException)
        {
            return; // client left before saying hello
        }

        var endpoint = new GrpcClientEndpoint(requestStream, responseStream, context, first);
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
