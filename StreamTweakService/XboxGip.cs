using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace StreamTweakService;

/// <summary>The outcome of one turn-off: the controller and the Win32 error of the IOCTL (0 = OK).</summary>
public readonly record struct XboxTurnOffResult(ulong DeviceId, int Error);

/// <summary>
/// The undocumented part of "Controllers off with the PC" (10.3.0), kept behind an interface so the
/// rest never depends on how it is done. If a Windows update changes the driver, this is the one
/// file to look at — and every caller already treats a failure as "nothing turned off".
/// </summary>
public interface IXboxGipControl
{
    /// <summary>Is an Xbox Wireless Adapter plugged in?</summary>
    bool AdapterPresent();

    /// <summary>The GIP device ids of the controllers connected to an adapter right now.
    /// Read from Plug and Play: a few milliseconds, safe to call while Windows suspends.</summary>
    IReadOnlyList<ulong> ConnectedControllers();

    /// <summary>Sends "turn off" to each controller. Never throws.</summary>
    IReadOnlyList<XboxTurnOffResult> TurnOff(IReadOnlyList<ulong> deviceIds);
}

/// <summary>
/// xboxgip.sys, the driver of the Xbox Wireless Adapter, turns the controllers off by itself only
/// when Windows goes to S5. A Fast Startup shut down is S4 (session 0 hibernated), so the
/// controllers lose the adapter and blink until they give up. This sends them the turn-off the
/// driver does not.
///
/// ⚠️ Undocumented. The IOCTL on \\.\XboxGIP_Admin was found by xbpoweroff
/// (github.com/Leclowndu93150/xbpoweroff, MIT licence): input = the device id (8 bytes, little
/// endian) and a control code; 0x02 = turn off. Its other codes clear or set the pairing (0x00,
/// 0x01), switch the idle disconnect (0x03, 0x04) and reset (0x05): never sent from here. The
/// device needs SYSTEM or LOCAL SERVICE, hence the service.
///
/// The ids do NOT come from reading \\.\XboxGIP (re-enumerate and read its announcements, up to
/// ~5 s — too slow for the ~2 s Windows gives a suspend, and it would mean a reader that may take
/// messages from Windows.Gaming.Input or Steam). The driver names each controller's device node
/// after its id — USB\VID_045E&amp;PID_0B12&amp;IG_00\00&amp;00&amp;0000E0EFA48DED7E, child of the adapter —
/// so the present nodes give the ids at once. Checked on FOGGY on 10/11/2026: same id from the
/// node and from \\.\XboxGIP, one controller.
/// </summary>
public sealed partial class XboxGip : IXboxGipControl
{
    private const uint IOCTL_GIP_ADMIN_CONTROL = 0x40001C4C;
    private const byte CONTROL_TURN_OFF = 0x02;

    // A controller on the adapter: its GIP id is the last 16 hex digits of the instance id.
    [GeneratedRegex(@"^USB\\VID_045E&PID_[0-9A-F]{4}&IG_[0-9A-F]{2}\\00&00&([0-9A-F]{16})$", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerNode();

    // A Microsoft USB device that is not an interface or a collection: the adapter itself, or a
    // controller on a cable (class XboxComposite). The adapter is the one in class Net.
    [GeneratedRegex(@"^USB\\VID_045E&PID_[0-9A-F]{4}\\[^&\\]+$", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftUsbDevice();

    public bool AdapterPresent()
    {
        foreach (string id in PresentUsbDevices())
            if (MicrosoftUsbDevice().IsMatch(id) && IsAdapter(id))
                return true;
        return false;
    }

    public IReadOnlyList<ulong> ConnectedControllers()
    {
        var ids = new List<ulong>();
        foreach (string id in PresentUsbDevices())
        {
            Match m = ControllerNode().Match(id);
            if (!m.Success) continue;
            // Only under an adapter: a controller on a cable has the same kind of node under its
            // own composite device, and the cable already powers it down with the PC.
            string? parent = ParentOf(id);
            if (parent == null || !MicrosoftUsbDevice().IsMatch(parent) || !IsAdapter(parent)) continue;
            ulong deviceId = Convert.ToUInt64(m.Groups[1].Value, 16);
            if (!ids.Contains(deviceId)) ids.Add(deviceId);
        }
        return ids;
    }

    public IReadOnlyList<XboxTurnOffResult> TurnOff(IReadOnlyList<ulong> deviceIds)
    {
        var results = new List<XboxTurnOffResult>(deviceIds.Count);
        if (deviceIds.Count == 0) return results;

        using SafeFileHandle h = CreateFileW(@"\\.\XboxGIP_Admin", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            foreach (ulong id in deviceIds) results.Add(new XboxTurnOffResult(id, err));
            return results;
        }

        var input = new byte[9];
        foreach (ulong id in deviceIds)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(input, id);
            input[8] = CONTROL_TURN_OFF;
            bool ok = DeviceIoControl(h, IOCTL_GIP_ADMIN_CONTROL, input, input.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            results.Add(new XboxTurnOffResult(id, ok ? 0 : Marshal.GetLastWin32Error()));
        }
        return results;
    }

    // ── Plug and Play ────────────────────────────────────────────────────────

    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT    = 0x00000100;
    private const uint CM_DRP_DEVICEDESC              = 0x00000001;
    private const uint CM_DRP_CLASS                   = 0x00000008;
    private const int  CR_SUCCESS      = 0x00;
    private const int  CR_BUFFER_SMALL = 0x1A;
    private const int  MAX_DEVICE_ID_LEN = 200;

    /// <summary>Instance ids of the USB devices that are present.</summary>
    private static List<string> PresentUsbDevices()
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, "USB", flags) != CR_SUCCESS || length == 0)
                return new List<string>();
            var buffer = new char[length];
            int cr = CM_Get_Device_ID_ListW("USB", buffer, length, flags);
            if (cr == CR_BUFFER_SMALL) continue;   // a device arrived between the two calls
            if (cr != CR_SUCCESS) return new List<string>();
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    private static string? ParentOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return null;
        if (CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS) return null;
        var buffer = new char[MAX_DEVICE_ID_LEN + 1];
        if (CM_Get_Device_IDW(parent, buffer, (uint)buffer.Length, 0) != CR_SUCCESS) return null;
        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>The Xbox Wireless Adapter: a network-class device that describes itself as Xbox
    /// ("Xbox Wireless Adapter for Windows"). The class alone would take a Microsoft USB Ethernet
    /// adapter too, such as Surface's.</summary>
    private static bool IsAdapter(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return false;
        string? cls = RegistryString(devInst, CM_DRP_CLASS);
        if (!string.Equals(cls, "Net", StringComparison.OrdinalIgnoreCase)) return false;
        string? desc = RegistryString(devInst, CM_DRP_DEVICEDESC);
        return desc != null && desc.Contains("Xbox", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RegistryString(uint devInst, uint property)
    {
        var buffer = new byte[512];
        uint length = (uint)buffer.Length;
        if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0) != CR_SUCCESS)
            return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0');
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const uint GENERIC_READ      = 0x80000000;
    private const uint GENERIC_WRITE     = 0x40000000;
    private const uint FILE_SHARE_READ   = 0x00000001;
    private const uint FILE_SHARE_WRITE  = 0x00000002;
    private const uint OPEN_EXISTING     = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint type,
        [Out] byte[] buffer, ref uint length, uint flags);
}
