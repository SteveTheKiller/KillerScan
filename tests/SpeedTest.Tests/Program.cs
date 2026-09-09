using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using KillerScan.Services.SpeedTest;

internal static class Program
{
    private static int _passed;
    private static SpeedTestResult? _internetResult;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Require(args.All(value => value == "--worker" || value == "--internet"), "Usage: SpeedTest.Tests.exe [--worker] [--internet]");
            await Run("Metric units, median, jitter and unavailable values", Metrics);
            await Run("Cloudflare upload response is accepted only for the exact official endpoint", CloudflareAcknowledgment);
            await Run("Public endpoint profile reduces request pressure without shortening the test", PublicProfile);
            await Run("Adaptive warmup adds streams on a per-connection bottleneck", () => Adaptive(Mode.SlowDownload, 8));
            await Run("Adaptive warmup retains fewer streams on a shared bottleneck", () => Adaptive(Mode.SharedDownload, 2));
            await Run("Cancellation during adaptive warmup prevents measurement", AdaptiveCancel);
            await Run("HTTPS configuration required without contacting a remote host", InvalidEndpoint);
            await Run("Real transfers, exact acknowledgments and per-direction byte budgets", Budget);
            await Run("Sustained timed download with loaded latency and cancellation of pending reads", Duration);
            await Run("Wrong upload acknowledgment rejected", () => Reject(Mode.WrongAck, SpeedTestFailureKind.InvalidResponse));
            await Run("String upload acknowledgment rejected", () => Reject(Mode.StringAck, SpeedTestFailureKind.InvalidResponse));
            await Run("Short declared download rejected", () => Reject(Mode.ShortDownload, SpeedTestFailureKind.InvalidResponse));
            await Run("Compressed download rejected", () => Reject(Mode.Compressed, SpeedTestFailureKind.InvalidResponse));
            await Run("HTTP 429 classified as rate limited", () => Reject(Mode.RateLimited, SpeedTestFailureKind.RateLimited));
            await Run("Retry-After is honored once before measurement", PreflightRetry);
            await Run("Repeated preflight rate limit stops after one retry", () => RateRetry(Mode.RepeatedRetry, 2));
            await Run("Long Retry-After is surfaced without automatic retry", () => RateRetry(Mode.LongRetry, 1));
            await Run("Preflight retry wait is cancellable", CancelRetry);
            await Run("Measured rate limit never retries or waits inside throughput timing", MeasurementRateLimit);
            await Run("Loaded-latency rate limit stops payload traffic", () => Reject(Mode.LoadedRate, SpeedTestFailureKind.RateLimited));
            await Run("HTTP 503 classified as HTTP error", () => Reject(Mode.Unavailable, SpeedTestFailureKind.HttpError));
            await Run("Stalled response bounded by request timeout", Timeout);
            await Run("No measured download bytes is a failure, not completion", () => Reject(Mode.NoPayload, SpeedTestFailureKind.TransferFailed));
            await Run("Unacknowledged uploads cannot produce a speed result", () => Reject(Mode.NoAck, SpeedTestFailureKind.TransferFailed));
            await Run("User cancellation stops active transfers promptly", Cancel);
            await Run("Incomplete upload stream disposal cannot escape cancellation", CancelDisposal);
            if (args.Contains("--worker"))
                await Run("Compiled engine exchanges exact payloads with the real Worker", Worker);
            if (args.Contains("--internet"))
                await Run("Live KillerScan endpoint completes native measurements", Internet);
            await Run("Themed terminal renders managed output and routes cancel, rerun and disposal", View);
            Console.WriteLine("PASS: " + _passed + " speed-test regression checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static async Task Run(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task PublicProfile()
    {
        var snapshot = typeof(SpeedTestEngine).GetMethod("ValidateAndCopy", BindingFlags.Static | BindingFlags.NonPublic)!;
        var profile = (SpeedTestOptions)snapshot.Invoke(null, new object[] { new SpeedTestOptions { Endpoint = new Uri("https://speed.cloudflare.com/"), MaximumStreams = 4 } })!;
        Require(profile.MaximumStreams == 2, "Public service uses at most two payload streams");
        Require(profile.PhaseDuration == TimeSpan.FromSeconds(10) && profile.ByteBudgetPerPhase == 3L * 1024 * 1024 * 1024,
            "Public service retains sustained duration and byte ceiling");
        Require(profile.DownloadPayloadBytes == 25000000 && profile.UploadPayloadBytes == 10000000,
            "Public payloads use documented Cloudflare measurement sizes");
        return Task.CompletedTask;
    }

    private static async Task PreflightRetry()
    {
        using var server = new LoopbackServer(Mode.FirstRetry);
        var clock = Stopwatch.StartNew();
        var result = await new SpeedTestEngine().RunAsync(Options(server), null, CancellationToken.None);
        Require(clock.Elapsed >= TimeSpan.FromSeconds(1), "Preflight honored Retry-After");
        Require(result.Download.Mbps > 0 && result.Upload.Mbps > 0, "Retry was outside valid throughput phases");
        Require(server.RateResponses == 1, "Only first preflight response was rate limited");
    }

    private static async Task RateRetry(Mode mode, int expectedRequests)
    {
        using var server = new LoopbackServer(mode);
        try
        {
            await new SpeedTestEngine().RunAsync(Options(server), null, CancellationToken.None);
            throw new InvalidOperationException("Rate-limited preflight returned success");
        }
        catch (SpeedTestException ex)
        {
            Require(ex.Kind == SpeedTestFailureKind.RateLimited && ex.RetryAfter == TimeSpan.FromSeconds(mode == Mode.LongRetry ? 30 : 1),
                "Rate limit retains server cooldown");
            Require(server.Requests.Count == expectedRequests, "Retry count remains bounded");
        }
    }

    private static async Task CancelRetry()
    {
        using var server = new LoopbackServer(Mode.RepeatedRetry);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await new SpeedTestEngine().RunAsync(Options(server), null, cancel.Token);
            throw new InvalidOperationException("Canceled preflight returned success");
        }
        catch (OperationCanceledException) { Require(server.Requests.Count == 1, "Cancellation prevents retry request"); }
    }

