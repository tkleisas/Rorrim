using Grpc.Core;
using Grpc.Net.Client;
using Rorrim.Agent.Capture;
using Rorrim.Agent.Pipeline;
using Rorrim.Agent.Input;
using Rorrim.Shared.Contracts;

namespace Rorrim.Agent.Broker;

/// <summary>
/// The Agent's loopback connection to the broker. Opens the RorrimAgent.Attach duplex stream,
/// announces its session via an x-rorrim-session metadata header, streams captured frames up, and
/// injects control input received from the broker.
/// </summary>
public sealed class AgentAttachClient : IDisposable
{
    private readonly string _brokerAddress;
    private readonly string _displayId;
    private readonly int _sessionId;
    private readonly Func<IVideoSource> _sourceFactory;
    private readonly Func<IVideoEncoder> _encoderFactory;

    public AgentAttachClient(
        string brokerAddress,
        int sessionId,
        string displayId,
        Func<IVideoSource>? sourceFactory = null,
        Func<IVideoEncoder>? encoderFactory = null)
    {
        _brokerAddress = brokerAddress;
        _sessionId = sessionId;
        _displayId = displayId;
        _sourceFactory = sourceFactory ?? (() => throw new NotSupportedException("No source factory provided."));
        _encoderFactory = encoderFactory ?? (() => new JpegVideoEncoder());
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Loop the connection so a transient failure doesn't permanently kill the agent.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[attach] connection error: {ex.Message}");
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        // Allow cleartext HTTP/2 over loopback for the broker (mTLS can be layered on later).
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        using var channel = GrpcChannel.ForAddress(_brokerAddress);
        var client = new RorrimAgent.RorrimAgentClient(channel);

        var headers = new Metadata
        {
            { "x-rorrim-session", _sessionId.ToString() }
        };

        using var call = client.Attach(headers, cancellationToken: ct);
        var request = call.RequestStream;
        var response = call.ResponseStream;

        // Announce that we're attached and which display we intend to capture.
        await request.WriteAsync(new AgentToServer
        {
            Hello = new AgentHello
            {
                DisplayId = _displayId,
                AcquireCapture = true
            }
        }, ct);
        await request.WriteAsync(new AgentToServer { Heartbeat = new Heartbeat { MonotonicUs = (ulong)Now() } }, ct);

        using var source = _sourceFactory();
        source.Start();

        // Build a bounded channel of encoded frames so the capture loop and the gRPC writer
        // run independently (the writer may block on backpressure).
        using var frameChannel = new FrameChannel();

        // Producer: capture + encode, push EncodedFrame into the channel.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            using var encoder = _encoderFactory();
            var controller = new StreamController(source, encoder);
            await foreach (var frame in controller.Produce(cts.Token))
            {
                await frameChannel.WriteAsync(frame, cts.Token);
            }
        }, ct);

        // Send encoded frames up on the response stream.
        Task sendFrames = SendFramesAsync(request, frameChannel, cts.Token);

        // Read commands/input down from the broker.
        Task receiveInput = ReceiveInputAsync(response, source, cts.Token);

        // Wait for any to finish (disconnect or cancel); then tear down the rest.
        await Task.WhenAny(receiveInput, sendFrames, producer);
        cts.Cancel();
        try { await Task.WhenAll(receiveInput, sendFrames, producer); }
        catch when (ct.IsCancellationRequested) { }
    }

    private static async Task SendFramesAsync(
        IClientStreamWriter<AgentToServer> request,
        FrameChannel channel,
        CancellationToken ct)
    {
        await foreach (var frame in channel.ReadAllAsync(ct))
        {
            await request.WriteAsync(new AgentToServer
            {
                Frame = new Frame
                {
                    Data = Google.Protobuf.ByteString.CopyFrom(frame.Data),
                    IsKeyframe = frame.IsKeyFrame,
                    TimestampUs = (ulong)frame.TimestampUs,
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height
                }
            }, ct);
        }
    }

    private static async Task ReceiveInputAsync(
        IAsyncStreamReader<ServerToAgent> response,
        IVideoSource source,
        CancellationToken ct)
    {
        while (await response.MoveNext(ct))
        {
            var msg = response.Current;
            if (msg?.Input is not null)
            {
                InputInjector.Inject(msg.Input);
            }
            // AgentCommand responses (start/stop) could toggle capture; not wired yet.
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose() { }
}
