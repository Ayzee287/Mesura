using System.Globalization;
using System.Runtime.InteropServices;
using Mesura.Platform.Windows.Gpu.Nvapi;

namespace Mesura.Platform.Windows.Gpu;

/// <summary>Whether Mesura could talk to NVIDIA's NVAPI on this PC, and if not, why.</summary>
public enum NvidiaAvailability
{
    Available,

    /// <summary>No <c>nvapi64.dll</c>: no NVIDIA driver.</summary>
    NoDriver,

    /// <summary>The DLL is present but unusable.</summary>
    Unusable,

    /// <summary>The DLL loads but NVAPI would not initialise.</summary>
    NotStarted,
}

/// <summary>
/// What the driver says about one display on an NVIDIA GPU: whether variable refresh (G-SYNC or G-SYNC Compatible) is possible for it, requested and active (<c>NvAPI_Disp_GetVRRInfo</c>, driver 525 and
/// later). Every answer the call did not give is <c>null</c>, with the status in <see cref="Problem"/>.
/// </summary>
public sealed record NvidiaDisplayReport(int Index, string? GdiName, bool? Active, bool? Connected, bool? Mst, bool? VrrPossible, bool? VrrRequested, bool? VrrEnabled, bool? InVrrMode, string? Problem);

public sealed record NvidiaGpuReport(int Index, string? Name, IReadOnlyList<NvidiaDisplayReport> Displays);

/// <summary>
/// One setting read directly from a driver profile (<c>NvAPI_DRS_GetSetting</c>). <see cref="Set"/> is false when the profile does not hold the setting (the driver's own default is in force); the value is
/// only given when the profile holds it. <see cref="Status"/> is NVAPI's answer, by name.
/// </summary>
public sealed record NvidiaSettingReading(string Id, string Label, bool? Set, uint? Value, string? Source, string Status);

/// <summary>A driver profile and the key settings read from it. The Base Profile is what NVIDIA Control Panel's "Global settings" edit.</summary>
public sealed record NvidiaProfileReport(string Kind, bool? SameAsBase, IReadOnlyList<NvidiaSettingReading> Settings, string? Problem);

public sealed record NvidiaReport(NvidiaAvailability Availability, string? DriverVersion, string? Branch, IReadOnlyList<NvidiaGpuReport> Gpus, IReadOnlyList<NvidiaProfileReport> Profiles, string? Failure);

/// <summary>
/// Reads what NVIDIA's driver says through NVAPI, in this process: the GPUs, each display's variable-refresh state, and the key V-Sync, latency and G-SYNC settings of the Base Profile (read
/// directly, so a setting nobody changed shows as "not set: the driver default"). Read only: no setter or save function is ever fetched. As with ADLX, a crash inside NVIDIA's DLL cannot be caught by managed
/// code, so a caller that must survive one starts <c>mesura gpu nvidia</c> as a child process.
/// </summary>
public static unsafe class NvidiaGpuReader
{
    private const uint IdDriverVersion = 0x2926aaad;
    private const uint IdEnumGpus = 0xe5ac921f;
    private const uint IdGpuName = 0xceee8e9f;
    private const uint IdConnectedIds = 0x0078dba2;
    private const uint IdVrrInfo = 0xdf8fda57;
    private const uint IdDisplayIdByName = 0xae457190;
    private const uint IdDrsCreate = 0x0694d52e;
    private const uint IdDrsDestroy = 0xdad9cff8;
    private const uint IdDrsLoad = 0x375dbd6b;
    private const uint IdDrsBase = 0xda8466a0;
    private const uint IdDrsGlobal = 0x617bff9f;
    private const uint IdDrsGetSetting = 0x73bf8338;

