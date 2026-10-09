using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using KillerScan.Engine;

internal static class EngineBehaviorTests
{
    private sealed class LoopbackPlatform : INetworkPlatform
    {
        public Func<string>? MacLookup { get; set; }
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => MacLookup?.Invoke() ?? "00:00:0C:12:34:56";
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
            (string Title, string Server)? fingerprintAtCompletion = null;
            NetworkScanner.DeviceCompleted = device =>
            {
                fingerprintAtCompletion = (device.HttpTitle, device.HttpServer);
                device.Hostname = "Saved device name";
            };
            var scanner = new NetworkScanner();
            NetworkDevice? reported = null;
            scanner.DeviceFound += device => reported = device;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var devices = await scanner.ScanSubnetAsync("127.0.0.1", deadline.Token);
            Require(devices.Count == 1, "Full loopback scan returns one device.");
            var result = devices.Single();
            Require(result.OpenPorts.Contains(server.Port), "Full scan discovers the controlled HTTP port.");
            // A runner may also serve HTTP on port 80, which correctly wins over this fixture.
            // Exact title/header extraction is checked separately with a controlled port list.
            Require(result.HttpTitle.Length > 0 || result.HttpServer.Length > 0,
                "Full scan retains a real HTTP fingerprint from the first responsive web port.");
            Require(fingerprintAtCompletion == (result.HttpTitle, result.HttpServer),
                "HTTP fingerprinting finishes before the completion hook and survives device reporting.");
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

    public static async Task HttpFingerprintResults()
    {
        using var server = new LoopbackHttpServer(stall: false, ephemeral: true);
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [server.Port] };
        await StartFingerprint("ProbeHttpAsync", device);
        Require(server.RequestReceived.IsCompleted, "The HTTP fingerprint reaches the controlled fixture.");
        Require(device.HttpTitle == "KillerScan loopback fixture", "The real HTTP probe extracts the exact title.");
        Require(device.HttpServer == "KillerScan-Test", "The real HTTP probe extracts the exact Server header.");
    }

    public static async Task DeepProbeCancellation()
    {
        var platform = NetworkPlatform.Current;
        var completed = NetworkScanner.DeviceCompleted;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<NetworkDevice>? scan = null;
        int completionHooks = 0;
        try
        {
            NetworkPlatform.Current = new LoopbackPlatform
            {
                MacLookup = () =>
                {
                    started.TrySetResult(true);
                    try { return release.Task.GetAwaiter().GetResult(); }
                    finally { finished.TrySetResult(true); }
                }
            };
            NetworkScanner.DeviceCompleted = _ => Interlocked.Increment(ref completionHooks);
            scan = new NetworkScanner().DeepProbeHostAsync("127.0.0.1", cancel.Token);
            Require(await Task.WhenAny(started.Task, Task.Delay(5000)) == started.Task,
                "Deep probe reaches the controlled MAC lookup.");
            await RequirePromptCancellation(scan, cancel);
            Require(!finished.Task.IsCompleted && completionHooks == 0,
                "Canceled deep probes never apply completion hooks or return stale results.");
        }
        finally
        {
            cancel.Cancel();
            release.TrySetResult("00:00:0C:12:34:56");
            if (scan != null) try { await scan; } catch { }
            if (started.Task.IsCompleted) await finished.Task;
            NetworkPlatform.Current = platform;
            NetworkScanner.DeviceCompleted = completed;
        }
        Require(completionHooks == 0, "Releasing the canceled lookup cannot complete a stale device.");
    }

    public static Task HttpFingerprintCancellation() => FingerprintCancellation("ProbeHttpAsync", tls: false);
    public static Task TlsFingerprintCancellation() => FingerprintCancellation("ProbeTlsCertAsync", tls: true);

    private static async Task FingerprintCancellation(string probeName, bool tls)
    {
        using var server = new LoopbackHttpServer(stall: true, ephemeral: true, tls: tls);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var device = new NetworkDevice { IpAddress = "127.0.0.1", OpenPorts = [server.Port] };
        // Exercise the released private probes and the exact cancellation wait used by the deep
        // fingerprint pass. An explicit port list isolates real HTTP/TLS I/O from runner services.
        var probe = StartFingerprint(probeName, device);
        var wait = typeof(NetworkScanner).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "AwaitWithCancellation" && !method.IsGenericMethod);
        var scan = (Task)wait.Invoke(null, new object[] { Task.WhenAll(probe), cancel.Token })!;
        try
        {
            Require(await Task.WhenAny(server.RequestReceived, scan, Task.Delay(5000)) == server.RequestReceived,
                "The real " + (tls ? "TLS ClientHello" : "HTTP GET") + " reaches the stalled fixture.");
            Require(!probe.IsCompleted && !scan.IsCompleted, "Fingerprinting is still waiting for the fixture response.");
            await RequirePromptCancellation(scan, cancel);
            Require(!probe.IsCompleted, "Stop returns before the fingerprint request times out.");
            Require(device.HttpTitle.Length == 0 && device.HttpServer.Length == 0 && device.TlsSubject.Length == 0,
                "The stalled fingerprint has no completed HTTP or TLS result.");
        }
        finally
        {
            cancel.Cancel();
            server.Dispose();
            await probe;
            try { await scan; } catch (OperationCanceledException) { }
        }
    }

    private static Task StartFingerprint(string probeName, NetworkDevice device) =>
        (Task)typeof(NetworkScanner).GetMethod(probeName, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { device, IPAddress.Loopback, true })!;

    private static async Task RequirePromptCancellation(Task scan, CancellationTokenSource cancel)
    {
        Require(!scan.IsCompleted, "The operation is pending before Stop.");
        var elapsed = Stopwatch.StartNew();
        cancel.Cancel();
        Require(await Task.WhenAny(scan, Task.Delay(500)) == scan, "Stop returns within 500 ms.");
        try
        {
            await scan;
            throw new InvalidOperationException("Cancellation must throw instead of returning a completed result.");
        }
        catch (OperationCanceledException) { }
        Require(scan.IsCanceled && elapsed.ElapsedMilliseconds < 500, "The operation is canceled promptly.");
    }

    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly TaskCompletionSource<bool> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _stall;
        private readonly bool _tls;
        public int Port { get; }
        public Task RequestReceived => _requested.Task;

        public LoopbackHttpServer(bool stall, bool ephemeral = false, bool tls = false)
        {
            _stall = stall;
            _tls = tls;
            foreach (int port in ephemeral ? new[] { 0 } : new[] { 8080, 5000, 8123 })
            {
                var candidate = new TcpListener(IPAddress.Loopback, port);
                try { candidate.Start(); _listener = candidate; Port = ((IPEndPoint)candidate.LocalEndpoint).Port; break; }
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
                    int count = 0;
                    while (count < 5)
                    {
                        int read = await stream.ReadAsync(input, count, input.Length - count, _stop.Token);
                        if (read == 0) return;
                        count += read;
                    }
                    bool http = Encoding.ASCII.GetString(input, 0, count).StartsWith("GET ", StringComparison.Ordinal);
                    bool tls = input[0] == 0x16 && input[1] == 0x03;
                    if (_stall && (_tls ? tls : http))
                    {
                        _requested.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }
                    if (!http) return;
                    _requested.TrySetResult(true);
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
