using System.Net;
using System.Net.Sockets;
using KillerScan.Engine;

// Deep fingerprinting must not send application payloads to raw services such as JetDirect
// printing, and must keep unknown ports connect-only unless the caller opts in.
internal static class SafeProbeTests
{
    internal static async Task Run()
    {
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
