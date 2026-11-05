using System.Runtime.InteropServices;

namespace Mesura.Platform.Windows.Gpu.Adlx;

/// <summary>What one ADLX call answered: its result and, when it succeeded, the value.</summary>
internal readonly record struct AdlxReading<T>(AdlxResult Result, T? Value)
{
    public bool Ok => Result.Succeeded();
}

internal static class AdlxReadingExtensions
{
    /// <summary>The value of a successful reading of a value type, or <c>null</c>: "unknown" and "false" or 0 stay apart (a failed call leaves its out parameter at whatever it was).</summary>
    public static T? Known<T>(this AdlxReading<T> reading)
        where T : struct => reading.Ok ? reading.Value : null;

    /// <summary>The text of a successful reading, or <c>null</c>.</summary>
    public static string? KnownText(this AdlxReading<string?> reading) => reading.Ok ? reading.Value : null;
}

// The vtable slot numbers below are the order of the functions in the C interface structs of AMD's ADLX 1.5 headers (IADLXGPUVtbl and the like). That order, not the order the C++ classes
// declare their methods in, is what the DLL's objects have; it was read from the headers, and an offline check (tools\check-adlx-abi.ps1) compares these numbers with a copy of the SDK.

/// <summary>Shared reading of an object's getters: <c>ADLX_RESULT f(this, T* out)</c> in a given slot.</summary>
internal abstract unsafe class AdlxReadable(AdlxSession session, nint pointer) : AdlxObject(session, pointer)
{
    protected AdlxReading<bool> ReadBool(int slot)
    {
        byte value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, byte*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &value);
        return new(result, value != 0);
    }

    protected AdlxReading<int> ReadInt(int slot)
    {
        var value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, int*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &value);
        return new(result, value);
    }

    protected AdlxReading<uint> ReadUInt(int slot)
    {
        uint value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, uint*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &value);
        return new(result, value);
    }

    protected AdlxReading<double> ReadDouble(int slot)
    {
        double value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, double*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &value);
        return new(result, value);
    }

    /// <summary>A <c>const char**</c> getter: the string belongs to the object, so it is copied here before anything can release the object.</summary>
    protected AdlxReading<string?> ReadText(int slot)
    {
        byte* text = null;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, byte**, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &text);
        return new(result, result.Succeeded() && text is not null ? Marshal.PtrToStringUTF8((nint)text) : null);
    }

    /// <summary>A getter that hands back another reference-counted object: <c>ADLX_RESULT f(this, IADLXGPU* gpu, T** out)</c> (or without the GPU).</summary>
    protected AdlxResult Obtain(int slot, nint argument, out nint item)
    {
        nint found = 0;
        var result = argument == 0
            ? (AdlxResult)((delegate* unmanaged[Stdcall]<nint, nint*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, &found)
            : (AdlxResult)((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, argument, &found);
        item = result.Succeeded() ? found : 0;
        return result;
    }
}

/// <summary><c>IADLXGPUList</c>: how many GPUs, and each one.</summary>
internal sealed unsafe class AdlxGpuList(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public uint Size => ((delegate* unmanaged[Stdcall]<nint, uint>)AdlxSession.Slot(Pointer, 3))(Pointer);

    /// <summary><c>At_GPUList</c> (slot 11, the typed <c>At</c>): the GPU with its own reference.</summary>
    public AdlxReading<AdlxGpu> At(uint index)
    {
        nint found = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)AdlxSession.Slot(Pointer, 11))(Pointer, index, &found);
        return new(result, result.Succeeded() && found != 0 ? new AdlxGpu(Session, found) : null);
    }
}

/// <summary><c>IADLXGPU</c>: what the GPU is. Every getter can fail on a driver that does not offer it; a failed one is "unknown", never a default.</summary>
internal sealed unsafe class AdlxGpu(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<string?> VendorId() => ReadText(3);

    public AdlxReading<int> AsicFamilyType() => ReadInt(4);

    public AdlxReading<int> GpuType() => ReadInt(5);

    public AdlxReading<bool> IsExternal() => ReadBool(6);

    public AdlxReading<string?> Name() => ReadText(7);

    public AdlxReading<string?> DriverPath() => ReadText(8);

    public AdlxReading<string?> PnpString() => ReadText(9);

    public AdlxReading<uint> TotalVramMb() => ReadUInt(11);

    public AdlxReading<string?> VramType() => ReadText(12);

    public AdlxReading<string?> DeviceId() => ReadText(14);

    public AdlxReading<string?> RevisionId() => ReadText(15);

    public AdlxReading<string?> SubSystemId() => ReadText(16);

    public AdlxReading<int> UniqueId() => ReadInt(18);

    /// <summary><c>BIOSInfo</c> (slot 13): part number, version and date.</summary>
    public AdlxReading<(string? Part, string? Version, string? Date)> BiosInfo()
    {
        byte* part = null, version = null, date = null;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, byte**, byte**, byte**, int>)AdlxSession.Slot(Pointer, 13))(Pointer, &part, &version, &date);
        static string? Copy(byte* s) => s is null ? null : Marshal.PtrToStringUTF8((nint)s);
        return new(result, result.Succeeded() ? (Copy(part), Copy(version), Copy(date)) : default);
    }
}

