using System.Diagnostics;

namespace Rorrim.Server;

/// <summary>
/// Registers the broker as a Windows service running as LocalSystem. LocalSystem has the
/// SeTcbPrivilege required by WTSQueryUserToken so the broker can inject the agent into an
/// interactive user session.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "Rorrim Server";

    public static void Install(string extraArgs)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Rorrim.Server.exe");
        string command = $"\"{exe}\" {extraArgs.Trim()}";

        Run($"sc.exe create \"{ServiceName}\" binPath= \"{command}\" start= auto DisplayName= \"Rorrim Remote Control\"");
        Run($"sc.exe config \"{ServiceName}\" obj= LocalSystem");
    }

    public static void Uninstall()
    {
        Run($"sc.exe stop \"{ServiceName}\"");
        Run($"sc.exe delete \"{ServiceName}\"");
    }

    private static void Run(string commandLine)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + commandLine)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (p.ExitCode != 0)
            Console.WriteLine($"[service] exit={p.ExitCode}\n  {stdout}\n  {stderr}");
        else
            Console.WriteLine($"[service] ok: {stdout}".Trim());
    }
}
