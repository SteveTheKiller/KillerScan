using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace KillerScan.Engine
{
    internal static class BoundWindowsNetwork
    {
        [DllImport("iphlpapi.dll")] private static extern uint SendARP(uint destination, uint source, byte[] mac, ref int length);
        [DllImport("iphlpapi.dll")] private static extern IntPtr IcmpCreateFile();
        [DllImport("iphlpapi.dll")] private static extern bool IcmpCloseHandle(IntPtr handle);
        [DllImport("iphlpapi.dll")] private static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr context,
            uint source, uint destination, byte[] data, ushort length, IntPtr options, IntPtr reply, uint replySize, uint timeout);
        [StructLayout(LayoutKind.Sequential)] private struct Route
        { public uint Destination, Mask, Policy, NextHop, Index, Type, Protocol, Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5; }
        [DllImport("iphlpapi.dll")] private static extern uint GetBestRoute(uint destination, uint source, out Route route);
        private static uint Native(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

        internal static IReadOnlyDictionary<string, string> Neighbors(ScanRun run)
        {
            run.Check();
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "arp.exe"), "-a -N " + run.Binding!.SourceAddress)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start);
            if (process == null) return new Dictionary<string, string>();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return new Dictionary<string, string>(); }
            run.Check();
            return ArpOutput.Parse(output).Where(pair => run.Binding.Contains(IPAddress.Parse(pair.Key)))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        internal static string Mac(IPAddress address, ScanRun run)
        {
            run.CheckTarget(address);
            byte[] mac = new byte[6]; int length = 6;
            if (SendARP(Native(address), Native(run.Binding!.SourceAddress), mac, ref length) != 0 || length != 6) return "";
            return mac.All(b => b == 0) ? "" : string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        internal static int? Ping(IPAddress address, int timeout, ScanRun run)
        {
            run.CheckTarget(address);
            var binding = run.Binding!;
            // Source-bound ICMP has no socket-level IP_UNICAST_IF. Refuse it if the source-aware
            // route cannot be confirmed, rather than allowing the default/VPN path to win.
            if (GetBestRoute(Native(address), Native(binding.SourceAddress), out var route) != 0 || route.Index != binding.InterfaceIndex)
                throw new NetworkBindingException("ICMP route does not match the selected interface. Disable ICMP or correct the interface route.");
            IntPtr handle = IcmpCreateFile();
            if (handle == new IntPtr(-1)) throw new NetworkBindingException("Source-bound ICMP is unavailable.");
            IntPtr reply = Marshal.AllocHGlobal(256);
            try
            {
                byte[] data = [0x4B, 0x53];
                run.Check();
                uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native(binding.SourceAddress), Native(address),
                    data, (ushort)data.Length, IntPtr.Zero, reply, 256, (uint)timeout);
                run.Check();
                return count > 0 && Marshal.ReadInt32(reply, 4) == 0 ? Marshal.ReadByte(reply, IntPtr.Size == 8 ? 24 : 20) : null;
            }
            finally { Marshal.FreeHGlobal(reply); IcmpCloseHandle(handle); }
        }
    }
}
