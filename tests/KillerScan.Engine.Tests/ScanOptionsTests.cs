using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using KillerScan.Engine;

internal static class ScanOptionsTests
{
    private const string Host = "192.0.2.10";
    private sealed class Probes : NetworkScanner.ScanProbes
    {
        internal readonly ConcurrentDictionary<string, int> Calls = new();
        internal bool FailMulticast;
        private void Hit(string name) => Calls.AddOrUpdate(name, 1, (_, count) => count + 1);
        internal override IReadOnlyDictionary<string, string> ReadNeighbors()
        { Hit("neighbor-cache"); return new Dictionary<string, string> { [Host] = "" }; }
        internal override string ResolveMac(IPAddress address) { Hit("arp"); return "00:00:0C:12:34:56"; }
        internal override void FlushDns() => Hit("dns-flush");
        internal override Task<string> Hostname(IPAddress address) { Hit("dns"); return Task.FromResult(""); }
        // No ICMP reply, so the TCP fallback is also exercised with all defaults enabled.
        internal override Task<int?> Ping(IPAddress address, int timeout) { Hit("icmp"); return Task.FromResult<int?>(null); }
        internal override Task<bool> TcpAlive(IPAddress address) { Hit("tcp-liveness"); return Task.FromResult(true); }
        internal override Task<bool> Connect(IPAddress address, int port, int timeout)
        { Hit("ports"); return Task.FromResult(port is 80 or 443 or 22); }
        internal override Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> Mdns(CancellationToken ct)
        {
            Hit("mdns");
            if (FailMulticast) return Task.FromException<Dictionary<string, MulticastDiscovery.MdnsInfo>>(new InvalidOperationException("Fixture collector failure"));
            return Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>
                { [Host] = new() { Name = "fixture.local" } });
        }
        internal override Task<Dictionary<string, string>> Ssdp(CancellationToken ct)
        { Hit("ssdp"); return Task.FromResult(new Dictionary<string, string> { [Host] = "Fixture" }); }
        internal override Task Http(NetworkDevice device, IPAddress address, bool deep) { Hit("http"); return Task.CompletedTask; }
        internal override Task Tls(NetworkDevice device, IPAddress address, bool deep) { Hit("tls"); return Task.CompletedTask; }
        internal override Task Ssh(NetworkDevice device, IPAddress address) { Hit("ssh"); return Task.CompletedTask; }
        internal override Task Snmp(NetworkDevice device, IPAddress address) { Hit("snmp"); return Task.CompletedTask; }
        internal override Task Netbios(NetworkDevice device, IPAddress address) { Hit("netbios"); return Task.CompletedTask; }
    }

    public static async Task DisabledProbes()
    {
        foreach (string feature in ScanOptions.Features)
        {
            var options = new ScanOptions().WithFeature(feature, false);
            foreach (bool quick in new[] { false, true })
            {
                var probes = new Probes();
                var devices = await new NetworkScanner(probes).ScanSubnetAsync(Host, CancellationToken.None, options, !quick);
                Require(devices.Count == 1, "Other discovery sources retain the host with " + feature + " disabled.");
                Require(!probes.Calls.ContainsKey(feature), feature + " was not started in " + (quick ? "quick" : "full") + " mode.");
                if (feature == "dns") Require(!probes.Calls.ContainsKey("dns-flush"), "DNS off also prevents the cache flush.");
                if (quick || feature == "ports")
                    foreach (string dependent in new[] { "ports", "http", "tls", "ssh" })
                        Require(!probes.Calls.ContainsKey(dependent), "No implicit " + dependent + " when port scanning is off or quick.");
            }
            var deep = new Probes();
            await new NetworkScanner(deep).DeepProbeHostAsync(Host, CancellationToken.None, options);
            Require(!deep.Calls.ContainsKey(feature), feature + " was not started by deep probe.");
            if (feature == "dns") Require(!deep.Calls.ContainsKey("dns-flush"), "Deep probe respects DNS off.");
            if (feature == "ports")
                foreach (string dependent in new[] { "ports", "http", "tls", "ssh" })
                    Require(!deep.Calls.ContainsKey(dependent), "Deep mode does not bypass ports off for " + dependent);
        }
        var none = new ScanOptions().Without(string.Join(",", ScanOptions.Features));
        var empty = new Probes();
        var scanner = new NetworkScanner(empty);
        Require((await scanner.ScanSubnetAsync(Host, CancellationToken.None, none)).Count == 0, "All off does not invent a live host.");
        await scanner.DeepProbeHostAsync(Host, CancellationToken.None, none);
        Require(empty.Calls.IsEmpty, "All off causes no probe I/O in either entry point.");
    }

    public static async Task CompatibilityAndIsolation()
    {
        var defaults = new Probes();
        await new NetworkScanner(defaults).ScanSubnetAsync(Host, CancellationToken.None);
        foreach (string feature in ScanOptions.Features)
            Require(defaults.Calls.ContainsKey(feature), "The existing API still enables " + feature);
        Require(defaults.Calls.ContainsKey("dns-flush"), "The existing API keeps the DNS cache flush.");

        var probes = new Probes();
        var scanner = new NetworkScanner(probes);
        var first = await scanner.ScanSubnetAsync(Host, CancellationToken.None);
        Require(first.Single().Hostname == "fixture.local" && first.Single().SsdpServer == "Fixture", "Enabled enrichment reaches the result.");
        var second = await scanner.ScanSubnetAsync(Host, CancellationToken.None, new ScanOptions().Without("mdns,ssdp"));
        Require(second.Single().Hostname.Length == 0 && second.Single().SsdpServer.Length == 0, "Disabled collectors do not leak a previous scan's data.");
        var deep = await scanner.DeepProbeHostAsync(Host, CancellationToken.None, new ScanOptions().Without("mdns,ssdp"));
        Require(deep.Hostname.Length == 0 && deep.SsdpServer.Length == 0, "Deep rescan also filters cached enrichment.");
        probes.FailMulticast = true;
        var partial = await scanner.ScanSubnetAsync(Host, CancellationToken.None);
        Require(partial.Count == 1 && partial[0].Hostname.Length == 0 && partial[0].SsdpServer == "Fixture",
            "A failed multicast collector retains other results and cannot reuse stale data.");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var canceled = new Probes();
        try { await new NetworkScanner(canceled).ScanSubnetAsync(Host, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Require(canceled.Calls.IsEmpty, "Already canceled scans start no I/O, including DNS flush.");
    }

    public static Task ProfileSerialization()
    {
        var old = JsonSerializer.Deserialize<ScanOptions>("{}")!;
        Require(old == new ScanOptions(), "Missing properties retain enabled defaults for old profiles.");
        var edited = old.Without(" DNS,mdns,ports ");
        Require(old.ReverseDns && !edited.ReverseDns, "Changing options creates a new immutable snapshot.");
        Require(JsonSerializer.Deserialize<ScanOptions>(JsonSerializer.Serialize(edited)) == edited, "Saved choices survive a JSON round trip.");
        Require(new ScanOptions().Without(edited.DisabledFeatures) == edited, "CLI names round trip every disabled choice.");
        foreach (string invalid in new[] { "", "dns,", "dnss", "dns,,http" })
        {
            try { old.Without(invalid); throw new InvalidOperationException("Unknown/empty names must fail: " + invalid); }
            catch (ArgumentException) { }
        }
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
