using System.Net;
using System.Net.Sockets;
using KillerScan.Engine;

// Deep fingerprinting must not send application payloads to raw services such as JetDirect
// printing, and must keep unknown ports connect-only unless the caller opts in.
internal static class SafeProbeTests
{
    internal static async Task Run()
    {
        // A Synology NAS with only DSM HTTPS open still gets its certificate read in a normal scan.
        Require(NetworkScanner.HasStandardTlsPort(new NetworkDevice { OpenPorts = [5001] }),
            "The normal-scan TLS gate must include 5001, like the probe's own port list.");
        Require(!NetworkScanner.HasStandardTlsPort(new NetworkDevice { OpenPorts = [80, 9100] }),
            "Plain HTTP and print ports do not start a TLS probe.");

        using (var raw = Recorder.TryBind(9100))
        using (var unknown = Recorder.Ephemeral())
        {
            Require(raw != null, "Port 9100 must be free on loopback to run this check.");
            var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [raw!.Port, unknown.Port] };
            await NetworkScanner.ProbeHttpAsync(device, IPAddress.Loopback, deep: true);
            await NetworkScanner.ProbeTlsCertAsync(device, IPAddress.Loopback, deep: true);
            await Task.Delay(200);
            Require(raw.Bytes == 0, "Raw print port 9100 received " + raw.Bytes + " payload bytes.");
            Require(unknown.Bytes > 0, "An explicit deep fingerprint still reaches an ordinary unknown port.");
        }

        var platform = NetworkPlatform.Current;
        try
        {
            NetworkPlatform.Current = new QuietPlatform();
            using var raw = Recorder.TryBind(9100)!;
            using var unknown = Recorder.FirstFree(1000, 1024)
                ?? throw new InvalidOperationException("A free well-known loopback port is needed for this check.");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var options = new ScanOptions { ReverseDns = false, Icmp = false, Snmp = false, Netbios = false, Ssh = false };

            var device = await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options);
            await Task.Delay(200);
            Require(device.OpenPorts.Contains(raw.Port) && device.OpenPorts.Contains(unknown.Port),
                "The deep port sweep still reports both ports as open.");
            Require(raw.Bytes == 0 && unknown.Bytes == 0,
                "Default deep scan is connect-only for unknown ports, got " + raw.Bytes + " and " + unknown.Bytes + " bytes.");

            raw.Reset(); unknown.Reset();
            await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options with { ProbeUnknownPorts = true });
            await Task.Delay(200);
            Require(unknown.Bytes > 0, "Opting in sends fingerprint payloads to unknown ports.");
            Require(raw.Bytes == 0, "Opting in never sends payloads to raw print port 9100, got " + raw.Bytes + " bytes.");
        }
        finally { NetworkPlatform.Current = platform; }
        Console.WriteLine("PASS: deep fingerprinting skips raw services and keeps unknown ports connect-only by default.");
        await ProxyIgnored();
        await RacesObserved();
    }

    // A connect or read that loses its timeout race still faults later; nothing may leave that
    // fault for the finalizer to report as an unobserved task exception.
    private static async Task RacesObserved()
    {
        int unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> count = (_, e) => { Interlocked.Increment(ref unobserved); e.SetObserved(); };
        // Faults left over from earlier checks are not this check's to count.
        GC.Collect(); GC.WaitForPendingFinalizers();
        TaskScheduler.UnobservedTaskException += count;
        var platform = NetworkPlatform.Current;
        try
        {
            NetworkPlatform.Current = new QuietPlatform();
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var options = new ScanOptions { ReverseDns = false, Icmp = false, Snmp = false, Netbios = false };
            await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options);
            // Refused loopback connects on Windows finish about two seconds after the race ends.
            await Task.Delay(3000);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        finally
        {
            NetworkPlatform.Current = platform;
            TaskScheduler.UnobservedTaskException -= count;
        }
        Require(unobserved == 0, unobserved + " probe task fault(s) were never observed.");
        Console.WriteLine("PASS: probe timeout races observe the tasks they abandon.");
    }

    // A system proxy must never answer for the device: fingerprinting talks to the target directly.
    private static async Task ProxyIgnored()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int requests = 0;
        var serve = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; }
                using (client)
                {
                    Interlocked.Increment(ref requests);
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    // One read is enough to know a request arrived; the reply never depends on it.
                    try { if (await stream.ReadAsync(buffer, 0, buffer.Length) <= 0) continue; } catch { continue; }
                    byte[] reply = System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nServer: Proxy-Fixture\r\nContent-Type: text/html\r\nContent-Length: 30\r\nConnection: close\r\n\r\n<title>Proxy page</title>     ");
                    try { await stream.WriteAsync(reply, 0, reply.Length); } catch { }
                }
            }
        });
        var proxy = new WebProxy("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/", false);
