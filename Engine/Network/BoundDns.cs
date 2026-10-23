using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace KillerScan.Engine
{
    internal static class BoundDns
    {
        internal static async Task<string> Hostname(IPAddress address, ScanRun run, int serverPort = 53)
        {
            run.CheckTarget(address);
            if (!IPAddress.TryParse(run.Binding!.DnsServer, out var server) || server.AddressFamily != AddressFamily.InterNetwork) return "";
            try
            {
                string reverse = string.Join(".", Enumerable.Reverse(address.GetAddressBytes())) + ".in-addr.arpa";
                var names = await Query(reverse, 12, server, serverPort, run);
                foreach (string name in names.Take(4))
                {
                    var addresses = await Query(name, 1, server, serverPort, run);
                    if (addresses.Contains(address.ToString())) return name.TrimEnd('.');
                }
            }
            catch (NetworkBindingException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { }
            return "";
        }

        private static async Task<List<string>> Query(string name, ushort type, IPAddress server, int port, ScanRun run)
        {
            byte[] id = new byte[2];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(id);
            byte[] packet = BuildQuery(name, type, id);
            using var udp = run.Udp();
            udp.Connect(server, port); // Only responses from the selected adapter's configured DNS server.
            run.Check();
            await udp.SendAsync(packet, packet.Length);
            var response = Receive();
            await run.Wait(response, 1200);
            return ReadAnswers(response.Result, id, name, type);

            async Task<byte[]> Receive()
            {
                while (true)
                {
                    // A configured DNS server may be routed outside the scan's local subnet.
                    var reply = await MulticastDiscovery.ReceivePinned(udp, run, requireSubnet: false);
                    if (reply != null && reply.Value.Sender.Address.Equals(server) && reply.Value.Sender.Port == port)
                        return reply.Value.Data;
                }
            }
        }

        internal static byte[] BuildQuery(string name, ushort type, byte[] id)
        {
            using var stream = new MemoryStream();
            byte[] header = [id[0], id[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0];
            stream.Write(header, 0, header.Length);
            foreach (string label in name.TrimEnd('.').Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63) throw new InvalidDataException("Invalid DNS label.");
                stream.WriteByte((byte)bytes.Length); stream.Write(bytes, 0, bytes.Length);
            }
            stream.WriteByte(0); stream.WriteByte((byte)(type >> 8)); stream.WriteByte((byte)type);
            stream.WriteByte(0); stream.WriteByte(1);
            return stream.ToArray();
        }

        internal static List<string> ReadAnswers(byte[] data, byte[] id, string question, ushort type)
        {
            var result = new List<string>();
            if (data.Length < 12 || data[0] != id[0] || data[1] != id[1] || (data[2] & 0x82) != 0x80 || (data[3] & 15) != 0 || U16(data, 4) != 1)
                return result;
            int offset = 12;
            if (!Name(data, ref offset).Equals(question.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) || U16(data, offset) != type || U16(data, offset + 2) != 1) return result;
            offset += 4;
            int count = Math.Min(128, (int)U16(data, 6));
            for (int n = 0; n < count; n++)
            {
                _ = Name(data, ref offset);
                ushort answerType = U16(data, offset), answerClass = U16(data, offset + 2), length = U16(data, offset + 8);
                offset += 10;
                if (offset + length > data.Length) throw new InvalidDataException("Truncated DNS answer.");
                if (answerType == type && answerClass == 1)
                {
                    int start = offset;
                    if (type == 12) result.Add(Name(data, ref start));
                    else if (type == 1 && length == 4) result.Add(new IPAddress(data.Skip(offset).Take(4).ToArray()).ToString());
                }
                offset += length;
            }
            return result;
        }
        private static ushort U16(byte[] data, int offset)
        {
            if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("Truncated DNS record.");
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }
        private static string Name(byte[] data, ref int offset)
        {
            var labels = new List<string>(); var visited = new HashSet<int>();
            int position = offset, end = -1, size = 0;
            while (true)
            {
                if (position < 0 || position >= data.Length || !visited.Add(position)) throw new InvalidDataException("Invalid DNS name pointer.");
                byte length = data[position++];
                if ((length & 0xC0) == 0xC0)
                {
                    if (position >= data.Length) throw new InvalidDataException("Truncated DNS pointer.");
                    if (end < 0) end = position + 1;
                    position = ((length & 63) << 8) | data[position]; continue;
                }
                if (length == 0) { offset = end < 0 ? position : end; return string.Join(".", labels); }
                size += length + 1;
                if (length > 63 || position + length > data.Length || size > 255) throw new InvalidDataException("Invalid DNS name.");
                labels.Add(Encoding.ASCII.GetString(data, position, length)); position += length;
            }
        }
    }
}
