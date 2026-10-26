using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerScan.Engine.SpeedTest;

internal static class UploadAccountingTests
{
    internal static async Task Run()
    {
        foreach (bool partial in new[] { false, true })
        {
            using var server = new Server(oneAck: true, partial: partial);
            var result = await new SpeedTestEngine().RunAsync(Options(server), null, CancellationToken.None);
            long received = server.Received, acknowledged = server.Acknowledged;
            Require(acknowledged == 65536 && result.Upload.BytesTransferred > acknowledged,
                "The selected sent-byte contract includes later unacknowledged payload after a valid acknowledgment.");
            Require(partial ? received < result.Upload.BytesTransferred : received == result.Upload.BytesTransferred,
                "Receiver-observed payload is recorded independently of serialized and acknowledged totals.");
            Require(result.Upload.CompletedDuration && result.Upload.Elapsed >= TimeSpan.FromMilliseconds(500) && result.Upload.Elapsed < TimeSpan.FromSeconds(2),
                "A stalled upload stops at the measurement interval.");
            long serialized = result.Upload.BytesTransferred;
            server.Drain();
            await Task.Delay(100);
            Require(result.Upload.BytesTransferred == serialized, "Buffered tails after cutoff cannot change the frozen result.");
            Console.WriteLine("UPLOAD partial=" + partial + " serialized=" + serialized + " receivedAtEnd=" + received + " acknowledged=" + acknowledged + " receivedAfterDrain=" + server.Received + " elapsedMs=" + result.Upload.Elapsed.TotalMilliseconds.ToString("F1"));
        }
        using (var server = new Server(oneAck: false, partial: false, noAck: true))
        {
            try { await new SpeedTestEngine().RunAsync(Options(server), null, CancellationToken.None); throw new InvalidOperationException("No-ack upload must fail."); }
            catch (SpeedTestException ex) { Require(ex.Kind == SpeedTestFailureKind.TransferFailed && server.Received > 0 && server.Acknowledged == 0, "Serialized and received bytes alone cannot establish endpoint validity."); }
        }
        using (var server = new Server(oneAck: false, partial: false))
        {
            var result = await new SpeedTestEngine().RunAsync(Options(server), null, CancellationToken.None);
            Require(result.Upload.ByteBudgetReached && result.Upload.BytesScheduled <= 262144 && result.Upload.BytesTransferred == server.Received && server.Received == server.Acknowledged,
                "Normally completed uploads preserve byte budgets and agree with received/acknowledged payload.");
        }
        using (var server = new Server(oneAck: true, partial: true))
        using (var cancel = new CancellationTokenSource())
        {
            var options = Options(server); options.PhaseDuration = TimeSpan.FromSeconds(5);
            var progress = new Callback(value => { if (value.Phase == SpeedTestPhase.Upload && value.BytesTransferred > 0) cancel.Cancel(); });
            var clock = Stopwatch.StartNew();
            try { await new SpeedTestEngine().RunAsync(options, progress, cancel.Token); throw new InvalidOperationException("Canceled upload must not return a completed result."); }
            catch (OperationCanceledException) { Require(clock.Elapsed < TimeSpan.FromSeconds(3), "User cancellation ends stalled payload work promptly."); }
        }
        Console.WriteLine("PASS: sent, received and acknowledged upload quantities retain their separate contracts.");

        // A loaded-latency failure that lands after the phase stopped must not fail a finished measurement.
        var serverError = new SpeedTestException(SpeedTestFailureKind.HttpError, "500");
        var limited = new SpeedTestException(SpeedTestFailureKind.RateLimited, "429");
        Require(SpeedTestEngine.LatencyFailure(serverError, phaseStopped: true) == SpeedTestEngine.LatencyFailureAction.Ignore
                && SpeedTestEngine.LatencyFailure(limited, phaseStopped: true) == SpeedTestEngine.LatencyFailureAction.Ignore,
            "A latency failure after the phase stopped is ignored.");
        Require(SpeedTestEngine.LatencyFailure(serverError, phaseStopped: false) == SpeedTestEngine.LatencyFailureAction.CountSample
                && SpeedTestEngine.LatencyFailure(limited, phaseStopped: false) == SpeedTestEngine.LatencyFailureAction.FailPhase,
            "During the phase a bad reply is one failed sample and a rate limit ends the phase.");
        Console.WriteLine("PASS: a late loaded-latency failure leaves a finished phase intact.");
    }

