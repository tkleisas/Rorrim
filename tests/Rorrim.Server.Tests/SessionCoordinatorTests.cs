using System.Collections.Concurrent;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Server.Services;
using Rorrim.Server.Sessions;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Tests;

public class SessionCoordinatorTests
{
    private static readonly SessionInfo TestSession = new(1, "user", "DOMAIN", SessionState.Active);

    private static async IAsyncEnumerable<T> EmptyStream<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static IClientEndpoint MakeClient(string? displayId)
    {
        var e = Substitute.For<IClientEndpoint>();
        e.ClientId.Returns("client-1");
        e.RequestedDisplayId.Returns(displayId);
        e.Incoming.Returns(EmptyStream<ClientToServer>());
        e.SendAsync(Arg.Any<ServerToClient>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return e;
    }

    private static IAgentEndpoint MakeAgent()
    {
        var e = Substitute.For<IAgentEndpoint>();
        e.AgentId.Returns("agent-1");
        e.SessionId.Returns(1);
        e.Incoming.Returns(EmptyStream<AgentToServer>());
        e.SendAsync(Arg.Any<ServerToAgent>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return e;
    }

    private static SessionCoordinator MakeCoordinator(
        ISessionProvider sessions,
        IAgentProcessLauncher launcher,
        IAgentRegistry registry,
        ISessionBroker broker,
        TimeSpan? attachTimeout = null,
        IAgentTokenStore? tokens = null) =>
        new(sessions, launcher, registry, broker,
            tokens ?? new InMemoryAgentTokenStore(),
            "http://localhost:50052",
            attachTimeout ?? TimeSpan.FromSeconds(10));

    [Fact]
    public async Task ClientHello_DisplayId_IsPassedToAgentLaunch()
    {
        var agent = MakeAgent();
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process);

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null, agent); // nothing attached, then the agent registers

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker);

        await coordinator.HandleClientAsync(MakeClient("2"), CancellationToken.None);

        await launcher.Received(1).LaunchAsync(1, AgentLaunchMode.Elevated,
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--display") && a.Contains("2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingDisplayId_LaunchesDisplayZero()
    {
        var agent = MakeAgent();
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process);

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null, agent);

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker);

        await coordinator.HandleClientAsync(MakeClient(null), CancellationToken.None);

        await launcher.Received(1).LaunchAsync(1, AgentLaunchMode.Elevated,
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--display") && a.Contains("0")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelayEnd_KillsAgentItLaunched()
    {
        var agent = MakeAgent();
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process);

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null, agent);

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker);

        await coordinator.HandleClientAsync(MakeClient("0"), CancellationToken.None);

        process.Received(1).Kill();
        registry.Received(1).Unregister(1, agent);
    }

    [Fact]
    public async Task ReusesRunningAgent_WithoutLaunchingOrKilling()
    {
        var agent = MakeAgent();

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns(agent);

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker);

        await coordinator.HandleClientAsync(MakeClient("1"), CancellationToken.None);

        await launcher.DidNotReceiveWithAnyArgs().LaunchAsync(default, default, null!, default);
        registry.Received(1).Unregister(1, agent);
    }

    [Fact]
    public async Task AttachTimeout_ReportsStatus_AndKillsLaunchedProcess()
    {
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process);

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null);

        var broker = Substitute.For<ISessionBroker>();
        var client = MakeClient("0");

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker, TimeSpan.FromMilliseconds(300));

        await coordinator.HandleClientAsync(client, CancellationToken.None);

        await client.Received().SendAsync(
            Arg.Is<ServerToClient>(m => m.Status != null && m.Status.Message.Contains("Agent failed to attach")),
            Arg.Any<CancellationToken>());
        process.Received(1).Kill();
    }

    [Fact]
    public async Task ConcurrentClients_AreSerializedPerSession()
    {
        var agent = MakeAgent();
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process)
            .AndDoes(_ => order.Enqueue("launch"));

        var registry = Substitute.For<IAgentRegistry>();
        // Get-call sequence: client1 find (null), client1 wait-for-attach (agent),
        // client1 unregister-check (null), client2 find (null), client2 wait-for-attach (agent),
        // client2 unregister-check (agent).
        registry.Get(1).Returns(
            (IAgentEndpoint?)null, agent, (IAgentEndpoint?)null,
            (IAgentEndpoint?)null, agent, agent);

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(_ => SlowRelay(order));

        static async ValueTask SlowRelay(ConcurrentQueue<string> order)
        {
            order.Enqueue("relay-start");
            await Task.Delay(200);
            order.Enqueue("relay-end");
        }

        var coordinator = MakeCoordinator(sessions, launcher, registry, broker);

        var first = coordinator.HandleClientAsync(MakeClient("0"), CancellationToken.None);
        await Task.Delay(50); // let the first client acquire the session
        var second = coordinator.HandleClientAsync(MakeClient("1"), CancellationToken.None);

        await Task.WhenAll(first, second);

        // The second client's launch must happen only after the first relay ended.
        var sequence = order.ToList();
        int firstRelayEnd = sequence.IndexOf("relay-end");
        int secondLaunch = sequence.LastIndexOf("launch");
        Assert.Equal(2, sequence.Count(s => s == "launch"));
        Assert.True(firstRelayEnd < secondLaunch,
            $"second launch must follow first relay end; got: {string.Join(',', sequence)}");
    }
}
