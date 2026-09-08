using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KillerScan.Services.SpeedTest
{
    /// <summary>A bounded HTTP throughput test against a compatible endpoint. Download counts
    /// bytes read from the response stream; upload counts only bytes acknowledged by the server.
    /// Neither number includes HTTP headers, TLS framing, or unfinished upload requests.</summary>
    public sealed class SpeedTestEngine
    {
        private const int BufferSize = 64 * 1024;

        public async Task<SpeedTestResult> RunAsync(SpeedTestOptions options,
            IProgress<SpeedTestProgress>? progress, CancellationToken token)
        {
            var settings = ValidateAndCopy(options);
            token.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            using var transferHandler = CreateHandler(settings.MaximumStreams);
            using var latencyHandler = CreateHandler(1);
            using var transfers = new HttpClient(transferHandler) { Timeout = Timeout.InfiniteTimeSpan };
            using var latency = new HttpClient(latencyHandler) { Timeout = Timeout.InfiniteTimeSpan };
            var idle = new List<double>();

            // Establish TLS and the latency connection before collecting idle samples.
            await LatencyAsync(latency, settings, token).ConfigureAwait(false);
            for (int i = 0; i < settings.IdleLatencySampleCount; i++)
            {
                var milliseconds = await LatencyAsync(latency, settings, token).ConfigureAwait(false);
                idle.Add(milliseconds);
                progress?.Report(new SpeedTestProgress
                {
                    Phase = SpeedTestPhase.IdleLatency, LatencyMs = milliseconds, Elapsed = clock.Elapsed
                });
                if (i + 1 < settings.IdleLatencySampleCount)
                    await Task.Delay(100, token).ConfigureAwait(false);
            }

            var download = await DirectionAsync(false, transfers, latency, settings, progress, token).ConfigureAwait(false);
            // Let the connection settle before the reverse direction's warmup.
            await Task.Delay(250, token).ConfigureAwait(false);
            var upload = await DirectionAsync(true, transfers, latency, settings, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = new SpeedTestResult
            {
                Endpoint = settings.Endpoint, StartedAt = started, Elapsed = clock.Elapsed,
                IdleLatencySamples = idle.AsReadOnly(), Download = download, Upload = upload
            };
            progress?.Report(new SpeedTestProgress { Phase = SpeedTestPhase.Completed, Elapsed = clock.Elapsed });
            return result;
        }

        private static HttpClientHandler CreateHandler(int streams) => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = streams
        };

        private static SpeedTestOptions ValidateAndCopy(SpeedTestOptions? options)
        {
            if (options?.Endpoint == null || !options.Endpoint.IsAbsoluteUri ||
                (options.Endpoint.Scheme != Uri.UriSchemeHttps &&
                 !(options.Endpoint.Scheme == Uri.UriSchemeHttp && options.Endpoint.IsLoopback)) ||
                !string.IsNullOrEmpty(options.Endpoint.UserInfo) ||
                !string.IsNullOrEmpty(options.Endpoint.Query) || !string.IsNullOrEmpty(options.Endpoint.Fragment))
                throw new SpeedTestException(SpeedTestFailureKind.InvalidConfiguration,
                    "A compatible HTTPS speed-test endpoint is required. HTTP is allowed only on loopback for testing.");
            if (options.MaximumStreams < 1 || options.MaximumStreams > 4 ||
                options.PhaseDuration < TimeSpan.FromMilliseconds(100) || options.PhaseDuration > TimeSpan.FromMinutes(1) ||
                options.WarmupDuration < TimeSpan.Zero || options.WarmupDuration > TimeSpan.FromSeconds(10) ||
                options.RequestTimeout < TimeSpan.FromMilliseconds(100) || options.RequestTimeout > TimeSpan.FromSeconds(30) ||
                options.ByteBudgetPerPhase < 8 || options.ByteBudgetPerPhase > 512L * 1024 * 1024 ||
                options.DownloadPayloadBytes < 1 || options.DownloadPayloadBytes > 8 * 1024 * 1024 ||
                options.UploadPayloadBytes < 1 || options.UploadPayloadBytes > 4 * 1024 * 1024 ||
                options.IdleLatencySampleCount < 5 || options.IdleLatencySampleCount > 20)
                throw new SpeedTestException(SpeedTestFailureKind.InvalidConfiguration,
                    "The speed-test duration, stream count, byte budget, payload, or latency sample count is outside its supported range.");
            // Snapshot mutable UI options so changing a setting cannot alter a test already running.
            return new SpeedTestOptions
            {
                Endpoint = new Uri(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/"),
                MaximumStreams = options.MaximumStreams, PhaseDuration = options.PhaseDuration,
                WarmupDuration = options.WarmupDuration, RequestTimeout = options.RequestTimeout,
                ByteBudgetPerPhase = options.ByteBudgetPerPhase, DownloadPayloadBytes = options.DownloadPayloadBytes,
                UploadPayloadBytes = options.UploadPayloadBytes, IdleLatencySampleCount = options.IdleLatencySampleCount
            };
        }

        private static async Task<SpeedTestPhaseResult> DirectionAsync(bool upload, HttpClient transfers,
            HttpClient latency, SpeedTestOptions options, IProgress<SpeedTestProgress>? progress, CancellationToken token)
        {
            long warmupBudget = Math.Min(64L * 1024 * 1024, options.ByteBudgetPerPhase / 8);
            var warmup = await TransferPhaseAsync(upload, true, warmupBudget, options.WarmupDuration,
                transfers, latency, options, progress, token).ConfigureAwait(false);
            var measured = await TransferPhaseAsync(upload, false, options.ByteBudgetPerPhase - warmup.BytesScheduled,
                options.PhaseDuration, transfers, latency, options, progress, token).ConfigureAwait(false);
            measured.WarmupBytes = warmup.BytesTransferred;
            measured.BytesScheduled += warmup.BytesScheduled;
            return measured;
        }

        private sealed class Counters
        {
            private readonly object _gate = new object();
            private long _bytes;
            private long _scheduled;
            private bool _ended;
            private TimeSpan _elapsed;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public int ActiveStreams;
            public int PeakStreams;
            public Exception? Failure;

            public int Reserve(int requested, long budget)
            {
                lock (_gate)
                {
                    if (_ended) return 0;
                    int reserved = (int)Math.Min(requested, budget - _scheduled);
                    _scheduled += reserved;
                    return reserved;
                }
            }
            public void Add(int bytes) { lock (_gate) { if (!_ended) _bytes += bytes; } }
            public void End() { lock (_gate) { if (!_ended) { _elapsed = Clock.Elapsed; _ended = true; } } }
            public (long Bytes, long Scheduled, TimeSpan Elapsed) Snapshot()
            {
                lock (_gate) return (_bytes, _scheduled, _ended ? _elapsed : Clock.Elapsed);
            }
            public void Enter()
            {
                lock (_gate)
                {
                    ActiveStreams++;
                    PeakStreams = Math.Max(PeakStreams, ActiveStreams);
                }
            }
            public void Leave() { lock (_gate) ActiveStreams--; }
        }

        private static async Task<SpeedTestPhaseResult> TransferPhaseAsync(bool upload, bool warmup,
            long budget, TimeSpan duration, HttpClient client, HttpClient latencyClient, SpeedTestOptions options,
            IProgress<SpeedTestProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (duration == TimeSpan.Zero)
                return new SpeedTestPhaseResult { CompletedDuration = true };

            var phase = upload ? (warmup ? SpeedTestPhase.UploadWarmup : SpeedTestPhase.Upload)
                               : (warmup ? SpeedTestPhase.DownloadWarmup : SpeedTestPhase.Download);
            var counters = new Counters();
            var samples = new List<double>();
            int failedSamples = 0;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var endRegistration = stop.Token.Register(counters.End);
            stop.CancelAfter(duration);
            progress?.Report(new SpeedTestProgress { Phase = phase });

            async Task WorkerAsync(int index)
            {
                try
                {
                    // Start one connection, then add the remaining connections evenly during warmup.
                    if (warmup && index > 0)
                        await Task.Delay(TimeSpan.FromTicks(duration.Ticks * index / options.MaximumStreams), stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    counters.Enter();
                    try
                    {
                        var buffer = new byte[BufferSize];
                        if (upload)
                            using (var random = RandomNumberGenerator.Create()) random.GetBytes(buffer);
                        int maximumPayload = upload ? options.UploadPayloadBytes : options.DownloadPayloadBytes;
                        int payload = Math.Min(BufferSize, maximumPayload);
                        while (!stop.IsCancellationRequested)
                        {
                            int size = counters.Reserve(payload, budget);
                            if (size == 0) break;
                            var exchange = Stopwatch.StartNew();
                            if (upload)
                                await UploadAsync(client, options, size, buffer, counters.Add, stop.Token).ConfigureAwait(false);
                            else
                                await DownloadAsync(client, options, size, buffer, counters.Add, stop.Token).ConfigureAwait(false);
                            // Keep acknowledgments frequent on slow links without imposing tiny
                            // HTTP requests on fast links. Configured payload sizes are ceilings.
                            double next = size * 250d / Math.Max(1, exchange.Elapsed.TotalMilliseconds);
                            payload = (int)Math.Min(maximumPayload, Math.Max(Math.Min(16 * 1024, maximumPayload), next));
                        }
                    }
                    finally { counters.Leave(); }
                }
                catch (Exception ex)
                {
                    if (!stop.IsCancellationRequested)
                    {
                        Interlocked.CompareExchange(ref counters.Failure, ex, null);
                        stop.Cancel();
                    }
                }
            }

            async Task ReportAsync()
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(100, stop.Token).ConfigureAwait(false);
                        var snapshot = counters.Snapshot();
                        progress?.Report(new SpeedTestProgress
                        {
                            Phase = phase, BytesTransferred = snapshot.Bytes, Elapsed = snapshot.Elapsed,
                            Mbps = warmup ? null : SpeedTestMetrics.MegabitsPerSecond(snapshot.Bytes, snapshot.Elapsed),
                            ActiveStreams = Volatile.Read(ref counters.ActiveStreams)
                        });
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }

            async Task LoadedLatencyAsync()
            {
                if (warmup) return;
                try
                {
                    while (true)
                    {
                        // Wait for the payload workers to put traffic on the connection first.
                        await Task.Delay(200, stop.Token).ConfigureAwait(false);
                        try
                        {
                            var milliseconds = await LatencyAsync(latencyClient, options, stop.Token).ConfigureAwait(false);
                            if (!stop.IsCancellationRequested)
                            {
                                samples.Add(milliseconds);
                                var snapshot = counters.Snapshot();
                                progress?.Report(new SpeedTestProgress
                                {
                                    Phase = phase, BytesTransferred = snapshot.Bytes, Elapsed = snapshot.Elapsed,
                                    Mbps = SpeedTestMetrics.MegabitsPerSecond(snapshot.Bytes, snapshot.Elapsed),
                                    LatencyMs = milliseconds, ActiveStreams = Volatile.Read(ref counters.ActiveStreams)
                                });
                            }
                        }
                        catch (SpeedTestException) when (!stop.IsCancellationRequested) { failedSamples++; }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }

            var reports = ReportAsync();
            var loadedLatency = LoadedLatencyAsync();
            try
            {
                await Task.WhenAll(Enumerable.Range(0, options.MaximumStreams).Select(WorkerAsync)).ConfigureAwait(false);
            }
            finally
            {
                counters.End();
                stop.Cancel();
                await Task.WhenAll(reports, loadedLatency).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (counters.Failure != null) throw Normalize(counters.Failure);
            var final = counters.Snapshot();
            if (!warmup && final.Bytes == 0)
                throw new SpeedTestException(SpeedTestFailureKind.TransferFailed,
                    upload ? "No upload payload was acknowledged before the measurement ended."
                           : "No download payload was received before the measurement ended.");
            var result = new SpeedTestPhaseResult
            {
                BytesTransferred = final.Bytes, BytesScheduled = final.Scheduled, Elapsed = final.Elapsed,
                Mbps = SpeedTestMetrics.MegabitsPerSecond(final.Bytes, final.Elapsed),
                StreamCount = counters.PeakStreams, ByteBudgetReached = final.Scheduled >= budget,
                CompletedDuration = final.Elapsed >= duration, LatencySamples = samples.AsReadOnly(),
                FailedLatencySamples = failedSamples
            };
            progress?.Report(new SpeedTestProgress
            {
                Phase = phase, BytesTransferred = result.BytesTransferred, Elapsed = result.Elapsed,
                Mbps = warmup ? null : result.Mbps, LatencyMs = result.LoadedLatencyMs, ActiveStreams = 0
            });
            return result;
        }

        private static HttpRequestMessage Request(SpeedTestOptions options, HttpMethod method, string path, int bytes)
        {
            var uri = new Uri(options.Endpoint, path + "?bytes=" + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&r=" + Guid.NewGuid().ToString("N"));
            var request = new HttpRequestMessage(method, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.ExpectContinue = false;
            return request;
        }

        private static void ValidateResponse(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new SpeedTestException((int)response.StatusCode == 429 ? SpeedTestFailureKind.RateLimited : SpeedTestFailureKind.HttpError,
                    "The speed-test endpoint returned HTTP " + (int)response.StatusCode + ".", statusCode: (int)response.StatusCode);
            if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The speed-test endpoint compressed the payload.");
            if (response.Headers.Age.HasValue && response.Headers.Age.Value > TimeSpan.Zero)
                throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The speed-test endpoint returned a cached response.");
        }

        private static async Task DownloadAsync(HttpClient client, SpeedTestOptions options, int bytes,
            byte[] buffer, Action<int> count, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.RequestTimeout);
            using var request = Request(options, HttpMethod.Get, "__down", bytes);
            using var requestCancellation = timeout.Token.Register(() => DisposeCanceled(request));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                ValidateResponse(response);
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != bytes)
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The download payload length did not match the request.");
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var streamCancellation = timeout.Token.Register(() => DisposeCanceled(stream));
                int received = 0;
                while (received < bytes)
                {
                    int read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, bytes - received), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The download payload ended before its declared length.");
                    received += read;
                    count(read);
                }
                if (await stream.ReadAsync(buffer, 0, 1, timeout.Token).ConfigureAwait(false) != 0)
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The download payload exceeded the requested length.");
            }
            catch (Exception ex) { throw RequestFailure(ex, token, timeout.Token); }
        }

        private static async Task UploadAsync(HttpClient client, SpeedTestOptions options, int bytes,
            byte[] buffer, Action<int> count, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.RequestTimeout);
            using var request = Request(options, HttpMethod.Post, "__up", bytes);
            var payload = new PayloadContent(bytes, buffer, timeout.Token);
            request.Content = payload;
            using var requestCancellation = timeout.Token.Register(() => DisposeCanceled(request));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                ValidateResponse(response);
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var streamCancellation = timeout.Token.Register(() => DisposeCanceled(stream));
                var reply = new byte[1025];
                int length = 0;
                while (length < reply.Length)
                {
                    int read = await stream.ReadAsync(reply, length, reply.Length - length, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                if (length == reply.Length)
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The upload acknowledgment was too large.");
                ValidateUploadAcknowledgment(options.Endpoint, response, Encoding.UTF8.GetString(reply, 0, length), payload.BytesSerialized, bytes);
                timeout.Token.ThrowIfCancellationRequested();
                count(payload.BytesSerialized);
            }
            catch (Exception ex) { throw RequestFailure(ex, token, timeout.Token); }
        }

        private static void ValidateUploadAcknowledgment(Uri endpoint, HttpResponseMessage response,
            string body, int serializedBytes, int expectedBytes)
        {
            if (serializedBytes != expectedBytes)
                throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The upload did not serialize the complete payload.");

            // Cloudflare's public speed-test API acknowledges uploads with an empty 200
            // response and server timing. Other endpoints must provide the exact byte count.
            bool cloudflare = endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Port == 443 &&
                endpoint.Host.Equals("speed.cloudflare.com", StringComparison.OrdinalIgnoreCase) && endpoint.AbsolutePath == "/" &&
                string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) && string.IsNullOrEmpty(endpoint.UserInfo);
            if (cloudflare)
            {
                bool timing = response.Headers.TryGetValues("Server-Timing", out var values) && values.Any(value =>
                    value.Split(',').Any(metric => metric.Trim().StartsWith("cfSpeedWorker;", StringComparison.Ordinal) &&
                        metric.Split(';').Any(part => part.Trim().StartsWith("dur=", StringComparison.Ordinal) &&
                            double.TryParse(part.Trim().Substring(4), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double duration) && duration >= 0 &&
                            !double.IsInfinity(duration) && !double.IsNaN(duration))));
                if (body.Length != 0 || !timing || response.Content.Headers.ContentType?.MediaType != "text/plain")
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "Cloudflare returned an unexpected upload acknowledgment.");
            }
            else
            {
                try
                {
                    using var json = JsonDocument.Parse(body);
                    if (json.RootElement.ValueKind != JsonValueKind.Object ||
                        !json.RootElement.TryGetProperty("bytes", out var value) || value.ValueKind != JsonValueKind.Number ||
                        !value.TryGetInt64(out long acknowledged) || acknowledged != expectedBytes)
                        throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The endpoint did not acknowledge the exact upload size.");
                }
                catch (JsonException ex)
                {
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The endpoint returned an invalid upload acknowledgment.", ex);
                }
            }
        }

        private static async Task<double> LatencyAsync(HttpClient client, SpeedTestOptions options, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.RequestTimeout);
            using var request = Request(options, HttpMethod.Get, "__down", 0);
            using var requestCancellation = timeout.Token.Register(() => DisposeCanceled(request));
            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                ValidateResponse(response);
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != 0)
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The latency response must have an empty body.");
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var streamCancellation = timeout.Token.Register(() => DisposeCanceled(stream));
                if (await stream.ReadAsync(new byte[1], 0, 1, timeout.Token).ConfigureAwait(false) != 0)
                    throw new SpeedTestException(SpeedTestFailureKind.InvalidResponse, "The latency response must have an empty body.");
                timeout.Token.ThrowIfCancellationRequested();
                return clock.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) { throw RequestFailure(ex, token, timeout.Token); }
        }

        private sealed class PayloadContent : HttpContent
        {
            private readonly int _length;
            private readonly byte[] _buffer;
            private readonly CancellationToken _token;
            private int _serialized;
            public int BytesSerialized => Volatile.Read(ref _serialized);
            public PayloadContent(int length, byte[] buffer, CancellationToken token)
            {
                _length = length; _buffer = buffer; _token = token;
                Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                Headers.ContentLength = length;
            }
            protected override bool TryComputeLength(out long length) { length = _length; return true; }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                using var cancellation = _token.Register(() => DisposeCanceled(stream));
                for (int sent = 0; sent < _length;)
                {
                    _token.ThrowIfCancellationRequested();
                    int size = Math.Min(_buffer.Length, _length - sent);
                    await stream.WriteAsync(_buffer, 0, size, _token).ConfigureAwait(false);
                    Interlocked.Add(ref _serialized, size);
                    sent += size;
                }
            }
        }

        private static void DisposeCanceled(IDisposable resource)
        {
            // Framework HTTP upload streams can throw while closing an incomplete request.
            // Cancellation still aborts the transfer, but the timer callback must not throw.
            try { resource.Dispose(); }
            catch (IOException) { }
            catch (WebException) { }
            catch (ObjectDisposedException) { }
        }

        private static Exception RequestFailure(Exception error, CancellationToken parent, CancellationToken timeout)
        {
            if (parent.IsCancellationRequested) return new OperationCanceledException(parent);
            if (timeout.IsCancellationRequested)
                return new SpeedTestException(SpeedTestFailureKind.Timeout, "The speed-test endpoint did not respond within the request timeout.", error);
            return Normalize(error);
        }

        private static SpeedTestException Normalize(Exception error)
        {
            if (error is SpeedTestException testError) return testError;
            for (Exception? inner = error; inner != null; inner = inner.InnerException)
            {
                if (inner is WebException web)
                {
                    var kind = web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure
                        ? SpeedTestFailureKind.TlsFailure : web.Status == WebExceptionStatus.Timeout
                        ? SpeedTestFailureKind.Timeout : SpeedTestFailureKind.EndpointUnavailable;
                    return new SpeedTestException(kind, "The speed-test endpoint could not be reached.", error);
                }
            }
            return new SpeedTestException(error is HttpRequestException ? SpeedTestFailureKind.EndpointUnavailable : SpeedTestFailureKind.TransferFailed,
                "The speed-test transfer failed.", error);
        }
    }
}
