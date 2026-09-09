using Rorrim.Agent;
using Rorrim.Agent.Broker;
using Rorrim.Agent.Capture;
using Rorrim.Agent.Display;
using Rorrim.Agent.Input;
using Rorrim.Agent.Pipeline;
using Rorrim.Shared.Contracts;

// Rorrim Agent. Two modes:
//   Rorrim.Agent --attach <brokerAddr> --session <id> [--token <t>] [--display <n>]
//       In-session capture: connects back to the broker over gRPC, streams frames, injects input.
//   Rorrim.Agent self-test [h264] [displayIdx]
//       Snapshots a display to disk for validation (h264 = use the H.264 encoder).

AgentLog.Write($"agent MAIN entered: args='{string.Join(' ', args)}'");
try
{
    if (args.Length > 0 && args[0].Equals("self-test", StringComparison.OrdinalIgnoreCase))
    {
        bool useH264 = args.Length > 1 && args[1].Equals("h264", StringComparison.OrdinalIgnoreCase);
        string? displayArg = useH264 ? (args.Length > 2 ? args[2] : null) : (args.Length > 1 ? args[1] : null);
        await SelfTest.RunAsync(displayArg, useH264);
        return;
    }

    var options = ParseArgs(args);
    if (options is null)
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Rorrim.Agent --attach <brokerAddr> --session <id> [--token <t>] [--display <n>]");
        Console.WriteLine("  Rorrim.Agent self-test [displayIdx]");
        return;
    }

    var displays = DisplayEnumerator.Enumerate();
    var display = DisplayEnumerator.Resolve(displays, options.DisplayId)
        ?? throw new InvalidOperationException("No displays found.");

    AgentLog.Write($"agent starting: attach={options.BrokerAddress} session={options.SessionId} display='{display.DeviceName}'");
    Console.WriteLine("Attaching to broker...");

    using var attach = new AgentAttachClient(
        options.BrokerAddress!,
        options.SessionId,
        display.DeviceName,
        (d, _) => VideoSourceFactory.Create(d),
        (_, codec) => codec == Codec.H264 ? new OpenH264VideoEncoder() : new JpegVideoEncoder(),
        new InputInjector(new DisplayRect(display.X, display.Y, display.Width, display.Height)),
        options.Token);

    await attach.RunAsync(CancellationToken.None);
}
catch (Exception ex)
{
    AgentLog.Write("UNHANDLED: " + ex);
    Console.WriteLine(ex);
}

// --- helpers ---

AttachOptions? ParseArgs(string[] args)
{
    string? broker = null;
    int session = -1;
    string? display = null;
    string? token = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--attach": if (i + 1 < args.Length) broker = args[++i]; break;
            case "--session": if (i + 1 < args.Length) int.TryParse(args[++i], out session); break;
            case "--display": if (i + 1 < args.Length) display = args[++i]; break;
            case "--token": if (i + 1 < args.Length) token = args[++i]; break;
        }
    }
    if (broker is null || session < 0) return null;
    return new AttachOptions(broker, session, display, token);
}

record AttachOptions(string BrokerAddress, int SessionId, string? DisplayId, string? Token);

static class SelfTest
{
    public static async Task RunAsync(string? outputIndexArg, bool useH264)
    {
        var displays = DisplayEnumerator.Enumerate();
        Console.WriteLine($"Found {displays.Count} display(s):");
        for (int i = 0; i < displays.Count; i++)
            Console.WriteLine($"  [{i}] {displays[i].Description}");

        if (displays.Count == 0)
        {
            Console.WriteLine("No displays found. Ensure an interactive desktop session is active.");
            return;
        }

        int index = 0;
        if (int.TryParse(outputIndexArg, out int parsed) && parsed >= 0 && parsed < displays.Count)
            index = parsed;

        var disp = displays[index];
        Console.WriteLine($"Capturing display {index}: {disp.DeviceName} {disp.Width}x{disp.Height} @({disp.X},{disp.Y})");

        using var source = VideoSourceFactory.Create(disp);
        source.Start();
        Console.WriteLine($"Capture source: {source.Kind}");
        using IVideoEncoder encoder = useH264 ? new OpenH264VideoEncoder() : new JpegVideoEncoder();
        Console.WriteLine($"Encoder: {(useH264 ? "h264 (openh264)" : "jpeg")}");
        string ext = useH264 ? "h264" : "jpg";

        var controller = new StreamController(source, encoder);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int captured = 0;

        await foreach (var frame in controller.Produce().WithCancellation(cts.Token))
        {
            string path = $"snapshot_{index}_{captured++}.{ext}";
            await File.WriteAllBytesAsync(path, frame.Data);
            Console.WriteLine($"  Frame {captured}: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes ({frame.Codec}) -> {path}");
            if (captured >= 3) break;
        }

        Console.WriteLine($"Done. Captured {captured} frames.");
    }
}
