using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Rorrim.Codecs;
using Rorrim.Shared.Contracts;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Opens the StreamDesktop duplex to the broker, sends the initial Hello, dispatches decoded frames
/// to a callback, and forwards input messages over the same stream. The UI layer owns the render
/// callback; this type only manages the gRPC session lifecycle. For mTLS brokers pass a client
/// certificate and the pinned CA certificate.
/// </summary>
public sealed class BrokerSessionClient : IDisposable, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly RorrimClient.RorrimClientClient _client;
    private AsyncDuplexStreamingCall<ClientToServer, ServerToClient>? _call;
    private readonly Func<DecodedFrame, ValueTask> _onFrame;
    private readonly Action<SessionStatus.Types.StatusKind, string>? _onStatus;
    private readonly Action<CancellationToken>? _onDisconnected;
    private readonly Action<DisplayListReply>? _onDisplays;

    public BrokerSessionClient(
        string brokerAddress,
        Func<DecodedFrame, ValueTask> onFrame,
        Action<SessionStatus.Types.StatusKind, string>? onStatus = null,
        Action<CancellationToken>? onDisconnected = null,
        X509Certificate2? clientCertificate = null,
        X509Certificate2? trustedCa = null,
        Action<DisplayListReply>? onDisplays = null)
    {
        // Allow cleartext HTTP/2 (loopback / test endpoint). Harmless for TLS endpoints.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions()
        };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        if (trustedCa is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, cert, _, _) => MutualTls.ValidateServerCertificate(
                    cert as X509Certificate2 ?? (cert is null ? null : new X509Certificate2(cert)),
                    trustedCa);
        }

        _channel = GrpcChannel.ForAddress(brokerAddress, new GrpcChannelOptions { HttpHandler = handler });
        _client = new RorrimClient.RorrimClientClient(_channel);
        _onFrame = onFrame;
        _onStatus = onStatus;
        _onDisconnected = onDisconnected;
        _onDisplays = onDisplays;
    }

    public async Task StartAsync(string displayId, CancellationToken ct)
    {
        // Request H.264 only where the OpenH264 native is loadable (H264Sharp ships no macOS
        // natives); everything else negotiates JPEG, which every platform decodes.
        var requestedCodec = H264FrameDecoder.IsSupported() ? Codec.H264 : Codec.Jpeg;
        _call = _client.StreamDesktop(cancellationToken: ct);
        await _call.RequestStream.WriteAsync(new ClientToServer
        {
            Hello = new Hello { DisplayId = displayId, RequestedCodec = requestedCodec }
        }, ct);

        var receive = ReceiveLoop(ct);
        await receive;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        using var decoder = new VideoFrameDecoder();
        try
        {
            await foreach (var msg in _call!.ResponseStream.ReadAllAsync(ct))
            {
                if (msg.Frame is { } frame)
                {
                    var decoded = decoder.Decode(frame);
                    if (decoded.Width == 0) continue; // H.264 decoder priming
                    await _onFrame(decoded);
                }
                else if (msg.Status is { } status)
                {
                    _onStatus?.Invoke(status.Kind, status.Message);
                }
                else if (msg.Displays is { } displays)
                {
                    _onDisplays?.Invoke(displays);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Channel torn down / RPC faulted during teardown — surface as a disconnect.
        }
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
