using Grpc.Core;
using Grpc.Net.Client;
using Rorrim.Agent.Capture;
using Rorrim.Agent.Display;
using Rorrim.Agent.Pipeline;
using Rorrim.Agent.Input;
using Rorrim.Shared.Contracts;

namespace Rorrim.Agent.Broker;

/// <summary>
/// One message flowing up from the agent to the broker: an encoded frame, a heartbeat, or an
/// (re-)announcement hello carrying the current display list. All share a single channel so the
/// gRPC request stream has a single writer.
/// </summary>
public readonly record struct AgentUp(EncodedFrame? Frame, Heartbeat? Heartbeat, AgentHello? Hello);

/// <summary>A capture-restart request: switch display and/or codec (rebuilds source + encoder).</summary>
public readonly record struct SwitchRequest(string DisplayId, Codec Codec);

/// <summary>
/// The Agent's loopback connection to the broker. Opens the RorrimAgent.Attach duplex stream,
/// announces its session via the x-rorrim-session / x-rorrim-token metadata headers (the token is
/// issued by the broker at launch), reports the displays it can capture, streams captured frames
/// up, handles display-switch commands, and injects control input received from the broker.
/// </summary>
public sealed class AgentAttachClient : IDisposable
{
    /// <summary>How often a heartbeat is pushed up to the broker.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly string _brokerAddress;
    private readonly string _displayId;
    private readonly int _sessionId;
    private readonly string? _token;
    private readonly InputInjector _inputInjector;
    private readonly Func<DisplayAdapter, Codec, IVideoSource> _sourceFactory;
    private readonly Func<DisplayAdapter, Codec, IVideoEncoder> _encoderFactory;
    private readonly StreamControllerOptions _controllerOptions;

    public AgentAttachClient(
        string brokerAddress,
        int sessionId,
        string displayId,
        Func<DisplayAdapter, Codec, IVideoSource> sourceFactory,
        Func<DisplayAdapter, Codec, IVideoEncoder>? encoderFactory,
        InputInjector? inputInjector = null,
        string? token = null,
        StreamControllerOptions? controllerOptions = null)
    {
        _brokerAddress = brokerAddress;
        _sessionId = sessionId;
        _displayId = displayId;
        _sourceFactory = sourceFactory;
        _encoderFactory = encoderFactory ?? ((_, codec) => CreateDefaultEncoder(codec));
        _inputInjector = inputInjector ?? new InputInjector(new DisplayRect(0, 0, 1920, 1080));
        _token = token;
        _controllerOptions = controllerOptions ?? new StreamControllerOptions
        {
            TargetFrameIntervalMs = 33 // cap at ~30 fps
        };
    }

    /// <summary>Default codec pipeline: H.264 when OpenH264 is available, JPEG otherwise.</summary>
    internal static IVideoEncoder CreateDefaultEncoder(Codec codec)
    {
        if (codec == Codec.H264)
        {
            try
            {
                return new OpenH264VideoEncoder();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[attach] h264 encoder unavailable ({ex.Message}); falling back to jpeg");
            }
        }
        return new JpegVideoEncoder();
    }

