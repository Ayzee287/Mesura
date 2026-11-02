using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>How one run of a NVIDIA tool ended: it didn't start (<see cref="Error"/>), it didn't finish in time, or it exited.</summary>
public sealed record ToolRun(bool Started, bool TimedOut, int ExitCode, string? Error);

/// <summary>Starts a NVIDIA tool and waits for it (the real thing, or a test's stand-in).</summary>
public interface IToolProcess
{
    /// <param name="asShellUser">Start it with the signed-in user's own, unelevated token (the desktop shell's) instead of this process's.</param>
    ToolRun Run(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool asShellUser, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Starts a tool either as this process does (an elevated Mesura gives it administrator rights) or as the user signed in at this desktop, unelevated: a primary token duplicated from the
/// desktop shell's process, which always runs as that user, handed to <c>CreateProcessWithTokenW</c>, the supported way for an elevated process to start an unelevated one (as Mesura
/// restarts itself after a machine-wide update and runs winget). A tool started that way has only the user's rights, so it cannot give anyone more than they have.
/// </summary>
public sealed class ToolProcess : IToolProcess
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
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint WaitObject0 = 0;
    private const int TokenElevation = 20;

    public ToolRun Run(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool asShellUser, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return asShellUser ? RunAsShellUser(executable, arguments, workingDirectory, timeout, cancellationToken) : RunHere(executable, arguments, workingDirectory, timeout, cancellationToken);
    }

    private static ToolRun RunHere(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
#pragma warning disable RS0030 // The setup layer's NVIDIA tool owner: NVIDIA Profile Inspector's export and merge import (NvidiaTools remarks).
            using var process = Process.Start(info);
#pragma warning restore RS0030
            if (process is null)
            {
                return new ToolRun(false, false, 0, "it didn't start");
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return new ToolRun(true, true, 0, null);
            }

            return new ToolRun(true, false, process.ExitCode, null);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new ToolRun(false, false, 0, e.Message);
        }
    }

    private static ToolRun RunAsShellUser(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var shellProcess) == 0 || shellProcess == 0)
        {
            return new ToolRun(false, false, 0, "the desktop shell isn't running, so there is no unelevated user token to start it with");
        }

        var token = DuplicateShellToken(shellProcess);
        if (token == IntPtr.Zero)
        {
            return new ToolRun(false, false, 0, "the desktop shell's token couldn't be used");
        }

        // "Unelevated" is the point of using this token: where the shell itself runs elevated (UAC off, an account without a split token) it is not, and nothing is started with it.
        if (IsElevated(token))
        {
            CloseHandle(token);
            return new ToolRun(false, false, 0, "the desktop shell is itself running elevated here, so its token is not an unelevated one");
        }

        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder(CommandLine(executable, arguments));
            if (!CreateProcessWithTokenW(token, 0, executable, commandLine, environment != IntPtr.Zero ? CreateUnicodeEnvironment : 0, environment, workingDirectory, ref startup, out var started))
            {
                return new ToolRun(false, false, 0, new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            CloseHandle(started.Thread);
            try
            {
                var deadline = DateTime.UtcNow + timeout;
                while (WaitForSingleObject(started.Process, 250) != WaitObject0)
                {
                    if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        return new ToolRun(true, true, 0, null); // left running, as for the same-token case: it may be showing a message
                    }
                }

                return GetExitCodeProcess(started.Process, out var code) ? new ToolRun(true, false, unchecked((int)code), null) : new ToolRun(true, false, 0, null);
            }
            finally
            {
                CloseHandle(started.Process);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandle(token);
        }
    }

    private static bool IsElevated(IntPtr token)
    {
        var size = sizeof(int);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // TokenElevation: a non-zero value means the token has the full (elevated) administrator rights; an unreadable answer is treated as elevated.
            return !GetTokenInformation(token, TokenElevation, buffer, size, out _) || Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder().Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ').Append('"');
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
        public uint ProcessId;
        public uint ThreadId;
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
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, StringBuilder commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startup, out ProcessInformation information);

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
}
