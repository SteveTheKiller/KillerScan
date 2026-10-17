using System.Runtime.InteropServices;

namespace KillerScan.Engine
{
    internal static class WindowsLinkIdentity
    {
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int valueType);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

        internal static string Read(string adapterId)
        {
            IntPtr handle = IntPtr.Zero, data = IntPtr.Zero;
            try
            {
                if (!Guid.TryParse(adapterId, out var id) || WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return "";
                // wlan_intf_opcode_current_connection = 7; WLAN_CONNECTION_ATTRIBUTES.
                if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out uint size, out data, out _) != 0 || size < 566) return "";
                if (Marshal.ReadInt32(data) != 1) return ""; // wlan_interface_state_connected
                int length = Marshal.ReadInt32(data, 520);
                if (length < 0 || length > 32) return "";
                var ssid = new byte[length];
                var bssid = new byte[6];
                Marshal.Copy(IntPtr.Add(data, 524), ssid, 0, length);
                Marshal.Copy(IntPtr.Add(data, 560), bssid, 0, 6);
                return BitConverter.ToString(ssid) + "/" + BitConverter.ToString(bssid);
            }
            catch { return ""; }
            finally
            {
                if (data != IntPtr.Zero) WlanFreeMemory(data);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }
}
