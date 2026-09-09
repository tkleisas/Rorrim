using NSubstitute;
using Rorrim.Server.Agents;
using Rorrim.Server.Broker;

namespace Rorrim.Server.Tests;

public class AgentRegistryTests
{
    private static IAgentEndpoint MakeEndpoint(string id)
    {
        var e = Substitute.For<IAgentEndpoint>();
        e.AgentId.Returns(id);
        return e;
    }

    [Fact]
    public void Register_NewestWins_PreviousReturned()
    {
        var registry = new AgentRegistry();
        var first = MakeEndpoint("agent-1");
        var second = MakeEndpoint("agent-2");

        Assert.Null(registry.Register(1, first));
        Assert.Same(first, registry.Register(1, second));

        // The fresh registration must be the one clients pair with.
        Assert.Same(second, registry.Get(1));
    }

    [Fact]
    public void Unregister_RemovesOnlyTheExactEndpoint()
    {
        var registry = new AgentRegistry();
        var first = MakeEndpoint("agent-1");
        var second = MakeEndpoint("agent-2");

        registry.Register(1, first);
        registry.Register(1, second);

        // The stale endpoint's unregister must not evict the current registration.
        registry.Unregister(1, first);
        Assert.Same(second, registry.Get(1));

        registry.Unregister(1, second);
        Assert.Null(registry.Get(1));
    }
}
