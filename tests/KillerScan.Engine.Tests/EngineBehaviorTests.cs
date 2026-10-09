using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerScan.Engine;

internal static class EngineBehaviorTests
{
    private sealed class LoopbackPlatform : INetworkPlatform
    {
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => "00:00:0C:12:34:56";
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Loopback", "");
    }

    public static async Task FullScan()
    {
        var platform = NetworkPlatform.Current;
        var manualType = NetworkScanner.ManualTypeLookup;
        var completed = NetworkScanner.DeviceCompleted;
        using var server = new LoopbackHttpServer(stall: false);
        try
        {
            NetworkPlatform.Current = new LoopbackPlatform();
            NetworkScanner.ManualTypeLookup = _ => "Printer";
            NetworkScanner.DeviceCompleted = device => device.Hostname = "Saved device name";
            var scanner = new NetworkScanner();
            NetworkDevice? reported = null;
            scanner.DeviceFound += device => reported = device;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var devices = await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token);
            Require(devices.Count == 1, "Full loopback scan returns one device.");
            var result = devices.Single();
            Require(result.OpenPorts.Contains(server.Port), "Full scan discovers the controlled HTTP port.");
            Require(result.HttpTitle == "KillerScan loopback fixture", "Full scan extracts the real HTTP title.");
            Require(result.DeviceType == "Printer" && result.Hostname == "Saved device name",
                "Manual classification and the saved-name hook survive the full probe.");
            Require(ReferenceEquals(result, reported), "The UI receives the completed device with host preferences applied.");
        }
        finally
        {
            NetworkPlatform.Current = platform;
            NetworkScanner.ManualTypeLookup = manualType;
            NetworkScanner.DeviceCompleted = completed;
        }
    }

    public static async Task DeepFingerprintCancellation()
    {
        var platform = NetworkPlatform.Current;
        var completed = NetworkScanner.DeviceCompleted;
        using var server = new LoopbackHttpServer(stall: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<NetworkDevice>? scan = null;
        int completionHooks = 0;
        try
        {
            NetworkPlatform.Current = new LoopbackPlatform();
            NetworkScanner.DeviceCompleted = _ => Interlocked.Increment(ref completionHooks);
            scan = new NetworkScanner().DeepProbeHostAsync("127.0.0.1", cancel.Token);
            Require(await Task.WhenAny(server.HttpRequested, Task.Delay(60000)) == server.HttpRequested,
                "Deep probe reaches a controlled stalled HTTP response.");
            var elapsed = Stopwatch.StartNew();
            cancel.Cancel();
            Require(await Task.WhenAny(scan, Task.Delay(500)) == scan, "Stop returns promptly during deep fingerprinting.");
            try
            {
                await scan;
                throw new InvalidOperationException("Canceled deep fingerprinting must not return a finished device.");
            }
            catch (OperationCanceledException) { }
            Require(elapsed.ElapsedMilliseconds < 500 && completionHooks == 0,
                "Canceled deep probes never apply completion hooks or return stale results.");
        }
        finally
        {
            cancel.Cancel();
            server.Dispose();
            if (scan != null) try { await scan; } catch { }
            NetworkPlatform.Current = platform;
            NetworkScanner.DeviceCompleted = completed;
        }
    }

    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly TaskCompletionSource<bool> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _stall;
        public int Port { get; }
        public Task HttpRequested => _requested.Task;

        public LoopbackHttpServer(bool stall)
        {
            _stall = stall;
            foreach (int port in new[] { 8080, 5000, 8123 })
            {
                var candidate = new TcpListener(IPAddress.Loopback, port);
                try { candidate.Start(); _listener = candidate; Port = port; break; }
                catch (SocketException) { candidate.Stop(); }
            }
            if (_listener == null) throw new InvalidOperationException("No loopback fixture port is available.");
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
                    var input = new byte[4096];
                    int count = await stream.ReadAsync(input, 0, input.Length, _stop.Token);
                    if (!Encoding.ASCII.GetString(input, 0, count).StartsWith("GET ", StringComparison.Ordinal)) return;
                    _requested.TrySetResult(true);
                    if (_stall) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                    const string body = "<html><title>KillerScan loopback fixture</title></html>";
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length +
                        "\r\nConnection: close\r\nServer: KillerScan-Test\r\n\r\n" + body);
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
