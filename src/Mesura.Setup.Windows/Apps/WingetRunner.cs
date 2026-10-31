using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Mesura.Core.Apps;
using Mesura.Core.Windows;
using System.IO.Pipes;

namespace Mesura.Setup.Windows.Apps;

/// <summary>
/// The one place that launches winget, and so the one owner of a process launch and kill besides the planned
/// PresentMon runner (architecture §2.3). It runs only six fixed command shapes — <c>--version</c>, and
/// <c>list --exact --id</c> (also with <c>--upgrade-available</c>), <c>install --exact --id</c>, <c>upgrade --exact --id</c> and <c>uninstall --exact --id</c> for a catalog package — with arguments passed as a list,
/// never a shell string. Install runs unattended (<c>--silent</c>, agreements accepted) because the user previewed and
/// confirmed the batch first.
/// <para>
/// Whose context each command runs in is <see cref="WingetContext"/>'s decision: in Mesura's own process, or as the
/// user signed in to this session, unelevated, with a duplicate of the desktop shell's token (the shell always runs as
/// that user). Starting a process with another token needs the impersonation privilege an elevated administrator has;
/// when it isn't available, or the shell's user isn't the signed-in user, the command is refused, never run as
/// someone else.
/// </para>
/// </summary>
public sealed class WingetRunner : IWingetRunner
{
    private const string Executable = "winget";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    private readonly UserContext? _user;
    private readonly bool _elevated;
    private readonly bool _sameUser;
    private bool? _available;