/// <summary>
/// A feature with <c>IsSupported</c> (slot 3) and <c>IsEnabled</c> (slot 4): Anti-Lag, Chill, Boost, Image Sharpening, Enhanced Sync, frame rate target control, SmartAccess Memory
/// have the same two getters in the same two slots (checked for each in the headers). Setters (slot 5 and later) are never called by the reading side.
/// </summary>
internal sealed unsafe class AdlxToggle(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<bool> IsSupported() => ReadBool(3);

    public AdlxReading<bool> IsEnabled() => ReadBool(4);

    /// <summary>An <c>int</c> getter of the same object in another slot: wait-for-vertical-refresh's mode (5), Radeon Super Resolution's sharpness (7).</summary>
    public AdlxReading<int> Int(int slot) => ReadInt(slot);

    /// <summary>Anti-Lag's <c>IADLX3DAntiLag1</c> (ADLX 1.1 and later): the level, found by asking; <see cref="AdlxResult.UnknownInterface"/> on a driver without it.</summary>
    public AdlxReading<int> AntiLagLevel()
    {
        var asked = Query("IADLX3DAntiLag1", out var level1);
        if (!asked.Succeeded() || level1 == 0)
        {
            return new(asked.Succeeded() ? AdlxResult.Fail : asked, 0);
        }

        using var levels = new AdlxLevel(Session, level1);
        return levels.Level();
    }
}

/// <summary>The <c>IADLX3DAntiLag1</c> object: <c>GetLevel</c> is slot 6.</summary>
internal sealed class AdlxLevel(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<int> Level() => ReadInt(6);
}

/// <summary>
/// A feature whose slot 3 is <c>IsSupported</c> and whose slot 4 is a <b>value</b> (an enum, 4 bytes), not <c>IsEnabled</c>: display scaling mode, colour depth, pixel format (<c>GetValue</c>/<c>GetMode</c>)
/// and tessellation (<c>GetMode</c>, with <c>GetLevel</c> in slot 5). Kept apart from <see cref="AdlxToggle"/> so that a value is never read through a one-byte boolean getter.
/// </summary>
internal sealed class AdlxValueFeature(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<bool> IsSupported() => ReadBool(3);

    public AdlxReading<int> Value() => ReadInt(4);

    public AdlxReading<int> SecondValue() => ReadInt(5);
}

/// <summary><c>IADLX3DSettingsServices</c>: the 3D features of a GPU (slots 3 to 9 are the getters Mesura reads, 8 wait for vertical refresh, 13 tessellation, 14 Radeon Super Resolution).</summary>
internal sealed class Adlx3DServices(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<AdlxToggle> AntiLag(AdlxGpu gpu) => Feature(3, gpu);

    public AdlxReading<AdlxToggle> Chill(AdlxGpu gpu) => Feature(4, gpu);

    public AdlxReading<AdlxToggle> Boost(AdlxGpu gpu) => Feature(5, gpu);

    public AdlxReading<AdlxToggle> ImageSharpening(AdlxGpu gpu) => Feature(6, gpu);

    public AdlxReading<AdlxToggle> EnhancedSync(AdlxGpu gpu) => Feature(7, gpu);

    public AdlxReading<AdlxToggle> FrameRateTargetControl(AdlxGpu gpu) => Feature(9, gpu);

    /// <summary>V-Sync: <c>IsSupported</c>, <c>IsEnabled</c>, and the mode in slot 5 (0 always off, 1 off unless the application asks, 2 on unless the application asks, 3 always on).</summary>
    public AdlxReading<AdlxToggle> WaitForVerticalRefresh(AdlxGpu gpu) => Feature(8, gpu);

    public AdlxReading<AdlxValueFeature> Tessellation(AdlxGpu gpu)
    {
        var result = Obtain(13, gpu.Pointer, out var item);
        return new(result, item != 0 ? new AdlxValueFeature(Session, item) : null);
    }

    /// <summary>Radeon Super Resolution takes no GPU argument (<c>GetRadeonSuperResolution(pp)</c>).</summary>
    public AdlxReading<AdlxToggle> RadeonSuperResolution()
    {
        var result = Obtain(14, 0, out var item);
        return new(result, item != 0 ? new AdlxToggle(Session, item) : null);
    }

    private AdlxReading<AdlxToggle> Feature(int slot, AdlxGpu gpu)
    {
        var result = Obtain(slot, gpu.Pointer, out var item);
        return new(result, item != 0 ? new AdlxToggle(Session, item) : null);
    }
}

