using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Rorrim.Shared.Contracts;

// Headless gRPC client for end-to-end diagnosis: connects like the Avalonia client, prints every
// message the broker delivers, and reports frame throughput.
//
//   Rorrim.Probe [address] [seconds] [displayId]

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

string address = args.Length > 0 ? args[0] : "http://localhost:50053";
int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 15;
string displayId = args.Length > 2 ? args[2] : "0";

using var channel = GrpcChannel.ForAddress(address);
var client = new RorrimClient.RorrimClientClient(channel);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var sw = Stopwatch.StartNew();
int frames = 0;
long lastFrameMs = 0;
long lastBytes = 0;

try
{
    using var call = client.StreamDesktop(cancellationToken: cts.Token);
    await call.RequestStream.WriteAsync(new ClientToServer { Hello = new Hello { DisplayId = displayId } });

    await foreach (var msg in call.ResponseStream.ReadAllAsync(cts.Token))
    {
        if (msg.Frame is { } frame)
        {
            frames++;
            long gap = sw.ElapsedMilliseconds - lastFrameMs;
            lastFrameMs = sw.ElapsedMilliseconds;
            lastBytes = frame.Data.Length;
            if (frames <= 3 || frames % 10 == 0)
                Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] frame {frames}: codec={frame.Codec} {frame.Width}x{frame.Height}, {frame.Data.Length} B (+{gap} ms)");
        }
        else if (msg.Displays is { } displays)
            Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] display list: {displays.Displays.Count} display(s)");
        else if (msg.Status is { } status)
            Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] status: {status.Kind} {status.Message}");
        else
            Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] (empty message)");
    }
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
{
    // expected on timeout
}
catch (OperationCanceledException) { }

Console.WriteLine($"done: {frames} frame(s) in {seconds}s, last frame {lastBytes} B");
return frames > 1 ? 0 : 1;
