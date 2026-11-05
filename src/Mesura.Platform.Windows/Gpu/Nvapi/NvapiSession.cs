using System.Runtime.InteropServices;

namespace Mesura.Platform.Windows.Gpu.Nvapi;

/// <summary>Why NVAPI could not be opened, in words a report can carry.</summary>
internal enum NvapiOpenFailure
{
    None,

    /// <summary>No <c>nvapi64.dll</c> in the system folder: no NVIDIA driver.</summary>
    DllMissing,

    /// <summary>The DLL is there but could not be loaded, or has no <c>nvapi_QueryInterface</c>.</summary>
    DllUnusable,

    /// <summary><c>NvAPI_Initialize</c> failed.</summary>
    InitializeFailed,
}

/// <summary>NVAPI's status codes that the reader tells apart (<c>nvapi_lite_common.h</c>); any other value is carried as its number.</summary>
internal static class NvapiStatus
{
    public const int Ok = 0;
    public const int Error = -1;
    public const int EndEnumeration = -7;
    public const int IncompatibleStructVersion = -9;
    public const int SettingNotFound = -160;

    public static string Name(int status) => status switch
    {
        Ok => "OK",
        EndEnumeration => "END_ENUMERATION",
        IncompatibleStructVersion => "INCOMPATIBLE_STRUCT_VERSION",
        SettingNotFound => "SETTING_NOT_FOUND",
        -1 => "ERROR",
        -3 => "NO_IMPLEMENTATION",
        -4 => "API_NOT_INITIALIZED",
        -5 => "INVALID_ARGUMENT",
        -6 => "NVIDIA_DEVICE_NOT_FOUND",
        -8 => "INVALID_HANDLE",
        _ => $"status {status}",
    };
}

/// <summary>
/// The one export of <c>nvapi64.dll</c>, <c>nvapi_QueryInterface</c>, which turns NVIDIA's documented interface ids (<c>nvapi_interface.h</c>, MIT licence) into function pointers: the real library
/// (<see cref="LoadSystemLibrary"/>) or a test double.
/// </summary>
internal sealed unsafe class NvapiEntryPoints(delegate* unmanaged[Cdecl]<uint, nint> queryInterface, nint library = 0)
{
    private const string DllName = "nvapi64.dll";

    public delegate* unmanaged[Cdecl]<uint, nint> QueryInterface { get; } = queryInterface;

    public nint Library { get; } = library;

    /// <summary>Loads the DLL from Windows' system folder by its full path (never by name), or says why not.</summary>
    public static NvapiEntryPoints? LoadSystemLibrary(out NvapiOpenFailure failure)
    {
        var path = Path.Combine(Environment.SystemDirectory, DllName);
        if (!File.Exists(path))
        {
            failure = NvapiOpenFailure.DllMissing;
            return null;
        }

        if (!NativeLibrary.TryLoad(path, out var module) || !NativeLibrary.TryGetExport(module, "nvapi_QueryInterface", out var query))
        {
            failure = NvapiOpenFailure.DllUnusable;
            return null;
        }

        failure = NvapiOpenFailure.None;
        return new((delegate* unmanaged[Cdecl]<uint, nint>)query, module);
    }
}

/// <summary>An initialised NVAPI. Closing it calls <c>NvAPI_Unload</c> once.</summary>
internal sealed unsafe class NvapiSession : IDisposable
{
    public const uint IdInitialize = 0x0150e828;
    public const uint IdUnload = 0xd22bdd7e;

    private readonly NvapiEntryPoints _entry;
    private bool _closed;

    private NvapiSession(NvapiEntryPoints entry) => _entry = entry;

    public static NvapiSession? TryOpen(NvapiEntryPoints entry, out NvapiOpenFailure failure, out int status)
    {
        status = NvapiStatus.Error;
        var initialize = entry.QueryInterface(IdInitialize);
        if (initialize == 0)
        {
            failure = NvapiOpenFailure.DllUnusable;
            return null;
        }

        status = ((delegate* unmanaged[Cdecl]<int>)initialize)();
        if (status != NvapiStatus.Ok)
        {
            failure = NvapiOpenFailure.InitializeFailed;
            return null;
        }

        failure = NvapiOpenFailure.None;
        return new NvapiSession(entry);
    }

    /// <summary>The function behind an interface id, or 0 when this driver does not have it (a missing function is "unknown", never an error to swallow).</summary>
    public nint Function(uint id)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return _entry.QueryInterface(id);
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        var unload = _entry.QueryInterface(IdUnload);
        if (unload != 0)
        {
            _ = ((delegate* unmanaged[Cdecl]<int>)unload)();
        }
    }
}