/// <summary><c>IADLXGPUTuningServices</c>: whether each kind of tuning exists (slots 6 to 11), whether the GPU is at factory settings (slot 4) and, through <c>IADLXGPUTuningServices1</c>, SmartAccess Memory.</summary>
internal sealed unsafe class AdlxTuningServices(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<bool> IsAtFactory(AdlxGpu gpu) => Flag(4, gpu);

    public AdlxReading<bool> AutoTuning(AdlxGpu gpu) => Flag(6, gpu);

    public AdlxReading<bool> PresetTuning(AdlxGpu gpu) => Flag(7, gpu);

    public AdlxReading<bool> ManualGfxTuning(AdlxGpu gpu) => Flag(8, gpu);

    public AdlxReading<bool> ManualVramTuning(AdlxGpu gpu) => Flag(9, gpu);

    public AdlxReading<bool> ManualFanTuning(AdlxGpu gpu) => Flag(10, gpu);

    public AdlxReading<bool> ManualPowerTuning(AdlxGpu gpu) => Flag(11, gpu);

    private AdlxReading<bool> Flag(int slot, AdlxGpu gpu)
    {
        byte value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)AdlxSession.Slot(Pointer, slot))(Pointer, gpu.Pointer, &value);
        return new(result, value != 0);
    }

    /// <summary>SmartAccess Memory (<c>GetSmartAccessMemory</c>, slot 18 of <c>IADLXGPUTuningServices1</c>): asked for by interface id because a driver older than ADLX 1.3 lacks it.</summary>
    public AdlxReading<AdlxToggle> SmartAccessMemory(AdlxGpu gpu)
    {
        var asked = Query("IADLXGPUTuningServices1", out var services1);
        if (!asked.Succeeded() || services1 == 0)
        {
            return new(asked.Succeeded() ? AdlxResult.Fail : asked, null);
        }

        using var wide = new AdlxTuningServices1(Session, services1);
        return wide.SmartAccessMemory(gpu);
    }
}

internal sealed class AdlxTuningServices1(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<AdlxToggle> SmartAccessMemory(AdlxGpu gpu)
    {
        var result = Obtain(18, gpu.Pointer, out var item);
        return new(result, item != 0 ? new AdlxToggle(Session, item) : null);
    }
}

/// <summary><c>IADLXPerformanceMonitoringServices</c>: one reading of a GPU's metrics (<c>GetCurrentGPUMetrics</c>, slot 18).</summary>
internal sealed class AdlxPerformanceServices(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<AdlxGpuMetrics> CurrentMetrics(AdlxGpu gpu)
    {
        var result = Obtain(18, gpu.Pointer, out var item);
        return new(result, item != 0 ? new AdlxGpuMetrics(Session, item) : null);
    }
}

/// <summary><c>IADLXGPUMetrics</c>: usage (slot 4), clocks (5, 6), temperatures (7, 8), power (9, 10), fan (11), voltage (13).</summary>
internal sealed class AdlxGpuMetrics(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<double> UsagePercent() => ReadDouble(4);

    public AdlxReading<int> ClockMhz() => ReadInt(5);

    public AdlxReading<int> MemoryClockMhz() => ReadInt(6);

    public AdlxReading<double> TemperatureC() => ReadDouble(7);

    public AdlxReading<double> HotspotC() => ReadDouble(8);

    public AdlxReading<double> PowerW() => ReadDouble(9);

    public AdlxReading<double> BoardPowerW() => ReadDouble(10);

    public AdlxReading<int> FanRpm() => ReadInt(11);

    public AdlxReading<int> VoltageMv() => ReadInt(13);
}

/// <summary><c>IADLXDisplayServices</c>: the displays and, for each, the display features Mesura reads (slot 9 FreeSync, 10 VSR, 11 GPU scaling, 12 scaling mode, 13 integer scaling, 14 colour depth, 15 pixel format).</summary>
internal sealed class AdlxDisplayServices(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<AdlxDisplayList> Displays()
    {
        var result = Obtain(4, 0, out var item);
        return new(result, item != 0 ? new AdlxDisplayList(Session, item) : null);
    }

    public AdlxReading<AdlxToggle> FreeSync(AdlxDisplay display) => Toggle(9, display);

    public AdlxReading<AdlxToggle> VirtualSuperResolution(AdlxDisplay display) => Toggle(10, display);

    public AdlxReading<AdlxToggle> GpuScaling(AdlxDisplay display) => Toggle(11, display);

    public AdlxReading<AdlxToggle> IntegerScaling(AdlxDisplay display) => Toggle(13, display);

    public AdlxReading<AdlxValueFeature> ScalingMode(AdlxDisplay display) => Value(12, display);

    public AdlxReading<AdlxValueFeature> ColorDepth(AdlxDisplay display) => Value(14, display);

    public AdlxReading<AdlxValueFeature> PixelFormat(AdlxDisplay display) => Value(15, display);

    private AdlxReading<AdlxToggle> Toggle(int slot, AdlxDisplay display)
    {
        var result = Obtain(slot, display.Pointer, out var item);
        return new(result, item != 0 ? new AdlxToggle(Session, item) : null);
    }

    private AdlxReading<AdlxValueFeature> Value(int slot, AdlxDisplay display)
    {
        var result = Obtain(slot, display.Pointer, out var item);
        return new(result, item != 0 ? new AdlxValueFeature(Session, item) : null);
    }
}

