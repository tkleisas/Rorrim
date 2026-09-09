using Grpc.Core;
using NSubstitute;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;
using Rorrim.Server.Services;
using Rorrim.Shared.Contracts;
namespace Rorrim.Server.Tests;

public class AgentServiceAuthTests
{
    private static ServerCallContext MakeContext(Metadata headers)
    {
        var ctx = Substitute.ForPartsOf<ServerCallContext>();
        ctx.RequestHeaders.Returns(headers);
        ctx.Peer.Returns("ipv4:127.0.0.1:50000");
        return ctx;
    }

    private static (IAgentRegistry registry, IAgentTokenStore store, RorrimAgentService service) Make()
    {
        var registry = Substitute.For<IAgentRegistry>();
        var store = new InMemoryAgentTokenStore();
        return (registry, store, new RorrimAgentService(registry, store));
    }

    [Fact]
    public async Task Attach_WithoutSessionHeader_IsRejected()
    {
        var (_, store, service) = Make();
        var ctx = MakeContext(new Metadata());
        var reader = Substitute.For<IAsyncStreamReader<AgentToServer>>();
        var writer = Substitute.For<IServerStreamWriter<ServerToAgent>>();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Attach(reader, writer, ctx));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Attach_WithoutToken_IsRejected()
    {
        var (_, store, service) = Make();
        var ctx = MakeContext(new Metadata { { "x-rorrim-session", "1" } });
        var reader = Substitute.For<IAsyncStreamReader<AgentToServer>>();
        var writer = Substitute.For<IServerStreamWriter<ServerToAgent>>();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Attach(reader, writer, ctx));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Attach_WithWrongToken_IsRejected()
    {
        var (registry, store, service) = Make();
        store.Set(1, "expected-token");
        var ctx = MakeContext(new Metadata
        {
            { "x-rorrim-session", "1" },
            { "x-rorrim-token", "stolen-token" }
        });
        var reader = Substitute.For<IAsyncStreamReader<AgentToServer>>();
        var writer = Substitute.For<IServerStreamWriter<ServerToAgent>>();

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Attach(reader, writer, ctx));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        registry.DidNotReceive().Register(1, Arg.Any<IAgentEndpoint>());
    }

    [Fact]
    public async Task Attach_WithValidToken_Registers_AndInvalidatesOnEnd()
    {
        var (registry, store, service) = Make();
        store.Set(1, "good-token");
        var ctx = MakeContext(new Metadata
        {
            { "x-rorrim-session", "1" },
            { "x-rorrim-token", "good-token" }
        });
        var reader = Substitute.For<IAsyncStreamReader<AgentToServer>>();
        var writer = Substitute.For<IServerStreamWriter<ServerToAgent>>();

        IAgentEndpoint? registered = null;
        registry.Register(1, Arg.Do<IAgentEndpoint>(e => registered = e));

        var task = service.Attach(reader, writer, ctx); // blocks on WaitUntilCompletedAsync
        await Task.Delay(100);
        registry.Received(1).Register(1, Arg.Any<IAgentEndpoint>());
        Assert.NotNull(registered);

        // End the pairing: the coordinator signals completion.
        registered!.MarkCompleted();
        await task;

        // The token must be invalidated once the agent call ends.
        Assert.False(store.TryValidate(1, "good-token"));
    }
}