    // Structure sizes and versions, as NVIDIA's header gives them (nvapi.h of github.com/NVIDIA/nvapi, measured with the C++ compiler): the version is sizeof | (n << 16).
    private const uint DisplayIdsVersion = 0x00030010; // NV_GPU_DISPLAYIDS_VER (struct version 3, 16 bytes)
    private const uint VrrInfoVersion = 0x00010018;    // NV_GET_VRR_INFO_VER1 (24 bytes)
    private const uint SettingVersion = 0x00013020;    // NVDRS_SETTING_VER (12320 bytes)
    private const int SettingSize = 12320;
    private const int SettingTypeOffset = 4104;
    private const int SettingLocationOffset = 4108;
    private const int SettingIsPredefinedOffset = 4112;
    private const int SettingCurrentValueOffset = 8220;

    /// <summary>The settings Mesura's V-Sync, latency and variable-refresh decisions are about (ids from NVIDIA's public header, confirmed by the driver on the first real run, 2026-11-03).</summary>
    internal static readonly (uint Id, string Label)[] KeySettings =
    [
        (0x00A879CF, "Vertical Sync"), (0x005A375C, "Vertical Sync Tear Control"), (0x10FDEC23, "Vsync - Behavior Flags"), (0x10835002, "Frame Rate Limiter"), (0x10835016, "Idle Application Max FPS Limit"),
        (0x1194F158, "Enable G-SYNC globally"), (0x1094F1F7, "VRR requested state"), (0x1094F157, "Toggle the VRR global feature"), (0x10A879CF, "G-SYNC (VRR app override)"), (0x10A879CE, "Variable refresh Rate"),
        (0x007BA09E, "Maximum pre-rendered frames"), (0x1057EB71, "Power management mode"), (0x00AE785C, "PowerThrottle"), (0x0064B541, "Preferred refresh rate"),
        (0x00198FFF, "Shader Cache"), (0x00AC8497, "Shader disk cache maximum size"), (0x20C1221E, "Threaded optimization"),
    ];

    /// <summary>Reads this PC through the real <c>nvapi64.dll</c>.</summary>
    public static NvidiaReport Read()
    {
        var entry = NvapiEntryPoints.LoadSystemLibrary(out var failure);
        return entry is null ? new(failure == NvapiOpenFailure.DllMissing ? NvidiaAvailability.NoDriver : NvidiaAvailability.Unusable, null, null, [], [], failure.ToString()) : Read(entry);
    }

    internal static NvidiaReport Read(NvapiEntryPoints entry)
    {
        using var session = NvapiSession.TryOpen(entry, out var failure, out var status);
        if (session is null)
        {
            return new(failure == NvapiOpenFailure.InitializeFailed ? NvidiaAvailability.NotStarted : NvidiaAvailability.Unusable, null, null, [], [],
                failure == NvapiOpenFailure.InitializeFailed ? $"NvAPI_Initialize answered {NvapiStatus.Name(status)}." : failure.ToString());
        }

        var (version, branch) = DriverVersion(session);
        return new(NvidiaAvailability.Available, version, branch, Gpus(session), Profiles(session), null);
    }

    private static (string? Version, string? Branch) DriverVersion(NvapiSession session)
    {
        var function = session.Function(IdDriverVersion);
        if (function == 0)
        {
            return (null, null);
        }

        uint version = 0;
        var branch = stackalloc byte[64];
        var result = ((delegate* unmanaged[Cdecl]<uint*, byte*, int>)function)(&version, branch);
        return result != NvapiStatus.Ok ? (null, null) : ($"{(version / 100).ToString(CultureInfo.InvariantCulture)}.{(version % 100).ToString("00", CultureInfo.InvariantCulture)}", Marshal.PtrToStringUTF8((nint)branch));
    }

    private static IReadOnlyList<NvidiaGpuReport> Gpus(NvapiSession session)
    {
        var enumerate = session.Function(IdEnumGpus);
        if (enumerate == 0)
        {
            return [];
        }

        var handles = stackalloc nint[64];
        uint count = 0;
        if (((delegate* unmanaged[Cdecl]<nint*, uint*, int>)enumerate)(handles, &count) != NvapiStatus.Ok)
        {
            return [];
        }

        var gdi = GdiDisplayIds(session);
        var gpus = new List<NvidiaGpuReport>();
        var text = stackalloc byte[64];
        for (uint i = 0; i < count && i < 64; i++)
        {
            string? name = null;
            var nameFunction = session.Function(IdGpuName);
            if (nameFunction != 0)
            {
                name = ((delegate* unmanaged[Cdecl]<nint, byte*, int>)nameFunction)(handles[i], text) == NvapiStatus.Ok ? Marshal.PtrToStringUTF8((nint)text) : null;
            }

            gpus.Add(new((int)i, name, Displays(session, handles[i], gdi)));
        }

        return gpus;
    }

