using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace Rorrim.Server;

/// <summary>
/// Registers the broker as a Windows service running as LocalSystem. LocalSystem has the
/// SeTcbPrivilege required by WTSQueryUserToken so the broker can inject the agent into an
/// interactive user session.
///
/// Installation uses the CreateService Win32 API rather than sc.exe: sc's binPath= quoting mangles
/// command lines that contain quoted paths, and direct registry writes are invisible to the SCM
/// until reboot. CreateService takes the ImagePath verbatim (no escaping) and registers instantly.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "Rorrim Server";
    private const string DisplayName = "Rorrim Remote Control";

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_DELETE = 0x00010000;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;

    public static void Install(IReadOnlyList<string> args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Rorrim.Server.exe");
        // The ImagePath is passed verbatim to CreateService; quote only tokens that need it.
        string imagePath = BuildCommandLine(new[] { exe }.Concat(args));

        StopServiceIfExists();

        IntPtr hSCM = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (hSCM == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        try
        {
            DeleteExistingService(hSCM);

            IntPtr hSvc = CreateServiceW(
                hSCM,
                ServiceName,
                DisplayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                imagePath,
                null,
                IntPtr.Zero,
                null,
                "LocalSystem",
                null);
            if (hSvc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 1073) // ERROR_SERVICE_EXISTS — should not happen after the delete above
                    throw new InvalidOperationException($"Service '{ServiceName}' already exists; delete it first.");
                throw new Win32Exception(err, "CreateService failed");
            }
            CloseServiceHandle(hSvc);
        }
        finally
        {
            CloseServiceHandle(hSCM);
        }

        ConfigureRestartOnCrash();
        StartAndWait();
    }

    public static void Uninstall()
    {
        StopServiceIfExists();
        IntPtr hSCM = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (hSCM == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        try
        {
            DeleteExistingService(hSCM);
        }
        finally
        {
            CloseServiceHandle(hSCM);
        }
    }

    private static void StopServiceIfExists()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (InvalidOperationException)
        {
            // Service not installed — nothing to stop.
        }
    }

    private static void DeleteExistingService(IntPtr hSCM)
    {
        IntPtr hSvc = OpenServiceW(hSCM, ServiceName, SERVICE_DELETE);
        if (hSvc == IntPtr.Zero)
            return; // not installed
        try
        {
            if (!DeleteService(hSvc))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1060) // ERROR_SERVICE_DOES_NOT_EXIST
                    throw new Win32Exception(err, "DeleteService failed");
            }
        }
        finally
        {
            CloseServiceHandle(hSvc);
        }
    }

    private static void ConfigureRestartOnCrash()
    {
        // Restart 5s after a crash, indefinitely (failure counter resets after a day).
        Run("failure", ServiceName, "reset=", "86400",
            "actions=", "restart/5000/restart/5000/restart/5000");
    }

    private static void StartAndWait()
    {
        using var sc = new ServiceController(ServiceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Quotes each element that contains spaces so the result parses back into the same tokens
    /// (paths are frequently "C:\Program Files\..." or under the user profile).
    /// </summary>
    public static string BuildCommandLine(IEnumerable<string> parts) =>
        string.Join(' ', parts.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));

    private static void Run(params string[] arguments)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            Console.WriteLine($"[service] sc {arguments.FirstOrDefault()} exit={p.ExitCode}\n  {stdout}\n  {stderr}");
    }

    // --- P/Invoke (SCM) ---

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
