using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerScan.Engine;

// NBSTAT replies must come from the queried host, echo its transaction ID and parse as a
// well-formed NBSTAT answer before their name can become the device hostname.
internal static class NetbiosTests
{
    internal static async Task Run()
    {
        Require(await Probe(query => Response(query, "OFFICE-PC")) == "OFFICE-PC", "A valid NBSTAT reply supplies the name.");
        Require(await Probe(query => Response(query, "OFFICE-PC"), wrongSource: true) == "OFFICE-PC",
            "Only the queried UDP endpoint supplies the name.");
        foreach (string fault in new[] { "id", "query-flag", "rcode", "type", "answers", "count", "truncated", "header" })
            Require(await Probe(query => Response(query, "WRONG-NAME", fault)) == "", "Reject malformed or mismatched reply: " + fault);
        Console.WriteLine("PASS: NetBIOS names come only from validated replies by the queried host.");
    }

    private static async Task<string> Probe(Func<byte[], byte[]> response, bool wrongSource = false)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var request = server.ReceiveAsync();
        var device = new NetworkDevice { IpAddress = "127.0.0.1" };
        var probe = NetworkScanner.ProbeNetbiosAsync(device, IPAddress.Loopback, ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        Require(await Task.WhenAny(request, Task.Delay(2000)) == request, "The NBSTAT query reaches the owned loopback fixture.");
        var received = await request;
        if (wrongSource)
        {
            using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] injected = Response(received.Buffer, "INJECTED");
            await rogue.SendAsync(injected, injected.Length, received.RemoteEndPoint);
            await Task.Delay(50);
        }
        byte[] bytes = response(received.Buffer);
        await server.SendAsync(bytes, bytes.Length, received.RemoteEndPoint);
        await probe;
        return device.NetbiosName;
    }

    private static byte[] Response(byte[] query, string name, string fault = "")
    {
        var bytes = new List<byte>();
        bytes.Add(fault == "id" ? (byte)(query[0] ^ 0xff) : query[0]);
        bytes.Add(query[1]);
        ushort flags = (ushort)(fault == "query-flag" ? 0x0400 : 0x8400);
        if (fault == "rcode") flags |= 0x0003;
        bytes.Add((byte)(flags >> 8)); bytes.Add((byte)flags);
        bytes.AddRange(new byte[] { 0, 0, 0, (byte)(fault == "answers" ? 0 : 1), 0, 0, 0, 0 });
        bytes.AddRange(query.Skip(12).Take(34));
        bytes.AddRange(new byte[] { 0, (byte)(fault == "type" ? 0x20 : 0x21), 0, 1, 0, 0, 0, 0 });
        var rdata = new List<byte> { (byte)(fault == "count" ? 9 : 1) };
        rdata.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)));
        rdata.Add(0x00);
        rdata.AddRange(new byte[] { 0x04, 0x00 });
        rdata.AddRange(new byte[46]);
        bytes.Add((byte)(rdata.Count >> 8)); bytes.Add((byte)rdata.Count);
        bytes.AddRange(rdata);
        if (fault == "truncated") return bytes.Take(60).ToArray();
        if (fault == "header") return bytes.Take(10).ToArray();
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
