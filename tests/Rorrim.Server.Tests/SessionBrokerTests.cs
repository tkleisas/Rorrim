using NSubstitute;
using Rorrim.Server.Broker;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Tests;

public class SessionBrokerTests
{
    private static IClientEndpoint MakeClient(params ClientToServer[] messages)
    {
        var e = Substitute.For<IClientEndpoint>();
        e.ClientId.Returns("client-1");
        e.RequestedDisplayId.Returns((string?)null);
        e.Incoming.Returns(StreamOf(messages));
        e.SendAsync(Arg.Any<ServerToClient>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return e;
    }

    private static IAgentEndpoint MakeAgent(params AgentToServer[] messages)
    {
        var e = Substitute.For<IAgentEndpoint>();
        e.AgentId.Returns("agent-1");
        e.SessionId.Returns(1);
        e.Incoming.Returns(StreamOf(messages));
        e.SendAsync(Arg.Any<ServerToAgent>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return e;
    }

    private static async IAsyncEnumerable<T> StreamOf<T>(params T[] items)
    {
        foreach (var i in items) yield return i;
    }

    [Fact]
    public async Task Relay_DeliversFrames_DownToClient()
    {
        var frame = new Frame { Data = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3 }), IsKeyframe = true, Width = 1920, Height = 1080 };
        var agent = MakeAgent(new AgentToServer { Frame = frame });
        var client = MakeClient();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await client.Received(1).SendAsync(
            Arg.Is<ServerToClient>(m => m.Frame != null && m.Frame.Data.Length == 3 && m.Frame.Width == 1920),
            Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public async Task Relay_ForwardsClientInput_UpToAgent()
    {
        var input = new PointerInput { Move = new MouseMove { X = 0.5, Y = 0.25 } };
        var agent = MakeAgent();
        var client = MakeClient(new ClientToServer { Input = input });
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await agent.Received(1).SendAsync(
            Arg.Is<ServerToAgent>(m => m.Input != null && m.Input.Move != null && m.Input.Move.X == 0.5),
            Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public async Task Relay_StopsWhenClientDisconnects()
    {
        var agent = MakeAgent();
        var client = MakeClient(); // incoming stream ends immediately
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(3000);
        var relayTask = broker.RunAsync(client, agent, cts.Token).AsTask();

        var completed = await Task.WhenAny(relayTask, Task.Delay(2000, cts.Token));
        Assert.Same(relayTask, completed);
    }

    [Fact]
    public async Task Relay_StopsGracefullyOnCancellation()
    {
        var agent = MakeAgent();
        var client = MakeClient();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(100);
        // Cancellation should end the relay cleanly rather than leaking an exception.
        await broker.RunAsync(client, agent, cts.Token);
    }

    [Fact]
    public async Task LockHeartbeat_IsRelayedAsLockedStatusToClient()
    {
        var agent = MakeAgent(new AgentToServer { Heartbeat = new Heartbeat { DesktopLocked = true } });
        var client = MakeClient();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await client.Received(1).SendAsync(
            Arg.Is<ServerToClient>(m => m.Status != null && m.Status.Kind == SessionStatus.Types.StatusKind.Locked),
            Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public async Task PlainHeartbeat_IsDropped()
    {
        var agent = MakeAgent(new AgentToServer { Heartbeat = new Heartbeat { DesktopLocked = false } });
        var client = MakeClient();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await client.DidNotReceive().SendAsync(Arg.Any<ServerToClient>(), Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public async Task AgentHello_DisplayList_IsRelayedToClient()
    {
        var hello = new AgentHello
        {
            DisplayId = """\\.\DISPLAY1""",
            Displays =
            {
                new DisplayInfo { DisplayId = """\\.\DISPLAY1""", Width = 1920, Height = 1080, IsPrimary = true },
                new DisplayInfo { DisplayId = """\\.\DISPLAY2""", Width = 2560, Height = 1440 }
            }
        };
        var agent = MakeAgent(new AgentToServer { Hello = hello });
        var client = MakeClient();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await client.Received(1).SendAsync(
            Arg.Is<ServerToClient>(m => m.Displays != null && m.Displays.Displays.Count == 2),
            Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public async Task ClientHello_IsRelayedAsSwitchDisplayCommand()
    {
        var client = MakeClient(new ClientToServer { Hello = new Hello { DisplayId = """\\.\DISPLAY2""" } });
        var agent = MakeAgent();
        var broker = new SessionBroker();

        using var cts = new CancellationTokenSource(2000);
        var relay = broker.RunAsync(client, agent, cts.Token).AsTask();

        await Task.Delay(200);
        await agent.Received(1).SendAsync(
            Arg.Is<ServerToAgent>(m => m.Command != null
                && m.Command.Kind == AgentCommand.Types.CommandKind.SwitchDisplay
                && m.Command.DisplayId == """\\.\DISPLAY2"""),
            Arg.Any<CancellationToken>());
        cts.Cancel();
    }

    [Fact]
    public void MapAgentToClient_DropsFramelessAndHeartbeatMessages()
    {
        Assert.Null(SessionBroker.MapAgentToClient(new AgentToServer()));
        Assert.Null(SessionBroker.MapAgentToClient(new AgentToServer { Heartbeat = new Heartbeat() }));
        Assert.NotNull(SessionBroker.MapAgentToClient(new AgentToServer { Heartbeat = new Heartbeat { DesktopLocked = true } }));
    }
}