    private static async Task MeasurementRateLimit()
    {
        using var server = new LoopbackServer(Mode.MeasuredRate);
        var options = Options(server);
        options.WarmupDuration = TimeSpan.Zero;
        options.MaximumStreams = 1;
        var clock = Stopwatch.StartNew();
        try
        {
            await new SpeedTestEngine().RunAsync(options, null, CancellationToken.None);
            throw new InvalidOperationException("Rate-limited transfer returned success");
        }
        catch (SpeedTestException ex)
        {
            Require(ex.Kind == SpeedTestFailureKind.RateLimited && ex.RetryAfter == TimeSpan.FromSeconds(1), "Measured cooldown retained");
            Require(server.RateResponses == 1 && clock.Elapsed < TimeSpan.FromSeconds(1.4), "Measured rate limit is not retried or delayed");
        }
    }

    private static Task CloudflareAcknowledgment()
    {
        Require(new SpeedTestOptions().Endpoint.AbsoluteUri == "https://speed.killerscan.net/", "Default endpoint requires no setup");
        var validate = typeof(SpeedTestEngine).GetMethod("ValidateUploadAcknowledgment", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
        response.Headers.TryAddWithoutValidation("Server-Timing", "cfSpeedEdge;dur=22, cfSpeedWorker;dur=13");
        void Check(string uri, string body, int serialized, bool valid)
        {
            try
            {
                validate.Invoke(null, new object[] { new Uri(uri), response, body, serialized, 2048 });
                Require(valid, "Unexpected Cloudflare acknowledgment accepted: " + uri);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is SpeedTestException failure)
            { Require(!valid && failure.Kind == SpeedTestFailureKind.InvalidResponse, "Cloudflare response validation"); }
        }
        Check("https://speed.cloudflare.com/", "", 2048, true);
        Check("https://speed.cloudflare.com/", "", 1024, false);
        Check("https://speed.cloudflare.com/", "unexpected", 2048, false);
        Check("https://speed.cloudflare.com.example/", "", 2048, false);
        Check("https://speed.cloudflare.com/other/", "", 2048, false);
        Check("http://speed.cloudflare.com/", "", 2048, false);
        Check("https://speed.cloudflare.com:8443/", "", 2048, false);
        response.Headers.Remove("Server-Timing");
        Check("https://speed.cloudflare.com/", "", 2048, false);
        return Task.CompletedTask;
    }

    private static async Task Internet()
    {
        var options = new SpeedTestOptions();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new SpeedTestEngine().RunAsync(options, null, deadline.Token);
        _internetResult = result;
        Require(result.Download.Mbps > 0 && result.Upload.Mbps > 0 && result.IdleLatencySamples.Count >= 5, "Live default endpoint measurements available");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "LIVE download={0:F2}Mbps upload={1:F2}Mbps idle={2:F2}ms jitter={3:F2}ms downloadElapsed={4:F3}s uploadElapsed={5:F3}s downloadFullDuration={6} uploadFullDuration={7} downloadLoaded={8:F2}ms uploadLoaded={9:F2}ms total={10:F2}s downloadStreams={11} uploadStreams={12}",
            result.Download.Mbps, result.Upload.Mbps, result.IdleLatencyMs, result.JitterMs,
            result.Download.Elapsed.TotalSeconds, result.Upload.Elapsed.TotalSeconds,
            result.Download.CompletedDuration, result.Upload.CompletedDuration,
            result.Download.LoadedLatencyMs, result.Upload.LoadedLatencyMs, result.Elapsed.TotalSeconds,
            result.Download.StreamCount, result.Upload.StreamCount));
    }

    private static async Task Worker()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
        Require(directory != null, "Repository containing the real Worker found");
        string script = Path.Combine(directory!.FullName, "server", "speedtest", "loopback.mjs");
        Require(File.Exists(script), "Worker loopback adapter exists");
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        int port;
        try
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        finally { reservation.Stop(); }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node", "\"" + script + "\" " + port.ToString(CultureInfo.InvariantCulture))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            }
        };
        bool started = false;
        try
        {
            started = process.Start();
            Require(started, "Node adapter started");
            var errors = process.StandardError.ReadToEndAsync();
            var firstLine = process.StandardOutput.ReadLineAsync();
            Require(await Task.WhenAny(firstLine, Task.Delay(5000)) == firstLine, "Worker startup completed within five seconds");
            string expected = "Worker loopback adapter: http://127.0.0.1:" + port;
            Require(await firstLine == expected, "Worker startup banner confirmed its loopback listener");
            var remainingOutput = process.StandardOutput.ReadToEndAsync();
            var options = new SpeedTestOptions
            {
                Endpoint = new Uri("http://127.0.0.1:" + port + "/"), WarmupDuration = TimeSpan.Zero,
                PhaseDuration = TimeSpan.FromSeconds(3), ByteBudgetPerPhase = 512 * 1024,
                DownloadPayloadBytes = 64 * 1024, UploadPayloadBytes = 64 * 1024,
                MaximumStreams = 2, RequestTimeout = TimeSpan.FromSeconds(2)
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var result = await new SpeedTestEngine().RunAsync(options, null, deadline.Token);
            foreach (var phase in new[] { result.Download, result.Upload })
            {
                Require(phase.Mbps > 0, "Real Worker produced a positive measurement");
                Require(phase.BytesTransferred == options.ByteBudgetPerPhase && phase.BytesScheduled == options.ByteBudgetPerPhase,
                    "Real Worker transferred and acknowledged the exact configured payload budget");
                Require(phase.ByteBudgetReached && !phase.CompletedDuration, "Real Worker result identified its byte cap");
            }
            Require(result.IdleLatencySamples.Count == 5, "Real Worker supports empty latency responses");
            Require(!process.HasExited, "Worker remained running throughout the exchange");
            // Drain both redirected pipes from startup onward. The finally block owns this child.
            GC.KeepAlive(errors);
            GC.KeepAlive(remainingOutput);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill();
                Require(process.WaitForExit(5000), "Owned Worker process stopped within five seconds");
            }
        }
    }

    private sealed class TestApplication : KillerScan.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private static Task View()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
                Require(directory != null, "Repository app resources found");
                // WPF has already inferred the console entry assembly. Redirect only this test
                // process before any pack resources load, so the real theme loader resolves them.
                typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                    .SetValue(null, typeof(KillerScan.App).Assembly);
                var app = new TestApplication();
                var xml = new XmlDocument();
                xml.Load(Path.Combine(directory!.FullName, "App.xaml"));
                string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
                    .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
                var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
                context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
                context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
                app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);
                var assembly = typeof(KillerScan.App).Assembly;
                var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
                var theme = assembly.GetType("KillerScan.Services.Theme", true)!;
                var accent = assembly.GetType("KillerScan.Services.Accent", true)!;
                var loadTheme = manager.GetMethod("LoadDict", BindingFlags.NonPublic | BindingFlags.Static)!;
                loadTheme.Invoke(null, new[] { Enum.Parse(theme, "Black"), Enum.Parse(accent, "Orange") });
                Require(app.TryFindResource("TextBrush") is Brush && app.TryFindResource("PrimaryBrush") is Brush,
                    "Text and accent theme brushes resolve");
                var terminalType = assembly.GetType("KillerScan.Terminal.TerminalControl", true)!;
                var terminal = (FrameworkElement)Activator.CreateInstance(terminalType)!;
                using var terminalLifetime = (IDisposable)terminal;
                using var cancellation = new CancellationTokenSource();
                int cancelInputs = 0, rerunInputs = 0, disposed = 0;
                Action<string> onInput = input =>
                {
                    if (input == "\u001b" || input == "\u0003") { cancelInputs++; cancellation.Cancel(); }
                    if (input == "\r") rerunInputs++;
                };
                terminalType.GetEvent("ManagedInput")!.AddEventHandler(terminal, onInput);
                terminalType.GetEvent("Disposed")!.AddEventHandler(terminal, (Action)(() => { disposed++; cancellation.Cancel(); }));
                terminal.Measure(new Size(900, 500));
                terminal.Arrange(new Rect(0, 0, 900, 500));
                terminalType.GetMethod("BeginManagedSession")!.Invoke(terminal, null);
                Require((bool)terminalType.GetProperty("IsManaged")!.GetValue(terminal)!, "Terminal accepts managed output");
                Require(terminalType.GetField("_pty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal) == null,
                    "Managed terminal starts no external process");
                void Write(string text) => terminalType.GetMethod("WriteManaged")!.Invoke(terminal, new object[] { text });
                string Text() => (string)terminalType.GetMethod("GetText")!.Invoke(terminal, null)!;
                Write("\u001b[32mDownload: 999 Mbps\u001b[0m");
                Write("\r\u001b[2KDownload: 42.5 Mbps\r\n");
                Require(Text().Contains("Download: 42.5 Mbps") && !Text().Contains("999") && !Text().Contains("\u001b"),
                    "ANSI overwrite produces clean current copy text");
                foreach (string input in new[] { "\u001b", "\u0003", "\r" })
                    terminalType.GetMethod("Send")!.Invoke(terminal, new object[] { input });
                Require(cancelInputs == 2 && rerunInputs == 1 && cancellation.IsCancellationRequested,
                    "Managed terminal forwards cancel and rerun inputs");

                Write("\u001b[2J\u001b[H\u001b[1;36mKillerScan speed test\u001b[0m\r\n\r\n");
                if (_internetResult != null)
                {
                    Write(string.Format(CultureInfo.InvariantCulture,
                        "\u001b[32mDownload: {0:F1} Mbps\r\nUpload: {1:F1} Mbps\u001b[0m\r\nLatency: {2:F1} ms\r\n\r\n",
                        _internetResult.Download.Mbps, _internetResult.Upload.Mbps, _internetResult.IdleLatencyMs));
                    Write((_internetResult.Download.CompletedDuration && _internetResult.Upload.CompletedDuration
                        ? "Test complete." : "Data limit reached; measurement duration was shortened.") + "\r\n");
                }
                else Write("Ready to test.\r\n");
                Write("\u001b[90mEnter: test again. Escape or Ctrl+C: cancel.\u001b[0m\r\n");
                terminal.UpdateLayout();
                var bitmap = new RenderTargetBitmap(900, 500, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(terminal);
                var imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpeedTestTerminal.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(imagePath)) encoder.Save(file);
                Console.WriteLine("Offscreen terminal: " + imagePath);
                terminalLifetime.Dispose();
                terminalLifetime.Dispose();
                Require(disposed == 1, "Disposal notification fires once");
                string closedText = Text();
                Write("must not appear");
                Require(Text() == closedText, "Disposed terminal ignores later progress");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Offscreen view construction failed.", failure);
        return Task.CompletedTask;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class IncompleteUploadStream : MemoryStream
    {
        public bool DisposeAttempted;
        private readonly TaskCompletionSource<bool> _write = new TaskCompletionSource<bool>();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => _write.Task;
        protected override void Dispose(bool disposing)
        {
            DisposeAttempted = true;
            _write.TrySetCanceled();
            throw new WebException("The request was canceled.", new IOException("Cannot close stream until all bytes are written."));
        }
    }

    private static async Task CancelDisposal()
    {
        using var cancel = new CancellationTokenSource();
        var contentType = typeof(SpeedTestEngine).GetNestedType("PayloadContent", BindingFlags.NonPublic)!;
        using var content = (HttpContent)Activator.CreateInstance(contentType, 2048, new byte[2048], cancel.Token)!;
        var stream = new IncompleteUploadStream();
        var transfer = content.CopyToAsync(stream);
        cancel.Cancel();
        Require(stream.DisposeAttempted, "Cancellation closes the active upload stream");
        try { await transfer; throw new InvalidOperationException("Canceled upload continued"); }
        catch (OperationCanceledException) { }
    }

    private static Task Metrics()
    {
        Require(SpeedTestMetrics.MegabitsPerSecond(1000000, TimeSpan.FromSeconds(1)) == 8, "Decimal Mbps conversion");
        Require(SpeedTestMetrics.MegabitsPerSecond(0, TimeSpan.FromSeconds(1)) == null, "No invented zero speed");
        Require(SpeedTestMetrics.MegabitsPerSecond(10, TimeSpan.Zero) == null, "Zero-duration measurement unavailable");
        Require(SpeedTestMetrics.Median(new[] { 4d, 1, 3, 2 }) == 2.5, "Even sample median");
        Require(SpeedTestMetrics.Median(new[] { double.NaN, double.PositiveInfinity, -1d }) == null, "Invalid latency excluded");
        Require(SpeedTestMetrics.Jitter(new[] { 10d, 20, 15 }) == 7.5, "Consecutive absolute difference jitter");
        Require(SpeedTestMetrics.Jitter(new[] { 10d }) == null, "One sample cannot establish jitter");
        return Task.CompletedTask;
    }

    private static SpeedTestOptions Options(LoopbackServer server) => new SpeedTestOptions
    {
        Endpoint = server.Endpoint, PhaseDuration = TimeSpan.FromMilliseconds(400),
        WarmupDuration = TimeSpan.FromMilliseconds(100), ByteBudgetPerPhase = 64 * 1024,
        DownloadPayloadBytes = 16 * 1024, UploadPayloadBytes = 16 * 1024,
        RequestTimeout = TimeSpan.FromSeconds(2), MaximumStreams = 4
    };

    private static async Task InvalidEndpoint()
    {
        try
        {
            await new SpeedTestEngine().RunAsync(new SpeedTestOptions { Endpoint = new Uri("http://192.0.2.1/") }, null, CancellationToken.None);
            throw new InvalidOperationException("Non-loopback HTTP was accepted");
        }
        catch (SpeedTestException ex) { Require(ex.Kind == SpeedTestFailureKind.InvalidConfiguration, "Endpoint failure classification"); }
    }

    private static async Task Budget()
    {
        using var server = new LoopbackServer(Mode.Normal);
        var options = Options(server);
        var updates = new ConcurrentQueue<SpeedTestProgress>();
        var result = await new SpeedTestEngine().RunAsync(options, new CallbackProgress(updates.Enqueue), CancellationToken.None);
        Require(result.IdleLatencySamples.Count == 5 && result.IdleLatencyMs.HasValue && result.JitterMs.HasValue, "Repeated idle latency");
        foreach (var phase in new[] { result.Download, result.Upload })
        {
            Require(phase.Mbps > 0 && phase.BytesTransferred > 0, "Positive measured throughput");
            Require(phase.ByteBudgetReached && !phase.CompletedDuration, "Budget-limited result identified");
            Require(phase.BytesScheduled <= options.ByteBudgetPerPhase, "Scheduled transfer budget");
            Require(phase.BytesTransferred + phase.WarmupBytes <= options.ByteBudgetPerPhase, "Transferred payload budget");
            double expected = phase.BytesTransferred * 8d / phase.Elapsed.TotalSeconds / 1000000d;
            Require(Math.Abs(phase.Mbps.GetValueOrDefault() - expected) < 0.000001, "Throughput uses actual interval");
        }
        Require(server.UploadBytes >= result.Upload.BytesTransferred + result.Upload.WarmupBytes,
            "Upload accounting cannot exceed bytes received by the server; cutoff may omit an unfinished acknowledgment");
        Require(server.DownloadBytes <= options.ByteBudgetPerPhase, "Server download byte bound");
        Require(server.UploadBytes <= options.ByteBudgetPerPhase, "Server upload byte bound");
        Require(server.Requests.All(path => path.Contains("r=")), "Every request bypasses cache by unique URL");
        var completed = updates.Where(update => update.IsPhaseComplete).ToArray();
        Require(completed.Length == 4 && completed.Select(update => update.Phase).Distinct().Count() == 4,
            "Exactly one explicit completion update is emitted for each transfer phase");
        Require(completed.Single(update => update.Phase == SpeedTestPhase.Download).BytesTransferred == result.Download.BytesTransferred &&
            completed.Single(update => update.Phase == SpeedTestPhase.Upload).BytesTransferred == result.Upload.BytesTransferred,
            "Phase completion updates carry final measured counts");
    }

    private static async Task AdaptiveCancel()
    {
        using var server = new LoopbackServer(Mode.SlowDownload);
        using var stop = new CancellationTokenSource();
        var options = Options(server);
        options.MaximumStreams = 8;
        options.WarmupDuration = TimeSpan.FromSeconds(3);
        options.ByteBudgetPerPhase = 64L * 1024 * 1024;
        int warmups = 0;
        bool measured = false;
        var updates = new CallbackProgress(p =>
        {
            if (p.Phase == SpeedTestPhase.Download) measured = true;
            if (p.Phase == SpeedTestPhase.DownloadWarmup && !p.IsPhaseComplete && p.Elapsed == TimeSpan.Zero &&
                Interlocked.Increment(ref warmups) == 2) stop.Cancel();
        });
        try
        {
            await new SpeedTestEngine().RunAsync(options, updates, stop.Token);
            throw new InvalidOperationException("Adaptive cancellation returned a result");
        }
        catch (OperationCanceledException)
        { Require(warmups == 2 && !measured, "Second warmup cancellation stops before measurement"); }
    }

    private static async Task Adaptive(Mode mode, int expectedStreams)
    {
        using var server = new LoopbackServer(mode);
        var options = Options(server);
        options.MaximumStreams = 8;
        options.WarmupDuration = TimeSpan.FromSeconds(3);
        options.ByteBudgetPerPhase = 64L * 1024 * 1024;
        var result = await new SpeedTestEngine().RunAsync(options, null, CancellationToken.None);
        Require(result.Download.StreamCount == expectedStreams, "Selected connection count follows measured scaling");
        Require(result.Download.CompletedDuration && result.Download.BytesTransferred > 0, "Selected streams complete measurement");
        Require(result.Download.BytesScheduled <= options.ByteBudgetPerPhase, "All adaptive stages share one byte budget");
    }

    private static async Task Duration()
    {
        using var server = new LoopbackServer(Mode.SlowDownload);
        var options = Options(server);
        options.ByteBudgetPerPhase = 16 * 1024 * 1024;
        options.PhaseDuration = TimeSpan.FromMilliseconds(650);
        var result = await new SpeedTestEngine().RunAsync(options, null, CancellationToken.None);
        Require(result.Download.CompletedDuration && !result.Download.ByteBudgetReached, "Timed download completed requested interval");
        Require(result.Download.Elapsed >= options.PhaseDuration && result.Download.Elapsed < TimeSpan.FromSeconds(2), "Timed transfer bounded");
        Require(result.Download.Mbps > 0 && result.Download.LatencySamples.Count > 0, "Payload and loaded latency collected");
        Require(result.Download.StreamCount == 4, "Four concurrent streams used");
    }

    private static async Task Reject(Mode mode, SpeedTestFailureKind expected)
    {
        using var server = new LoopbackServer(mode);
        var options = Options(server);
        if (mode == Mode.LoadedRate)
        {
            options.ByteBudgetPerPhase = 16 * 1024 * 1024;
            options.PhaseDuration = TimeSpan.FromMilliseconds(650);
        }
        try
        {
            await new SpeedTestEngine().RunAsync(options, null, CancellationToken.None);
            throw new InvalidOperationException("Invalid endpoint response was accepted: " + mode);
        }
        catch (SpeedTestException ex) { Require(ex.Kind == expected, mode + " returned " + ex.Kind + " instead of " + expected); }
    }

    private static async Task Timeout()
    {
        using var server = new LoopbackServer(Mode.Stall);
        var options = Options(server);
        options.RequestTimeout = TimeSpan.FromMilliseconds(150);
        var clock = Stopwatch.StartNew();
        try
        {
            await new SpeedTestEngine().RunAsync(options, null, CancellationToken.None);
            throw new InvalidOperationException("Stalled response completed");
        }
        catch (SpeedTestException ex)
        {
            Require(ex.Kind == SpeedTestFailureKind.Timeout, "Request timeout classification");
            Require(clock.Elapsed < TimeSpan.FromSeconds(2), "Request timeout bound");
        }
    }

    private sealed class CallbackProgress : IProgress<SpeedTestProgress>
    {
        private readonly Action<SpeedTestProgress> _callback;
        public CallbackProgress(Action<SpeedTestProgress> callback) { _callback = callback; }
        public void Report(SpeedTestProgress value) => _callback(value);
    }

    private static async Task Cancel()
    {
        using var server = new LoopbackServer(Mode.SlowDownload);
        using var cancel = new CancellationTokenSource();
        var options = Options(server);
        options.PhaseDuration = TimeSpan.FromSeconds(8);
        options.ByteBudgetPerPhase = 16 * 1024 * 1024;
        var clock = new Stopwatch();
        int armed = 0;
        var progress = new CallbackProgress(value =>
        {
            if (value.Phase == SpeedTestPhase.Download && value.BytesTransferred > 0 && Interlocked.Exchange(ref armed, 1) == 0)
            { clock.Start(); cancel.CancelAfter(50); }
        });
        try
        {
            await new SpeedTestEngine().RunAsync(options, progress, cancel.Token);
            throw new InvalidOperationException("Canceled transfer returned a result");
        }
        catch (OperationCanceledException)
        {
            Require(armed == 1 && clock.Elapsed < TimeSpan.FromSeconds(2), "Cancellation after active transfer is prompt");
        }
    }

    private enum Mode { Normal, SlowDownload, SharedDownload, WrongAck, StringAck, ShortDownload, Compressed, RateLimited, Unavailable, Stall, NoPayload, NoAck,
        FirstRetry, RepeatedRetry, LongRetry, MeasuredRate, LoadedRate }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
        private readonly SemaphoreSlim _bandwidth = new SemaphoreSlim(1, 1);
        private readonly Mode _mode;
        private readonly Task _accept;
        private long _downloadBytes;
        private long _uploadBytes;
        private int _requestCount;
        private int _rateResponses;
        public int RateResponses => Volatile.Read(ref _rateResponses);
        public readonly ConcurrentBag<string> Requests = new ConcurrentBag<string>();
        public Uri Endpoint { get; }
        public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
        public long UploadBytes => Interlocked.Read(ref _uploadBytes);

        public LoopbackServer(Mode mode)
        {
            _mode = mode;
            _listener.Start();
            Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    _clients.Add(client);
                    _ = ServeAsync(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private static async Task<string?> HeaderAsync(NetworkStream stream, CancellationToken token)
        {
            var data = new List<byte>();
            var next = new byte[1];
            while (data.Count < 16384)
            {
                if (await stream.ReadAsync(next, 0, 1, token) == 0) return null;
                data.Add(next[0]);
                int count = data.Count;
                if (count >= 4 && data[count - 4] == 13 && data[count - 3] == 10 && data[count - 2] == 13 && data[count - 1] == 10)
                    return Encoding.ASCII.GetString(data.ToArray());
            }
            throw new IOException("Request headers exceeded the test server limit.");
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    while (!_stop.IsCancellationRequested)
                    {
                        var header = await HeaderAsync(stream, _stop.Token);
                        if (header == null) return;
                        var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var parts = lines[0].Split(' ');
                        string path = parts[1];
                        Requests.Add(path);
                        int requestNumber = Interlocked.Increment(ref _requestCount);
                        bool upload = parts[0] == "POST";
                        int requested = 0;
                        foreach (string item in path.Substring(path.IndexOf('?') + 1).Split('&'))
                            if (item.StartsWith("bytes=", StringComparison.Ordinal)) requested = int.Parse(item.Substring(6), CultureInfo.InvariantCulture);
                        if (_mode == Mode.Stall) { await Task.Delay(3000, _stop.Token); return; }
                        bool retry = _mode == Mode.RepeatedRetry || _mode == Mode.LongRetry ||
                            (_mode == Mode.FirstRetry && requestNumber == 1) ||
                            (_mode == Mode.MeasuredRate && requested > 0) ||
                            (_mode == Mode.LoadedRate && requested == 0 && requestNumber > 6);
                        if (retry)
                        {
                            Interlocked.Increment(ref _rateResponses);
                            await WriteHeader(stream, "429 Too Many Requests", 0, "Retry-After: " + (_mode == Mode.LongRetry ? "30" : "1") + "\r\n");
                            continue;
                        }
                        if (_mode == Mode.RateLimited || _mode == Mode.Unavailable)
                        {
                            await WriteHeader(stream, _mode == Mode.RateLimited ? "429 Too Many Requests" : "503 Service Unavailable", 0);
                            continue;
                        }
                        if (upload)
                        {
                            string contentLength = lines.First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                            int remaining = int.Parse(contentLength.Substring(contentLength.IndexOf(':') + 1).Trim(), CultureInfo.InvariantCulture);
                            while (remaining > 0)
                            {
                                int read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining), _stop.Token);
                                if (read == 0) return;
                                remaining -= read;
                                Interlocked.Add(ref _uploadBytes, read);
                            }
                            if (_mode == Mode.NoAck) { await Task.Delay(3000, _stop.Token); return; }
                            string json = _mode == Mode.StringAck ? "{\"bytes\":\"wrong\"}" :
                                "{\"bytes\":" + (_mode == Mode.WrongAck ? requested + 1 : requested) + "}";
                            byte[] response = Encoding.UTF8.GetBytes(json);
                            await WriteHeader(stream, "200 OK", response.Length);
                            await stream.WriteAsync(response, 0, response.Length, _stop.Token);
                        }
                        else
                        {
                            int length = _mode == Mode.ShortDownload && requested > 0 ? requested - 1 : requested;
                            await WriteHeader(stream, "200 OK", length, _mode == Mode.Compressed ? "Content-Encoding: gzip\r\n" : "");
                            if (_mode == Mode.NoPayload && length > 0) { await Task.Delay(3000, _stop.Token); return; }
                            while (length > 0)
                            {
                                if (_mode == Mode.SlowDownload || _mode == Mode.LoadedRate) await Task.Delay(20, _stop.Token);
                                int size = Math.Min(buffer.Length, length);
                                if (_mode == Mode.SharedDownload)
                                {
                                    await _bandwidth.WaitAsync(_stop.Token);
                                    try
                                    {
                                        await Task.Delay(20, _stop.Token);
                                        await stream.WriteAsync(buffer, 0, size, _stop.Token);
                                    }
                                    finally { _bandwidth.Release(); }
                                }
                                else await stream.WriteAsync(buffer, 0, size, _stop.Token);
                                Interlocked.Add(ref _downloadBytes, size);
                                length -= size;
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
            }
        }

        private async Task WriteHeader(NetworkStream stream, string status, int length, string extra = "")
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: " + length +
                "\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n" + extra + "\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients) client.Dispose();
            try { _accept.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
