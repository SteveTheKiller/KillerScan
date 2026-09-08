using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerScan.Services;
using KillerScan.Services.SpeedTest;

namespace KillerScan.Controls
{
    public partial class SpeedTestView : UserControl, IDisposable
    {
        private readonly List<(double Seconds, double Mbps)> _download = new();
        private readonly List<(double Seconds, double Mbps)> _upload = new();
        private CancellationTokenSource? _run;
        private SpeedTestResult? _result;
        private string _statusKey = "Str_Speed_Ready";
        private string? _error;
        private bool _disposed;
        public string StatusText => Status.Text;
        public bool IsRunning => _run != null;
        public event Action? StatusChanged;
        public void Cancel() => _run?.Cancel();
        public void FocusInput()
        {
            if (IsRunning) StopButton.Focus();
            else EndpointBox.Focus();
        }

        public SpeedTestView()
        {
            InitializeComponent();
            EndpointBox.Text = App.GetSetting("SpeedTestEndpoint") ?? "";
            LocaleManager.LocaleChanged += RefreshLocale;
            RefreshLocale();
        }

        private string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
        private string Value(double? value, string unit) => value.HasValue ? value.Value.ToString("N2") + " " + unit : L("Str_Speed_Unavailable");
        private void SetStatus(string key, string? error = null)
        {
            _statusKey = key; _error = error;
            Status.Text = error == null ? L(key) : string.Format(L(key), error);
            StatusChanged?.Invoke();
        }
        private void RefreshLocale()
        {
            SetStatus(_statusKey, _error);
            if (_result != null) ShowResult(_result);
            else if (!IsRunning)
            {
                DownloadValue.Text = UploadValue.Text = IdleValue.Text = JitterValue.Text = L("Str_Speed_Unavailable");
            }
        }
        private async void Start_Click(object sender, RoutedEventArgs e) => await StartTestAsync();

        public async Task StartTestAsync()
        {
            if (_disposed || IsRunning) return;
            if (!Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp)) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            { SetStatus("Str_Speed_EndpointInvalid"); return; }
            App.SetSetting("SpeedTestEndpoint", endpoint.AbsoluteUri);
            _result = null; _download.Clear(); _upload.Clear();
            DownloadValue.Text = UploadValue.Text = IdleValue.Text = JitterValue.Text = L("Str_Speed_Unavailable");
            LoadedValue.Text = Details.Text = "";
            DrawCharts();
            var cancellation = _run = new CancellationTokenSource();
            StartButton.IsEnabled = EndpointBox.IsEnabled = CopyButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StopButton.Focus();
            SetStatus("Str_Speed_Run");
            var progress = new Progress<SpeedTestProgress>(p => { if (!_disposed && ReferenceEquals(_run, cancellation)) ShowProgress(p); });
            try
            {
                var result = await new SpeedTestEngine().RunAsync(new SpeedTestOptions { Endpoint = endpoint }, progress, cancellation.Token);
                if (_disposed) return;
                _result = result;
                ShowResult(result);
                SetStatus(result.Download.CompletedDuration && result.Upload.CompletedDuration ? "Str_Speed_Completed" : "Str_Speed_Limited");
                CopyButton.IsEnabled = true;
            }
            catch (OperationCanceledException) { if (!_disposed) SetStatus("Str_Speed_Canceled"); }
            catch (SpeedTestException ex)
            {
                if (!_disposed) SetStatus(ex.Kind switch
                {
                    SpeedTestFailureKind.InvalidConfiguration => "Str_Speed_EndpointInvalid",
                    SpeedTestFailureKind.Timeout => "Str_Speed_Timeout",
                    SpeedTestFailureKind.EndpointUnavailable => "Str_Speed_Unreachable",
                    SpeedTestFailureKind.TlsFailure => "Str_Speed_Tls",
                    SpeedTestFailureKind.RateLimited => "Str_Speed_RateLimited",
                    SpeedTestFailureKind.InvalidResponse or SpeedTestFailureKind.HttpError => "Str_Speed_BadResponse",
                    _ => "Str_Speed_TransferFailed"
                });
            }
            catch (Exception) { if (!_disposed) SetStatus("Str_Speed_TransferFailed"); }
            finally
            {
                _run = null;
                cancellation.Dispose();
                if (!_disposed) { StartButton.IsEnabled = EndpointBox.IsEnabled = true; StopButton.IsEnabled = false; }
            }
        }

