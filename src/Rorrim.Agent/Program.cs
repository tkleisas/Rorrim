using Rorrim.Agent;
using Rorrim.Agent.Broker;
using Rorrim.Agent.Capture;
using Rorrim.Agent.Display;
using Rorrim.Agent.Pipeline;

// Rorrim Agent. Two modes:
//   Rorrim.Agent --attach <brokerAddr> --session <id> [--display <n>]
//       In-session capture: connects back to the broker over gRPC, streams frames, injects input.
//   Rorrim.Agent self-test [displayIdx]
//       Snapshots a display to disk for validation.

AgentLog.Write($"agent MAIN entered: args='{string.Join(' ', args)}'");
try
{
    if (args.Length > 0 && args[0].Equals("self-test", StringComparison.OrdinalIgnoreCase))
    {
        await SelfTest.RunAsync(args.Length > 1 ? args[1] : null);
        return;
    }

    var options = ParseArgs(args);
    if (options is null)
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Rorrim.Agent --attach <brokerAddr> --session <id> [--display <n>]");
        Console.WriteLine("  Rorrim.Agent self-test [displayIdx]");
        return;
    }

    var displayIndex = options.DisplayId ?? 0;
    var displayName = ResolveDisplayName(displayIndex);

    AgentLog.Write($"agent starting: attach={options.BrokerAddress} session={options.SessionId} display='{displayName}'");
    Console.WriteLine("Attaching to broker...");

    using var attach = new AgentAttachClient(
        options.BrokerAddress!,
        options.SessionId,
        displayName,
        () => CreateSource(displayIndex),
        () => new JpegVideoEncoder());

    await attach.RunAsync(CancellationToken.None);
}
catch (Exception ex)
{
    AgentLog.Write("UNHANDLED: " + ex);
    Console.WriteLine(ex);
}

// --- helpers ---

static IVideoSource CreateSource(int displayIndex)
{
    var displays = DisplayEnumerator.Enumerate();
    var d = displays.Count > 0
        ? displays[Math.Min(displayIndex, displays.Count - 1)]
        : throw new InvalidOperationException("No displays found.");
    return new GdiVideoSource(d.X, d.Y, d.Width, d.Height);
}

static string ResolveDisplayName(int displayIndex)
{
    var displays = DisplayEnumerator.Enumerate();
    return displays.Count > 0
        ? displays[Math.Min(displayIndex, displays.Count - 1)].DeviceName
        : "";
}

AttachOptions? ParseArgs(string[] args)
{
    string? broker = null;
    int session = -1;
    int? display = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--attach": if (i + 1 < args.Length) broker = args[++i]; break;
            case "--session": if (i + 1 < args.Length) int.TryParse(args[++i], out session); break;
            case "--display": if (i + 1 < args.Length && int.TryParse(args[++i], out int dd)) display = dd; break;
        }
    }
    if (broker is null || session < 0) return null;
    return new AttachOptions(broker, session, display);
}

record AttachOptions(string BrokerAddress, int SessionId, int? DisplayId);

static class SelfTest
{
    public static async Task RunAsync(string? outputIndexArg)
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

        using var source = new GdiVideoSource(disp.X, disp.Y, disp.Width, disp.Height);
        source.Start();
        using var encoder = new JpegVideoEncoder();

        var controller = new StreamController(source, encoder);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int captured = 0;

        await foreach (var frame in controller.Produce().WithCancellation(cts.Token))
        {
            string path = $"snapshot_{index}_{captured++}.jpg";
            await File.WriteAllBytesAsync(path, frame.Data);
            Console.WriteLine($"  Frame {captured}: {frame.Width}x{frame.Height}, {frame.Data.Length} bytes -> {path}");
            if (captured >= 3) break;
        }

        Console.WriteLine($"Done. Captured {captured} frames.");
    }
}