#if NETFRAMEWORK
        var previous = WebRequest.DefaultWebProxy;
        WebRequest.DefaultWebProxy = proxy;
#else
        var previous = System.Net.Http.HttpClient.DefaultProxy;
        System.Net.Http.HttpClient.DefaultProxy = proxy;
#endif
        try
        {
            // A closed port on this PC's own adapter address: traffic to it never leaves the
            // machine, and both runtimes would send it through a configured proxy, so only the
            // proxy could produce a fingerprint. (.NET Framework skips proxies for loopback.)
            var target = OwnAddress() ?? IPAddress.Parse("127.0.0.2");
            if (target.Equals(IPAddress.Parse("127.0.0.2")))
                Console.WriteLine("NOTE: no adapter address; on .NET Framework the loopback fallback does not exercise the proxy.");
            var closed = new TcpListener(IPAddress.Any, 0);
            closed.Start();
            int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            var device = new NetworkDevice { IpAddress = target.ToString(), OpenPorts = [closedPort] };
            await NetworkScanner.ProbeHttpAsync(device, target, deep: true);
            Require(Volatile.Read(ref requests) == 0, "Device fingerprinting sent " + requests + " request(s) through the system proxy.");
            Require(device.HttpTitle.Length == 0 && device.HttpServer.Length == 0,
                "A proxy response was attributed to the device: " + device.HttpTitle + " / " + device.HttpServer);
        }
        finally
        {
#if NETFRAMEWORK
            WebRequest.DefaultWebProxy = previous;
#else
            System.Net.Http.HttpClient.DefaultProxy = previous;
#endif
            listener.Stop();
            await serve;
        }
        Console.WriteLine("PASS: HTTP fingerprinting ignores the system proxy.");
    }

    // An IPv4 address assigned to an adapter that is up, other than loopback and link-local.
    private static IPAddress? OwnAddress() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));

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

    // Accepts connections and counts every byte received without ever replying.
    private sealed class Recorder : IDisposable
    {
        private readonly TcpListener _listener;
        private int _bytes;
        internal int Port { get; }
        internal int Bytes => Volatile.Read(ref _bytes);
        internal void Reset() => Interlocked.Exchange(ref _bytes, 0);

        private Recorder(TcpListener listener)
        {
            _listener = listener;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        internal static Recorder Ephemeral() => new(new TcpListener(IPAddress.Loopback, 0));

        internal static Recorder? TryBind(int port)
        {
            try { return new Recorder(new TcpListener(IPAddress.Loopback, port)); }
            catch (SocketException) { return null; }
        }

        internal static Recorder? FirstFree(int from, int to)
        {
            for (int port = from; port <= to; port++)
                if (TryBind(port) is { } recorder) return recorder;
            return null;
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Drain(client);
            }
        }

        private async Task Drain(TcpClient client)
        {
            using (client)
            {
                var buffer = new byte[4096];
                try
                {
                    var stream = client.GetStream();
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        Interlocked.Add(ref _bytes, read);
                }
                catch { }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
