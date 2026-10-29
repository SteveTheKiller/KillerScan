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
                // Ping throws for some failures (resources under a wide sweep, no route); that is no
                // reply, and the caller's TCP liveness check still gets its turn.
                try
                {
                    var reply = await ping.SendPingAsync(address, timeoutMs);
                    return (reply.Status == IPStatus.Success, reply.Options?.Ttl ?? 0);
                }
                catch (PingException) { return (false, 0); }
            }
            return await BoundEchoAsync(binding.LocalAddress, address, timeoutMs);
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
            uint sourceAddress, uint destinationAddress, IntPtr requestData, ushort requestSize,
            IntPtr requestOptions, IntPtr replyBuffer, uint replySize, uint timeout);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpParseReplies(IntPtr replyBuffer, uint replySize);

        private const int ErrorIoPending = 997;
        private const int PayloadSize = 32;
        private const int ReplySize = 64 + PayloadSize + 8 + 64;

        /// <summary>
        /// System.Net.NetworkInformation.Ping cannot choose a source address; IcmpSendEcho2Ex can.
        /// It runs in its asynchronous mode: Windows signals an event when the reply or the
        /// timeout arrives, and a registered wait completes the task, so a bound sweep of silent
        /// hosts holds no thread while it waits. The request and reply buffers are unmanaged and
        /// stay alive until that signal.
        /// </summary>
        private static Task<(bool Success, int Ttl)> BoundEchoAsync(IPAddress source, IPAddress destination, int timeoutMs)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Task.FromResult((false, 0));
            IntPtr handle = IcmpCreateFile();
            if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return Task.FromResult((false, 0));
            var signal = new ManualResetEvent(false);
            IntPtr payload = Marshal.AllocHGlobal(PayloadSize);
            IntPtr reply = Marshal.AllocHGlobal(ReplySize);
            for (int i = 0; i < PayloadSize; i++) Marshal.WriteByte(payload, i, 0);
            var done = new TaskCompletionSource<(bool, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Release(bool freeBuffers)
            {
                IcmpCloseHandle(handle);
                signal.Dispose();
                if (!freeBuffers) return;
                Marshal.FreeHGlobal(payload);
                Marshal.FreeHGlobal(reply);
            }

            uint sent = IcmpSendEcho2Ex(handle, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(source.GetAddressBytes(), 0), BitConverter.ToUInt32(destination.GetAddressBytes(), 0),
                payload, PayloadSize, IntPtr.Zero, reply, ReplySize, (uint)timeoutMs);
            if (sent == 0 && Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                Release(freeBuffers: true);
                return Task.FromResult((false, 0));
            }

            RegisteredWaitHandle? registration = null;
            registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
            {
                registration?.Unregister(null);
                if (timedOut)
                {
                    // Windows has not finished with the buffers, so they are left allocated
                    // rather than freed under it. ICMP's own timeout makes this all but unreachable.
                    Release(freeBuffers: false);
                    done.TrySetResult((false, 0));
                    return;
                }
                (bool, int) result = (false, 0);
                if (IcmpParseReplies(reply, ReplySize) > 0)
                {
                    // ICMP_ECHO_REPLY: Address, Status, RoundTripTime (4 bytes each), DataSize and
                    // Reserved (2 each), a Data pointer, then IP_OPTION_INFORMATION starting with Ttl.
                    uint status = (uint)Marshal.ReadInt32(reply, 4);
                    result = (status == 0, Marshal.ReadByte(reply, 16 + IntPtr.Size));
                }
                Release(freeBuffers: true);
                done.TrySetResult(result);
            }, null, timeoutMs + 5000, executeOnlyOnce: true);
            // A reply fast enough to finish before the line above assigned the registration left it
            // registered; it is released here instead.
            if (done.Task.IsCompleted) registration.Unregister(null);
            return done.Task;
        }
    }
}
