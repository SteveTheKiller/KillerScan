using System.Net;
using System.Text;
using KillerScan.Engine;

// mDNS names and services belong to the host the records describe: never to a host that only
// asked, and never to a reflector or repeater that relayed another device's answer.
internal static class MdnsTests
{
    internal static Task Run()
    {
        var map = Parse(Packet(response: false, ("printer.local", 1, Address("192.0.2.40")), ("_ipp._tcp.local", 12, Name("Office._ipp._tcp.local"))), "192.0.2.9");
        Require(map.Count == 0, "A query's known-answer records are not attributed to the host that asked.");

        map = Parse(Packet(response: true, ("printer.local", 1, Address("192.0.2.40")), ("_ipp._tcp.local", 12, Name("Office._ipp._tcp.local"))), "192.0.2.40");
        Require(map.Count == 1 && map["192.0.2.40"].Name == "printer" && map["192.0.2.40"].Services.Contains("office._ipp._tcp.local"),
            "A host's own response names it and lists its services.");

        map = Parse(Packet(response: true, ("tv.local", 1, Address("192.0.2.77")), ("_googlecast._tcp.local", 12, Name("TV._googlecast._tcp.local"))), "192.0.2.1");
        Require(!map.ContainsKey("192.0.2.1") && map.TryGetValue("192.0.2.77", out var relayed) && relayed.Name == "tv"
                && relayed.Services.Contains("tv._googlecast._tcp.local"),
            "A relayed response is filed under the address its A record names, not the reflector.");

        map = Parse(Packet(response: true, ("_hap._tcp.local", 12, Name("Bridge._hap._tcp.local"))), "192.0.2.50");
        Require(map.ContainsKey("192.0.2.50") && map["192.0.2.50"].Services.Contains("bridge._hap._tcp.local"),
            "A response without an A record still belongs to its sender.");

        Console.WriteLine("PASS: mDNS names and services land only on the host the records describe.");
        return Task.CompletedTask;
    }

    private static Dictionary<string, MulticastDiscovery.MdnsInfo> Parse(byte[] packet, string source)
    {
        var map = new Dictionary<string, MulticastDiscovery.MdnsInfo>(StringComparer.OrdinalIgnoreCase);
        MulticastDiscovery.ParseResponse(packet, source, map);
        return map;
    }

    private static byte[] Address(string ip) => IPAddress.Parse(ip).GetAddressBytes();

    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.')) { var l = Encoding.UTF8.GetBytes(label); bytes.Add((byte)l.Length); bytes.AddRange(l); }
        bytes.Add(0);
        return bytes.ToArray();
    }

    // A packet with no questions and every record in the answer section.
    private static byte[] Packet(bool response, params (string Name, int Type, byte[] Data)[] records)
    {
        var bytes = new List<byte> { 0, 0, (byte)(response ? 0x84 : 0x00), 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
        foreach (var (name, type, data) in records)
        {
            bytes.AddRange(Name(name));
            bytes.AddRange(new byte[] { 0, (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length });
            bytes.AddRange(data);
        }
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
