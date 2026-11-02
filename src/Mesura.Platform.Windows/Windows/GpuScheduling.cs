using System.Globalization;
using System.Runtime.InteropServices;

namespace Mesura.Platform.Windows.Windows;

/// <summary>
/// Whether hardware-accelerated GPU scheduling is supported and in effect, as the graphics kernel reports it (read only):
/// <c>D3DKMTQueryAdapterInfo</c> with <c>KMTQAITYPE_WDDM_2_7_CAPS</c> returns, per adapter, a bit field whose bit 0 is
/// <c>HwSchSupported</c> and bit 1 <c>HwSchEnabled</c>. The registry value <c>HwSchMode</c> only says what was asked for; this says what
/// Windows did with it (it changes after a restart).
/// </summary>
internal static class GpuScheduling
{
    private const int Wddm27Caps = 70; // KMTQAITYPE_WDDM_2_7_CAPS
    private const uint HwSchSupported = 1;
    private const uint HwSchEnabled = 2;

    /// <summary><c>enabled</c>, <c>disabled</c>, <c>unsupported</c> (no hardware adapter supports it) or <c>null</c> when nothing could be read.</summary>
    public static string? Read()
    {
        try
        {
            var read = 0;
            var anySupported = false;
            var anyEnabled = false;
            foreach (var adapter in Collectors.AdapterCollector.Collect().Adapters)
            {
                if (adapter.IsHardware.Value is false || !TryLuid(adapter.AdapterId, out var luid))
                {
                    continue;
                }

                if (Query(luid) is not { } caps)
                {
                    continue;
                }

                read++;
                if ((caps & HwSchSupported) != 0)
                {
                    anySupported = true;
                    anyEnabled |= (caps & HwSchEnabled) != 0;
                }
            }

            return read == 0 ? null : !anySupported ? "unsupported" : anyEnabled ? "enabled" : "disabled";
        }
        catch (Exception e) when (e is SourceUnavailableException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool TryLuid(string id, out Luid luid)
    {
        luid = default;
        if (id.Length != 16 || !uint.TryParse(id.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(id.AsSpan(8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low))
        {
            return false;
        }

        luid = new Luid { LowPart = low, HighPart = unchecked((int)high) };
        return true;
    }

    private static unsafe uint? Query(Luid luid)
    {
        var open = new OpenAdapterFromLuid { AdapterLuid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0)
        {
            return null;
        }

        try
        {
            uint caps = 0;
            var query = new QueryAdapterInfo { Adapter = open.Adapter, Type = Wddm27Caps, PrivateDriverData = &caps, PrivateDriverDataSize = sizeof(uint) };
            return D3DKMTQueryAdapterInfo(ref query) == 0 ? caps : null;
        }
        finally
        {
            var close = new CloseAdapter { Adapter = open.Adapter };
            _ = D3DKMTCloseAdapter(ref close);
        }
    }

    // The three kernel-mode thunks of gdi32 that query an adapter (read only), declared here because the Win32 metadata that
    // generates the other signatures has no definition for them. Layouts are those of d3dkmthk.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid
    {
        public Luid AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromLuid")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);
}
