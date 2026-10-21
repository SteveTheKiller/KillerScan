using System.Text.Json;
using KillerScan.Engine;

// A labeled corpus of made-up devices: each row says what the device is and how sure the
// classifier should be. The corpus includes weak, empty and contradictory devices on purpose,
// so the checks cover Low and conflicting results, not only easy positives.
internal static class RecognitionTests
{
    private sealed class Case(string label, NetworkDevice device, string type, RecognitionConfidence confidence, bool Conflict = false)
    {
        public string Label { get; } = label;
        public NetworkDevice Device { get; } = device;
        public string Type { get; } = type;
        public RecognitionConfidence Confidence { get; } = confidence;
        public bool Conflict { get; } = Conflict;
    }

    private static NetworkDevice Device(string ip, params int[] ports) => new() { IpAddress = ip, OpenPorts = [.. ports] };

    internal static Task Run()
    {
        string oldGateway = NetworkScanner.GatewayIp, oldDns = NetworkScanner.DnsIp;
        try
        {
            NetworkScanner.GatewayIp = "192.0.2.1";
            NetworkScanner.DnsIp = "192.0.2.53";
            var corpus = new List<Case>
            {
                new("gateway", Device("192.0.2.1", 53, 80), "Router", RecognitionConfidence.High),
                new("printer, mDNS and SNMP agree",
                    new NetworkDevice { IpAddress = "192.0.2.10", Vendor = "Brother Industries", OpenPorts = [631, 9100],
                        MdnsServices = ["_ipp._tcp"], SnmpDescr = "Brother NC-8300w printer" },
                    "Printer", RecognitionConfidence.High),
                new("printer by port only", Device("192.0.2.11", 9100), "Printer", RecognitionConfidence.Low),
                new("domain controller by ports", Device("192.0.2.12", 53, 88, 135, 389, 445, 3389), "Windows Server", RecognitionConfidence.Medium),
                new("Windows with OpenSSH and NetBIOS",
                    new NetworkDevice { IpAddress = "192.0.2.13", OpenPorts = [22, 445], NetbiosName = "DESK-01", SshBanner = "SSH-2.0-OpenSSH_for_Windows_9.5" },
                    "Windows", RecognitionConfidence.High),
                new("Linux by SSH banner", new NetworkDevice { IpAddress = "192.0.2.14", OpenPorts = [22], SshBanner = "SSH-2.0-OpenSSH_9.6p1", Ttl = 64 },
                    "Linux/SSH", RecognitionConfidence.Medium),
                new("NAS by hostname alone", new NetworkDevice { IpAddress = "192.0.2.15", Hostname = "office-synology" }, "NAS", RecognitionConfidence.Medium),
                new("hostname outvoted by protocols",
                    new NetworkDevice { IpAddress = "192.0.2.16", Hostname = "old-pihole", OpenPorts = [631, 9100],
                        MdnsServices = ["_ipp._tcp"], SnmpDescr = "HP LaserJet M404", HttpTitle = "Embedded Web Server" },
                    "Printer", RecognitionConfidence.High),
                new("close call between name and protocol",
                    new NetworkDevice { IpAddress = "192.0.2.25", Hostname = "office-synology", OpenPorts = [9100], MdnsServices = ["_ipp._tcp"] },
                    "Printer", RecognitionConfidence.Low, Conflict: true),
                new("smart plug, no ports", new NetworkDevice { IpAddress = "192.0.2.17", MacAddress = "00:11:22:33:44:55" }, "IoT", RecognitionConfidence.Low),
                new("private MAC phone", new NetworkDevice { IpAddress = "192.0.2.18", MacAddress = "DA:11:22:33:44:55" }, "Mobile", RecognitionConfidence.Low),
                new("nothing at all", Device("192.0.2.19"), "Unknown", RecognitionConfidence.Low),
                new("odd port only", Device("192.0.2.20", 5000), "Other", RecognitionConfidence.Low),
                new("Chromecast", new NetworkDevice { IpAddress = "192.0.2.21", MdnsServices = ["_googlecast._tcp"], OpenPorts = [8008, 8009] },
                    "Smart TV", RecognitionConfidence.Medium),
                new("camera vendor and title",
                    new NetworkDevice { IpAddress = "192.0.2.22", Vendor = "Hangzhou Hikvision", OpenPorts = [80, 554], HttpTitle = "Hikvision NVR" },
                    "Camera", RecognitionConfidence.Medium),
                new("Proxmox port and title", new NetworkDevice { IpAddress = "192.0.2.23", OpenPorts = [22, 8006], HttpTitle = "proxmox virtual environment" },
                    "Hypervisor", RecognitionConfidence.Medium),
                new("Cisco switch by vendor only", new NetworkDevice { IpAddress = "192.0.2.24", Vendor = "Cisco Systems" }, "Switch/AP", RecognitionConfidence.Low),
            };

            int unknown = 0;
            foreach (var test in corpus)
            {
                var result = NetworkScanner.AssessDevice(test.Device, null);
                Require(result.Type == test.Type, $"{test.Label}: expected {test.Type}, got {result.Type}.");
                Require(result.Confidence == test.Confidence, $"{test.Label}: expected {test.Confidence} confidence, got {result.Confidence}.");
                Require(result.Conflict == test.Conflict, $"{test.Label}: conflict should be {test.Conflict}.");
                Require(result.Signals.Count > 0, $"{test.Label}: every result names at least one signal.");
                Require(result.AutomaticType == result.Type && result.AutomaticConfidence == result.Confidence, $"{test.Label}: no manual type, so automatic and shown agree.");
                Require(NetworkScanner.ClassifyDevice(test.Device, null) == result.Type, $"{test.Label}: ClassifyDevice and AssessDevice agree.");
                if (result.Type is "Unknown" or "Other") unknown++;
            }
            Console.WriteLine($"Recognition corpus: {corpus.Count} devices, {unknown} left Unknown or Other.");

            // The outvoted hostname is still on record, as the alternative it pointed to.
            var outvoted = NetworkScanner.AssessDevice(corpus.Single(c => c.Label == "hostname outvoted by protocols").Device, null);
            Require(outvoted.Alternatives.Any(a => a.Type == "DNS Server"), "An outvoted hostname keeps its type as an alternative.");
            Require(outvoted.Signals.Select(s => s.Source).Distinct().Count() >= 3, "Signals record each source family that agreed.");

            // Evidence for a refinement of the winner is listed with it.
            var router = NetworkScanner.AssessDevice(new NetworkDevice { IpAddress = "192.0.2.40", Vendor = "MikroTik", OpenPorts = [53] }, null);
            Require(router.Type == "Router" && router.Signals.Any(s => s.Source == RecognitionSources.Vendor && s.Detail == "MikroTik"),
                "A router's network-vendor hint is part of its evidence.");

            // A manual type wins and keeps the automatic result alongside.
            var printer = corpus.Single(c => c.Label == "printer by port only").Device;
            printer.MacAddress = "00:00:5E:00:53:10";
            var manual = NetworkScanner.AssessDevice(printer, mac => mac == printer.MacAddress ? "NAS" : null);
            Require(manual.Type == "NAS" && manual.Confidence == RecognitionConfidence.Manual &&
                    manual.AutomaticType == "Printer" && manual.AutomaticConfidence == RecognitionConfidence.Low,
                "A manual type is shown as Manual with the automatic result kept.");

            // Fallback guesses say which rule produced them, with a stable key.
            var plug = NetworkScanner.AssessDevice(corpus.Single(c => c.Label == "smart plug, no ports").Device, null);
            Require(plug.Signals[0].Source == RecognitionSources.Fallback && plug.Signals[0].Detail == RecognitionRules.NoPortsWithMac,
                "A fallback guess records its rule.");

            // Observed values are kept short and on one line.
            var hostile = NetworkScanner.AssessDevice(new NetworkDevice
            {
                IpAddress = "192.0.2.30", OpenPorts = [9100],
                SnmpDescr = "printer\r\n" + new string('x', 500),
            }, null);
            Require(hostile.Signals.All(s => s.Detail.Length <= 80 && !s.Detail.Any(char.IsControl)), "Evidence values are bounded and single-line.");

            // The assessment survives a JSON round trip with stable, readable enum names.
            string json = JsonSerializer.Serialize(outvoted);
            Require(json.Contains("\"High\"") && json.Contains("\"snmp\""), "JSON uses readable confidence and source names: " + json);
            var back = JsonSerializer.Deserialize<DeviceRecognition>(json)!;
            Require(back.Type == outvoted.Type && back.Confidence == outvoted.Confidence && back.Signals.Count == outvoted.Signals.Count,
                "A saved assessment reads back unchanged.");

            // Assessing reads the device only.
            var before = JsonSerializer.Serialize(printer);
            NetworkScanner.AssessDevice(printer, null);
            Require(JsonSerializer.Serialize(printer) == before, "Assessing a device does not change it.");
        }
        finally
        {
            NetworkScanner.GatewayIp = oldGateway;
            NetworkScanner.DnsIp = oldDns;
        }
        Console.WriteLine("PASS: recognition gives each type its evidence, alternatives and a confidence grade.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
