using Rorrim.Shared.Contracts;

namespace Rorrim.Server.Broker;

/// <summary>
/// Default <see cref="ISessionBroker"/>. Runs the bidirectional relay between a client and an agent:
/// agent frames are pushed down to the client as <see cref="ServerToClient"/>, client input is pushed
/// up to the agent as <see cref="ServerToAgent"/>. The relay ends when either side's incoming stream
/// completes, is cancelled, or faults.
/// </summary>
public sealed class SessionBroker : ISessionBroker
{
    public async ValueTask RunAsync(IClientEndpoint client, IAgentEndpoint agent, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Push agent frames -> client.
        Task agentToClient = PumpAsync(
            agent.Incoming,
            frame => new ServerToClient { Frame = frame.Frame },
            client.SendAsync,
            cts.Token);

        // Push client input -> agent.
        Task clientToAgent = PumpAsync(
            client.Incoming,
            input => new ServerToAgent { Input = input.Input },
            agent.SendAsync,
            cts.Token);

        // Wait for either to finish (client leaving ends the session).
        await Task.WhenAny(agentToClient, clientToAgent).ConfigureAwait(false);
        cts.Cancel();

        try { await Task.WhenAll(agentToClient, clientToAgent).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch
        {
            // Swallow downstream relay errors on teardown; the streams are being abandoned.
        }
    }

    // A generic two-way pump. `select` converts an agent/client message into the peer's message type.
    private static async Task PumpAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> incoming,
        Func<TIn, TOut> select,
        Func<TOut, CancellationToken, ValueTask> send,
        CancellationToken ct)
    {
        await foreach (var item in incoming.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await send(select(item), ct).ConfigureAwait(false);
        }
    }
}
