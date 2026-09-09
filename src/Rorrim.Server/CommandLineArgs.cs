namespace Rorrim.Server;

/// <summary>
/// Minimal command-line parser. Accepts both "--name value" and "--name=value" forms; flags like
/// --test-mode take no value.
/// </summary>
public static class CommandLineArgs
{
    public static bool Has(IReadOnlyList<string> args, string name)
    {
        foreach (var a in args)
        {
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsNameValue(a, name))
                return true;
        }
        return false;
    }

    public static string? GetString(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            var (ok, value) = TrySplit(a, name);
            if (ok) return value;
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }
        return null;
    }

    public static int GetInt(IReadOnlyList<string> args, string name, int def) =>
        int.TryParse(GetString(args, name), out int v) ? v : def;

    private static bool IsNameValue(string arg, string name) => TrySplit(arg, name).ok;

    private static (bool ok, string value) TrySplit(string arg, string name)
    {
        int idx = arg.IndexOf('=', StringComparison.OrdinalIgnoreCase);
        if (idx > 0 && arg[..idx].Equals(name, StringComparison.OrdinalIgnoreCase))
            return (true, arg[(idx + 1)..]);
        return (false, "");
    }
}
