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

        MixedHosts();

        Console.WriteLine("PASS: mDNS names and services land only on the host the records describe.");
        return Task.CompletedTask;
    }

    private static byte[] Srv(string target) => [0, 0, 0, 0, 2, 119, .. Name(target)];

    // One packet can describe several devices, as a reflector's does. The SRV record says which
    // host each service instance runs on, and the A record says where that host is.
    private static void MixedHosts()
    {
        (string, int, byte[])[] records =
        [
            ("printer.local", 1, Address("192.0.2.40")),
            ("_ipp._tcp.local", 12, Name("Office._ipp._tcp.local")),
            ("Office._ipp._tcp.local", 33, Srv("printer.local")),
            ("speaker.local", 1, Address("192.0.2.50")),
            ("_airplay._tcp.local", 12, Name("Speaker._airplay._tcp.local")),
            ("Speaker._airplay._tcp.local", 33, Srv("speaker.local")),
        ];
        void TwoHosts(Dictionary<string, MulticastDiscovery.MdnsInfo> found, string label)
        {
            Require(found.Count == 2 && !found.ContainsKey("192.0.2.1"), label + ": the reflector gets nothing and both hosts appear.");
            var printer = found["192.0.2.40"];
            var speaker = found["192.0.2.50"];
            Require(printer.Name == "printer" && speaker.Name == "speaker", label + ": each host keeps its own name.");
            Require(printer.Services.Contains("office._ipp._tcp.local") && printer.Services.Contains("_ipp._tcp.local")
                    && !printer.Services.Any(service => service.Contains("airplay")), label + ": the printer has only printer services.");
            Require(speaker.Services.Contains("speaker._airplay._tcp.local") && speaker.Services.Contains("_airplay._tcp.local")
                    && !speaker.Services.Any(service => service.Contains("ipp")), label + ": the speaker has only speaker services.");
        }
        TwoHosts(Parse(Packet(true, records), "192.0.2.1"), "Relayed");
        TwoHosts(Parse(Packet(true, Enumerable.Reverse(records).ToArray()), "192.0.2.1"), "Reordered");
        TwoHosts(Parse(Packet(true, records.Concat(records).ToArray()), "192.0.2.1"), "Duplicated");
        TwoHosts(Parse(Packet(true, records), "192.0.2.40"), "Sent by one of them");

        // Names compressed the way responders send them: pointers back into the packet.
        var compressed = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 2, 0, 0, 0, 0 };
        int hostName = compressed.Count;
        compressed.AddRange(Name("printer.local"));
        compressed.AddRange(new byte[] { 0, 1, 0, 1, 0, 0, 0, 120, 0, 4 });
        compressed.AddRange(Address("192.0.2.40"));
        compressed.AddRange(Name("Office._ipp._tcp.local"));
        compressed.AddRange(new byte[] { 0, 33, 0, 1, 0, 0, 0, 120, 0, 8, 0, 0, 0, 0, 2, 119, 0xC0, (byte)hostName });
        var one = Parse(compressed.ToArray(), "192.0.2.1");
        Require(one.Count == 1 && one["192.0.2.40"].Services.Contains("office._ipp._tcp.local"), "A compressed SRV target still finds its host.");

        // A service with no A record for its host belongs to the sender when the sender is the only host in sight.
        var direct = Parse(Packet(true, ("Office._ipp._tcp.local", 33, Srv("printer.local"))), "192.0.2.40");
        Require(direct["192.0.2.40"].Name == "printer" && direct["192.0.2.40"].Services.Contains("office._ipp._tcp.local"), "An SRV with no A record names its sender.");

        // Never guessed: a relayed service that says nothing about its host lands on no host at all.
        var mixed = Parse(Packet(true, [.. records, ("_hap._tcp.local", 12, Name("Bridge._hap._tcp.local"))]), "192.0.2.1");
        Require(!mixed.Values.Any(info => info.Services.Any(service => service.Contains("hap"))), "A relayed service with no owner is dropped, not given to the first host.");
        TwoHosts(mixed, "With an orphan");

        // A service whose SRV names a host with no address here is that host's, not the packet's one known host's.
        (string, int, byte[])[] stray =
        [
            ("printer.local", 1, Address("192.0.2.40")),
            ("_airplay._tcp.local", 12, Name("Speaker._airplay._tcp.local")),
            ("Speaker._airplay._tcp.local", 33, Srv("speaker.local")),
        ];
        foreach (string source in new[] { "192.0.2.1", "192.0.2.40" })
        {
            var strayed = Parse(Packet(true, stray), source);
            Require(!strayed.Values.Any(info => info.Services.Any(service => service.Contains("airplay"))), "A service is never given to a host its SRV does not name: " + source);
            Require(strayed["192.0.2.40"].Name == "printer", "The known host keeps its own name: " + source);
        }

        var listing = Parse(Packet(true, ("_services._dns-sd._udp.local", 12, Name("_ipp._tcp.local"))), "192.0.2.40");
        Require(listing["192.0.2.40"].Services.Contains("_ipp._tcp.local"), "A service-type listing belongs to its sender.");

        // Damaged packets keep what was read before the damage and throw nothing.
        byte[] whole = Packet(true, records);
        for (int length = 0; length < whole.Length; length++) Parse(whole.Take(length).ToArray(), "192.0.2.1");
        var cut = Parse(whole.Take(whole.Length - 8).ToArray(), "192.0.2.1");
        Require(cut["192.0.2.40"].Services.Contains("office._ipp._tcp.local"), "A truncated packet keeps the records before the cut.");
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
