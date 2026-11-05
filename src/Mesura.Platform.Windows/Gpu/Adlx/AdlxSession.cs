using System.Runtime.InteropServices;

namespace Mesura.Platform.Windows.Gpu.Adlx;

/// <summary>ADLX_RESULT, in the order AMD's <c>ADLXDefines.h</c> declares it (the values are the ABI).</summary>
internal enum AdlxResult
{
    Ok = 0,
    AlreadyEnabled,
    AlreadyInitialized,
    Fail,
    InvalidArgs,
    BadVersion,
    UnknownInterface,
    Terminated,
    AdlInitError,
    NotFound,
    InvalidObject,
    OrphanObjects,
    NotSupported,
    PendingOperation,
    GpuInactive,
    GpuInUse,
    TimeoutOperation,
    NotActive,
    ResetNeeded,
}

internal static class AdlxResultExtensions
{
    /// <summary>ADLX_SUCCEEDED: ok, already enabled and already initialised all count as success.</summary>
    public static bool Succeeded(this AdlxResult result) => result is AdlxResult.Ok or AdlxResult.AlreadyEnabled or AdlxResult.AlreadyInitialized;
}

/// <summary>An ADLX version as the DLL reports it: four 16-bit parts in one 64-bit number (major, minor, release, build).</summary>
internal readonly record struct AdlxVersion(ulong Full)
{
    public int Major => (int)((Full >> 48) & 0xFFFF);

    public int Minor => (int)((Full >> 32) & 0xFFFF);

    public int Release => (int)((Full >> 16) & 0xFFFF);

    public int Build => (int)(Full & 0xFFFF);

    public static AdlxVersion Make(int major, int minor, int release, int build) =>
        new(((ulong)major << 48) | ((ulong)minor << 32) | ((ulong)release << 16) | (uint)build);

    public override string ToString() => $"{Major}.{Minor}.{Release}.{Build}";
}

/// <summary>Why ADLX could not be opened, in words a report can carry.</summary>
internal enum AdlxOpenFailure
{
    None,

    /// <summary>No <c>amdadlx64.dll</c> in the system folder: no AMD driver with ADLX.</summary>
    DllMissing,

    /// <summary>The DLL is there but could not be loaded, or lacks an entry point every ADLX has.</summary>
    DllUnusable,

    /// <summary>The DLL did not report its version.</summary>
    VersionUnreadable,

    /// <summary>The driver refused the version Mesura asked for, or ADLX could not start.</summary>
    InitializeFailed,
}

