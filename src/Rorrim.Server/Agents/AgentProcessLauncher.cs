using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rorrim.Server.Agents;
/// <summary>
/// Launches the Agent process inside a user's interactive logon session and (optionally) elevated to
/// High Integrity so it can capture and control elevated windows. The key trick: a LocalSystem
/// service obtains the session user token via WTSQueryUserToken, raises its Integrity Level to
/// HighIL with TokenIntegrityLevel, then CreateProcessAsUser — all without a UAC prompt.
/// </summary>
public sealed class AgentProcessLauncher : IAgentProcessLauncher
{
    private readonly string _agentExecutablePath;
    private readonly IReadOnlyDictionary<uint, SafeAccessTokenHandle> _sessionTokens;

    /// <param name="agentExecutablePath">Full path to Rorrim.Agent.exe.</param>
    /// <param name="sessionTokens">
    /// Optional pre-opened session tokens (used in tests). When empty (normal operation) the launcher
    /// queries tokens from WTS on demand.
    /// </param>
    public AgentProcessLauncher(string agentExecutablePath, IReadOnlyDictionary<uint, SafeAccessTokenHandle>? sessionTokens = null)
    {
        _agentExecutablePath = agentExecutablePath;
        _sessionTokens = sessionTokens ?? new Dictionary<uint, SafeAccessTokenHandle>();
    }

