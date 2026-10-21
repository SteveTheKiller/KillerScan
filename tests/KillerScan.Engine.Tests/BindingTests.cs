using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerScan.Engine;

// A scan bound to an adapter sends every probe from that adapter's address and refuses targets
// that are neither on its network nor routed through it. Loopback stands in for the adapter: 127.0.0.2 is the bound source and
// the fixtures on 127.0.0.1 record which address each probe came from.
internal static class BindingTests
{
    internal static async Task Run()
    {
        var source = IPAddress.Parse("127.0.0.2");
        var binding = new ScanBinding("Loopback test", source, 8);
        var platform = NetworkPlatform.Current;
        try
        {
            NetworkPlatform.Current = new QuietPlatform();
            var scanner = new NetworkScanner(new IsolatedDiscovery()) { Binding = binding };

            await RequireRefused(() => scanner.ScanSubnetAsync("192.0.2.0/30", CancellationToken.None, new ScanOptions()),
                "A bound scan refuses targets outside the adapter's network.");
            await RequireRefused(() => scanner.DeepProbeHostAsync("192.0.2.1", CancellationToken.None, new ScanOptions()),
                "A bound deep probe refuses a target outside the adapter's network.");
            Require(binding.Contains(IPAddress.Parse("127.9.9.9")) && !binding.Contains(IPAddress.Parse("10.0.0.1")),
                "The adapter's network is its address and prefix length.");

            // Routed targets: allowed only when Windows' best route goes through the bound adapter.
            var routed = new RoutedPlatform { Through = "Loopback test", Network = "198.51.100." };
            NetworkPlatform.Current = routed;
            Require(binding.Reaches(IPAddress.Parse("198.51.100.20")), "A target routed through the bound adapter is reachable.");
            Require(!binding.Reaches(IPAddress.Parse("203.0.113.20")), "A target routed through another adapter is not.");
            Require(binding.Reaches(IPAddress.Parse("127.0.0.9")), "On-link targets stay reachable.");
            var quiet = new ScanOptions().Without(string.Join(",", ScanOptions.Features));
            var viaRoute = await scanner.ScanSubnetAsync("198.51.100.20", CancellationToken.None, quiet);
            Require(viaRoute != null, "A bound scan accepts a target routed through its adapter.");
            await RequireRefused(() => scanner.ScanSubnetAsync("198.51.100.20,203.0.113.20", CancellationToken.None, quiet),
                "A bound scan refuses a target Windows routes through another adapter.");
            try { await scanner.ScanSubnetAsync("203.0.113.20", CancellationToken.None, quiet); }
            catch (ArgumentException ex)
            {
                Require(ex.Message.Contains("203.0.113.20") && ex.Message.Contains("Loopback test") && ex.Message.Contains("Other"),
                    "The refusal names the target, the bound adapter and the adapter Windows would use: " + ex.Message);
            }
            NetworkPlatform.Current = new QuietPlatform();

            using var tcp = new TcpRecorder();
            using var udp = new UdpRecorder();
            using var http = new TcpRecorder(reply: "HTTP/1.1 200 OK\r\nServer: Bound-Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            NetworkScanner.UdpPortOverride = udp.Port;
            var options = new ScanOptions().Without("arp,neighbor-cache,mdns,ssdp,dns,snmp,ssh,tls");
            NetworkScanner.DeepPortsOverride = [tcp.Port, http.Port];
            var device = await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options with { ProbeUnknownPorts = true });
            await Task.Delay(300);
            Require(device.OpenPorts.Contains(tcp.Port), "The bound deep probe still finds the open port.");
            Require(tcp.Sources.Count > 0 && tcp.Sources.All(s => s.Equals(source)), "TCP probes come from the bound address: " + string.Join(", ", tcp.Sources));
            Require(http.Sources.Count > 0 && http.Sources.All(s => s.Equals(source)), "HTTP fingerprints come from the bound address: " + string.Join(", ", http.Sources));
            Require(device.HttpServer == "Bound-Fixture", "The bound HTTP fingerprint still reads the response.");
            Require(udp.Sources.Count > 0 && udp.Sources.All(s => s.Equals(source)), "UDP probes come from the bound address: " + string.Join(", ", udp.Sources));

            // ICMP needs a source address actually assigned to an adapter, which 127.0.0.2 is
            // not, so the bound ping is checked from 127.0.0.1.
            var pinger = new NetworkScanner(new IsolatedDiscovery()) { Binding = new ScanBinding("Loopback", IPAddress.Loopback, 8) };
            var pinged = await pinger.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options.Without("ports,http"));
            Require(pinged.Ttl > 0, "A bound ping from the adapter's address gets a reply and a TTL.");
            // Bound pings run asynchronously, so many at once all complete with their own reply.
            var burst = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
            {
                ScanSource.Current = new ScanBinding("Loopback", IPAddress.Loopback, 8);
                try { return await ScanSource.PingAsync(IPAddress.Loopback, 1000); }
                finally { ScanSource.Current = null; }
            })));
            Require(burst.All(reply => reply.Success && reply.Ttl > 0), "64 concurrent bound pings each get a reply and a TTL.");

            var unbound = new NetworkScanner(new IsolatedDiscovery());
            int before = tcp.Sources.Count;
            await unbound.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options);
            await Task.Delay(300);
            var later = tcp.Sources.Skip(before).ToList();
            Require(later.Count > 0 && later.All(s => s.Equals(IPAddress.Loopback)), "An unbound scan uses the normal route.");
        }
        finally
        {
            NetworkPlatform.Current = platform;
            NetworkScanner.UdpPortOverride = null;
            NetworkScanner.DeepPortsOverride = null;
        }
        Console.WriteLine("PASS: a bound scan sends TCP, UDP, HTTP and ping from the adapter and refuses targets Windows routes elsewhere.");
    }

    private static async Task RequireRefused(Func<Task> call, string message)
    {
        try { await call(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class QuietPlatform : INetworkPlatform
    {
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => "";
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Loopback", "");
    }

    private sealed class RoutedPlatform : INetworkPlatform
    {
        internal string Through = "", Network = "";
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => "";
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) =>
            address.ToString().StartsWith(Network) ? (Through, "10.8.0.1") : ("Other", "192.168.1.1");
    }

    private sealed class TcpRecorder : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string? _reply;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<IPAddress> Sources = new();
        internal int Port { get; }
        internal TcpRecorder(string? reply = null)
        {
            _reply = reply;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Accept();
        }
        private async Task Accept()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                Sources.Enqueue(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            if (_reply != null && await stream.ReadAsync(buffer, 0, buffer.Length) > 0)
                            {
                                var bytes = Encoding.ASCII.GetBytes(_reply);
                                await stream.WriteAsync(bytes, 0, bytes.Length);
                            }
                            else await Task.Delay(300);
                        }
                        catch { }
                    }
                });
            }
        }
        public void Dispose() => _listener.Stop();
    }

    private sealed class UdpRecorder : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal readonly System.Collections.Concurrent.ConcurrentBag<IPAddress> Sources = new();
        internal int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        internal UdpRecorder() => _ = Receive();
        private async Task Receive()
        {
            while (true)
            {
                try { Sources.Add((await _udp.ReceiveAsync()).RemoteEndPoint.Address); } catch { return; }
            }
        }
        public void Dispose() => _udp.Dispose();
    }
}
