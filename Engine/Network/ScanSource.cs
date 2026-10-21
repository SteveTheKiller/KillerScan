using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KillerScan.Engine
{
    /// <summary>
    /// Where this scan's probes come from. The scanner sets <see cref="Current"/> at the start of
    /// a run; it flows through every await and task of that run, so the static probe helpers
    /// bind their sockets, pings and ARP requests without each taking a parameter. Unbound, every
    /// helper behaves exactly as before and Windows picks the route.
    /// </summary>
    internal static class ScanSource
    {
        private static readonly AsyncLocal<ScanBinding?> Run = new();

        internal static ScanBinding? Current
        {
            get => Run.Value;
            set => Run.Value = value;
        }

        internal static TcpClient Tcp()
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            if (Current is { } binding) client.Client.Bind(new IPEndPoint(binding.LocalAddress, 0));
            return client;
        }

        internal static UdpClient Udp() =>
            Current is { } binding ? new UdpClient(new IPEndPoint(binding.LocalAddress, 0)) : new UdpClient();

        /// <summary>An echo request; bound runs send it from the adapter's address.</summary>
        internal static async Task<(bool Success, int Ttl)> PingAsync(IPAddress address, int timeoutMs)
        {
            if (Current is not { } binding)
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(address, timeoutMs);
                return (reply.Status == IPStatus.Success, reply.Options?.Ttl ?? 0);
            }
            var source = binding.LocalAddress;
            return await Task.Run(() => BoundEcho(source, address, timeoutMs));
        }

        /// <summary>The MAC for an address; bound runs ask from the adapter's address.</summary>
        internal static string ResolveMac(IPAddress address)
        {
            // Only the Windows platform exists when it is the current one, so the guard holds.
            if (Current is { } binding && NetworkPlatform.Current is WindowsNetworkPlatform windows
                && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#pragma warning disable CA1416
                return windows.ResolveMacFrom(address, binding.LocalAddress);
#pragma warning restore CA1416
            }
            return NetworkPlatform.Current.ResolveMac(address);
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
            uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize,
            IntPtr requestOptions, IntPtr replyBuffer, uint replySize, uint timeout);

        // System.Net.NetworkInformation.Ping cannot choose a source address; IcmpSendEcho2Ex can.
        private static (bool Success, int Ttl) BoundEcho(IPAddress source, IPAddress destination, int timeoutMs)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return (false, 0);
            IntPtr handle = IcmpCreateFile();
            if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return (false, 0);
            byte[] payload = new byte[32];
            int replySize = 64 + payload.Length + 8 + 64;
            IntPtr reply = Marshal.AllocHGlobal(replySize);
            try
            {
                uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    BitConverter.ToUInt32(source.GetAddressBytes(), 0), BitConverter.ToUInt32(destination.GetAddressBytes(), 0),
                    payload, (ushort)payload.Length, IntPtr.Zero, reply, (uint)replySize, (uint)timeoutMs);
                if (count == 0) return (false, 0);
                // ICMP_ECHO_REPLY: Address, Status, RoundTripTime (4 bytes each), DataSize and
                // Reserved (2 each), a Data pointer, then IP_OPTION_INFORMATION starting with Ttl.
                uint status = (uint)Marshal.ReadInt32(reply, 4);
                int ttl = Marshal.ReadByte(reply, 16 + IntPtr.Size);
                return (status == 0, ttl);
            }
            finally
            {
                Marshal.FreeHGlobal(reply);
                IcmpCloseHandle(handle);
            }
        }
    }
}