/// <summary>
/// The ADLX entry points of <c>amdadlx64.dll</c> as function pointers: the real library (<see cref="LoadSystemLibrary"/>) or a test double. <see cref="Initialize2"/> is
/// optional (older drivers lack it, as AMD's own helper handles); everything else is required.
/// <para>
/// One load serves ONE session: <see cref="AdlxSession"/> terminates ADLX and then calls <see cref="Unload"/>, exactly as AMD's <c>ADLXHelper::Terminate</c> does (Terminate, then FreeLibrary; the
/// next initialisation loads the DLL again). Calling ADLXInitialize2 a second time on a DLL that stayed loaded after ADLXTerminate crashed amdadlx64.dll with an access violation (0xC0000005) on
/// the real driver (RX 9070 XT, ADLX 1.5.0.124, 2026-11-04), so a second session needs a new <see cref="LoadSystemLibrary"/>.
/// </para>
/// </summary>
internal sealed unsafe class AdlxEntryPoints(
    delegate* unmanaged[Cdecl]<ulong*, int> queryFullVersion,
    delegate* unmanaged[Cdecl]<ulong, nint*, int> initialize,
    delegate* unmanaged[Cdecl]<ulong, nint*, nint*, int> initialize2,
    delegate* unmanaged[Cdecl]<int> terminate,
    nint library = 0,
    Action? onUnload = null)
{
    private bool _unloaded;

    private const string DllName = "amdadlx64.dll";

    public delegate* unmanaged[Cdecl]<ulong*, int> QueryFullVersion { get; } = queryFullVersion;

    public delegate* unmanaged[Cdecl]<ulong, nint*, int> Initialize { get; } = initialize;

    public delegate* unmanaged[Cdecl]<ulong, nint*, nint*, int> Initialize2 { get; } = initialize2;

    public delegate* unmanaged[Cdecl]<int> Terminate { get; } = terminate;

    /// <summary>The module handle of the real library (0 for a test double).</summary>
    public nint Library { get; } = library;

    /// <summary>Unloads the DLL (once), after ADLXTerminate, as AMD's helper does. Nothing may call a function pointer of this object afterwards.</summary>
    public void Unload()
    {
        if (_unloaded)
        {
            return;
        }

        _unloaded = true;
        if (Library != 0)
        {
            NativeLibrary.Free(Library);
        }

        onUnload?.Invoke();
    }

    /// <summary>
    /// Loads the DLL from Windows' system folder by its full path (never by name, so no other folder, the current directory or the application's own is searched), or says why not.
    /// </summary>
    public static AdlxEntryPoints? LoadSystemLibrary(out AdlxOpenFailure failure)
    {
        var path = Path.Combine(Environment.SystemDirectory, DllName);
        if (!File.Exists(path))
        {
            failure = AdlxOpenFailure.DllMissing;
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out var module))
        {
            failure = AdlxOpenFailure.DllUnusable;
            return null;
        }

        if (!NativeLibrary.TryGetExport(module, "ADLXQueryFullVersion", out var query)
            || !NativeLibrary.TryGetExport(module, "ADLXInitialize", out var initialize)
            || !NativeLibrary.TryGetExport(module, "ADLXTerminate", out var terminate))
        {
            NativeLibrary.Free(module);
            failure = AdlxOpenFailure.DllUnusable;
            return null;
        }

        nint initialize2 = NativeLibrary.TryGetExport(module, "ADLXInitialize2", out var second) ? second : 0;
        failure = AdlxOpenFailure.None;
        return new AdlxEntryPoints(
            (delegate* unmanaged[Cdecl]<ulong*, int>)query,
            (delegate* unmanaged[Cdecl]<ulong, nint*, int>)initialize,
            initialize2 == 0 ? null : (delegate* unmanaged[Cdecl]<ulong, nint*, nint*, int>)initialize2,
            (delegate* unmanaged[Cdecl]<int>)terminate,
            module);
    }
}

/// <summary>
/// One initialised ADLX: the system object plus every COM-like object obtained from it, released in the reverse order they were obtained and before ADLX is terminated. ADLX objects are
/// reference counted but not safe to use after <c>ADLXTerminate</c>, so every wrapper asks the session first and refuses with <see cref="ObjectDisposedException"/> once it is closed
/// (a managed exception instead of a call into a terminated library, which would crash the process). Not thread safe: one session, one thread.
/// </summary>
internal sealed unsafe class AdlxSession : IDisposable
{
    /// <summary>
    /// The highest ADLX version Mesura's interface tables were written for (1.5). A newer driver is asked for no more than this, so the interfaces behave as the tables say; an older driver
    /// is asked for its own version, which it cannot refuse, and the optional interfaces (Anti-Lag levels, SmartAccess Memory) are found by asking, never assumed.
    /// </summary>
    public static readonly AdlxVersion Basis = AdlxVersion.Make(1, 5, 0, 124);

    private readonly AdlxEntryPoints _entry;
    private readonly List<AdlxObject> _live = [];
    private nint _system;
    private bool _closed;

    private AdlxSession(AdlxEntryPoints entry, AdlxVersion dll, AdlxVersion asked, nint system)
    {
        _entry = entry;
        DllVersion = dll;
        AskedVersion = asked;
        _system = system;
    }

    public AdlxVersion DllVersion { get; }

    public AdlxVersion AskedVersion { get; }

    public bool Closed => _closed;