    /// <param name="user">The signed-in user (from the system reader); <c>null</c> runs everything in Mesura's own context (tests, the CLI's old behaviour).</param>
    /// <param name="elevated">Whether Mesura runs elevated.</param>
    public WingetRunner(UserContext? user = null, bool elevated = false)
    {
        _user = user;
        _elevated = elevated;
        using var identity = WindowsIdentity.GetCurrent();
        _sameUser = user is { Resolved: true } && string.Equals(identity.User?.Value, user.Sid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The winget version string from the last successful probe, e.g. <c>v1.29.380</c>.</summary>
    public string? Version { get; private set; }

    /// <inheritdoc />
    public string Refusal { get; private set; } = "";

    /// <summary>
    /// Whether winget runs. Only a positive answer is kept: a long-running app re-probes after App Installer is
    /// added (a missing winget fails the probe at once, so asking again is cheap).
    /// </summary>
    public bool IsAvailable()
    {
        if (_available is not true)
        {
            var run = Run(["--version"], AppRunContext.Any, null, ProbeTimeout, CancellationToken.None);
            _available = run.Launched && run.ExitCode == 0;
            Version = _available.Value ? run.Stdout.Trim() : null;
            Refusal = run.Refusal;
        }

        return _available.Value;
    }

    public WingetRunResult List(AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        // Detection runs where the install would: a per-user app is listed as the signed-in user sees it, against the
        // source the install uses. The source also keeps winget from querying the Store for every app, which takes
        // 40 to 60 seconds a call where the Store is slow or blocked (it still matches apps installed outside winget).
        var run = Run(
            ["list", "--exact", "--id", package.WingetId, "--source", package.SourceArgument, "--accept-source-agreements", "--disable-interactivity"],
            package.Context, null, ListTimeout, CancellationToken.None);
        return new WingetRunResult(run.Launched, run.ExitCode, run.Stdout, run.Refusal);
    }

    public WingetRunResult Install(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Source == AppSource.Manual)
        {
            return WingetRunResult.NotLaunched;
        }

        // --no-upgrade: an app that turns out to be installed is reported as installed, never upgraded in place
        // (an upgrade can rewrite its files and settings).
        var arguments = new List<string>
        {
            "install", "--exact", "--id", package.WingetId, "--source", package.SourceArgument, "--silent", "--no-upgrade",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (package.InstallLocation && InstallFolders.Valid(package.InstallFolder) is { } folder)
        {
            // Only a package whose installer takes a folder gets one; the path is passed as one argument, never through a shell.
            arguments.AddRange(["--location", folder]);
        }

        var run = Run(arguments, package.Context, onOutput, InstallTimeout, cancellationToken);
        return new WingetRunResult(run.Launched, run.ExitCode, "", run.Refusal);
    }

    /// <summary>Asks whether winget offers an update: <c>list --exact --id --upgrade-available</c>, in the context the update would run in.</summary>
    public WingetRunResult ListUpgradeAvailable(AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var run = Run(
            ["list", "--exact", "--id", package.WingetId, "--source", package.SourceArgument, "--upgrade-available", "--accept-source-agreements", "--disable-interactivity"],
            package.Context, null, ListTimeout, CancellationToken.None);
        return new WingetRunResult(run.Launched, run.ExitCode, run.Stdout, run.Refusal);
    }

    public WingetRunResult Upgrade(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Source == AppSource.Manual || !package.Upgradable)
        {
            return WingetRunResult.NotLaunched;
        }

        // No --include-unknown (an app whose installed version winget can't read isn't touched) and no --force: winget's own refusals stand.
        var run = Run(
            ["upgrade", "--exact", "--id", package.WingetId, "--source", package.SourceArgument, "--silent",
             "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            package.Context, onOutput, InstallTimeout, cancellationToken);
        return new WingetRunResult(run.Launched, run.ExitCode, "", run.Refusal);
    }

    public WingetRunResult Uninstall(AppPackage package, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Source == AppSource.Manual || !package.Uninstallable)
        {
            return WingetRunResult.NotLaunched;
        }

        if (!_elevated)
        {
            // An MSI uninstall without administrator rights fails with installer code 1603, and some vendor uninstallers exit 0 having done nothing:
            // not started rather than started to fail.
            return WingetRunResult.Refused("Uninstalling an app needs Mesura to run as administrator.");
        }

        // No --force (winget's own refusals stand) and no --purge/--preserve (those are for portable packages).
        var run = Run(
            ["uninstall", "--exact", "--id", package.WingetId, "--source", package.SourceArgument, "--silent",
             "--accept-source-agreements", "--disable-interactivity"],
            package.Context, onOutput, InstallTimeout, cancellationToken);
        return new WingetRunResult(run.Launched, run.ExitCode, "", run.Refusal);
    }

    private sealed record RunResult(bool Launched, int ExitCode, string Stdout, string Refusal = "");

    private RunResult Run(IReadOnlyList<string> arguments, AppRunContext context, Action<string>? onLine, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_user is null)
        {
            return RunDirect(arguments, onLine, timeout, cancellationToken);
        }

        // Only an elevated process can start one with the shell's token, so only it opens the token, and only when the
        // decision needs it.
        var (launch, reason) = WingetContext.Decide(_user.Resolved, _sameUser, _elevated, context, signedInUserReachable: _elevated);
        if (launch == WingetLaunch.Direct)
        {
            return RunDirect(arguments, onLine, timeout, cancellationToken);
        }

        if (launch == WingetLaunch.Refused)
        {
            return new RunResult(false, 0, "", reason);
        }

        using var shell = ShellUser.Open(_user.Sid);
        if (shell is null)
        {
            // The shell isn't running, runs as someone else, or that user has no winget: refuse with the reason.
            return new RunResult(false, 0, "", WingetContext.Decide(_user.Resolved, _sameUser, _elevated, context, signedInUserReachable: false).Reason);
        }

        return ShellUser.Run(shell, arguments, onLine, timeout, cancellationToken, out var error) is { } run
            ? run
            : new RunResult(false, 0, "", LaunchFailure(_user.Account, error));
    }

    /// <summary>Why winget couldn't be started as the signed-in user, in words for the common causes.</summary>
    internal static string LaunchFailure(string account, int error) => error switch
    {
        1314 => $"Mesura couldn't start winget as {account}: that needs Mesura running as administrator (Windows error 1314).",
        1058 or 1068 => $"Mesura couldn't start winget as {account}: Windows' Secondary Logon service is disabled (Windows error {error}). Set it to Manual in Services, or start Mesura without elevation to install this app.",
        _ => $"Mesura couldn't start winget as {account} (Windows error {error}).",
    };

    private static RunResult RunDirect(IReadOnlyList<string> arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var program = Executable;
        if (Environment.IsPrivilegedProcess)
        {
            // An elevated launch names the file itself and only takes a winget a non-administrator can't have planted (WingetLocator, docs/security.md).
            var located = WingetLocator.ForElevatedLaunch();
            if (located.Refusal is { } refusal)
            {
                return new RunResult(false, 0, "", refusal);
            }

            program = located.Path ?? Executable;
        }

        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        try
        {
#pragma warning disable RS0030 // The winget runner is the one owner of a process launch (see class remarks).
            if (!process.Start())
#pragma warning restore RS0030
            {
                return new RunResult(false, 0, "");
            }
        }
        catch (Win32Exception)
        {
            // winget (App Installer) is not installed, or its execution alias is disabled.
            return new RunResult(false, 0, "");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() => Stop(process)))
        {
            if (!process.WaitForExit(timeout))
            {
                Stop(process);
                onLine?.Invoke($"winget did not finish within {timeout.TotalMinutes:0} minutes and was stopped.");
            }

            // The parameterless wait drains the asynchronous readers; it is only safe once the process has gone.
            if (process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.WaitForExit();
            }
        }

        lock (stdout)
        {
            // A process that outlived its stop request reports -1, which the caller describes as a failure.
            return new RunResult(true, process.HasExited ? process.ExitCode : -1, stdout.ToString());
        }
    }