    public async Task<IAgentProcess> LaunchAsync(int sessionId, AgentLaunchMode mode, IReadOnlyList<string> args, CancellationToken ct)
    {
        using SafeAccessTokenHandle userToken = await GetSessionUserTokenAsync((uint)sessionId, ct);

        int hr = DuplicateTokenEx(
            userToken, 0x02000000 /*TOKEN_ALL_ACCESS*/, IntPtr.Zero,
            2 /*SecurityImpersonation*/,
            1 /*TokenPrimary*/,
            out SafeAccessTokenHandle primaryToken);
        if (hr == 0)
            ThrowWin32("DuplicateTokenEx", hr);

        try
        {
            if (mode == AgentLaunchMode.Elevated)
            {
                hr = RaiseIntegrityToHigh(primaryToken);
                if (hr == 0)
                {
                    // SetTokenInformation(TokenIntegrityLevel) requires SeTcbPrivilege held/enabled in
                    // the effective token. When that's unavailable, fall back to the token's natural
                    // (Medium) integrity so the agent still launches and can capture the desktop.
                    BrokerLog.Write($"[launch] elevation unavailable (0x{Marshal.GetLastWin32Error():X8}); launching at token integrity");
                }
            }

            string commandLine = BuildCommandLine(_agentExecutablePath, args);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var pi = new PROCESS_INFORMATION();

            // Build the user's environment block so the launched .NET process gets correct env vars.
            IntPtr env = IntPtr.Zero;
            CreateEnvironmentBlock(out env, userToken, false);
            try
            {
                var si2 = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "winsta0\\default"
                };
                hr = CreateProcessAsUser(
                    primaryToken,
                    null!,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    (uint)CreateProcessFlags.UnicodeEnvironment | (uint)CreateProcessFlags.NewConsole,
                    env,
                    Path.GetDirectoryName(_agentExecutablePath)!,
                    ref si2,
                    out pi);

                if (hr == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    BrokerLog.Write($"[launch] CreateProcessAsUser FAILED err=0x{err:X8} ({err})");
                    ThrowWin32("CreateProcessAsUser", hr);
                }
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            }
            BrokerLog.Write($"[launch] CreateProcessAsUser OK pid={pi.dwProcessId}");

            return new LaunchedAgentProcess(pi.hProcess, pi.dwProcessId);
        }
        finally
        {
            primaryToken.Dispose();
        }
    }

    private async Task<SafeAccessTokenHandle> GetSessionUserTokenAsync(uint sessionId, CancellationToken ct)
    {
        if (_sessionTokens.TryGetValue(sessionId, out var cached))
            return cached;

        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            int hr = WTSQueryUserToken(sessionId, out SafeAccessTokenHandle token);
            if (hr == 0)
                ThrowWin32($"WTSQueryUserToken(session {sessionId})", hr);
            return token;
        }, ct).ConfigureAwait(false);
    }

    private static int RaiseIntegrityToHigh(SafeAccessTokenHandle token)
    {
        // SetTokenInformation(TokenIntegrityLevel) on a lower token requires SeTcbPrivilege (or
        // SeIncreaseQuotaPrivilege). A LocalSystem service has these but they may be disabled in the
        // process token; enable them first.
        EnablePrivilege("SeTcbPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        // CreateProcessWithTokenW needs SeImpersonatePrivilege in the calling process token.
        EnablePrivilege("SeImpersonatePrivilege");

        IntPtr sid = IntPtr.Zero;
        try
        {
            uint len = 0;
            CreateWellKnownSid(WellKnownSidType.HighLabelSid, IntPtr.Zero, IntPtr.Zero, ref len);
            sid = Marshal.AllocHGlobal((int)len);
            if (!CreateWellKnownSid(WellKnownSidType.HighLabelSid, IntPtr.Zero, sid, ref len))
                throw new Win32Exception();
            bool ok = ConvertSidToStringSid(sid, out IntPtr str);
            if (ok) BrokerLog.Write($"[launch] HighLabelSid = {Marshal.PtrToStringUni(str)} (len={len})");

            var tki = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x20 } };
            IntPtr tkiPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TOKEN_MANDATORY_LABEL>());
            try
            {
                BrokerLog.Write($"[launch] TOKEN_MANDATORY_LABEL size={Marshal.SizeOf<TOKEN_MANDATORY_LABEL>()}");
                Marshal.StructureToPtr(tki, tkiPtr, false);
                return SetTokenInformation(token, TokenInfoClass.TokenIntegrityLevel, tkiPtr, (uint)Marshal.SizeOf<TOKEN_MANDATORY_LABEL>());
            }
            finally
            {
                Marshal.FreeHGlobal(tkiPtr);
            }
        }
        finally
        {
            if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
        }
    }

    private static string BuildCommandLine(string exe, IReadOnlyList<string> args) =>
        string.Join(' ', args.Prepend("\"" + exe + "\""));

    /// <summary>
    /// Enables a privilege on the current process token (e.g. SeTcbPrivilege on a LocalSystem service).
    /// When running as LocalSystem the privilege is present but disabled; it must be enabled before
    /// SetTokenInformation can raise another token's integrity.
    /// </summary>
    private static void EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out SafeAccessTokenHandle token))
        {
            BrokerLog.Write($"[launch] OpenProcessToken failed err={Marshal.GetLastWin32Error()}");
            return;
        }
        using (token)
        {
            var luid = new LUID();
            if (!LookupPrivilegeValue(null, privilege, out luid))
            {
                BrokerLog.Write($"[launch] LookupPrivilegeValue failed for {privilege} err={Marshal.GetLastWin32Error()}");
                return;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new[] { new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = 0x2 } }
            };
            int size = Marshal.SizeOf<TOKEN_PRIVILEGES>();
            if (!AdjustTokenPrivileges(token, false, ref tp, (uint)size, IntPtr.Zero, IntPtr.Zero))
                BrokerLog.Write($"[launch] AdjustTokenPrivileges call failed for {privilege} err={Marshal.GetLastWin32Error()}");
            else
                BrokerLog.Write($"[launch] enabled {privilege} (err={Marshal.GetLastWin32Error()})");
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public LUID_AND_ATTRIBUTES[] Privileges; }

    private static void ThrowWin32(string op, int hr)
    {
        int err = hr != 0 ? hr : Marshal.GetLastWin32Error();
        throw new Win32Exception(err, $"{op} failed (0x{err:X8}): {GetErrorMessage(err)}");
    }

    private static string GetErrorMessage(int code)
    {
        IntPtr buf = IntPtr.Zero;
        int len = FormatMessage(0x00001000 | 0x00000200 /*FORMAT_MESSAGE_ALLOCATE_BUFFER|FROM_SYSTEM*/,
            IntPtr.Zero, (uint)code, 0, ref buf, 0, IntPtr.Zero);
        try
        {
            return len > 0 && buf != IntPtr.Zero
                ? (Marshal.PtrToStringUni(buf) ?? "").TrimEnd('\r', '\n')
                : $"unknown(0x{code:X8})";
        }
        finally
        {
            if (buf != IntPtr.Zero) LocalFree(buf);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int FormatMessage(uint dwFlags, IntPtr lpSource, uint dwMessageId, uint dwLanguageId, ref IntPtr lpBuffer, uint nSize, IntPtr arguments);

    [Flags]
    private enum CreateProcessFlags : uint
    {
        None = 0,
        UnicodeEnvironment = 0x00000400, // CREATE_UNICODE_ENVIRONMENT
        NewConsole = 0x00000010          // CREATE_NEW_CONSOLE
    }

    private enum TokenInfoClass
    {
        TokenIntegrityLevel = 25
    }

    private enum WellKnownSidType
    {
        HighLabelSid = 34
    }

    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    private sealed class LaunchedAgentProcess : IAgentProcess
    {
        private readonly IntPtr _processHandle;
        private readonly Task<bool> _exitTask;
        public int ProcessId { get; }

        public LaunchedAgentProcess(IntPtr processHandle, int processId)
        {
            _processHandle = processHandle;
            ProcessId = processId;
            _exitTask = Task.Run(() => WaitForProcessExit(processHandle));
        }

        public ValueTask<bool> WaitForExitAsync(CancellationToken ct)
        {
            return ct.CanBeCanceled
                ? new ValueTask<bool>(_exitTask.WaitAsync(ct))
                : new ValueTask<bool>(_exitTask);
        }

        private static bool WaitForProcessExit(IntPtr handle)
        {
            WaitForSingleObject(handle, uint.MaxValue);
            return true;
        }

        public void Kill()
        {
            if (_processHandle != IntPtr.Zero)
                TerminateProcess(_processHandle, 1);
        }

        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    }

    // --- P/Invoke ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern int WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int DuplicateTokenEx(
        SafeAccessTokenHandle hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetTokenInformation(
        SafeAccessTokenHandle hToken,
        TokenInfoClass tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateWellKnownSid(WellKnownSidType wellKnownSidType, IntPtr domainSid, IntPtr pSid, ref uint cbSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CreateProcessWithTokenW(
        SafeAccessTokenHandle hToken,
        uint dwLogonFlags,
        string lpApplicationName,
        string lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CreateProcessAsUser(
        SafeAccessTokenHandle hToken,
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
}
