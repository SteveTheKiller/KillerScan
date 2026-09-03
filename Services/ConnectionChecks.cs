using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KillerScan.Services
{
    internal static class ConnectionChecks
    {
        internal static bool TryTargets(string text, out IPAddress[] addresses)
        {
            var parts = text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var parsed = new List<IPAddress>();
            foreach (string part in parts)
            {
                if (!IPAddress.TryParse(part.Trim(), out var address)) { addresses = []; return false; }
                if (!parsed.Contains(address)) parsed.Add(address);
            }
            addresses = [.. parsed];
            return addresses.Length is > 0 and <= 16;
        }

        internal static async Task<long?> PingAsync(IPAddress address)
        {
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(address, 1200).ConfigureAwait(false);
                return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (PingException) { return null; }
            catch (SocketException) { return null; }
        }

        internal static async Task<bool> TcpAsync(IPAddress address, int port, CancellationToken token)
        {
            using var client = new TcpClient(address.AddressFamily);
            try
            {
                await BoundedAsync(client.ConnectAsync(address, port), token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (SocketException) { return false; }
            catch (TimeoutException) { return false; }
        }

        internal static async Task BoundedAsync(Task task, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var winner = await Task.WhenAny(task, Task.Delay(1500, timeout.Token)).ConfigureAwait(false);
            if (winner != task)
            {
                // DNS cannot be canceled on net48. Observe a late fault without holding up the UI.
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            timeout.Cancel();
            await task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        internal static async Task<T> BoundedAsync<T>(Task<T> task, CancellationToken token)
        {
            await BoundedAsync((Task)task, token).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardRow
        {
            public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol,
                Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestRoute(uint destination, uint source, out ForwardRow route);

        internal static (string Interface, string NextHop)? Route(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return null;
            if (GetBestRoute(BitConverter.ToUInt32(address.GetAddressBytes(), 0), 0, out var route) != 0) return null;
            var iface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            {
                try { return n.GetIPProperties().GetIPv4Properties()?.Index == route.InterfaceIndex; }
                catch (NetworkInformationException) { return false; }
            });
            return (iface?.Name ?? route.InterfaceIndex.ToString(),
                route.NextHop == 0 ? string.Empty : new IPAddress(route.NextHop).ToString());
        }
    }

    internal sealed class ConnectionSample
    {
        public string Address { get; }
        public int Sent { get; private set; }
        public int Received { get; private set; }
        public long? Latest { get; private set; }
        public double Average => Received == 0 ? 0 : (double)_total / Received;
        public double Loss => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
        public DateTimeOffset? Changed { get; private set; }
        private long _total;
        internal ConnectionSample(string address) => Address = address;
        internal bool Record(long? latency, DateTimeOffset time)
        {
            bool changed = Sent == 0 || Latest.HasValue != latency.HasValue;
            Sent++;
            Latest = latency;
            if (latency.HasValue) { Received++; _total += latency.Value; }
            if (changed) Changed = time;
            return changed;
        }
    }
}
