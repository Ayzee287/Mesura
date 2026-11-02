using System.Runtime.InteropServices;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// What the signed-in session is really using for the mouse and the keyboard's accessibility hotkeys, as <c>SystemParametersInfo</c> reports it (read only).
/// The registry values under <c>HKCU\Control Panel</c> are what Windows loads at sign-in; the session's live state only follows them after the next sign-in
/// (or when Settings changes both), so a value Mesura has stored reads "in effect" here only once Windows is really using it.
/// Only the <c>SPI_GET*</c> actions are declared and called: nothing here can set anything (architecture §2.3).
/// </summary>
internal static class SessionInput
{
    private const uint SpiGetMouse = 0x0003;
    private const uint SpiGetFilterKeys = 0x0032;
    private const uint SpiGetToggleKeys = 0x0034;
    private const uint SpiGetMouseKeys = 0x0036;
    private const uint SpiGetStickyKeys = 0x003A;
    private const uint HotkeyActive = 0x4; // SKF_, FKF_, TKF_ and MKF_HOTKEYACTIVE: the keyboard shortcut that turns the feature on

    [StructLayout(LayoutKind.Sequential)]
    private struct FlagsOnly
    {
        public uint CbSize;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint CbSize;
        public uint Flags;
        public uint WaitMsec;
        public uint DelayMsec;
        public uint RepeatMsec;
        public uint BounceMsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseKeys
    {
        public uint CbSize;
        public uint Flags;
        public uint MaxSpeed;
        public uint TimeToMaxSpeed;
        public uint CtrlSpeed;
        public uint Reserved1;
        public uint Reserved2;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Get(uint action, uint size, ref FlagsOnly value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Get(uint action, uint size, ref FilterKeys value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Get(uint action, uint size, ref MouseKeys value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool Get(uint action, uint size, int[] value, uint winIni);

    /// <summary><c>disabled</c> when none of Sticky, Filter, Toggle and Mouse Keys can be started by its keyboard shortcut, <c>enabled</c> when one can, <c>null</c> when it can't be read.</summary>
    public static string? AccessibilityHotkeys()
    {
        try
        {
            var sticky = new FlagsOnly { CbSize = (uint)Marshal.SizeOf<FlagsOnly>() };
            var toggle = new FlagsOnly { CbSize = (uint)Marshal.SizeOf<FlagsOnly>() };
            var filter = new FilterKeys { CbSize = (uint)Marshal.SizeOf<FilterKeys>() };
            var mouse = new MouseKeys { CbSize = (uint)Marshal.SizeOf<MouseKeys>() };
            if (!Get(SpiGetStickyKeys, sticky.CbSize, ref sticky, 0) || !Get(SpiGetToggleKeys, toggle.CbSize, ref toggle, 0)
                || !Get(SpiGetFilterKeys, filter.CbSize, ref filter, 0) || !Get(SpiGetMouseKeys, mouse.CbSize, ref mouse, 0))
            {
                return null;
            }

            return ((sticky.Flags | toggle.Flags | filter.Flags | mouse.Flags) & HotkeyActive) == 0 ? "disabled" : "enabled";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary><c>disabled</c> when the pointer acceleration level is 0 ("Enhance pointer precision" off), <c>enabled</c> otherwise, <c>null</c> when it can't be read.</summary>
    public static string? PointerAcceleration()
    {
        try
        {
            var mouse = new int[3];
            if (!Get(SpiGetMouse, 0, mouse, 0))
            {
                return null;
            }

            return mouse[2] == 0 ? "disabled" : "enabled";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }
}
