using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mesura.Platform.Windows.Gpu.Adlx;

namespace Mesura.Platform.Tests.Gpu;

/// <summary>What one fake feature answers: whether the driver hands it out, whether it is supported, whether it is on.</summary>
internal sealed record FakeFeature(AdlxResult GetResult = AdlxResult.Ok, AdlxResult SupportedResult = AdlxResult.Ok, bool Supported = true, AdlxResult EnabledResult = AdlxResult.Ok, bool Enabled = false,
    AdlxResult WriteResult = AdlxResult.Ok, bool WriteTakes = true);

/// <summary>One fake display: what the driver says about it and about the display features Mesura reads.</summary>
internal sealed class FakeDisplay
{
    public string Name { get; init; } = "Test Monitor 27";

    public int Width { get; init; } = 2560;

    public int Height { get; init; } = 1440;

    public double Refresh { get; init; } = 165.0;

    public int Connector { get; init; } = 14; // DisplayPort

    public int ScalingMode { get; init; } = 1;

    public int ColorDepth { get; init; } = 3;

    public int PixelFormat { get; init; } = 1;

    public AdlxResult NameResult { get; init; } = AdlxResult.Ok;

    public Dictionary<string, FakeFeature> Features { get; } = new(StringComparer.Ordinal)
    {
        ["freesync"] = new(Enabled: true), ["vsr"] = new(), ["gpuscaling"] = new(), ["integerscaling"] = new(Supported: false),
    };
}

internal sealed class FakeGpu
{
    public string Name { get; init; } = "AMD Radeon RX 9070 XT";

    public string VendorId { get; init; } = "1002";

    public string DeviceId { get; init; } = "7550";

    public AdlxResult AtResult { get; init; } = AdlxResult.Ok;

    public AdlxResult NameResult { get; init; } = AdlxResult.Ok;

    public Dictionary<string, FakeFeature> Features { get; } = new(StringComparer.Ordinal)
    {
        ["antilag"] = new(Enabled: true), ["chill"] = new(), ["boost"] = new(Supported: false), ["sharpening"] = new(Enabled: true),
        ["enhancedsync"] = new(), ["frtc"] = new(), ["sam"] = new(Enabled: true), ["wfvr"] = new(), ["rsr"] = new(Supported: false),
    };

    public int AntiLagLevel { get; init; } = 2;

    public int VsyncMode { get; init; } = 1;

    public int TessellationMode { get; init; }

    public int TessellationLevel { get; init; } = 64;
}

/// <summary>
/// A stand-in for <c>amdadlx64.dll</c> that is real at the ABI level: its objects are native memory with a pointer to a native vtable of function pointers in the slot order of AMD's C interface
/// structs, called by the code under test through the same unmanaged calls it makes into the real DLL. It counts every reference taken and released, every call made after Terminate, and
/// every call to a setter or an unexpected slot, so a test can say what the code did to ADLX. It proves the layout Mesura assumes against itself (the headers are checked by
/// <c>tools\check-adlx-abi.ps1</c>) and the lifetime rules; it does NOT prove anything about AMD's DLL.
/// </summary>
internal static unsafe class FakeAdlx
{
    // object kinds
    private const int KGpuList = 1, KGpu = 2, KServices3D = 3, KToggle = 4, KAntiLag1 = 5, KTuning = 6, KTuning1 = 7, KPerf = 8, KMetrics = 9, KDisplayServices = 10, KDisplayList = 11, KDisplay = 12, KValue = 13;

    private sealed class Obj
    {
        public int Kind;
        public int Rc;
        public bool Freed;
        public int Gpu;
        public int Display = -1;
        public string Feature = "";
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Obj> Objects = [];
    private static readonly List<nint> Strings = [];
    private static readonly Dictionary<int, nint> Vtables = [];
    private static nint s_system;

    public static List<FakeGpu> Gpus { get; private set; } = [];

    public static List<FakeDisplay> Displays { get; private set; } = [];

