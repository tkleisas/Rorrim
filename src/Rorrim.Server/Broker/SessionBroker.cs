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

        // Push agent messages -> client (frames, hello display list, lock heartbeats).
        Task agentToClient = PumpAsync(
            agent.Incoming,
            MapAgentToClient,
            client.SendAsync,
            cts.Token);

        // Push client messages -> agent (input, hello as display switch).
        Task clientToAgent = PumpAsync(
            client.Incoming,
            MapClientToAgent,
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

    // A generic two-way pump. `select` converts an agent/client message into the peer's message type;
    // it returns null for messages with no peer representation, which are dropped.
    private static async Task PumpAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> incoming,
        Func<TIn, TOut?> select,
        Func<TOut, CancellationToken, ValueTask> send,
        CancellationToken ct) where TOut : class
    {
        await foreach (var item in incoming.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var output = select(item);
            if (output is null)
                continue;
            await send(output, ct).ConfigureAwait(false);
        }
    }

    internal static ServerToClient? MapAgentToClient(AgentToServer msg)
    {
        if (msg.Frame is { } frame)
            return new ServerToClient { Frame = frame };

        // A heartbeat reporting a locked desktop becomes a LOCKED status for the client; plain
        // keep-alive heartbeats are dropped.
        if (msg.Heartbeat is { } hb && hb.DesktopLocked)
            return new ServerToClient
            {
                Status = new SessionStatus
                {
                    Kind = SessionStatus.Types.StatusKind.Locked,
                    Message = "The desktop is locked on the host."
                }
            };

        // The agent's hello carries the display list for the client's UI.
        if (msg.Hello is { } hello && hello.Displays.Count > 0)
            return new ServerToClient
            {
                Displays = new DisplayListReply { Displays = { hello.Displays } }
            };

        return null;
    }

    internal static ServerToAgent? MapClientToAgent(ClientToServer msg)
    {
        if (msg.Input is { } input)
            return new ServerToAgent { Input = input };

        // A Hello (re)selects the display: forward as a display-switch command.
        if (msg.Hello is { } hello && !string.IsNullOrWhiteSpace(hello.DisplayId))
            return new ServerToAgent
            {
                Command = new AgentCommand
                {
                    Kind = AgentCommand.Types.CommandKind.SwitchDisplay,
                    DisplayId = hello.DisplayId
                }
            };

        return null;
    }
}
