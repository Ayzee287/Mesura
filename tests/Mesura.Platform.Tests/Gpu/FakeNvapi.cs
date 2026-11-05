using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mesura.Platform.Windows.Gpu.Nvapi;

namespace Mesura.Platform.Tests.Gpu;

internal sealed record FakeNvDisplay(uint Id, uint Flags, uint VrrFlags, int VrrStatus = 0, string? Gdi = null);

internal sealed record FakeNvSetting(uint Value, int Location = 2, bool Predefined = false);

/// <summary>
/// A stand-in for <c>nvapi64.dll</c> that is real at the ABI level: <c>nvapi_QueryInterface</c> hands out native function pointers by NVIDIA's interface ids, and each function checks the structure
/// version it is given, as the real one does (a wrong version answers INCOMPATIBLE_STRUCT_VERSION). It records every interface id the code asked for, so a test can say that no setter or save function was
/// ever fetched, and counts sessions opened and closed. It proves the layout and the lifetime Mesura assumes against itself; it says nothing about NVIDIA's driver.
/// </summary>
internal static unsafe class FakeNvapi
{
    private static readonly object Gate = new();

    public static string[] GpuNames { get; set; } = ["NVIDIA GeForce RTX 3080"];

    public static List<FakeNvDisplay> Displays { get; } = [];

    public static Dictionary<uint, FakeNvSetting> BaseSettings { get; } = [];

    public static HashSet<uint> Missing { get; } = [];

    public static int InitializeStatus { get; set; }

    public static bool GlobalIsBase { get; set; } = true;

    public static HashSet<uint> Requested { get; } = [];

    public static int UnloadCalls { get; private set; }

    public static int SessionsOpened { get; private set; }

    public static int SessionsDestroyed { get; private set; }

    public static int CallsAfterUnload { get; private set; }

    public static bool Initialised { get; private set; }

    public static void Reset()
    {
        GpuNames = ["NVIDIA GeForce RTX 3080"];
        Displays.Clear();
        Displays.Add(new FakeNvDisplay(0x80061088, Flags: (1u << 2) | (1u << 6), VrrFlags: 0, Gdi: @"\\.\DISPLAY1"));
        BaseSettings.Clear();
        Missing.Clear();
        Requested.Clear();
        InitializeStatus = 0;
        GlobalIsBase = true;
        UnloadCalls = SessionsOpened = SessionsDestroyed = CallsAfterUnload = 0;
        Initialised = false;
    }

    public static NvapiEntryPoints EntryPoints() => new(&QueryInterface);