/// <summary><c>IADLXDisplayList</c>: how many displays (slot 3) and each one (<c>At_DisplayList</c>, slot 11).</summary>
internal sealed unsafe class AdlxDisplayList(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public uint Size => ((delegate* unmanaged[Stdcall]<nint, uint>)AdlxSession.Slot(Pointer, 3))(Pointer);

    public AdlxReading<AdlxDisplay> At(uint index)
    {
        nint found = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)AdlxSession.Slot(Pointer, 11))(Pointer, index, &found);
        return new(result, result.Succeeded() && found != 0 ? new AdlxDisplay(Session, found) : null);
    }
}

/// <summary>
/// <c>IADLXDisplay</c>: what the display is (slots 3 manufacturer id, 4 type, 5 connector, 6 name, 8 native resolution, 9 refresh rate, 11 scan type, 12 the GPU it is on). The EDID (slot 7) is
/// <b>not</b> read: it carries the monitor's serial number.
/// </summary>
internal sealed unsafe class AdlxDisplay(AdlxSession session, nint pointer) : AdlxReadable(session, pointer)
{
    public AdlxReading<uint> ManufacturerId() => ReadUInt(3);

    public AdlxReading<int> DisplayType() => ReadInt(4);

    public AdlxReading<int> ConnectorType() => ReadInt(5);

    public AdlxReading<string?> Name() => ReadText(6);

    public AdlxReading<(int Width, int Height)> NativeResolution()
    {
        int width = 0, height = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, int*, int*, int>)AdlxSession.Slot(Pointer, 8))(Pointer, &width, &height);
        return new(result, (width, height));
    }

    public AdlxReading<double> RefreshRate() => ReadDouble(9);

    public AdlxReading<int> ScanType() => ReadInt(11);

    /// <summary>The unique id of the GPU the display is connected to (<c>GetGPU</c>, slot 12, then that GPU's <c>UniqueId</c>).</summary>
    public AdlxReading<int> GpuUniqueId()
    {
        var result = Obtain(12, 0, out var item);
        if (item == 0)
        {
            return new(result.Succeeded() ? AdlxResult.Fail : result, 0);
        }

        using var gpu = new AdlxGpu(Session, item);
        return gpu.UniqueId();
    }
}

/// <summary>The ADLX system object's getters (slots 1, 7, 8, 9): not reference counted, owned by ADLX until it is terminated, so it is not an <see cref="AdlxObject"/>.</summary>
internal sealed unsafe class AdlxSystem(AdlxSession session)
{
    public AdlxReading<AdlxGpuList> Gpus()
    {
        nint found = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, nint*, int>)AdlxSession.Slot(session.System, 1))(session.System, &found);
        return new(result, result.Succeeded() && found != 0 ? new AdlxGpuList(session, found) : null);
    }

    public AdlxReading<Adlx3DServices> Services3D() => Service(7, (s, p) => new Adlx3DServices(s, p));

    public AdlxReading<AdlxTuningServices> Tuning() => Service(8, (s, p) => new AdlxTuningServices(s, p));

    public AdlxReading<AdlxPerformanceServices> Performance() => Service(9, (s, p) => new AdlxPerformanceServices(s, p));

    public AdlxReading<AdlxDisplayServices> Displays() => Service(3, (s, p) => new AdlxDisplayServices(s, p));

    /// <summary><c>GetHybridGraphicsType</c> (slot 0): 0 none, 1 AMD, 2 other.</summary>
    public AdlxReading<int> HybridGraphics()
    {
        var value = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, int*, int>)AdlxSession.Slot(session.System, 0))(session.System, &value);
        return new(result, value);
    }

    private AdlxReading<T> Service<T>(int slot, Func<AdlxSession, nint, T> make)
        where T : class
    {
        nint found = 0;
        var result = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, nint*, int>)AdlxSession.Slot(session.System, slot))(session.System, &found);
        return new(result, result.Succeeded() && found != 0 ? make(session, found) : null);
    }
}