    private static SpeedTestOptions Options(Server server) => new()
    {
        Endpoint = server.Endpoint, WarmupDuration = TimeSpan.Zero, PhaseDuration = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromSeconds(2), MaximumStreams = 1, ByteBudgetPerPhase = 262144,
        DownloadPayloadBytes = 65536, UploadPayloadBytes = 65536
    };
    private sealed class Callback(Action<SpeedTestProgress> action) : IProgress<SpeedTestProgress> { public void Report(SpeedTestProgress value) => action(value); }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private readonly TaskCompletionSource<bool> _drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _oneAck, _partial, _noAck;
        private long _received, _acknowledged;
        private int _uploads;
        internal long Received => Interlocked.Read(ref _received);
        internal long Acknowledged => Interlocked.Read(ref _acknowledged);
        internal Uri Endpoint { get; }
        internal Server(bool oneAck, bool partial, bool noAck = false)
        {
            _oneAck = oneAck; _partial = partial; _noAck = noAck;
            _listener.Start(); Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/"); _ = Accept();
        }
        internal void Drain() => _drain.TrySetResult(true);
        private async Task Accept()
        {
            while (!_stop.IsCancellationRequested)
                try { var client = await _listener.AcceptTcpClientAsync(); _clients.Add(client); _ = Serve(client); }
                catch when (_stop.IsCancellationRequested) { return; }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    while (!_stop.IsCancellationRequested)
                    {
                        var header = new StringBuilder(); var next = new byte[1];
                        while (header.Length < 16384 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        { if (await stream.ReadAsync(next, 0, 1, _stop.Token) == 0) return; header.Append((char)next[0]); }
                        string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                        bool upload = lines[0].StartsWith("POST ", StringComparison.Ordinal);
                        string path = lines[0].Split(' ')[1];
                        int count = int.Parse(path.Split('?')[1].Split('&')[0].Split('=')[1]);
                        if (!upload)
                        {
                            await Respond(stream, count);
                            while (count > 0) { int size = Math.Min(count, buffer.Length); await stream.WriteAsync(buffer, 0, size, _stop.Token); count -= size; }
                            continue;
                        }
                        int sequence = Interlocked.Increment(ref _uploads);
                        int remaining = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                        bool stall = _noAck || (_oneAck && sequence > 1);
                        bool paused = false;
                        while (remaining > 0)
                        {
                            int n = await stream.ReadAsync(buffer, 0, Math.Min(remaining, buffer.Length), _stop.Token);
                            if (n == 0) return;
                            remaining -= n; Interlocked.Add(ref _received, n);
                            if (stall && _partial && !paused)
                            { paused = true; await Task.WhenAny(_drain.Task, Task.Delay(Timeout.Infinite, _stop.Token)); _stop.Token.ThrowIfCancellationRequested(); }
                        }
                        if (stall) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                        byte[] acknowledgment = Encoding.ASCII.GetBytes("{\"bytes\":" + count + "}");
                        await Respond(stream, acknowledgment.Length);
                        await stream.WriteAsync(acknowledgment, 0, acknowledgment.Length, _stop.Token);
                        Interlocked.Add(ref _acknowledged, count);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        private async Task Respond(NetworkStream stream, int length)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + length + "\r\nConnection: keep-alive\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length, _stop.Token);
        }
        public void Dispose() { _stop.Cancel(); _drain.TrySetResult(true); _listener.Stop(); foreach (var client in _clients) client.Dispose(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
