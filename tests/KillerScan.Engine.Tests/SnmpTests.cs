using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerScan.Engine;

internal static class SnmpTests
{
    internal static async Task Run()
    {
        foreach (int length in new[] { 33, 127, 128, 188, 255, 256, 0, 1, 3, 4 })
        {
            string text = new string(Enumerable.Range(0, length).Select(i => (char)('A' + i % 26)).ToArray());
            Require(await Probe(query => Response(query, text)) == text, "Valid sysDescr length " + length + " must survive exactly.");
        }
        Require(await Probe(query => Response(query, "public")) == "public", "A description equal to the community is still a valid description.");
        string embeddedTag = "Device \u0004\btag-like bytes remain in the actual description";
        Require(await Probe(query => Response(query, embeddedTag)) == embeddedTag, "Payload bytes are never mistaken for BER tags.");
        Require(await Probe(query => Response(query, "Verified source"), wrongSource: true) == "Verified source", "Only the queried UDP source endpoint supplies the description.");
        foreach (string fault in new[] { "request", "oid", "error", "index", "version", "community", "pdu", "truncated", "indefinite", "overflow", "trailing" })
            Require(await Probe(query => Response(query, "Wrong device description", fault)) == "", "Reject malformed or mismatched response: " + fault);
        Console.WriteLine("PASS: bounded SNMP BER decoding preserves valid lengths and rejects wrong identities.");
    }

    private static async Task<string> Probe(Func<byte[], byte[]> response, bool wrongSource = false)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 161));
        var request = server.ReceiveAsync();
        var device = new NetworkDevice { IpAddress = "127.0.0.1" };
        var probe = NetworkScanner.ProbeSnmpAsync(device, IPAddress.Loopback);
        Require(await Task.WhenAny(request, Task.Delay(2000)) == request, "The SNMP query reaches the owned loopback fixture.");
        var received = await request;
        if (wrongSource)
        {
            using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] injected = Response(received.Buffer, "Injected source");
            await rogue.SendAsync(injected, injected.Length, received.RemoteEndPoint);
            await Task.Delay(50);
        }
        byte[] bytes = response(received.Buffer);
        await server.SendAsync(bytes, bytes.Length, received.RemoteEndPoint);
        Require(await Task.WhenAny(probe, Task.Delay(2000)) == probe, "The SNMP probe has a bounded lifetime.");
        await probe;
        return device.SnmpDescr;
    }

    private static byte[] Response(byte[] query, string text, string fault = "")
    {
        byte[] id = query.Skip(17).Take(4).ToArray();
        if (fault == "request") id[3] ^= 1;
        byte[] oid = [0x2b, 6, 1, 2, 1, 1, 1, 0];
        if (fault == "oid") oid[6] = 2;
        byte[] value = Pack(0x04, Encoding.UTF8.GetBytes(text));
        if (fault == "indefinite") value = [0x04, 0x80, .. Encoding.UTF8.GetBytes(text), 0, 0];
        if (fault == "overflow") value = [0x04, 0x84, 0x7f, 0xff, 0xff, 0xff, .. Encoding.UTF8.GetBytes(text)];
        byte[] data = Pack(0x30, Pack(0x02, new byte[] { (byte)(fault == "version" ? 1 : 0) }),
            Pack(0x04, Encoding.ASCII.GetBytes(fault == "community" ? "private" : "public")),
            Pack(fault == "pdu" ? (byte)0xa0 : (byte)0xa2, Pack(0x02, id),
                Pack(0x02, new byte[] { (byte)(fault == "error" ? 2 : 0) }), Pack(0x02, new byte[] { (byte)(fault == "index" ? 1 : 0) }),
                Pack(0x30, Pack(0x30, Pack(0x06, oid), value))));
        if (fault == "truncated") return data.Take(data.Length - 1).ToArray();
        if (fault == "trailing") return [.. data, 0x04, 0x08, .. Encoding.ASCII.GetBytes("injected")];
        return data;
    }

    private static byte[] Pack(byte tag, params byte[][] values)
    {
        byte[] body = values.SelectMany(value => value).ToArray();
        byte[] length = body.Length < 128 ? [(byte)body.Length] : body.Length < 256 ? [(byte)0x81, (byte)body.Length] : [(byte)0x82, (byte)(body.Length >> 8), (byte)body.Length];
        return [tag, .. length, .. body];
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