    /// <summary>Which Windows output (<c>\\.\DISPLAYn</c>) each NVAPI display id is: asked for the first 16 output names, so the VRR state can be tied to the refresh rate Windows shows for that output.</summary>
    private static Dictionary<uint, string> GdiDisplayIds(NvapiSession session)
    {
        var map = new Dictionary<uint, string>();
        var function = session.Function(IdDisplayIdByName);
        if (function == 0)
        {
            return map;
        }

        for (var n = 1; n <= 16; n++)
        {
            var name = $@"\\.\DISPLAY{n.ToString(CultureInfo.InvariantCulture)}";
            var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            uint id = 0;
            fixed (byte* p = bytes)
            {
                if (((delegate* unmanaged[Cdecl]<byte*, uint*, int>)function)(p, &id) == NvapiStatus.Ok && id != 0)
                {
                    map[id] = name;
                }
            }
        }

        return map;
    }

    private static IReadOnlyList<NvidiaDisplayReport> Displays(NvapiSession session, nint gpu, Dictionary<uint, string> gdi)
    {
        var connected = session.Function(IdConnectedIds);
        if (connected == 0)
        {
            return [];
        }

        uint count = 0;
        var counted = ((delegate* unmanaged[Cdecl]<nint, byte*, uint*, uint, int>)connected)(gpu, null, &count, 0);
        if (counted != NvapiStatus.Ok || count == 0)
        {
            return counted == NvapiStatus.Ok ? [] : [new(-1, null, null, null, null, null, null, null, null, $"display count: {NvapiStatus.Name(counted)}")];
        }

        var bytes = stackalloc byte[(int)(count * 16)];
        for (var i = 0; i < count; i++)
        {
            *(uint*)(bytes + (i * 16)) = DisplayIdsVersion; // each element carries the structure version
        }

        var listed = ((delegate* unmanaged[Cdecl]<nint, byte*, uint*, uint, int>)connected)(gpu, bytes, &count, 0);
        if (listed != NvapiStatus.Ok)
        {
            return [new(-1, null, null, null, null, null, null, null, null, $"display list: {NvapiStatus.Name(listed)}")];
        }

        var vrr = session.Function(IdVrrInfo);
        var result = new List<NvidiaDisplayReport>();
        var info = stackalloc byte[24];
        for (var i = 0; i < count; i++)
        {
            var element = bytes + (i * 16);
            var id = *(uint*)(element + 8);
            var flags = *(uint*)(element + 12);
            bool Bit(int n) => (flags & (1u << n)) != 0;
            gdi.TryGetValue(id, out var gdiName);
            if (vrr == 0)
            {
                result.Add(new(i, gdiName, Bit(2), Bit(6), Bit(0), null, null, null, null, "NvAPI_Disp_GetVRRInfo is not in this driver"));
                continue;
            }

            new Span<byte>(info, 24).Clear();
            *(uint*)info = VrrInfoVersion;
            var status = ((delegate* unmanaged[Cdecl]<uint, byte*, int>)vrr)(id, info);
            if (status != NvapiStatus.Ok)
            {
                result.Add(new(i, gdiName, Bit(2), Bit(6), Bit(0), null, null, null, null, $"VRR info: {NvapiStatus.Name(status)}"));
                continue;
            }

            var vrrFlags = *(uint*)(info + 4);
            result.Add(new(i, gdiName, Bit(2), Bit(6), Bit(0), (vrrFlags & 2) != 0, (vrrFlags & 4) != 0, (vrrFlags & 1) != 0, (vrrFlags & 16) != 0, null));
        }

        return result;
    }

