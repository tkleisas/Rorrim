using System.Collections.Concurrent;

namespace Rorrim.Server.Agents;

/// <summary>
/// Holds the one-time agent registration tokens the coordinator issued per session. An agent may
/// only register on the loopback Attach endpoint if it can present the token that was passed to it
/// at launch — this keeps unrelated local processes from registering as an agent and receiving
/// injected input.
/// </summary>
public interface IAgentTokenStore
{
    /// <summary>Records the expected token for a session (replaces any previous one).</summary>
    void Set(int sessionId, string token);

    /// <summary>Returns true when <paramref name="token"/> matches the expected token for the session.</summary>
    bool TryValidate(int sessionId, string? token);

    /// <summary>Invalidates the token for a session (agent stopped / pairing ended).</summary>
    void Remove(int sessionId);
}

/// <summary>Thread-safe in-memory implementation.</summary>
public sealed class InMemoryAgentTokenStore : IAgentTokenStore
{
    private readonly ConcurrentDictionary<int, string> _tokens = new();

    public void Set(int sessionId, string token) =>
        _tokens[sessionId] = token;

    public bool TryValidate(int sessionId, string? token) =>
        !string.IsNullOrEmpty(token)
        && _tokens.TryGetValue(sessionId, out var expected)
        && FixedTimeEquals(expected, token);

    public void Remove(int sessionId) =>
        _tokens.TryRemove(sessionId, out _);

    /// <summary>Compares without early exit so token checks don't leak length/prefix timing.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        int diff = 0;
        for (int i = 0; i < ab.Length; i++)
            diff |= ab[i] ^ bb[i];
        return diff == 0;
    }
}
