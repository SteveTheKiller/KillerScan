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
            if (args.SequenceEqual(new[] { "--terminal" })) { await View(); return 0; }
            Require(args.All(value => value == "--worker" || value == "--internet"), "Usage: SpeedTest.Tests.exe [--worker] [--internet]");
            await Run("Metric units, median, jitter and unavailable values", Metrics);
            await Run("Scan terminal progress stays on one line and results fit narrow widths", ScanPresentation);
            await Run("Terminal history survives resizing, clears and shell replacement", TerminalHistory);
            await Run("Cloudflare upload response is accepted only for the exact official endpoint", CloudflareAcknowledgment);
            await Run("Public endpoint profile reduces request pressure without shortening the test", PublicProfile);
            await Run("Adaptive warmup adds streams on a per-connection bottleneck", () => Adaptive(Mode.SlowDownload, 8));
            await Run("Download measurement keeps all streams after a shared warmup bottleneck", () => Adaptive(Mode.SharedDownload, 8));
            await Run("Cancellation during adaptive warmup prevents measurement", AdaptiveCancel);
            await Run("Measurement retains the payload size learned during warmup", WarmupPayload);
            await Run("HTTPS configuration required without contacting a remote host", InvalidEndpoint);
            await Run("Latency probes preserve the shared server connection limit", LatencyConnectionLimit);
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

    private static Task TerminalHistory()
    {
        var assembly = typeof(SpeedTestEngine).Assembly;
        var type = assembly.GetType("KillerScan.Terminal.TerminalBuffer")!;
        var parserType = assembly.GetType("KillerScan.Terminal.VtParser")!;
        var buffer = Activator.CreateInstance(type, 40, 5)!;
        var parser = Activator.CreateInstance(parserType, buffer)!;
        void Feed(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            parserType.GetMethod("Feed")!.Invoke(parser, new object[] { bytes, bytes.Length });
        }
        string Text(object source)
        {
            var text = new StringBuilder();
            int total = (int)type.GetProperty("TotalLines")!.GetValue(source)!;
            for (int row = 0; row < total; row++)
            {
                var cells = (Array)type.GetMethod("LineAt")!.Invoke(source, new object[] { row })!;
                foreach (object cell in cells)
                {
                    int ch = (int)cell.GetType().GetField("Ch")!.GetValue(cell)!;
                    text.Append(ch == 0 ? ' ' : (char)ch);
                }
                text.AppendLine();
            }
            return text.ToString();
        }
        Feed("first-row-retained\r\nsecond-row-retained\r\nthird-row-retained");
        type.GetMethod("Resize")!.Invoke(buffer, new object[] { 8, 2 });
        type.GetMethod("Resize")!.Invoke(buffer, new object[] { 40, 8 });
        Feed("\u001b[2J\u001b[3J\u001b[Hnew-output");
        foreach (string expected in new[] { "first-row-retained", "second-row-retained", "third-row-retained", "new-output" })
            Require(Text(buffer).Contains(expected), "Resize and clear preserve " + expected);
        type.GetMethod("ClearAll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(buffer, null);
        Require(Text(buffer).Contains("new-output"), "Clear menu preserves output");
        var next = Activator.CreateInstance(type, 40, 8)!;
        type.GetMethod("ImportHistory")!.Invoke(next, new[] { buffer });
        Require(Text(next).Contains("first-row-retained") && Text(next).Contains("new-output"), "Replacement shell inherits the session");
        return Task.CompletedTask;
    }

    private static Task ScanPresentation()
    {
        var type = typeof(SpeedTestEngine).Assembly.GetType("KillerScan.Terminal.TerminalScanPresentation")!;
        var devices = new List<KillerScan.Models.NetworkDevice>
        {
            new() { IpAddress = "192.168.0.1", Hostname = "router.internal", Vendor = "A long vendor name for wrapping",
                MacAddress = "00:11:22:33:44:55", OpenPorts = [22, 53, 80, 443, 8080, 8443] }
        };
        foreach (int width in new[] { 24, 40, 80, 120 })
        {
            var view = Activator.CreateInstance(type, new Func<string, string>(_ => "Open Ports"), new Func<int>(() => width))!;
            string progress = (string)type.GetMethod("Progress")!.Invoke(view, new object[] { "Progress: 72%" })!;
            Require(progress.StartsWith("\r\u001b[2K") && !progress.Contains('\n'), "Progress replaces the current row");
            string rendered = (string)type.GetMethod("Result")!.Invoke(view, new object[] { devices })!;
            string plain = System.Text.RegularExpressions.Regex.Replace(rendered, "\u001b\\[[0-9;]*[A-Za-z]", "");
            Require(plain.Split('\n').All(line => line.Trim('\r').Length <= width - 2), "Every result row fits the terminal");
            foreach (string port in new[] { "22", "53", "80", "443", "8080", "8443" })
                Require(plain.Contains(port), "Complete port numbers are preserved");
            Require(!plain.Contains("---"), "No table separator lines");
        }
        return Task.CompletedTask;
    }

    private static Task PublicProfile()
    {
        var snapshot = typeof(SpeedTestEngine).GetMethod("ValidateAndCopy", BindingFlags.Static | BindingFlags.NonPublic)!;
        var profile = (SpeedTestOptions)snapshot.Invoke(null, new object[] { new SpeedTestOptions
        {
            Endpoint = new Uri("https://speed.cloudflare.com/"), MaximumStreams = 4,
            DownloadPayloadBytes = 25000000, UploadPayloadBytes = 10000000
        } })!;
        Require(profile.MaximumStreams == 2, "Public service uses at most two payload streams");
        Require(profile.PhaseDuration == TimeSpan.FromSeconds(15) && profile.ByteBudgetPerPhase == 3L * 1024 * 1024 * 1024,
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
        var progress = new CallbackProgress(p =>
        {
            if (p.IsPhaseComplete)
                Console.WriteLine($"PHASE {p.Phase} bytes={p.BytesTransferred} seconds={p.Elapsed.TotalSeconds:F3} Mbps={SpeedTestMetrics.MegabitsPerSecond(p.BytesTransferred, p.Elapsed):F2}");
        });
        var result = await new SpeedTestEngine().RunAsync(options, progress, deadline.Token);
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
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
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
                using var cancellation = new CancellationTokenSource();
                using var terminalLifetime = (IDisposable)terminal;
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
                Write("\u001b[90mEscape or Ctrl+C: cancel.\u001b[0m\r\n");
                terminal.UpdateLayout();
                var bitmap = new RenderTargetBitmap(900, 500, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(terminal);
                var imagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpeedTestTerminal.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(imagePath)) encoder.Save(file);
                Console.WriteLine("Offscreen terminal: " + imagePath);
                string? networkSample = Environment.GetEnvironmentVariable("KILLERSCAN_NETWORK_COLOR_SAMPLE");
                if (!string.IsNullOrEmpty(networkSample))
                {
                    Write("\u001b[2J\u001b[H" + File.ReadAllText(networkSample));
                    terminal.UpdateLayout();
                    var networkBitmap = new RenderTargetBitmap(900, 500, 96, 96, PixelFormats.Pbgra32);
                    networkBitmap.Render(terminal);
                    var networkEncoder = new PngBitmapEncoder();
                    networkEncoder.Frames.Add(BitmapFrame.Create(networkBitmap));
                    using var output = File.Create(Path.ChangeExtension(networkSample, ".png"));
                    networkEncoder.Save(output);
                }
                terminalType.GetMethod("EndManagedSession")!.Invoke(terminal, null);
                bool Busy() => (bool)terminalType.GetProperty("HasRunningCommand")!.GetValue(terminal)!;
                void Send(string value) => terminalType.GetMethod("Send")!.Invoke(terminal, new object[] { value });
                void WaitFor(Func<bool> ready)
                {
                    var clock = Stopwatch.StartNew();
                    while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                        timer.Start();
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                    }
                    Require(ready(), "Shell state reached before deadline");
                }
                string shell = (string)assembly.GetType("KillerScan.Shell.MainWindow", true)!
                    .GetMethod("ResolveTerminalShell", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                terminalType.GetEvent("StartFailed")!.AddEventHandler(terminal,
                    (Action<Exception>)(ex => throw new InvalidOperationException("Shell launch failed", ex)));
                var windowType = assembly.GetType("KillerScan.Shell.MainWindow", true)!;
                string promptArgs = (string)windowType.GetMethod("PromptArgs", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new[] { terminalType.GetProperty("ManagedShellSetup")!.GetValue(terminal) })!;
                string palettePath = (string)windowType.GetProperty("PromptPalettePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var primary = ((SolidColorBrush)app.FindResource("PrimaryBrush")).Color;
                Require(File.ReadAllText(palettePath).Contains($"ACCENT=#{primary.R:X2}{primary.G:X2}{primary.B:X2}"),
                    "Prompt receives the selected theme accent instead of the red fallback");
                string? previousTerm = Environment.GetEnvironmentVariable("TERM");
                string? previousNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
                try
                {
                    // Exercise the colored desktop terminal even when the test runner disables color.
                    Environment.SetEnvironmentVariable("TERM", "xterm-256color");
                    Environment.SetEnvironmentVariable("NO_COLOR", null);
                    terminalType.GetMethod("Start")!.Invoke(terminal, new object[] {
                        "\"" + shell + "\" -NoLogo -NoProfile" + promptArgs,
                        Path.GetTempPath() });
                }
                finally
                {
                    Environment.SetEnvironmentVariable("TERM", previousTerm);
                    Environment.SetEnvironmentVariable("NO_COLOR", previousNoColor);
                }
                WaitFor(() => !Busy());
                Require((bool)terminalType.GetProperty("HasShell")!.GetValue(terminal)!, "Shell remains alive at prompt");
                Send("ping -n 1 127.0.0.1\r");
                WaitFor(() => !Busy() && Text().Contains("TTL="));
                var networkBuffer = terminalType.GetProperty("Buffer")!.GetValue(terminal)!;
                bool ColoredReply()
                {
                    int count = (int)networkBuffer.GetType().GetProperty("TotalLines")!.GetValue(networkBuffer)!;
                    for (int row = 0; row < count; row++)
                    {
                        var cells = (Array)networkBuffer.GetType().GetMethod("LineAt")!.Invoke(networkBuffer, new object[] { row })!;
                        var line = new StringBuilder();
                        foreach (var cell in cells) line.Append(char.ConvertFromUtf32((int)cell.GetType().GetField("Ch")!.GetValue(cell)!));
                        int address = line.ToString().IndexOf("127.0.0.1", StringComparison.Ordinal);
                        if (address >= 0 && line.ToString().Contains("TTL="))
                        {
                            object cell = cells.GetValue(address)!;
                            return (int)cell.GetType().GetField("Fg")!.GetValue(cell)! == 6;
                        }
                    }
                    return false;
                }
                Require(ColoredReply(), "Typed ping reaches the real terminal with teal address cells");
                Send("$ksTestValue = 42\r");
                Require(Busy(), "Submitting a command marks the terminal busy");
                WaitFor(() => !Busy());
                var begin = (Task)terminalType.GetMethod("BeginShellManagedSessionAsync")!.Invoke(terminal, new object[] { CancellationToken.None })!;
                WaitFor(() => begin.IsCompleted);
                begin.GetAwaiter().GetResult();
                var presentationType = assembly.GetType("KillerScan.Terminal.SpeedTestPresentation", true)!;
                var presentation = Activator.CreateInstance(presentationType,
                    new Func<string, string>(key => (string)app.FindResource(key)), new Func<int>(() => 100))!;
                void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
                var sample = new SpeedTestResult();
                var download = new SpeedTestPhaseResult();
                var upload = new SpeedTestPhaseResult();
                Set(sample, "Endpoint", new Uri("https://speed.killerscan.net/"));
                Set(sample, "Download", download); Set(sample, "Upload", upload);
                Set(sample, "IdleLatencySamples", new double[] { 12, 13, 11 });
                Set(download, "Mbps", 812.4d); Set(upload, "Mbps", 38.7d);
                Set(download, "StreamCount", 8); Set(upload, "StreamCount", 2);
                Set(download, "CompletedDuration", true); Set(upload, "CompletedDuration", true);
                Set(download, "LatencySamples", new double[] { 28, 30, 29 });
                Set(upload, "LatencySamples", new double[] { 43, 45, 44 });
                Write((string)presentationType.GetMethod("Header")!.Invoke(presentation, new object[] { sample.Endpoint })!);
                Write((string)presentationType.GetMethod("Result")!.Invoke(presentation, new object[] { sample })!);
                Write("\r\nSPEEDTEST-RESULT\r\n");
                WaitFor(() => Text().Contains("SPEEDTEST-RESULT"));
                Require(Busy(), "Live output reaches the console before the managed command ends");
                var end = (Task)terminalType.GetMethod("EndShellManagedSessionAsync")!.Invoke(terminal, null)!;
                WaitFor(() => end.IsCompleted);
                end.GetAwaiter().GetResult();
                WaitFor(() => !Busy());
                var buffer = terminalType.GetProperty("Buffer")!.GetValue(terminal)!;
                string Visible()
                {
                    int rows = (int)buffer.GetType().GetProperty("Rows")!.GetValue(buffer)!;
                    int total = (int)buffer.GetType().GetProperty("TotalLines")!.GetValue(buffer)!;
                    var visible = new System.Text.StringBuilder();
                    for (int row = total - rows; row < total; row++)
                        foreach (var cell in (Array)buffer.GetType().GetMethod("LineAt")!.Invoke(buffer, new object[] { row })!)
                            visible.Append(char.ConvertFromUtf32((int)cell.GetType().GetField("Ch")!.GetValue(cell)!));
                    return visible.ToString();
                }
                Require(Visible().Contains("SPEEDTEST-RESULT"), "Completed results remain on the visible screen above the prompt");
                Require(Visible().Contains("812.4") && Visible().Contains("38.7"), "Both results remain visible after the prompt redraw");
                int totalLines = (int)buffer.GetType().GetProperty("TotalLines")!.GetValue(buffer)!;
                int expectedAccent = 0x1000000 | (primary.R << 16) | (primary.G << 8) | primary.B;
                bool promptAccent = false;
                int screenRows = (int)buffer.GetType().GetProperty("Rows")!.GetValue(buffer)!;
                int cursorRow = (int)buffer.GetType().GetProperty("CursorRow")!.GetValue(buffer)!;
                for (int row = Math.Max(0, totalLines - screenRows + cursorRow - 2); row < totalLines; row++)
                    foreach (var cell in (Array)buffer.GetType().GetMethod("LineAt")!.Invoke(buffer, new object[] { row })!)
                    {
                        promptAccent |= (int)cell.GetType().GetField("Fg")!.GetValue(cell)! == expectedAccent ||
                            (int)cell.GetType().GetField("Bg")!.GetValue(cell)! == expectedAccent;
                    }
                Require(promptAccent, "The visible prompt uses the selected accent color");
                terminal.UpdateLayout();
                var completedBitmap = new RenderTargetBitmap(900, 500, 96, 96, PixelFormats.Pbgra32);
                completedBitmap.Render(terminal);
                var completedEncoder = new PngBitmapEncoder();
                completedEncoder.Frames.Add(BitmapFrame.Create(completedBitmap));
                using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpeedTestCompleted.png"))) completedEncoder.Save(file);
                Send("Write-Output ('KEPT-' + $ksTestValue)\r");
                WaitFor(() => Text().Contains("KEPT-42") && !Busy());
                Require(Text().Contains("SPEEDTEST-RESULT"), "Returning to shell retains speed-test output and variables");
                Require(rerunInputs == 1, "Shell Enter is no longer routed to the managed handler");
                begin = (Task)terminalType.GetMethod("BeginShellManagedSessionAsync")!.Invoke(terminal, new object[] { CancellationToken.None })!;
                WaitFor(() => begin.IsCompleted);
                begin.GetAwaiter().GetResult();
                bool canceled = false;
                terminalType.GetEvent("ManagedInput")!.AddEventHandler(terminal, (Action<string>)(s => canceled |= s == "\u0003"));
                Send("\u0003");
                Require(canceled, "Ctrl+C reaches the running test instead of interrupting its shell");
                Write("\r\nCANCELED-RESULT\r\n");
                end = (Task)terminalType.GetMethod("EndShellManagedSessionAsync")!.Invoke(terminal, null)!;
                WaitFor(() => end.IsCompleted);
                end.GetAwaiter().GetResult();
                WaitFor(() => !Busy());
                Require(Visible().Contains("CANCELED-RESULT"), "A repeated canceled run also leaves visible output and a usable prompt");
                Send("Write-Output ('RUN' + 'NING'); Start-Sleep -Seconds 30\r");
                WaitFor(() => Text().Contains("RUNNING"));
                Require(Busy(), "Running shell command requires confirmation before replacement");
                Send("\u0003");
                WaitFor(() => !Busy());
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
        using var content = (HttpContent)Activator.CreateInstance(contentType, 2048, new byte[2048], new Action<int>(_ => { }), cancel.Token)!;
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

    private static async Task LatencyConnectionLimit()
    {
        using var server = new LoopbackServer(Mode.Normal);
        var options = Options(server);
        int lowestLimit = int.MaxValue;
        var progress = new CallbackProgress(p =>
        {
            if (p.Phase == SpeedTestPhase.IdleLatency)
                lowestLimit = Math.Min(lowestLimit, ServicePointManager.FindServicePoint(server.Endpoint).ConnectionLimit);
        });
        await new SpeedTestEngine().RunAsync(options, progress, CancellationToken.None);
        Require(lowestLimit == options.MaximumStreams, "Latency requests must not lower the shared ServicePoint limit");
    }

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
        int warmupStages = 0;
        var progress = new CallbackProgress(p =>
        {
            if (p.Phase == SpeedTestPhase.DownloadWarmup && p.IsPhaseComplete) warmupStages++;
        });
        var result = await new SpeedTestEngine().RunAsync(options, progress, CancellationToken.None);
        Require(warmupStages == 3, "A plateau must not prevent testing the remaining connection counts");
        Require(result.Download.StreamCount == expectedStreams, "Download measurement keeps the configured connection count");
        Require(result.Download.CompletedDuration && result.Download.BytesTransferred > 0, "Selected streams complete measurement");
        Require(result.Download.BytesScheduled <= options.ByteBudgetPerPhase, "All adaptive stages share one byte budget");
    }

    private static async Task WarmupPayload()
    {
        using var server = new LoopbackServer(Mode.SlowDownload);
        var options = Options(server);
        options.MaximumStreams = 4;
        options.WarmupDuration = TimeSpan.FromSeconds(3);
        options.DownloadPayloadBytes = 1024 * 1024;
        options.ByteBudgetPerPhase = 16 * 1024 * 1024;
        int measuredStart = -1;
        int warmupStages = 0, expandedStart = -1;
        var updates = new CallbackProgress(p =>
        {
            if (p.Phase == SpeedTestPhase.DownloadWarmup && p.IsPhaseComplete) warmupStages++;
            if (p.Phase == SpeedTestPhase.DownloadWarmup && !p.IsPhaseComplete &&
                p.Elapsed == TimeSpan.Zero && warmupStages == 1)
                expandedStart = server.Requests.Count;
            if (p.Phase == SpeedTestPhase.Download && p.Elapsed == TimeSpan.Zero)
                measuredStart = server.Requests.Count;
        });
        await new SpeedTestEngine().RunAsync(options, updates, CancellationToken.None);
        Require(measuredStart >= 0, "Measurement started after warmup");
        Require(expandedStart >= 0, "Expanded connection comparison started");
        var expandedRequests = server.Requests.Skip(expandedStart)
            .Where(p => p.StartsWith("/__down?bytes=") && !p.StartsWith("/__down?bytes=0&")).Take(4).ToArray();
        Require(expandedRequests.Length == 4 && expandedRequests.All(p => !p.StartsWith("/__down?bytes=65536&")),
            "Added connections inherit the learned payload instead of restarting with 64 KiB requests");
        Require(expandedRequests.All(p => int.Parse(p.Split('=')[1].Split('&')[0], CultureInfo.InvariantCulture)
                <= options.DownloadPayloadBytes / 2),
            "Doubling connections divides the initial per-connection payload to respect shared bandwidth");
        string request = server.Requests.Skip(measuredStart).First(p => p.StartsWith("/__down?bytes=") && !p.StartsWith("/__down?bytes=0&"));
        Require(!request.StartsWith("/__down?bytes=65536&"), "Measurement must not restart with the initial 64 KiB payload");
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
        public readonly ConcurrentQueue<string> Requests = new ConcurrentQueue<string>();
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
                        Requests.Enqueue(path);
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
