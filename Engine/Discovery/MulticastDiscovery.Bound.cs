using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KillerScan.Engine
{
    public static partial class MulticastDiscovery
    {
        internal static async Task<Dictionary<string, MdnsInfo>> CollectBoundMdns(ScanRun run)
        {
            var map = new Dictionary<string, MdnsInfo>(StringComparer.OrdinalIgnoreCase);
            var group = IPAddress.Parse("224.0.0.251");
            using var udp = run.Udp(5353, multicastReceive: true);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, run.Binding!.SourceAddress.GetAddressBytes());
            udp.JoinMulticastGroup(group, run.Binding.SourceAddress);
            try
            {
                foreach (string question in MdnsQueries)
                {
                    run.Check(); byte[] query = BuildQuery(question);
                    await udp.SendAsync(query, query.Length, new IPEndPoint(group, 5353));
                }
                var deadline = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < deadline)
                {
                    var message = await ReceivePinned(udp, run);
                    if (message != null)
                        try { ParseResponse(message.Value.Data, message.Value.Sender.Address.ToString(), map); } catch { }
                }
            }
            catch (NetworkBindingException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { }
            return map;
        }

        internal static async Task<Dictionary<string, string>> CollectBoundSsdp(ScanRun run)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var udp = run.Udp();
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, run.Binding!.SourceAddress.GetAddressBytes());
            byte[] query = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
            try
            {
                var destination = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                run.Check(); await udp.SendAsync(query, query.Length, destination);
                run.Check(); await udp.SendAsync(query, query.Length, destination);
                var deadline = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < deadline)
                {
                    var message = await ReceivePinned(udp, run);
                    if (message == null) continue;
                    string text = Encoding.ASCII.GetString(message.Value.Data);
                    string server = ExtractHeader(text, "SERVER");
                    if (server.Length == 0) server = ExtractHeader(text, "USN");
                    if (server.Length > 0) map[message.Value.Sender.Address.ToString()] = server.Trim();
                }
            }
            catch (NetworkBindingException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { }
            return map;
        }

        internal static Task<(byte[] Data, IPEndPoint Sender)?> ReceivePinned(UdpClient udp, ScanRun run, bool requireSubnet = true) => Task.Run(() =>
        {
            run.Check();
            udp.Client.ReceiveTimeout = 250;
            var buffer = new byte[65536];
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            SocketFlags flags = SocketFlags.None;
            try
            {
                int size = udp.Client.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref sender, out var info);
                run.Check();
                if (info.Interface != run.Binding!.InterfaceIndex || sender is not IPEndPoint source || (requireSubnet && !run.Binding.Contains(source.Address)))
                    return ((byte[], IPEndPoint)?)null;
                return (buffer.Take(size).ToArray(), source);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { return null; }
        }, run.Token);
    }
}