    public static AdlxResult DisplayServicesResult { get; set; }

    /// <summary>Every setter the code under test called, as "feature=value" (a read-only caller leaves this empty).</summary>
    public static List<string> Writes { get; } = [];

    /// <summary>Whether turning Anti-Lag off makes the driver put Chill back as it was (AMD's documentation says it restores the earlier configuration; this lets a test try both).</summary>
    public static bool DriverRestoresChill { get; set; }

    private static bool? s_chillBeforeAntiLag;

    /// <summary>Runs just before a setter changes anything: lets a test look at what exists on disk at that moment.</summary>
    public static Action? BeforeWrite { get; set; }

    public static int Hybrid { get; set; }

    public static ulong DllVersion { get; set; }

    public static bool HasInitialize2 { get; set; }

    public static AdlxResult InitializeResult { get; set; }

    public static AdlxResult GetGpusResult { get; set; }

    public static AdlxResult ServicesResult { get; set; }

    public static bool AntiLag1Available { get; set; }

    public static bool Tuning1Available { get; set; }

    public static AdlxResult MetricsResult { get; set; }

    public static ulong AskedVersion { get; private set; }

    public static int InitializeCalls { get; private set; }

    public static int TerminateCalls { get; private set; }

    public static bool Initialised { get; private set; }

    public static int CallsAfterTerminate { get; private set; }

    public static int UnexpectedCalls { get; private set; }

    public static int ReleaseUnderflows { get; private set; }

    /// <summary>Whether the fake DLL is loaded (set by <see cref="EntryPoints"/>, cleared when a session unloads it).</summary>
    public static bool LibraryLoaded { get; private set; }

    public static int LoadCount { get; private set; }

    public static int UnloadCount { get; private set; }

    /// <summary>Initialisations of a DLL that was terminated but not unloaded: on the real amdadlx64.dll (RX 9070 XT, 1.5.0.124) this is an access violation, so the fake counts it as a crash.</summary>
    public static int ReinitWithoutReload { get; private set; }

    /// <summary>Initialisations attempted while the DLL was not loaded (an entry used again after its session unloaded it).</summary>
    public static int InitializeWhileUnloaded { get; private set; }

    private static bool s_terminatedSinceLoad;

    public static List<string> Log { get; } = [];

    /// <summary>Objects still holding a reference.</summary>
    public static int LiveReferences => Objects.Values.Where(o => !o.Freed).Sum(o => o.Rc);

    public static int ObjectsEverCreated => Objects.Count;

    public static void Reset(params FakeGpu[] gpus)
    {
        Gpus = [.. gpus.Length == 0 ? [new FakeGpu()] : gpus];
        Displays = [new FakeDisplay()];
        Writes.Clear();
        DriverRestoresChill = false;
        s_chillBeforeAntiLag = null;
        BeforeWrite = null;
        DisplayServicesResult = AdlxResult.Ok;
        Hybrid = 0;
        DllVersion = Version(1, 5, 0, 124);
        HasInitialize2 = true;
        InitializeResult = AdlxResult.Ok;
        GetGpusResult = AdlxResult.Ok;
        ServicesResult = AdlxResult.Ok;
        MetricsResult = AdlxResult.Ok;
        AntiLag1Available = true;
        Tuning1Available = true;
        Objects.Clear();
        AskedVersion = 0;
        InitializeCalls = TerminateCalls = CallsAfterTerminate = UnexpectedCalls = ReleaseUnderflows = 0;
        LibraryLoaded = false;
        LoadCount = UnloadCount = ReinitWithoutReload = InitializeWhileUnloaded = 0;
        s_terminatedSinceLoad = false;
        Initialised = false;
        Log.Clear();
        EnsureVtables();
        s_system = New(0, 0);
    }

    public static ulong Version(int major, int minor, int release, int build) => AdlxVersion.Make(major, minor, release, build).Full;

