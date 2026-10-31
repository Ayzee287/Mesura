using System.Runtime.InteropServices;
using System.Text;

namespace Mesura.Updating;

/// <summary>
/// Starts a program as the user signed in at this desktop, unelevated, from an elevated process: the way Mesura is started again after a machine-wide update.
/// The updater of such an update runs with administrator rights (the user approved the prompt, possibly with another account's password); a program it starts the
/// ordinary way inherits them, so Mesura would reopen as administrator whatever the user chose for it, and, after a prompt answered with another account, as that
/// account, in its profile. The desktop shell always runs as the signed-in user, unelevated: its token, duplicated, is the right one to start the app with.
/// </summary>
internal static class ShellProcess
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

    /// <returns><c>true</c> when the program was started with the shell's token; <c>false</c> when it couldn't be (no shell, or it isn't the signed-in user's) and nothing was started.</returns>
    public static bool TryStart(string executable, string arguments, string workingDirectory)
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return false;
        }

        var token = DuplicateShellToken(processId);
        if (token == IntPtr.Zero)
        {
            return false;
        }

        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder("\"" + executable + "\"" + (arguments.Length > 0 ? " " + arguments : ""));
            if (!CreateProcessWithTokenW(token, 0, executable, commandLine, environment != IntPtr.Zero ? CreateUnicodeEnvironment : 0, environment, workingDirectory, ref startup, out var info))
            {
                return false;
            }

            CloseHandle(info.Process);
            CloseHandle(info.Thread);
            return true;
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
    private static extern bool CloseHandle(IntPtr handle);
}