    private static void Use()
    {
        lock (Gate)
        {
            if (!Initialised)
            {
                CallsAfterUnload++;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint QueryInterface(uint id)
    {
        lock (Gate)
        {
            Requested.Add(id);
            if (Missing.Contains(id))
            {
                return 0;
            }
        }

        return id switch
        {
            0x0150e828 => (nint)(delegate* unmanaged[Cdecl]<int>)&Initialize,
            0xd22bdd7e => (nint)(delegate* unmanaged[Cdecl]<int>)&Unload,
            0x2926aaad => (nint)(delegate* unmanaged[Cdecl]<uint*, byte*, int>)&DriverVersion,
            0xe5ac921f => (nint)(delegate* unmanaged[Cdecl]<nint*, uint*, int>)&EnumGpus,
            0xceee8e9f => (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&GpuName,
            0x0078dba2 => (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint*, uint, int>)&ConnectedIds,
            0xdf8fda57 => (nint)(delegate* unmanaged[Cdecl]<uint, byte*, int>)&VrrInfo,
            0xae457190 => (nint)(delegate* unmanaged[Cdecl]<byte*, uint*, int>)&DisplayIdByName,
            0x0694d52e => (nint)(delegate* unmanaged[Cdecl]<nint*, int>)&DrsCreate,
            0xdad9cff8 => (nint)(delegate* unmanaged[Cdecl]<nint, int>)&DrsDestroy,
            0x375dbd6b => (nint)(delegate* unmanaged[Cdecl]<nint, int>)&DrsLoad,
            0xda8466a0 => (nint)(delegate* unmanaged[Cdecl]<nint, nint*, int>)&DrsBase,
            0x617bff9f => (nint)(delegate* unmanaged[Cdecl]<nint, nint*, int>)&DrsGlobal,
            0x73bf8338 => (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, int>)&DrsGetSetting,
            _ => 0, // an id the fake does not know: any setter, save or restore function lands here and is recorded in Requested
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Initialize()
    {
        if (InitializeStatus == 0)
        {
            Initialised = true;
        }

        return InitializeStatus;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unload()
    {
        UnloadCalls++;
        Initialised = false;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DriverVersion(uint* version, byte* branch)
    {
        Use();
        *version = 61088;
        var text = "r610_85"u8;
        for (var i = 0; i < text.Length; i++)
        {
            branch[i] = text[i];
        }

        branch[text.Length] = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EnumGpus(nint* handles, uint* count)
    {
        Use();
        for (var i = 0; i < GpuNames.Length; i++)
        {
            handles[i] = i + 1;
        }

        *count = (uint)GpuNames.Length;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GpuName(nint handle, byte* text)
    {
        Use();
        var bytes = System.Text.Encoding.ASCII.GetBytes(GpuNames[(int)handle - 1] + "\0");
        for (var i = 0; i < bytes.Length; i++)
        {
            text[i] = bytes[i];
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConnectedIds(nint gpu, byte* ids, uint* count, uint flags)
    {
        Use();
        if (ids == null)
        {
            *count = (uint)Displays.Count;
            return 0;
        }

        for (var i = 0; i < Displays.Count; i++)
        {
            var element = ids + (i * 16);
            if (*(uint*)element != 0x00030010)
            {
                return NvapiStatus.IncompatibleStructVersion; // the real driver refuses a wrong version
            }

            *(uint*)(element + 8) = Displays[i].Id;
            *(uint*)(element + 12) = Displays[i].Flags;
        }

        *count = (uint)Displays.Count;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VrrInfo(uint displayId, byte* info)
    {
        Use();
        if (*(uint*)info != 0x00010018)
        {
            return NvapiStatus.IncompatibleStructVersion;
        }

        var display = Displays.First(d => d.Id == displayId);
        if (display.VrrStatus != 0)
        {
            return display.VrrStatus;
        }

        *(uint*)(info + 4) = display.VrrFlags;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DisplayIdByName(byte* name, uint* id)
    {
        Use();
        var text = Marshal.PtrToStringAnsi((nint)name);
        var found = Displays.FirstOrDefault(d => d.Gdi == text);
        if (found is null)
        {
            return -6;
        }

        *id = found.Id;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsCreate(nint* session)
    {
        Use();
        SessionsOpened++;
        *session = 0x1000;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsDestroy(nint session)
    {
        SessionsDestroyed++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsLoad(nint session) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsBase(nint session, nint* profile)
    {
        *profile = 0x2000;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsGlobal(nint session, nint* profile)
    {
        *profile = GlobalIsBase ? 0x2000 : 0x3000;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DrsGetSetting(nint session, nint profile, uint id, byte* setting)
    {
        Use();
        if (*(uint*)setting != 0x00013020)
        {
            return NvapiStatus.IncompatibleStructVersion;
        }

        if (profile != 0x2000 || !BaseSettings.TryGetValue(id, out var found))
        {
            return NvapiStatus.SettingNotFound;
        }

        *(int*)(setting + 4104) = 0;                        // a DWORD setting
        *(int*)(setting + 4108) = found.Location;
        *(uint*)(setting + 4112) = found.Predefined ? 1u : 0u;
        *(uint*)(setting + 8220) = found.Value;
        return 0;
    }
}
