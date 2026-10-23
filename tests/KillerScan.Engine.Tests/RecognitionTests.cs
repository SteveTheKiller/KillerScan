using System.Net;
using System.Text.Json;
using KillerScan.Engine;

internal static class RecognitionTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static DeviceIdentification Assess(NetworkDevice device, Func<string, string?>? manual = null)
    {
        device.DeviceType = NetworkScanner.ClassifyDeviceForNetwork(device, "", "", manual);
        return device.Identification!;
    }
    internal static Task WeakSignals()
    {
        foreach (var device in new[] { new NetworkDevice(), new NetworkDevice { MacAddress = "02:00:00:00:00:01" } })
        {
            var result = Assess(device);
            Require(result.AutomaticType == "Unknown" && result.Confidence == "None", "Silence/private MAC cannot prove IoT or a mobile device.");
        }
        var vendor = Assess(new NetworkDevice { Vendor = "Synology" });
        Require(vendor.AutomaticType == "NAS" && vendor.Confidence == "Low" && vendor.ReportedModel == null,
            "OUI is a weak role hint, never a model declaration.");
        var name = Assess(new NetworkDevice { Hostname = "office-synology" });
        Require(name.AutomaticType == "NAS" && name.Confidence == "Low", "A name alone has low confidence.");
        var contrary = Assess(new NetworkDevice { Hostname = "synology", HttpTitle = "VMware ESXi", TlsSubject = "CN=VMware ESXi" });
        Require(contrary.AutomaticType == "Hypervisor" && contrary.Confidence == "High", "Protocol evidence can outweigh a weak name.");
        var port = Assess(new NetworkDevice { OpenPorts = [8006] });
        Require(port.AutomaticType == "Hypervisor" && port.Confidence == "Low", "A port alone cannot provide high confidence.");
        return Task.CompletedTask;
    }
    internal static Task CorroborationAndConflict()
    {
        var one = Assess(new NetworkDevice { HttpTitle = "Proxmox VMware ESXi XenServer", HttpServer = "Proxmox" });
        Require(one.AutomaticType == "Hypervisor" && one.Confidence == "Medium", "Repeated hints from HTTP are one source family.");
        var unrelated = Assess(new NetworkDevice { HttpTitle = "VMware ESXi", TlsSubject = "CN=unrelated fixture" });
        Require(unrelated.Confidence == "Medium" && unrelated.Candidates.Single().Sources.SequenceEqual(new[] { "HTTP" }),
            "An unrelated TLS value does not corroborate a matched HTTP rule.");
        var conflict = Assess(new NetworkDevice { HttpTitle = "VMware ESXi", TlsSubject = "CN=VMware", SnmpDescr = "Printer; model=Unit42" });
        Require(conflict.ConflictingSignals && conflict.Confidence == "Low", "A strong contradictory source is visible even when its score is lower.");
        Require(conflict.Rules.Any(rule => rule.Candidate == "Printer" && rule.Sources.Contains("SNMP")), "The alternative has its actual source recorded.");
        var tie = Assess(new NetworkDevice { SnmpDescr = "Printer", HttpTitle = "Synology" });
        Require(tie.ConflictingSignals && tie.Confidence == "Low", "Ties cannot silently produce high confidence.");
        var independent = Assess(new NetworkDevice { SnmpDescr = "Windows Server", HttpTitle = "Unrelated site" });
        Require(independent.Candidates.Single().Sources.SequenceEqual(new[] { "SNMP" }), "Protocol words in a payload do not create a second source.");
        Require(Assess(new NetworkDevice { MdnsServices = { "_PRINTER._TCP.LOCAL" } }).AutomaticType == "Printer", "Service recognition is case-insensitive.");
        return Task.CompletedTask;
    }
    internal static Task ModelsAndUntrustedValues()
    {
        var named = Assess(new NetworkDevice { Vendor = "Synology", Hostname = "DS999", HttpTitle = "DiskStation DS999" });
        Require(named.ReportedModel == null, "Model-looking names/titles are not explicit model declarations.");
        var explicitModel = Assess(new NetworkDevice { SnmpDescr = "Printer; model=Unit42", HttpTitle = "Embedded Web Server; model=Unit42" });
        Require(explicitModel.ReportedModel == "Unit42" && !explicitModel.ModelConflict, "Matching explicit declarations remain labelled as reported, not verified.");
        var different = Assess(new NetworkDevice { SnmpDescr = "model=Unit42", SsdpServer = "model=Other99" });
        Require(different.ReportedModel == null && different.ModelConflict, "Conflicting model declarations do not silently pick one.");
        var hostile = Assess(new NetworkDevice { HttpTitle = new string('a', 600) + "VMware ESXi", SnmpDescr = "model==HYPERLINK(bad)", SsdpServer = "x\r\n\0<script>" });
        Require(hostile.AutomaticType == "Unknown" && hostile.ReportedModel == null, "Uncaptured suffixes and formula-like declarations are not used.");
        Require(hostile.Evidence.All(fact => fact.Value.Length <= 512 && !fact.Value.Any(char.IsControl)), "Displayed/stored evidence is bounded and strips control characters.");
        var longModel = Assess(new NetworkDevice { SnmpDescr = "model=" + new string('X', 70) });
        Require(longModel.ReportedModel == null, "A long model is not truncated into a different model name.");
        return Task.CompletedTask;
    }
    internal static Task ManualAndSnapshots()
    {
        var device = new NetworkDevice { Hostname = "fixture", HttpTitle = "VMware ESXi", TlsSubject = "CN=VMware" };
        var auto = Assess(device, _ => "Printer");
        Require(auto.AutomaticType == "Hypervisor" && auto.EffectiveConfidence == "Manual" && device.DeviceType == "Printer",
            "Manual assignment is distinct from automatic inference and confidence.");
        var stamp = auto.AssessedAtUtc;
        device.Hostname = "synology-renamed-by-user";
        DeviceIdentification.ApplyManualType(device, null);
        Require(device.DeviceType == "Hypervisor" && device.Identification!.AssessedAtUtc == stamp,
            "Clearing a manual assignment restores the frozen inference without treating a user name as new evidence.");
        var copy = device.Identification!.Snapshot();
        device.Identification.Candidates[0].Sources[0] = "changed";
        Require(!copy.Candidates[0].Sources.Contains("changed"), "History snapshots have independent source arrays.");
        var roundTrip = JsonSerializer.Deserialize<DeviceIdentification>(JsonSerializer.Serialize(copy))!;
        Require(roundTrip.AutomaticType == copy.AutomaticType && roundTrip.Evidence.Length == copy.Evidence.Length, "JSON preserves the evidence contract.");
        Require(copy.ObservationCompletedAtUtc == null && copy.Evidence.All(fact => fact.ObservedAtUtc == null), "External classification does not invent capture timestamps.");
        Require(!JsonSerializer.Serialize(new NetworkDevice()).Contains("Identification"), "Unassessed/old devices keep the optional field absent.");
        return Task.CompletedTask;
    }
    private sealed class Probes : NetworkScanner.ScanProbes
    {
        internal override IReadOnlyDictionary<string, string> ReadNeighbors() => new Dictionary<string, string> { ["192.0.2.10"] = "" };
        internal override string ResolveMac(IPAddress address) => "";
        internal override void FlushDns() { }
        internal override Task<string> Hostname(IPAddress address) => Task.FromResult("fixture");
        internal override Task<int?> Ping(IPAddress address, int timeout) => Task.FromResult<int?>(64);
        internal override Task<bool> Connect(IPAddress address, int port, int timeout) => Task.FromResult(port == 80);
        internal override Task<bool> TcpAlive(IPAddress address) => Task.FromResult(false);
        internal override Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> Mdns(CancellationToken token) => Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>
        { ["192.0.2.10"] = new() { Services = { "_printer._tcp.local" } } });
        internal override Task<Dictionary<string, string>> Ssdp(CancellationToken token) => Task.FromResult(new Dictionary<string, string> { ["192.0.2.10"] = "model=Unit42" });
        internal override Task Http(NetworkDevice device, IPAddress address, bool deep) { device.HttpTitle = "Embedded Web Server; model=Unit42"; return Task.CompletedTask; }
        internal override Task Tls(NetworkDevice device, IPAddress address, bool deep) => Task.CompletedTask;
        internal override Task Ssh(NetworkDevice device, IPAddress address) => Task.CompletedTask;
        internal override Task Snmp(NetworkDevice device, IPAddress address) { device.SnmpDescr = "Printer; model=Unit42"; return Task.CompletedTask; }
        internal override Task Netbios(NetworkDevice device, IPAddress address) => Task.CompletedTask;
    }
    internal static async Task ScanProvenance()
    {
        var scanner = new NetworkScanner(new Probes());
        var full = (await scanner.ScanSubnetAsync("192.0.2.10", CancellationToken.None)).Single();
        Require(full.Identification is { Scope: "full", Confidence: "High" }, "A full scan records corroborated protocol evidence.");
        var first = full.Identification!;
        Require(first.ObservationStartedAtUtc <= first.ObservationCompletedAtUtc && first.Evidence.Any(fact => fact.Source == "HTTP" && fact.ObservedAtUtc != null), "Scan windows and protocol observation times are available.");
        var deep = await scanner.DeepProbeHostAsync("192.0.2.10", CancellationToken.None, new ScanOptions().Without("http,snmp"));
        Require(deep.Identification is { Scope: "deep" } && deep.Identification.DisabledFeatures.Contains("http"), "Deep scope records disabled probes.");
        var cached = deep.Identification!.Evidence.Single(fact => fact.Source == "mDNS");
        Require(cached.Reused && cached.ObservedAtUtc == first.Evidence.Single(fact => fact.Source == "mDNS").ObservedAtUtc,
            "Cached multicast values keep their old timestamps.");
        Require(deep.Identification.Confidence != "High" && !deep.Identification.Evidence.Any(fact => fact.Source is "HTTP" or "SNMP"), "Skipped or cached sources cannot masquerade as fresh corroboration.");
        var quick = (await scanner.ScanSubnetAsync("192.0.2.10", CancellationToken.None, new ScanOptions(), false)).Single();
        Require(quick.Identification is { Scope: "quick" } && !quick.Identification.Evidence.Any(fact => fact.Source is "TCP" or "HTTP" or "SNMP"), "Quick scans report their limited scope without inventing port/protocol observations.");
    }
}
