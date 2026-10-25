using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Mesura.Core.Raw;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;

namespace Mesura.Platform.Windows;

/// <summary>Read-only access to PnP device nodes and their properties through CfgMgr32.</summary>
internal static class DeviceNodes
{
    private const int MaxAttempts = 5;

    /// <summary>Instance ids of the present devices a bus enumerator (e.g. <c>PCI</c>) reports.</summary>
    public static unsafe string[] ListPresent(string enumerator)
    {
        const uint flags = PInvoke.CM_GETIDLIST_FILTER_ENUMERATOR | PInvoke.CM_GETIDLIST_FILTER_PRESENT;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var result = PInvoke.CM_Get_Device_ID_List_Size(out var length, enumerator, flags);
            if (result != CONFIGRET.CR_SUCCESS)
            {
                throw new SourceUnavailableException(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Device_ID_List_Size", result));
            }

            var buffer = new char[length];
            fixed (char* pBuffer = buffer)
            fixed (char* pFilter = enumerator)
            {
                result = PInvoke.CM_Get_Device_ID_List(pFilter, pBuffer, length, flags);
            }

            if (result == CONFIGRET.CR_BUFFER_SMALL)
            {
                continue; // a device arrived between the two calls
            }

            if (result != CONFIGRET.CR_SUCCESS)
            {
                throw new SourceUnavailableException(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Device_ID_List", result));
            }

            return SplitMultiString(buffer);
        }

        throw new SourceUnavailableException(
            MissingReason.SourceFailed,
            string.Create(CultureInfo.InvariantCulture, $"CM_Get_Device_ID_List kept returning CR_BUFFER_SMALL; devices changed {MaxAttempts} times while they were listed"));
    }

    public static unsafe CONFIGRET Locate(string instanceId, out uint node)
    {
        var id = (instanceId + '\0').ToCharArray();
        fixed (char* pId = id)
        {
            return PInvoke.CM_Locate_DevNode(out node, pId, CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL);
        }
    }

    public static Reading<string> GetInstanceId(uint node)
    {
        // Instance ids are at most MAX_DEVICE_ID_LEN (200) characters.
        Span<char> buffer = stackalloc char[201];
        var result = PInvoke.CM_Get_Device_IDW(node, buffer, 0);
        return result == CONFIGRET.CR_SUCCESS
            ? Reading.Known(UpToNull(buffer).ToString())
            : Reading.Missing<string>(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Device_IDW", result));
    }

    public static Reading<uint> GetParent(uint node)
    {
        var result = PInvoke.CM_Get_Parent(out var parent, node, 0);
        return result == CONFIGRET.CR_SUCCESS
            ? Reading.Known(parent)
            : Reading.Missing<uint>(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Parent", result));
    }

    /// <summary>An integer property of any unsigned integer type (BYTE, UINT16, UINT32).</summary>
    public static Reading<uint> GetUnsigned(uint node, in DEVPROPKEY key, string keyName)
    {
        var (result, type, data) = GetProperty(node, key);
        if (result != CONFIGRET.CR_SUCCESS)
        {
            return Reading.Missing<uint>(Win32Errors.Reason(result), $"{keyName}: {Win32Errors.Describe("CM_Get_DevNode_PropertyW", result)}");
        }

        return type switch
        {
            DEVPROPTYPE.DEVPROP_TYPE_BYTE when data.Length == 1 => Reading.Known<uint>(data[0]),
            DEVPROPTYPE.DEVPROP_TYPE_UINT16 when data.Length == 2 => Reading.Known<uint>(BinaryPrimitives.ReadUInt16LittleEndian(data)),
            DEVPROPTYPE.DEVPROP_TYPE_UINT32 when data.Length == 4 => Reading.Known(BinaryPrimitives.ReadUInt32LittleEndian(data)),
            _ => Reading.Missing<uint>(MissingReason.ParseFailed, string.Create(CultureInfo.InvariantCulture, $"{keyName}: unexpected property type {type} with {data.Length} bytes")),
        };
    }

    public static Reading<string[]> GetStringList(uint node, in DEVPROPKEY key, string keyName)
    {
        var (result, type, data) = GetProperty(node, key);
        if (result != CONFIGRET.CR_SUCCESS)
        {
            return Reading.Missing<string[]>(Win32Errors.Reason(result), $"{keyName}: {Win32Errors.Describe("CM_Get_DevNode_PropertyW", result)}");
        }

        return type == DEVPROPTYPE.DEVPROP_TYPE_STRING_LIST
            ? Reading.Known(SplitMultiString(Encoding.Unicode.GetString(data).ToCharArray()))
            : Reading.Missing<string[]>(MissingReason.ParseFailed, $"{keyName}: unexpected property type {type}");
    }

    /// <summary>The device instance id behind a device interface path (e.g. a monitor's path from DisplayConfig).</summary>
    public static unsafe Reading<string> GetInterfaceInstanceId(string interfacePath)
    {
        var key = PInvoke.DEVPKEY_Device_InstanceId;
        uint size = 0;
        var result = PInvoke.CM_Get_Device_Interface_Property(interfacePath, in key, out _, default, ref size, 0);
        if (result is not (CONFIGRET.CR_BUFFER_SMALL or CONFIGRET.CR_SUCCESS))
        {
            return Reading.Missing<string>(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Device_Interface_PropertyW(DEVPKEY_Device_InstanceId)", result));
        }

        var data = new byte[size];
        result = PInvoke.CM_Get_Device_Interface_Property(interfacePath, in key, out var type, data, ref size, 0);
        if (result != CONFIGRET.CR_SUCCESS)
        {
            return Reading.Missing<string>(Win32Errors.Reason(result), Win32Errors.Describe("CM_Get_Device_Interface_PropertyW(DEVPKEY_Device_InstanceId)", result));
        }

        return type == DEVPROPTYPE.DEVPROP_TYPE_STRING
            ? Reading.Known(Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0'))
            : Reading.Missing<string>(MissingReason.ParseFailed, $"DEVPKEY_Device_InstanceId: unexpected property type {type}");
    }

    private static (CONFIGRET Result, DEVPROPTYPE Type, byte[] Data) GetProperty(uint node, in DEVPROPKEY key)
    {
        uint size = 0;
        var result = PInvoke.CM_Get_DevNode_Property(node, in key, out var type, default, ref size, 0);

        for (var attempt = 0; attempt < MaxAttempts && result == CONFIGRET.CR_BUFFER_SMALL; attempt++)
        {
            // On CR_BUFFER_SMALL, size now holds the length needed; it can grow between calls.
            var data = new byte[size];
            result = PInvoke.CM_Get_DevNode_Property(node, in key, out type, data, ref size, 0);
            if (result == CONFIGRET.CR_SUCCESS)
            {
                return (result, type, data[..(int)size]);
            }
        }

        // CR_SUCCESS without a buffer means an empty value; anything else is the failure itself.
        return (result, type, []);
    }

    private static ReadOnlySpan<char> UpToNull(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        return end < 0 ? buffer : buffer[..end];
    }

    private static string[] SplitMultiString(ReadOnlySpan<char> buffer)
    {
        var items = new List<string>();
        foreach (var range in buffer.Split('\0'))
        {
            var item = buffer[range];
            if (item.IsEmpty)
            {
                break; // the list ends at the first empty string
            }

            items.Add(item.ToString());
        }

        return [.. items];
    }
}
