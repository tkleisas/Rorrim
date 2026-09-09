using NSubstitute;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Server.Services;
using Rorrim.Server.Sessions;
using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Tests;

public class AgentTokenStoreTests
{
    [Fact]
    public void Set_ThenValidate_WithSameToken_Succeeds()
    {
        var store = new InMemoryAgentTokenStore();
        store.Set(1, "abc123");
        Assert.True(store.TryValidate(1, "abc123"));
    }

    [Fact]
    public void Validate_WithWrongOrMissingToken_Fails()
    {
        var store = new InMemoryAgentTokenStore();
        store.Set(1, "abc123");

        Assert.False(store.TryValidate(1, "wrong"));
        Assert.False(store.TryValidate(1, null));
        Assert.False(store.TryValidate(1, ""));
        Assert.False(store.TryValidate(2, "abc123")); // other session
        Assert.False(store.TryValidate(99, "abc123")); // unknown session
    }

    [Fact]
    public void Remove_InvalidatesToken()
    {
        var store = new InMemoryAgentTokenStore();
        store.Set(1, "abc123");
        store.Remove(1);
        Assert.False(store.TryValidate(1, "abc123"));
    }

    [Fact]
    public void Set_ReplacesPreviousToken()
    {
        var store = new InMemoryAgentTokenStore();
        store.Set(1, "old");
        store.Set(1, "new");
        Assert.False(store.TryValidate(1, "old"));
        Assert.True(store.TryValidate(1, "new"));
    }
}

public class SessionCoordinatorTokenTests
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

    [Fact]
    public async Task Launch_PassesRegistrationToken_AndStoreAcceptsIt()
    {
        var agent = MakeAgent();
        var process = Substitute.For<IAgentProcess>();
        process.ProcessId.Returns(42);

        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        IReadOnlyList<string>? launchArgs = null;
        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(process)
            .AndDoes(ci => launchArgs = ci.ArgAt<IReadOnlyList<string>>(2));

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null, agent);

        var broker = Substitute.For<ISessionBroker>();
        broker.RunAsync(Arg.Any<IClientEndpoint>(), Arg.Any<IAgentEndpoint>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        // Recording store: the token must be issued for the session, match the launch args, and be
        // invalidated when the pairing ends.
        string? storedToken = null;
        var store = Substitute.For<IAgentTokenStore>();
        store.When(s => s.Set(1, Arg.Any<string>())).Do(ci => storedToken = ci.ArgAt<string>(1));

        var coordinator = new SessionCoordinator(sessions, launcher, registry, broker, store,
            "http://localhost:50052", TimeSpan.FromSeconds(10));

        await coordinator.HandleClientAsync(MakeClient("0"), CancellationToken.None);

        Assert.NotNull(launchArgs);
        int tokenIdx = launchArgs!.ToList().IndexOf("--token");
        Assert.True(tokenIdx >= 0 && tokenIdx + 1 < launchArgs.Count, "launch args must carry --token <value>");
        string token = launchArgs[tokenIdx + 1];
        Assert.True(token.Length >= 32, "token should have meaningful entropy");

        Assert.NotNull(storedToken);
        Assert.Equal(token, storedToken);

        // The pairing ended (broker relay completed), so the token must be invalidated.
        store.Received(1).Remove(1);
    }

    [Fact]
    public async Task LaunchFailure_ClearsToken()
    {
        var sessions = Substitute.For<ISessionProvider>();
        sessions.GetActiveInteractiveSession().Returns(TestSession);

        var launcher = Substitute.For<IAgentProcessLauncher>();
        launcher.LaunchAsync(Arg.Any<int>(), Arg.Any<AgentLaunchMode>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromException<IAgentProcess>(new InvalidOperationException("boom")));

        var registry = Substitute.For<IAgentRegistry>();
        registry.Get(1).Returns((IAgentEndpoint?)null);

        var broker = Substitute.For<ISessionBroker>();
        var store = new InMemoryAgentTokenStore();
        var coordinator = new SessionCoordinator(sessions, launcher, registry, broker, store,
            "http://localhost:50052", TimeSpan.FromSeconds(1));

        var client = MakeClient("0");
        await coordinator.HandleClientAsync(client, CancellationToken.None);

        // The failed launch must not leave a usable token behind.
        Assert.False(store.TryValidate(1, "anything"));
        await client.Received().SendAsync(
            Arg.Is<ServerToClient>(m => m.Status != null && m.Status.Message.Contains("Agent launch failed")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReusePath_DoesNotSetToken()
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

        var store = new InMemoryAgentTokenStore();
        var coordinator = new SessionCoordinator(sessions, launcher, registry, broker, store,
            "http://localhost:50052", TimeSpan.FromSeconds(10));

        await coordinator.HandleClientAsync(MakeClient("1"), CancellationToken.None);

        // No launch happened, so no token was issued for the session.
        Assert.False(store.TryValidate(1, "guess"));
    }
}
