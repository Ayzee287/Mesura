using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Mesura.Core.Raw;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DXCore;

namespace Mesura.Platform.Windows.Collectors;

/// <summary>
/// <c>adapters</c>: every graphics adapter DXCore lists (architecture §4.4, §7). The integrated flag
/// is DXCore's own <c>IsIntegrated</c>; nothing is inferred from a vendor ID or a name, which would
/// misclassify Intel Arc. A property the running Windows doesn't support is <c>NotSupported</c>
/// (D008), never filled in from another property.
/// </summary>
internal static class AdapterCollector
{
    public const string Source = "win32.DXCoreCreateAdapterFactory (adapters with DXCORE_ADAPTER_ATTRIBUTE_D3D11_GRAPHICS)";

    public static unsafe AdapterData Collect()
    {
        var factoryId = typeof(IDXCoreAdapterFactory).GUID;
        var created = PInvoke.DXCoreCreateAdapterFactory(&factoryId, out var factoryObject);
        if (created.Failed)
        {
            throw new SourceUnavailableException(Win32Errors.ReasonForHResult(created.Value), Win32Errors.Describe("DXCoreCreateAdapterFactory", created.Value));
        }

        var factory = (IDXCoreAdapterFactory)factoryObject;
        IDXCoreAdapterList? list = null;
        try
        {
            factory.CreateAdapterList([PInvoke.DXCORE_ADAPTER_ATTRIBUTE_D3D11_GRAPHICS], out IDXCoreAdapterList graphicsAdapters);
            list = graphicsAdapters;
            var count = list.GetAdapterCount();
            var adapters = new List<GraphicsAdapter>((int)count);

            for (uint i = 0; i < count; i++)
            {
                list.GetAdapter(i, out IDXCoreAdapter adapter);
                try
                {
                    adapters.Add(Read(adapter));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return new AdapterData(adapters);
        }
        finally
        {
            if (list is not null)
            {
                Marshal.ReleaseComObject(list);
            }

            Marshal.ReleaseComObject(factory);
        }
    }

    private static GraphicsAdapter Read(IDXCoreAdapter adapter)
    {
        var luid = Property(adapter, DXCoreAdapterProperty.InstanceLuid, static data => MemoryMarshal.Read<LUID>(data));
        if (!luid.IsKnown)
        {
            // Without its LUID the adapter can't be joined to a display; this is a source failure, not a gap.
            throw new SourceUnavailableException(luid.Missing!.Value, $"An adapter's InstanceLuid could not be read: {luid.Detail}");
        }

        return new GraphicsAdapter(
            AdapterId: DisplayConfig.FormatAdapterId(luid.Value),
            Description: Property(adapter, DXCoreAdapterProperty.DriverDescription, static data => Encoding.UTF8.GetString(UpToNull(data))),
            PciIds: Property(adapter, DXCoreAdapterProperty.HardwareIDParts, static data =>
            {
                var parts = MemoryMarshal.Read<DXCoreHardwareIDParts>(data);
                return new PciIds(
                    VendorId: checked((int)parts.vendorID),
                    DeviceId: checked((int)parts.deviceID),
                    SubsystemVendorId: checked((int)parts.subVendorID),
                    SubsystemId: checked((int)parts.subSystemID),
                    RevisionId: checked((int)parts.revisionID));
            }),
            HardwareId: Property(adapter, DXCoreAdapterProperty.HardwareID, static data =>
            {
                var id = MemoryMarshal.Read<DXCoreHardwareID>(data);
                return new PciHardwareId(
                    VendorId: checked((int)id.vendorID),
                    DeviceId: checked((int)id.deviceID),
                    SubsystemCombined: id.subSysID,
                    RevisionId: checked((int)id.revision));
            }),
            IsIntegrated: Property(adapter, DXCoreAdapterProperty.IsIntegrated, static data => data[0] != 0),
            IsHardware: Property(adapter, DXCoreAdapterProperty.IsHardware, static data => data[0] != 0),
            DriverVersion: Property(adapter, DXCoreAdapterProperty.DriverVersion, static data => FormatDriverVersion(BinaryPrimitives.ReadUInt64LittleEndian(data))),
            DedicatedMemoryBytes: Property(adapter, DXCoreAdapterProperty.DedicatedAdapterMemory, static data => BinaryPrimitives.ReadUInt64LittleEndian(data)));
    }

    /// <summary>The four 16-bit fields of a Windows driver version, most significant first.</summary>
    internal static string FormatDriverVersion(ulong version) =>
        string.Create(CultureInfo.InvariantCulture, $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}");

    private delegate T Decode<T>(ReadOnlySpan<byte> data);

    private static Reading<T> Property<T>(IDXCoreAdapter adapter, DXCoreAdapterProperty property, Decode<T> decode)
    {
        var notSupported = string.Create(CultureInfo.InvariantCulture, $"DXCore property {property} is not supported on Windows build {Environment.OSVersion.Version.Build}");
        if (!IsPropertySupported(adapter, property))
        {
            return Reading.Missing<T>(MissingReason.NotSupported, notSupported);
        }

        var call = "IDXCoreAdapter.GetPropertySize";
        try
        {
            adapter.GetPropertySize(property, out var size);
            var data = new byte[(int)size];
            call = "IDXCoreAdapter.GetProperty";
            adapter.GetProperty(property, data);
            return data.Length == 0
                ? Reading.Missing<T>(MissingReason.NotPresent, $"DXCore property {property} is empty")
                : Reading.Known(decode(data));
        }
        catch (COMException ex) when (ex.HResult is DxgiErrorInvalidCall or DxgiErrorUnsupported)
        {
            // The documented answers for a property this OS doesn't recognise or this adapter doesn't support.
            return Reading.Missing<T>(MissingReason.NotSupported, $"{notSupported} ({Win32Errors.Describe($"{call}({property})", ex.HResult)})");
        }
        catch (COMException ex)
        {
            return Reading.Missing<T>(Win32Errors.ReasonForHResult(ex.HResult), Win32Errors.Describe($"{call}({property})", ex.HResult));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reading.Missing<T>(MissingReason.ParseFailed, $"DXCore property {property} is shorter than its documented type");
        }
    }

    /// <summary>
    /// <c>IDXCoreAdapter::IsPropertySupported</c> returns a one-byte C++ <c>bool</c>, and the rest of the
    /// return register is undefined. The generated signature reads a wider value, so on this machine it
    /// returned true for every property. This calls the method's vtable slot and reads only the low byte.
    /// </summary>
    private static unsafe bool IsPropertySupported(IDXCoreAdapter adapter, DXCoreAdapterProperty property)
    {
        var pointer = Marshal.GetComInterfaceForObject<IDXCoreAdapter, IDXCoreAdapter>(adapter);
        try
        {
            // IUnknown (0–2), IsValid (3), IsAttributeSupported (4), IsPropertySupported (5): dxcore_interface.h.
            var vtable = *(void***)pointer;
            var isPropertySupported = (delegate* unmanaged[Stdcall]<nint, DXCoreAdapterProperty, byte>)vtable[5];
            return isPropertySupported(pointer, property) != 0;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);

    private static ReadOnlySpan<byte> UpToNull(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return end < 0 ? data : data[..end];
    }
}