    private static void Stop(Process process)
    {
        try
        {
#pragma warning disable RS0030 // Stopping the winget process this runner started, on cancel or timeout (see class remarks).
            process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Could not be stopped (e.g. an elevated child); WaitForExit still returns when it ends.
        }
        catch (AggregateException)
        {
            // winget stopped, but a child it started couldn't be (an installer elevated through UAC); the wait still ends.
        }
    }

    private static void Stop(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Stop(process);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
    }

    /// <summary>A command line with each argument quoted the way the C runtime parses it back.</summary>
    internal static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        foreach (var argument in arguments.Prepend(executable))
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                line.Append(c);
            }

            line.Append('\\', backslashes * 2).Append('"');
        }

        return line.ToString();
    }

    /// <summary>
    /// Starting winget as the signed-in user: a primary token duplicated from the desktop shell's process (it runs as
    /// that user, unelevated), that user's environment block, their own winget alias, and the output read through a
    /// pipe. <c>CreateProcessWithTokenW</c> is the supported way for an elevated process to start one unelevated with
    /// redirected output (the secondary logon service duplicates the standard handles into it).
    /// </summary>
    private sealed class ShellUser : IDisposable
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const uint TokenImpersonate = 0x0004;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int StartfUseStdHandles = 0x00000100;
        private const int StartfUseShowWindow = 0x00000001;

        private const uint WaitTimeout = 0x00000102;
        private static readonly Guid LocalAppDataFolder = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        private readonly IntPtr _token;

        private ShellUser(IntPtr token, string executable)
        {
            _token = token;
            ExecutablePath = executable;
        }

        public string ExecutablePath { get; }

        /// <summary>The shell's token, when the shell runs and its user is <paramref name="sid"/>; otherwise <c>null</c>.</summary>
        public static ShellUser? Open(string? sid)
        {
            if (sid is null)
            {
                return null;
            }

            var window = GetShellWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
            {
                return null;
            }

            var primary = DuplicateShellToken(processId);
            if (primary == IntPtr.Zero)
            {
                return null;
            }

            var winget = "";
            try
            {
                using (var identity = new WindowsIdentity(primary))
                {
                    if (!string.Equals(identity.User?.Value, sid, StringComparison.OrdinalIgnoreCase))
                    {
                        return null; // the shell runs as someone else: never start winget as them
                    }
                }

                if (SHGetKnownFolderPath(LocalAppDataFolder, 0, primary, out var folder) == 0)
                {
                    try
                    {
                        var localAppData = Marshal.PtrToStringUni(folder) ?? "";
                        winget = localAppData.Length == 0 ? "" : Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(folder);
                    }
                }

                if (winget.Length == 0 || !File.Exists(winget))
                {
                    return null; // that user has no winget (App Installer)
                }

                var opened = new ShellUser(primary, winget);
                primary = IntPtr.Zero; // owned by the instance now
                return opened;
            }
            catch (Exception e) when (e is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
            finally
            {
                if (primary != IntPtr.Zero)
                {
                    CloseHandle(primary);
                }
            }
        }

        /// <summary>A primary token duplicated from the shell process's token, or zero.</summary>
        private static IntPtr DuplicateShellToken(uint processId)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var shellToken))
                {
                    return IntPtr.Zero;
                }

                try
                {
                    // TOKEN_IMPERSONATE as well: SHGetKnownFolderPath and CreateEnvironmentBlock read the user's folders with it.
                    return DuplicateTokenEx(shellToken, TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId | TokenImpersonate,
                        IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary)
                        ? primary
                        : IntPtr.Zero;
                }
                finally
                {
                    CloseHandle(shellToken);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// <summary>Runs winget as the shell's user; <c>null</c> when the process couldn't be started (<paramref name="error"/> says why).</summary>
        public static RunResult? Run(ShellUser user, IReadOnlyList<string> arguments, Action<string>? onLine, TimeSpan timeout,
            CancellationToken cancellationToken, out int error)
        {
            // The pipe's write end is inheritable (the secondary logon service duplicates it into winget); the read end isn't.
            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            if (!CreateEnvironmentBlock(out var environment, user._token, false))
            {
                environment = IntPtr.Zero;
            }

            var write = pipe.ClientSafePipeHandle.DangerousGetHandle();
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles | StartfUseShowWindow,
                ShowWindow = 0,
                StdOutput = write,
                StdError = write,
            };
            ProcessInformation info;
            bool started;
            try
            {
                started = CreateProcessWithTokenW(user._token, 0, user.ExecutablePath, new StringBuilder(CommandLine(user.ExecutablePath, arguments)),
                    CreateNoWindow | (environment != IntPtr.Zero ? CreateUnicodeEnvironment : 0), environment,
                    Path.GetDirectoryName(user.ExecutablePath), ref startup, out info);
                error = started ? 0 : Marshal.GetLastPInvokeError();
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }

                // With our copy of the write end closed, the read ends when winget and anything it started have closed theirs.
                pipe.DisposeLocalCopyOfClientHandle();
            }

            if (!started)
            {
                return null;
            }

            CloseHandle(info.Thread);
            var stdout = new StringBuilder();
            var reader = new Thread(() =>
            {
                try
                {
                    using var text = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                    while (text.ReadLine() is { } line)
                    {
                        lock (stdout)
                        {
                            stdout.AppendLine(line);
                        }

                        onLine?.Invoke(line);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // The pipe broke, or the run ended while an installer still held it: the output so far is kept.
                }
            })
            { IsBackground = true, Name = "winget output" };
            reader.Start();

            try
            {
                var deadline = DateTime.UtcNow + timeout;
                var stopped = false;
                while (WaitForSingleObject(info.Process, 250) == WaitTimeout)
                {
                    if (!stopped && (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            onLine?.Invoke($"winget did not finish within {timeout.TotalMinutes:0} minutes and was stopped.");
                        }

                        Stop(info.ProcessId);
                        stopped = true;
                        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                    }
                    else if (stopped && DateTime.UtcNow > deadline)
                    {
                        break; // it outlived its stop request
                    }
                }

                reader.Join(TimeSpan.FromSeconds(5)); // an installer it started may hold the pipe open longer
                var exited = GetExitCodeProcess(info.Process, out var code) && code != StillActive;
                lock (stdout)
                {
                    // A process that outlived its stop request reports -1, which the caller describes as a failure.
                    return new RunResult(true, exited ? unchecked((int)code) : -1, stdout.ToString());
                }
            }
            finally
            {
                CloseHandle(info.Process);
            }
        }

        private const uint StillActive = 259;

        public void Dispose() => CloseHandle(_token);


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Count;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder commandLine,
            uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation information);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);


        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    }
}