        private void ShowProgress(SpeedTestProgress progress)
        {
            SetStatus(progress.Phase is SpeedTestPhase.DownloadWarmup or SpeedTestPhase.UploadWarmup ? "Str_Speed_Warmup" : "Str_Speed_Run");
            if (progress.Phase == SpeedTestPhase.IdleLatency) IdleValue.Text = Value(progress.LatencyMs, "ms");
            if (progress.Phase == SpeedTestPhase.Download)
            {
                DownloadValue.Text = Value(progress.Mbps, "Mbps");
                if (progress.Mbps.HasValue) _download.Add((progress.Elapsed.TotalSeconds, progress.Mbps.Value));
            }
            if (progress.Phase == SpeedTestPhase.Upload)
            {
                UploadValue.Text = Value(progress.Mbps, "Mbps");
                if (progress.Mbps.HasValue) _upload.Add((progress.Elapsed.TotalSeconds, progress.Mbps.Value));
            }
            if (progress.LatencyMs.HasValue && progress.Phase != SpeedTestPhase.IdleLatency)
                LoadedValue.Text = Value(progress.LatencyMs, "ms");
            Details.Text = $"{L("Str_Speed_Streams")}: {progress.ActiveStreams}   {L("Str_Speed_Transferred")}: {progress.BytesTransferred / 1048576d:N1} MiB";
            DrawCharts();
        }
        private void ShowResult(SpeedTestResult result)
        {
            DownloadValue.Text = Value(result.Download.Mbps, "Mbps"); UploadValue.Text = Value(result.Upload.Mbps, "Mbps");
            IdleValue.Text = Value(result.IdleLatencyMs, "ms"); JitterValue.Text = Value(result.JitterMs, "ms");
            LoadedValue.Text = $"{L("Str_Speed_Download")}: {Value(result.Download.LoadedLatencyMs, "ms")}   {L("Str_Speed_Upload")}: {Value(result.Upload.LoadedLatencyMs, "ms")}";
            Details.Text = $"{result.Endpoint.Host}   {result.Elapsed.TotalSeconds:N1} s   {L("Str_Speed_Transferred")}: {(result.Download.BytesTransferred + result.Upload.BytesTransferred + result.Download.WarmupBytes + result.Upload.WarmupBytes) / 1048576d:N1} MiB";
        }
        private void DrawCharts()
        {
            Draw(DownloadChart, DownloadLine, DownloadScale, _download);
            Draw(UploadChart, UploadLine, UploadScale, _upload);
        }
        private static void Draw(Canvas chart, Polyline line, TextBlock scale, List<(double Seconds, double Mbps)> samples)
        {
            if (chart == null || line == null || scale == null) return;
            double maximum = samples.Count == 0 ? 1 : Math.Max(1, samples.Max(p => p.Mbps));
            double seconds = Math.Max(8, samples.Count == 0 ? 0 : samples[samples.Count - 1].Seconds);
            line.Points = new PointCollection(samples.Select(p => new Point(p.Seconds / seconds * chart.ActualWidth, chart.Height - p.Mbps / maximum * (chart.Height - 15))));
            scale.Text = maximum.ToString("N1") + " Mbps";
        }
        private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCharts();
        private void Stop_Click(object sender, RoutedEventArgs e) => Cancel();
        private void View_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsRunning) { _run?.Cancel(); e.Handled = true; }
        }
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            string completion = _result.Download.CompletedDuration && _result.Upload.CompletedDuration
                ? "Str_Speed_Completed" : "Str_Speed_Limited";
            var text = new StringBuilder().AppendLine(L("Str_Speed_Title")).AppendLine(_result.Endpoint.AbsoluteUri)
                .AppendLine(_result.StartedAt.ToString("O"))
                .AppendLine($"{L("Str_Speed_Download")}: {DownloadValue.Text}")
                .AppendLine($"{L("Str_Speed_Upload")}: {UploadValue.Text}")
                .AppendLine($"{L("Str_Speed_Idle")}: {IdleValue.Text}")
                .AppendLine($"{L("Str_Speed_Jitter")}: {JitterValue.Text}")
                .AppendLine($"{L("Str_Speed_Loaded")}: {LoadedValue.Text}").AppendLine(Details.Text).AppendLine(L(completion));
            try { Clipboard.SetText(text.ToString()); SetStatus(completion); }
            catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Str_Speed_ClipboardFailed"); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LocaleManager.LocaleChanged -= RefreshLocale;
            _run?.Cancel();
        }
    }
}