    /// <summary>
    /// Starts ADLX through <paramref name="entry"/>: reads the DLL's version, asks for the lower of it and <see cref="Basis"/>, and keeps the system object. Never throws.
    /// Takes over <paramref name="entry"/>: it is unloaded when this fails and when the session is disposed, so each session needs an entry of its own (a fresh load).
    /// </summary>
    public static AdlxSession? TryOpen(AdlxEntryPoints entry, out AdlxOpenFailure failure, out AdlxResult result)
    {
        ArgumentNullException.ThrowIfNull(entry);
        result = AdlxResult.Fail;
        ulong full = 0;
        if (!((AdlxResult)entry.QueryFullVersion(&full)).Succeeded() || full == 0)
        {
            failure = AdlxOpenFailure.VersionUnreadable;
            entry.Unload();
            return null;
        }

        var dll = new AdlxVersion(full);
        var asked = dll.Full < Basis.Full ? dll : Basis;
        nint system = 0;
        result = entry.Initialize2 != null
            ? InitializeWithMapping(entry, asked, &system)
            : (AdlxResult)entry.Initialize(asked.Full, &system);
        if (!result.Succeeded() || system == 0)
        {
            failure = AdlxOpenFailure.InitializeFailed;
            entry.Unload();
            return null;
        }

        failure = AdlxOpenFailure.None;
        return new AdlxSession(entry, dll, asked, system);
    }

    private static AdlxResult InitializeWithMapping(AdlxEntryPoints entry, AdlxVersion asked, nint* system)
    {
        // ADLXInitialize2 also hands back an ADL mapping object Mesura has no use for; it is neither used nor released (it belongs to ADLX until Terminate).
        nint mapping = 0;
        return (AdlxResult)entry.Initialize2(asked.Full, system, &mapping);
    }

    /// <summary>The ADLX system object (not reference counted; owned by ADLX until Terminate).</summary>
    internal nint System
    {
        get
        {
            ThrowIfClosed();
            return _system;
        }
    }

    internal void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);

    internal void Register(AdlxObject item) => _live.Add(item);

    internal void Forget(AdlxObject item) => _live.Remove(item);

    /// <summary>Releases everything still held (newest first), then terminates ADLX once. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        for (var i = _live.Count - 1; i >= 0; i--)
        {
            _live[i].ReleaseNow();
        }

        _live.Clear();
        _closed = true;
        _system = 0;
        _entry.Terminate();
        _entry.Unload();
    }

    // ---- calls through a COM-like object's vtable (slot numbers from the C interface structs of AMD's ADLX 1.5 headers) ------------------------------

    /// <summary>The function stored in vtable slot <paramref name="slot"/> of the object at <paramref name="instance"/>.</summary>
    internal static nint Slot(nint instance, int slot)
    {
        var table = *(nint**)instance;
        return table[slot];
    }
}

/// <summary>
/// A reference-counted ADLX object the session obtained (a list, a GPU, a service, a feature): released exactly once, by <see cref="Dispose"/> or by the session closing. ADLX objects whose
/// vtable starts with <c>Acquire</c>/<c>Release</c>/<c>QueryInterface</c> (slots 0, 1, 2) are the ones wrapped here; the system object is not one of them.
/// </summary>
internal abstract unsafe class AdlxObject : IDisposable
{
    private nint _pointer;

    protected AdlxObject(AdlxSession session, nint pointer)
    {
        Session = session;
        _pointer = pointer;
        session.Register(this);
    }

    protected AdlxSession Session { get; }

    /// <summary>The object's pointer, for passing to a call that takes it; refuses when it was released or the session closed.</summary>
    internal nint Pointer
    {
        get
        {
            Session.ThrowIfClosed();
            ObjectDisposedException.ThrowIf(_pointer == 0, this);
            return _pointer;
        }
    }

    /// <summary>Releases the reference (once). The session closing calls this for what the caller did not.</summary>
    internal void ReleaseNow()
    {
        var pointer = _pointer;
        _pointer = 0;
        if (pointer != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)AdlxSession.Slot(pointer, 1))(pointer);
        }
    }

    public void Dispose()
    {
        if (!Session.Closed)
        {
            ReleaseNow();
            Session.Forget(this);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <c>QueryInterface</c> (slot 2) with the interface's id (its name as a wide string). The interface returned has its own reference, which the caller owns;
    /// <see cref="AdlxResult.UnknownInterface"/> (an older driver that lacks it) is an answer, not an error.
    /// </summary>
    protected AdlxResult Query(string interfaceId, out nint result)
    {
        nint found = 0;
        fixed (char* id = interfaceId)
        {
            var status = (AdlxResult)((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)AdlxSession.Slot(Pointer, 2))(Pointer, id, &found);
            result = status.Succeeded() ? found : 0;
            return status;
        }
    }
}
