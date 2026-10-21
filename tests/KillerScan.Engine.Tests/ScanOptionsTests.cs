using KillerScan.Engine;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class ScanOptionsTests
{
    public static async Task Run()
    {
        var defaults = new ScanOptions();
        Require(defaults.DisabledFeatures.Length == 0 && ScanOptions.Features.All(defaults.IsEnabled),
            "All probes remain enabled for a new scan.");
        var selected = defaults.Without(" DNS, http ").WithFeature("icmp", false);
        Require(!selected.ReverseDns && !selected.Http && !selected.Icmp && selected.Ports && selected.Tls,
            "Only the selected checks are disabled.");
        Require(selected.DisabledFeatures == "icmp,dns,http", "Disabled checks have stable names and order.");
        Require(defaults.ReverseDns && defaults.Http && defaults.Icmp,
            "Changing a selection leaves the captured options unchanged.");
        Require(selected.WithFeature("http", true).Http, "A disabled check can be enabled again.");
        foreach (string invalid in new[] { "", "http,", "unknown" })
        {
            bool rejected = false;
            try { defaults.Without(invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid check names are refused: " + invalid);
        }
        Console.WriteLine("PASS: scan options preserve defaults, immutable selections and validated names.");
        await DiscoveryChoices();
        await FullProbeChoices();
        await DeepProbeChoices();
    }

    private static ScanOptions None() => new ScanOptions().Without(string.Join(",", ScanOptions.Features));

    private static async Task DeepProbeChoices()
    {
        var original = NetworkPlatform.Current;
        var platform = new ControlledPlatform();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            NetworkPlatform.Current = platform;
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var device = await scanner.DeepProbeHostAsync("127.0.0.1", deadline.Token, None());
            Require(device.IpAddress == "127.0.0.1" && device.MacAddress.Length == 0 && device.Hostname.Length == 0 &&
                device.Ttl == null && device.OpenPorts.Count == 0 && device.HttpTitle.Length == 0 &&
                device.HttpServer.Length == 0 && device.TlsSubject.Length == 0 && device.SshBanner.Length == 0 &&
                device.NetbiosName.Length == 0 && device.SnmpDescr.Length == 0,
                "Deep rescan respects disabled probes instead of silently restoring its defaults.");
            Require(platform.Reads == 0 && platform.Macs == 0 && platform.Flushes == 0,
                "Deep rescan skips disabled platform discovery and cache flushes.");
            device = await scanner.DeepProbeHostAsync("127.0.0.1", deadline.Token, None().WithFeature("arp", true));
            Require(device.MacAddress == "00:00:0C:12:34:56" && platform.Macs == 1,
                "Deep rescan can enable active MAC resolution independently.");
        }
        finally { NetworkPlatform.Current = original; }
        Console.WriteLine("PASS: deep rescans retain the selected checks.");
    }

    private static async Task FullProbeChoices()
    {
        var original = NetworkPlatform.Current;
        using var server = new HttpFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            NetworkPlatform.Current = new ControlledPlatform();
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var options = None().WithFeature("neighbor-cache", true);
            var device = (await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token, options)).Single();
            Require(device.OpenPorts.Count == 0 && device.Hostname.Length == 0 && device.Ttl == null &&
                device.HttpTitle.Length == 0 && device.TlsSubject.Length == 0 && device.SshBanner.Length == 0 &&
                device.SnmpDescr.Length == 0 && device.NetbiosName.Length == 0,
                "A full scan omits disabled port, DNS, ICMP and fingerprint results.");
            Require(server.Requests == 0, "Disabled port and HTTP checks send no request to the fixture.");

            options = options.WithFeature("ports", true);
            device = (await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token, options)).Single();
            Require(device.OpenPorts.Contains(server.Port), "Enabling TCP ports discovers the controlled web port.");
            Require(device.HttpTitle.Length == 0 && device.HttpServer.Length == 0 && server.Requests == 0,
                "Discovering a web port does not implicitly enable its HTTP check.");

            device = (await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token,
                options.WithFeature("http", true))).Single();
            Require(server.Requests > 0 && device.HttpTitle == "Selected scan checks" &&
                device.HttpServer == "KillerScan-Options",
                "Re-enabling HTTP queries the owned fixture and returns its exact title and Server header.");
        }
        finally { NetworkPlatform.Current = original; }
        Console.WriteLine("PASS: full scans independently gate port discovery and fingerprints.");
    }

    private static async Task DiscoveryChoices()
    {
        var original = NetworkPlatform.Current;
        var platform = new ControlledPlatform();
        var discovery = new ControlledDiscovery();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            NetworkPlatform.Current = platform;
            var scanner = new NetworkScanner(discovery);
            int progress = 0;
            scanner.ProgressChanged += value => progress = value;
            var devices = await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token, None(), fullScan: false);
            Require(devices.Count == 0 && progress == 100, "With discovery disabled, no loopback device is invented.");
            Require(platform.Reads == 0 && platform.Macs == 0 && platform.Flushes == 0 &&
                discovery.MdnsCalls == 0 && discovery.SsdpCalls == 0,
                "Disabled discovery avoids neighbor, DNS cache and multicast operations.");

            devices = await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token,
                None().WithFeature("neighbor-cache", true), fullScan: false);
            Require(devices.Count == 1 && devices[0].MacAddress.Length == 0 && devices[0].Hostname.Length == 0,
                "Neighbor-only discovery preserves the cached device without active ARP or DNS enrichment.");
            Require(platform.Reads == 2 && platform.Macs == 0 && platform.Flushes == 0,
                "Neighbor selection reads the OS cache without requesting active resolution.");

            devices = await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token,
                None().WithFeature("icmp", true).WithFeature("arp", true), fullScan: false);
            Require(devices.Count == 1 && devices[0].MacAddress == "00:00:0C:12:34:56" && platform.Macs == 1,
                "Real loopback ICMP discovers the host and enabled ARP obtains its address.");
            Require(platform.Reads == 2 && platform.Flushes == 0,
                "An ICMP scan still respects disabled neighbor and DNS choices.");
        }
        finally { NetworkPlatform.Current = original; }
        Console.WriteLine("PASS: quick scans obey discovery options through OS and multicast boundaries.");
    }

    private sealed class ControlledPlatform : INetworkPlatform
    {
        public int Reads, Macs, Flushes;
        public IReadOnlyDictionary<string, string> ReadNeighborCache()
        {
            Reads++;
            return new Dictionary<string, string> { ["127.0.0.1"] = string.Empty };
        }
        public string ResolveMac(IPAddress address) { Macs++; return "00:00:0C:12:34:56"; }
        public void FlushDnsCache() => Flushes++;
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Loopback", "");
    }

    private sealed class ControlledDiscovery : IScanDiscovery
    {
        public int MdnsCalls, SsdpCalls;
        public Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> CollectMdnsAsync(CancellationToken ct)
        {
            MdnsCalls++;
            return Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>());
        }
        public Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct)
        {
            SsdpCalls++;
            return Task.FromResult(new Dictionary<string, string>());
        }
    }

    private sealed class HttpFixture : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> _clients = new();
        private int _requests;
        public int Port => 80;
        public int Requests => Volatile.Read(ref _requests);

        public HttpFixture()
        {
            // Own the first HTTP candidate so another local service cannot supply the result.
            _listener = new TcpListener(IPAddress.Loopback, Port) { ExclusiveAddressUse = true };
            try { _listener.Start(); }
            catch (SocketException ex)
            {
                _listener.Stop();
                throw new InvalidOperationException("The scan options fixture requires exclusive loopback port 80.", ex);
            }
            _ = Accept();
        }

        private async Task Accept()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _clients.Add(client);
                    _ = Respond(client);
                }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
            }
        }

        private async Task Respond(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var bytes = new byte[4096];
                    int count = await stream.ReadAsync(bytes, 0, bytes.Length, _stop.Token);
                    if (count == 0 || !Encoding.ASCII.GetString(bytes, 0, count).StartsWith("GET ", StringComparison.Ordinal)) return;
                    Interlocked.Increment(ref _requests);
                    const string body = "<title>Selected scan checks</title>";
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                        "\r\nConnection: close\r\nServer: KillerScan-Options\r\n\r\n" + body);
                    await stream.WriteAsync(response, 0, response.Length, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients) client.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