    /// <summary>Delay before re-attaching after a pairing ends, so the broker can release the old registration.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

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
                continue;
            }

            // The pairing ended (client disconnected). Back off before re-attaching: registering
            // too early would race the broker's cleanup and get the fresh registration discarded.
            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        // Allow cleartext HTTP/2 over loopback for the broker (mTLS can be layered on later).
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var displays = DisplayEnumerator.Enumerate();
        var current = DisplayEnumerator.Resolve(displays, _displayId)
            ?? throw new InvalidOperationException("No displays found to capture.");

        using var channel = GrpcChannel.ForAddress(_brokerAddress);
        var client = new RorrimAgent.RorrimAgentClient(channel);

        var headers = new Metadata
        {
            { "x-rorrim-session", _sessionId.ToString() }
        };
        if (!string.IsNullOrEmpty(_token))
            headers.Add("x-rorrim-token", _token);

        using var call = client.Attach(headers, cancellationToken: ct);
        var request = call.RequestStream;
        var response = call.ResponseStream;

        // The producer announces the hello (with a fresh display list) on every capture rebuild;
        // heartbeats flow from the heartbeat task below.

        // Switch requests arrive on the receive side and are consumed by the producer loop.
        using var switchRequests = new FrameChannel<SwitchRequest>(capacity: 4);

        // Bounded channel of uplink messages; backpressure instead of unbounded memory growth.
        using var frameChannel = new FrameChannel<AgentUp>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Producer: capture + encode for the current display/codec; rebuilds on switch requests.
        var producer = Task.Run(() => ProduceLoopAsync(
            current, Codec.H264, switchRequests, frameChannel, cts.Token), cts.Token);

        // Heartbeat writer: keep-alive + desktop lock state (shares the channel; channel serializes).
        var heartbeats = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await frameChannel.WriteAsync(
                    new AgentUp(null, new Heartbeat { MonotonicUs = (ulong)Now(), DesktopLocked = false }, null),
                    cts.Token);
                await Task.Delay(HeartbeatInterval, cts.Token);
            }
        }, cts.Token);

        // Send uplink messages on the request stream.
        Task sendUp = SendUpAsync(request, frameChannel, cts.Token);

        // Read commands/input down from the broker.
        Task receiveInput = ReceiveInputAsync(response, switchRequests, current.DeviceName, Codec.H264, cts.Token);

        // Wait for any to finish (disconnect or cancel); then tear down the rest.
        await Task.WhenAny(receiveInput, sendUp, producer, heartbeats);
        cts.Cancel();
        try { await Task.WhenAll(receiveInput, sendUp, producer, heartbeats); }
        catch when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Capture loop for the current display/codec. When a switch request arrives (display and/or
    /// codec change, or a restart), the running enumeration is interrupted via a per-iteration
    /// cancellation link and the source/encoder are rebuilt. Exits when the token is cancelled.
    /// </summary>
    private async Task ProduceLoopAsync(
        DisplayAdapter initial,
        Codec initialCodec,
        FrameChannel<SwitchRequest> switchRequests,
        FrameChannel<AgentUp> outChannel,
        CancellationToken ct)
    {
        string currentId = initial.DeviceName;
        Codec currentCodec = initialCodec;
        while (!ct.IsCancellationRequested)
        {
            var display = DisplayEnumerator.Resolve(DisplayEnumerator.Enumerate(), currentId);
            if (display is null)
                return;

            // Cancel this iteration's enumeration when a switch request shows up.
            using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task waitSwitch = switchRequests.WaitToReadAsync(iterationCts.Token);
            _ = waitSwitch.ContinueWith(
                t => { if (t.Status == TaskStatus.RanToCompletion) iterationCts.Cancel(); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                using var source = _sourceFactory(display.Value, currentCodec);
                source.Start();
                using var encoder = _encoderFactory(display.Value, currentCodec);
                using var controller = new StreamController(source, encoder, _controllerOptions);

                // (Re-)announce the hello with a freshly enumerated display list on every rebuild,
                // so the client always sees current monitors/resolutions.
                await outChannel.WriteAsync(new AgentUp(null, null, new AgentHello
                {
                    DisplayId = display.Value.DeviceName,
                    AcquireCapture = true,
                    Displays = { BuildDisplayList(DisplayEnumerator.Enumerate()) }
                }), ct);

                await foreach (var frame in controller.Produce(iterationCts.Token))
                {
                    await outChannel.WriteAsync(new AgentUp(frame, null, null), ct);
                }

                // Enumeration ended: switch pending -> rebuild for the new display/codec; else done.
                if (switchRequests.TryRead(out SwitchRequest requested))
                {
                    currentId = requested.DisplayId;
                    currentCodec = requested.Codec;
                    continue;
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[attach] capture error on {currentId} ({currentCodec}): {ex.Message}");
                // Capture failed; if a switch is pending, honor it, else stop.
                if (switchRequests.TryRead(out SwitchRequest requested))
                {
                    currentId = requested.DisplayId;
                    currentCodec = requested.Codec;
                    continue;
                }
                return;
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<DisplayInfo> BuildDisplayList(
        System.Collections.Generic.IReadOnlyList<DisplayAdapter> displays)
    {
        foreach (var d in displays)
        {
            yield return new DisplayInfo
            {
                DisplayId = d.DeviceName,
                Name = d.AdapterName,
                Width = (uint)d.Width,
                Height = (uint)d.Height,
                X = d.X,
                Y = d.Y,
                IsPrimary = d.IsPrimary
            };
        }
    }

    private static async Task SendUpAsync(
        IClientStreamWriter<AgentToServer> request,
        FrameChannel<AgentUp> channel,
        CancellationToken ct)
    {
        await foreach (var up in channel.ReadAllAsync(ct))
        {
            var msg = new AgentToServer();
            if (up.Frame is { } frame)
            {
                msg.Frame = new Frame
                {
                    Data = Google.Protobuf.ByteString.CopyFrom(frame.Data),
                    IsKeyframe = frame.IsKeyFrame,
                    TimestampUs = (ulong)frame.TimestampUs,
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height,
                    Codec = frame.Codec
                };
            }
            else if (up.Heartbeat is { } hb)
            {
                msg.Heartbeat = hb;
            }
            else if (up.Hello is { } hello)
            {
                msg.Hello = hello;
            }
            await request.WriteAsync(msg, ct);
        }
    }

    private async Task ReceiveInputAsync(
        IAsyncStreamReader<ServerToAgent> response,
        FrameChannel<SwitchRequest> switchRequests,
        string currentDisplayId,
        Codec currentCodec,
        CancellationToken ct)
    {
        while (await response.MoveNext(ct))
        {
            var msg = response.Current;
            if (msg is null) continue;

            if (msg.Input is { } input)
            {
                _inputInjector.Inject(input);
            }
            else if (msg.Command is { } command && command.Kind == AgentCommand.Types.CommandKind.SwitchDisplay)
            {
                var resolved = DisplayEnumerator.Resolve(
                    DisplayEnumerator.Enumerate(), command.DisplayId);

                // Determine what actually changes: display, codec, or both. A request that changes
                // nothing (e.g. the client's initial hello) is not a switch.
                var targetDisplay = resolved is { } r && !string.IsNullOrWhiteSpace(command.DisplayId)
                    ? r.DeviceName
                    : currentDisplayId;
                var targetCodec = command.Codec != Codec.Unspecified ? command.Codec : currentCodec;

                if (targetDisplay != currentDisplayId || targetCodec != currentCodec)
                {
                    if (resolved is { } r2)
                        _inputInjector.SetDisplay(new DisplayRect(r2.X, r2.Y, r2.Width, r2.Height));
                    currentDisplayId = targetDisplay;
                    currentCodec = targetCodec;
                    await switchRequests.WriteAsync(new SwitchRequest(targetDisplay, targetCodec), ct);
                }
            }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.UtcTicks / 10; // microseconds

    public void Dispose() { }
}