    /// <summary>One load of the fake DLL, as <c>AdlxEntryPoints.LoadSystemLibrary</c> is one load of the real one: a session that ends terminates ADLX and unloads it.</summary>
    public static AdlxEntryPoints EntryPoints(bool withInitialize2 = true)
    {
        LibraryLoaded = true;
        LoadCount++;
        s_terminatedSinceLoad = false;
        return new(&QueryFullVersion, &Initialize, withInitialize2 ? &Initialize2 : null, &Terminate, 0, static () =>
        {
            LibraryLoaded = false;
            UnloadCount++;
        });
    }

    // ---- bookkeeping ----------------------------------------------------------------------------------------------------------------------------------------------

    private static nint New(int kind, int gpu, string feature = "", bool counted = true, int display = -1)
    {
        var memory = (nint*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
        memory[0] = kind == 0 ? SystemVtable : Vtables[kind];
        var instance = (nint)memory;
        lock (Gate)
        {
            Objects[instance] = new Obj { Kind = kind, Rc = counted && kind != 0 ? 1 : 0, Gpu = gpu, Feature = feature, Display = display };
        }

        return instance;
    }

    private static Obj Touch(nint self, string what)
    {
        lock (Gate)
        {
            Log.Add(what);
            if (!Initialised)
            {
                CallsAfterTerminate++;
            }

            if (!Objects.TryGetValue(self, out var o) || o.Freed)
            {
                CallsAfterTerminate++;
                return new Obj { Kind = -1 };
            }

            return o;
        }
    }

    private static int Unexpected(string what)
    {
        lock (Gate)
        {
            UnexpectedCalls++;
            Log.Add("UNEXPECTED " + what);
        }

        return (int)AdlxResult.Fail;
    }

    private static nint Text(string value)
    {
        var p = Marshal.StringToCoTaskMemUTF8(value);
        lock (Gate)
        {
            Strings.Add(p);
        }

        return p;
    }

    // ---- the DLL's exports ----------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryFullVersion(ulong* version)
    {
        *version = DllVersion;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Initialize(ulong version, nint* system) => DoInitialize(version, system);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Initialize2(ulong version, nint* system, nint* mapping)
    {
        *mapping = 0;
        return DoInitialize(version, system);
    }

    private static int DoInitialize(ulong version, nint* system)
    {
        InitializeCalls++;
        AskedVersion = version;
        if (!LibraryLoaded)
        {
            InitializeWhileUnloaded++;
            return (int)AdlxResult.Fail;
        }

        if (s_terminatedSinceLoad)
        {
            ReinitWithoutReload++;
            return (int)AdlxResult.Fail;
        }

        if (version > DllVersion)
        {
            return (int)AdlxResult.BadVersion;
        }

        if (InitializeResult != AdlxResult.Ok)
        {
            return (int)InitializeResult;
        }

        Initialised = true;
        *system = s_system;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Terminate()
    {
        TerminateCalls++;
        s_terminatedSinceLoad = true;
        Initialised = false;
        return (int)AdlxResult.Ok;
    }

    // ---- common IADLXInterface slots ---------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Acquire(nint self)
    {
        var o = Touch(self, "Acquire");
        return ++o.Rc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Release(nint self)
    {
        var o = Touch(self, "Release");
        if (o.Rc <= 0)
        {
            ReleaseUnderflows++;
            return 0;
        }

        o.Rc--;
        if (o.Rc == 0)
        {
            o.Freed = true;
        }

        return o.Rc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, char* id, nint* result)
    {
        var o = Touch(self, "QueryInterface");
        var name = new string(id);
        if (o.Kind == KToggle && name == "IADLX3DAntiLag1" && AntiLag1Available && o.Feature == "antilag")
        {
            *result = New(KAntiLag1, o.Gpu, "antilag");
            return (int)AdlxResult.Ok;
        }

        if (o.Kind == KTuning && name == "IADLXGPUTuningServices1" && Tuning1Available)
        {
            *result = New(KTuning1, o.Gpu);
            return (int)AdlxResult.Ok;
        }

        *result = 0;
        return (int)AdlxResult.UnknownInterface;
    }

    // ---- system ----------------------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemHybrid(nint self, int* result)
    {
        Touch(self, "GetHybridGraphicsType");
        *result = Hybrid;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemGetDisplays(nint self, nint* result)
    {
        Touch(self, "GetDisplaysServices");
        if (DisplayServicesResult != AdlxResult.Ok)
        {
            return (int)DisplayServicesResult;
        }

        *result = New(KDisplayServices, 0);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemGetGpus(nint self, nint* list)
    {
        Touch(self, "GetGPUs");
        if (GetGpusResult != AdlxResult.Ok)
        {
            return (int)GetGpusResult;
        }

        *list = New(KGpuList, 0);
        return (int)AdlxResult.Ok;
    }

    private static int Service(nint self, nint* result, int kind, string what)
    {
        Touch(self, what);
        if (ServicesResult != AdlxResult.Ok)
        {
            return (int)ServicesResult;
        }

        *result = New(kind, 0);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemGet3D(nint self, nint* result) => Service(self, result, KServices3D, "Get3DSettingsServices");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemGetTuning(nint self, nint* result) => Service(self, result, KTuning, "GetGPUTuningServices");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SystemGetPerf(nint self, nint* result) => Service(self, result, KPerf, "GetPerformanceMonitoringServices");

    // ---- GPU list ----------------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ListSize(nint self)
    {
        Touch(self, "Size");
        return (uint)Gpus.Count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ListAtGpu(nint self, uint index, nint* gpu)
    {
        Touch(self, "At_GPUList");
        if (index >= Gpus.Count)
        {
            return (int)AdlxResult.InvalidArgs;
        }

        if (Gpus[(int)index].AtResult != AdlxResult.Ok)
        {
            return (int)Gpus[(int)index].AtResult;
        }

        *gpu = New(KGpu, (int)index);
        return (int)AdlxResult.Ok;
    }

    // ---- GPU -----------------------------------------------------------------------------------------------------------------------------------------------------------

    private static int GpuText(nint self, byte** result, string what, Func<FakeGpu, string?> get)
    {
        var o = Touch(self, what);
        var gpu = Gpus[o.Gpu];
        var value = get(gpu);
        if (what == "Name" && gpu.NameResult != AdlxResult.Ok)
        {
            return (int)gpu.NameResult;
        }

        *result = value is null ? null : (byte*)Text(value);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuVendor(nint self, byte** result) => GpuText(self, result, "VendorId", g => g.VendorId);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuName(nint self, byte** result) => GpuText(self, result, "Name", g => g.Name);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuDeviceId(nint self, byte** result) => GpuText(self, result, "DeviceId", g => g.DeviceId);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuPnp(nint self, byte** result) => GpuText(self, result, "PNPString", _ => "PCI\\VEN_1002&DEV_7550");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuVramType(nint self, byte** result) => GpuText(self, result, "VRAMType", _ => "GDDR6");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuRevision(nint self, byte** result) => GpuText(self, result, "RevisionId", _ => "C0");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuSubSystem(nint self, byte** result) => GpuText(self, result, "SubSystemId", _ => null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuType(nint self, int* result)
    {
        Touch(self, "Type");
        *result = 2; // discrete
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuExternal(nint self, byte* result)
    {
        Touch(self, "IsExternal");
        *result = 0;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuVram(nint self, uint* result)
    {
        Touch(self, "TotalVRAM");
        *result = 16304;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuBios(nint self, byte** part, byte** version, byte** date)
    {
        Touch(self, "BIOSInfo");
        *part = (byte*)Text("113-TEST");
        *version = (byte*)Text("022.001.002.001");
        *date = (byte*)Text("2026/01/01");
        return (int)AdlxResult.Ok;
    }

    // ---- 3D services and features -------------------------------------------------------------------------------------------------------------------------------

    private static int GetFeature(nint self, nint gpuPointer, nint* result, string feature)
    {
        Touch(self, "Get" + feature);
        var gpu = Objects[gpuPointer].Gpu;
        var f = Gpus[gpu].Features[feature];
        if (f.GetResult != AdlxResult.Ok)
        {
            *result = 0;
            return (int)f.GetResult;
        }

        *result = New(KToggle, gpu, feature);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetAntiLag(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "antilag");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetChill(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "chill");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetBoost(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "boost");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetSharpening(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "sharpening");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetEnhancedSync(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "enhancedsync");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFrameRate(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "frtc");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetWfvr(nint self, nint gpu, nint* result) => GetFeature(self, gpu, result, "wfvr");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetRsr(nint self, nint* result)
    {
        Touch(self, "GetRadeonSuperResolution");
        if (Gpus[0].Features["rsr"].GetResult != AdlxResult.Ok)
        {
            *result = 0;
            return (int)Gpus[0].Features["rsr"].GetResult;
        }

        *result = New(KToggle, 0, "rsr");
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTessellation(nint self, nint gpuPointer, nint* result)
    {
        Touch(self, "GetTessellation");
        *result = New(KValue, Objects[gpuPointer].Gpu, "tessellation");
        return (int)AdlxResult.Ok;
    }

    /// <summary>Slot 5 of a toggle: wait-for-vertical-refresh's GetMode, or SetEnabled of Anti-Lag, SmartAccess Memory and FreeSync; anything else in this slot is a setter nobody may call.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToggleSlot5(nint self, nint argument)
    {
        var o = Touch(self, "slot 5");
        if (o.Feature == "wfvr" && o.Display < 0)
        {
            *(int*)argument = Gpus[o.Gpu].VsyncMode;
            return (int)AdlxResult.Ok;
        }

        return o.Feature is "antilag" or "sam" or "freesync" ? DoSet(o, (byte)argument != 0) : Unexpected("setter in slot 5 of " + o.Feature);
    }

    /// <summary>Slot 7 of a toggle: Radeon Super Resolution's GetSharpness, or Boost's SetEnabled.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToggleSlot7(nint self, nint argument)
    {
        var o = Touch(self, "slot 7");
        if (o.Feature == "rsr")
        {
            *(int*)argument = 80;
            return (int)AdlxResult.Ok;
        }

        return o.Feature == "boost" ? DoSet(o, (byte)argument != 0) : Unexpected("slot 7 of " + o.Feature);
    }

    /// <summary>Slot 8 of a toggle: Chill's SetEnabled.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToggleSlot8(nint self, nint argument)
    {
        var o = Touch(self, "slot 8");
        return o.Feature == "chill" ? DoSet(o, (byte)argument != 0) : Unexpected("slot 8 of " + o.Feature);
    }

    private static int DoSet(Obj o, bool on)
    {
        BeforeWrite?.Invoke();
        lock (Gate)
        {
            Writes.Add($"{o.Feature}={on}");
            var features = o.Display >= 0 ? Displays[o.Display].Features : Gpus[o.Gpu].Features;
            var f = features[o.Feature];
            if (f.WriteResult is not (AdlxResult.Ok or AdlxResult.AlreadyEnabled or AdlxResult.ResetNeeded))
            {
                return (int)f.WriteResult;
            }

            if (f.WriteTakes)
            {
                features[o.Feature] = f with { Enabled = on };
                if (o.Feature == "antilag" && o.Display < 0)
                {
                    // AMD: Anti-Lag and Chill exclude each other; the driver turns Chill off when Anti-Lag goes on.
                    var chill = features["chill"];
                    if (on)
                    {
                        s_chillBeforeAntiLag = chill.Enabled;
                        features["chill"] = chill with { Enabled = false };
                    }
                    else if (DriverRestoresChill && s_chillBeforeAntiLag is { } was)
                    {
                        features["chill"] = chill with { Enabled = was };
                    }
                }
            }

            return (int)f.WriteResult;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuUniqueId(nint self, int* result)
    {
        var o = Touch(self, "UniqueId");
        *result = 2560 + o.Gpu;
        return (int)AdlxResult.Ok;
    }

    private static FakeFeature FeatureOf(Obj o) => o.Display >= 0 ? Displays[o.Display].Features[o.Feature] : Gpus[o.Gpu].Features[o.Feature];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToggleSupported(nint self, byte* result)
    {
        var o = Touch(self, "IsSupported");
        var f = FeatureOf(o);
        if (f.SupportedResult == AdlxResult.Ok)
        {
            *result = f.Supported ? (byte)1 : (byte)0;
        }

        return (int)f.SupportedResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToggleEnabled(nint self, byte* result)
    {
        var o = Touch(self, "IsEnabled");
        var f = FeatureOf(o);
        if (f.EnabledResult == AdlxResult.Ok)
        {
            *result = f.Enabled ? (byte)1 : (byte)0;
        }

        return (int)f.EnabledResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AntiLagLevel(nint self, int* result)
    {
        var o = Touch(self, "GetLevel");
        *result = Gpus[o.Gpu].AntiLagLevel;
        return (int)AdlxResult.Ok;
    }

    // ---- displays -------------------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplaysGet(nint self, nint* result)
    {
        Touch(self, "GetDisplays");
        *result = New(KDisplayList, 0);
        return (int)AdlxResult.Ok;
    }

    private static int DisplayToggle(nint self, nint displayPointer, nint* result, string feature)
    {
        Touch(self, "Get" + feature);
        var display = Objects[displayPointer].Display;
        var f = Displays[display].Features[feature];
        if (f.GetResult != AdlxResult.Ok)
        {
            *result = 0;
            return (int)f.GetResult;
        }

        *result = New(KToggle, 0, feature, display: display);
        return (int)AdlxResult.Ok;
    }

    private static int DisplayValue(nint self, nint displayPointer, nint* result, string feature)
    {
        Touch(self, "Get" + feature);
        *result = New(KValue, 0, feature, display: Objects[displayPointer].Display);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFreeSync(nint self, nint d, nint* result) => DisplayToggle(self, d, result, "freesync");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetVsr(nint self, nint d, nint* result) => DisplayToggle(self, d, result, "vsr");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetGpuScaling(nint self, nint d, nint* result) => DisplayToggle(self, d, result, "gpuscaling");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIntegerScaling(nint self, nint d, nint* result) => DisplayToggle(self, d, result, "integerscaling");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetScalingMode(nint self, nint d, nint* result) => DisplayValue(self, d, result, "scaling");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetColorDepth(nint self, nint d, nint* result) => DisplayValue(self, d, result, "color");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPixelFormat(nint self, nint d, nint* result) => DisplayValue(self, d, result, "pixel");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ValueSupported(nint self, byte* result)
    {
        Touch(self, "IsSupported (value)");
        *result = 1;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ValueGet(nint self, int* result)
    {
        var o = Touch(self, "GetValue");
        *result = o.Feature switch
        {
            "scaling" => Displays[o.Display].ScalingMode,
            "color" => Displays[o.Display].ColorDepth,
            "pixel" => Displays[o.Display].PixelFormat,
            "tessellation" => Gpus[o.Gpu].TessellationMode,
            _ => -1,
        };
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ValueGet2(nint self, int* result)
    {
        var o = Touch(self, "GetLevel");
        if (o.Feature != "tessellation")
        {
            return Unexpected("slot 5 of " + o.Feature);
        }

        *result = Gpus[o.Gpu].TessellationLevel;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint DisplayListSize(nint self)
    {
        Touch(self, "Size (displays)");
        return (uint)Displays.Count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayListAt(nint self, uint index, nint* result)
    {
        Touch(self, "At_DisplayList");
        if (index >= Displays.Count)
        {
            return (int)AdlxResult.InvalidArgs;
        }

        *result = New(KDisplay, 0, display: (int)index);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayManufacturer(nint self, uint* result) { Touch(self, "ManufacturerID"); *result = 0x1E6D; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayType(nint self, int* result) { Touch(self, "DisplayType"); *result = 1; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayConnector(nint self, int* result) { var o = Touch(self, "ConnectorType"); *result = Displays[o.Display].Connector; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayName(nint self, byte** result)
    {
        var o = Touch(self, "Name (display)");
        var d = Displays[o.Display];
        if (d.NameResult != AdlxResult.Ok)
        {
            return (int)d.NameResult;
        }

        *result = (byte*)Text(d.Name);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayResolution(nint self, int* width, int* height)
    {
        var o = Touch(self, "NativeResolution");
        *width = Displays[o.Display].Width;
        *height = Displays[o.Display].Height;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayRefresh(nint self, double* result) { var o = Touch(self, "RefreshRate"); *result = Displays[o.Display].Refresh; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayScan(nint self, int* result) { Touch(self, "ScanType"); *result = 0; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisplayGpu(nint self, nint* result) { Touch(self, "GetGPU"); *result = New(KGpu, 0); return 0; }

    // ---- tuning services -----------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int TuningFlag(nint self, nint gpu, byte* result)
    {
        Touch(self, "IsSupported*Tuning");
        *result = 1;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int TuningAtFactory(nint self, nint gpu, byte* result)
    {
        Touch(self, "IsAtFactory");
        *result = 1;
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int TuningGetSam(nint self, nint gpuPointer, nint* result) => GetFeature(self, gpuPointer, result, "sam");

    // ---- performance -------------------------------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PerfCurrentMetrics(nint self, nint gpuPointer, nint* result)
    {
        Touch(self, "GetCurrentGPUMetrics");
        if (MetricsResult != AdlxResult.Ok)
        {
            return (int)MetricsResult;
        }

        *result = New(KMetrics, Objects[gpuPointer].Gpu);
        return (int)AdlxResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricUsage(nint self, double* result) { Touch(self, "GPUUsage"); *result = 12.5; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricClock(nint self, int* result) { Touch(self, "GPUClockSpeed"); *result = 2400; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricMemoryClock(nint self, int* result) { Touch(self, "GPUVRAMClockSpeed"); *result = 1124; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricTemperature(nint self, double* result) { Touch(self, "GPUTemperature"); *result = 41.5; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricHotspot(nint self, double* result) { Touch(self, "GPUHotspotTemperature"); *result = 52; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricPower(nint self, double* result) { Touch(self, "GPUPower"); *result = 33.25; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricBoardPower(nint self, double* result) { Touch(self, "GPUTotalBoardPower"); *result = 41; return (int)AdlxResult.NotSupported; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricFan(nint self, int* result) { Touch(self, "GPUFanSpeed"); *result = 0; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MetricVoltage(nint self, int* result) { Touch(self, "GPUVoltage"); *result = 850; return 0; }

    // ---- everything that must never be called: setters, resets, interfaces the reader does not use ---------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Never(nint self) => Unexpected("slot with one argument");

    // ---- vtables: the slot order of AMD's ADLX 1.5 C interface structs ----------------------------------------------------------------------------

    private static nint SystemVtable { get; set; }

    private static void EnsureVtables()
    {
        if (Vtables.Count > 0)
        {
            return;
        }

        nint never = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Never;
        nint acquire = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Acquire;
        nint release = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Release;
        nint query = (nint)(delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)&QueryInterface;

        nint[] Make(int slots, params (int Slot, nint Function)[] set)
        {
            var table = new nint[slots];
            Array.Fill(table, never);
            table[0] = acquire;
            table[1] = release;
            table[2] = query;
            foreach (var (slot, function) in set)
            {
                table[slot] = function;
            }

            return table;
        }

        nint Native(nint[] table)
        {
            var memory = (nint*)NativeMemory.AllocZeroed((nuint)(table.Length * sizeof(nint)));
            for (var i = 0; i < table.Length; i++)
            {
                memory[i] = table[i];
            }

            return (nint)memory;
        }

        // IADLXSystem: no Acquire/Release; slot 1 GetGPUs, 7 Get3DSettingsServices, 8 GetGPUTuningServices, 9 GetPerformanceMonitoringServices.
        var system = new nint[12];
        Array.Fill(system, never);
        system[0] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&SystemHybrid;
        system[1] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&SystemGetGpus;
        system[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&SystemGetDisplays;
        system[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&SystemGet3D;
        system[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&SystemGetTuning;
        system[9] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&SystemGetPerf;
        SystemVtable = Native(system);

        Vtables[KGpuList] = Native(Make(13, (3, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ListSize), (11, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&ListAtGpu)));
        Vtables[KGpu] = Native(Make(19,
            (3, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuVendor), (5, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GpuType),
            (6, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&GpuExternal), (7, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuName),
            (9, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuPnp), (11, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GpuVram),
            (12, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuVramType), (13, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, byte**, byte**, int>)&GpuBios),
            (14, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuDeviceId), (15, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuRevision),
            (16, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&GpuSubSystem), (18, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GpuUniqueId)));
        Vtables[KServices3D] = Native(Make(17,
            (3, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetAntiLag), (4, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetChill),
            (5, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetBoost), (6, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetSharpening),
            (7, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetEnhancedSync), (8, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetWfvr),
            (9, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetFrameRate), (13, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetTessellation),
            (14, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetRsr)));
        Vtables[KToggle] = Native(Make(9, (3, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&ToggleSupported), (4, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&ToggleEnabled),
            (5, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ToggleSlot5), (7, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ToggleSlot7),
            (8, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&ToggleSlot8)));
        Vtables[KValue] = Native(Make(6, (3, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&ValueSupported), (4, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&ValueGet),
            (5, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&ValueGet2)));
        Vtables[KDisplayServices] = Native(Make(20,
            (4, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&DisplaysGet), (9, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetFreeSync),
            (10, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetVsr), (11, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetGpuScaling),
            (12, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetScalingMode), (13, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetIntegerScaling),
            (14, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetColorDepth), (15, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetPixelFormat)));
        Vtables[KDisplayList] = Native(Make(13, (3, (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&DisplayListSize), (11, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&DisplayListAt)));
        Vtables[KDisplay] = Native(Make(14,
            (3, (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&DisplayManufacturer), (4, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&DisplayType),
            (5, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&DisplayConnector), (6, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, int>)&DisplayName),
            (8, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int*, int>)&DisplayResolution), (9, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&DisplayRefresh),
            (11, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&DisplayScan), (12, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&DisplayGpu)));
        Vtables[KAntiLag1] = Native(Make(8, (3, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&ToggleSupported), (4, (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&ToggleEnabled),
            (6, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&AntiLagLevel)));
        var tuningSlots = new List<(int, nint)> { (4, (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)&TuningAtFactory) };
        for (var slot = 6; slot <= 11; slot++)
        {
            tuningSlots.Add((slot, (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)&TuningFlag));
        }

        Vtables[KTuning] = Native(Make(18, [.. tuningSlots]));
        Vtables[KTuning1] = Native(Make(19, [.. tuningSlots, (18, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&TuningGetSam)]));
        Vtables[KPerf] = Native(Make(23, (18, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&PerfCurrentMetrics)));
        Vtables[KMetrics] = Native(Make(15,
            (4, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&MetricUsage), (5, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MetricClock),
            (6, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MetricMemoryClock), (7, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&MetricTemperature),
            (8, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&MetricHotspot), (9, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&MetricPower),
            (10, (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&MetricBoardPower), (11, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MetricFan),
            (13, (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&MetricVoltage)));
    }
}