    private static IReadOnlyList<NvidiaProfileReport> Profiles(NvapiSession session)
    {
        var create = session.Function(IdDrsCreate);
        var destroy = session.Function(IdDrsDestroy);
        var load = session.Function(IdDrsLoad);
        var baseProfile = session.Function(IdDrsBase);
        var global = session.Function(IdDrsGlobal);
        var getSetting = session.Function(IdDrsGetSetting);
        if (create == 0 || destroy == 0 || load == 0 || baseProfile == 0 || getSetting == 0)
        {
            return [new("base", null, [], "this driver's NVAPI lacks a driver-settings (DRS) function")];
        }

        nint drs = 0;
        var created = ((delegate* unmanaged[Cdecl]<nint*, int>)create)(&drs);
        if (created != NvapiStatus.Ok || drs == 0)
        {
            return [new("base", null, [], $"DRS session: {NvapiStatus.Name(created)}")];
        }

        try
        {
            var loaded = ((delegate* unmanaged[Cdecl]<nint, int>)load)(drs);
            if (loaded != NvapiStatus.Ok)
            {
                return [new("base", null, [], $"DRS load: {NvapiStatus.Name(loaded)}")];
            }

            var profiles = new List<NvidiaProfileReport>();
            nint baseHandle = 0;
            if (((delegate* unmanaged[Cdecl]<nint, nint*, int>)baseProfile)(drs, &baseHandle) == NvapiStatus.Ok && baseHandle != 0)
            {
                profiles.Add(new("base", null, ReadKeys(getSetting, drs, baseHandle), null));
            }
            else
            {
                profiles.Add(new("base", null, [], "the Base Profile could not be obtained"));
            }

            nint globalHandle = 0;
            if (global != 0 && ((delegate* unmanaged[Cdecl]<nint, nint*, int>)global)(drs, &globalHandle) == NvapiStatus.Ok && globalHandle != 0)
            {
                // Normally the same profile as the Base Profile; only when it is another is it read again.
                profiles.Add(globalHandle == baseHandle ? new("currentGlobal", true, [], null) : new("currentGlobal", false, ReadKeys(getSetting, drs, globalHandle), null));
            }

            return profiles;
        }
        finally
        {
            _ = ((delegate* unmanaged[Cdecl]<nint, int>)destroy)(drs);
        }
    }

    private static IReadOnlyList<NvidiaSettingReading> ReadKeys(nint getSetting, nint drs, nint profile)
    {
        var readings = new List<NvidiaSettingReading>();
        var buffer = (byte*)NativeMemory.AllocZeroed(SettingSize);
        try
        {
            foreach (var (id, label) in KeySettings)
            {
                new Span<byte>(buffer, SettingSize).Clear();
                *(uint*)buffer = SettingVersion;
                var status = ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte*, int>)getSetting)(drs, profile, id, buffer);
                var idText = $"0x{id:X8}";
                if (status == NvapiStatus.SettingNotFound)
                {
                    readings.Add(new(idText, label, false, null, null, NvapiStatus.Name(status)));
                }
                else if (status != NvapiStatus.Ok)
                {
                    readings.Add(new(idText, label, null, null, null, NvapiStatus.Name(status)));
                }
                else
                {
                    var type = *(int*)(buffer + SettingTypeOffset);
                    var location = *(int*)(buffer + SettingLocationOffset);
                    var predefined = *(uint*)(buffer + SettingIsPredefinedOffset) != 0;
                    uint? value = type == 0 ? *(uint*)(buffer + SettingCurrentValueOffset) : null; // 0 = a DWORD setting; the key settings are all DWORDs
                    var source = location switch { 0 => "this profile", 1 => "global profile", 2 => "Base Profile", 3 => "driver default profile", _ => $"location {location}" };
                    readings.Add(new(idText, label, true, value, predefined ? $"{source} (predefined)" : source, NvapiStatus.Name(status)));
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return readings;
    }
}
