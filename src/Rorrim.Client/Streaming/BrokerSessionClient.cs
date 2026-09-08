using Grpc.Core;
using Grpc.Net.Client;
using Rorrim.Shared.Contracts;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Opens the StreamDesktop duplex to the broker, sends the initial Hello, dispatches decoded frames
/// to a callback, and forwards input messages over the same stream. The UI layer owns the render
/// callback; this type only manages the gRPC session lifecycle.
/// </summary>
public sealed class BrokerSessionClient : IDisposable, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly RorrimClient.RorrimClientClient _client;
    private AsyncDuplexStreamingCall<ClientToServer, ServerToClient>? _call;
    private readonly Func<DecodedFrame, ValueTask> _onFrame;
    private readonly Action<SessionStatus.Types.StatusKind, string>? _onStatus;
    private readonly Action<CancellationToken>? _onDisconnected;

    public BrokerSessionClient(
        string brokerAddress,
        Func<DecodedFrame, ValueTask> onFrame,
        Action<SessionStatus.Types.StatusKind, string>? onStatus = null,
        Action<CancellationToken>? onDisconnected = null)
    {
        // Allow cleartext HTTP/2 (loopback / test endpoint). Harmless for TLS endpoints.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        _channel = GrpcChannel.ForAddress(brokerAddress);
        _client = new RorrimClient.RorrimClientClient(_channel);
        _onFrame = onFrame;
        _onStatus = onStatus;
        _onDisconnected = onDisconnected;
    }

    public async Task StartAsync(string displayId, CancellationToken ct)
    {
        _call = _client.StreamDesktop(cancellationToken: ct);
        await _call.RequestStream.WriteAsync(new ClientToServer
        {
            Hello = new Hello { DisplayId = displayId }
        }, ct);

        var receive = ReceiveLoop(ct);
        await receive;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _call!.ResponseStream.ReadAllAsync(ct))
            {
                if (msg.Frame is { } frame)
                {
                    var decoded = FrameDecoder.Decode(frame);
                    await _onFrame(decoded);
                }
                else if (msg.Status is { } status)
                {
                    _onStatus?.Invoke(status.Kind, status.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _onDisconnected?.Invoke(ct);
        }
    }

    public async ValueTask SendAsync(ClientToServer message, CancellationToken ct)
    {
        if (_call is null) return;
        await _call.RequestStream.WriteAsync(message, ct);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _call?.RequestStream.CompleteAsync();
            _call?.Dispose();
        }
        catch { }
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        try { _call?.Dispose(); } catch { }
        _channel.Dispose();
    }
}
