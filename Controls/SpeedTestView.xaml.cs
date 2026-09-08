using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Services;
using KillerScan.Services.SpeedTest;

namespace KillerScan.Controls
{
    public partial class SpeedTestView : UserControl, IDisposable
    {
        private CancellationTokenSource? _run;
        private SpeedTestResult? _result;
        private string _statusKey = "Str_St_Ready";
        private bool _disposed;
        public string StatusText => Status.Text;
        public bool IsRunning => _run != null;
        public event Action? StatusChanged;
        public void Cancel() => _run?.Cancel();
        public void FocusInput()
        {
            if (IsRunning) StopButton.Focus();
            else StartButton.Focus();
        }

        public SpeedTestView()
        {
            InitializeComponent();
            LocaleManager.LocaleChanged += RefreshLocale;
            RefreshLocale();
        }

        private string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
        private static string Value(double? value) => value.HasValue ? value.Value.ToString("N1") : "...";
        private void SetStatus(string key)
        {
            _statusKey = key;
            Status.Text = L(key);
            StatusChanged?.Invoke();
        }
        private void RefreshLocale()
        {
            SetStatus(_statusKey);
            if (_result != null) ShowResult(_result);
            else if (!IsRunning) DownloadValue.Text = UploadValue.Text = IdleValue.Text = "...";
        }
        private async void Start_Click(object sender, RoutedEventArgs e) => await StartTestAsync();

        public async Task StartTestAsync()
        {
            if (_disposed || IsRunning) return;
            _result = null;
            DownloadValue.Text = UploadValue.Text = IdleValue.Text = "...";
            TestProgress.Value = 0;
            TestProgress.IsIndeterminate = true;
            var cancellation = _run = new CancellationTokenSource();
            StartButton.Visibility = CopyButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            StopButton.Focus();
            SetStatus("Str_Speed_Run");
            var progress = new Progress<SpeedTestProgress>(p =>
            {
                if (!_disposed && ReferenceEquals(_run, cancellation)) ShowProgress(p);
            });
            try
            {
                var result = await new SpeedTestEngine().RunAsync(new SpeedTestOptions(), progress, cancellation.Token);
                if (_disposed) return;
                _result = result;
                ShowResult(result);
                TestProgress.Value = 100;
                SetStatus(CompletionKey(result));
                CopyButton.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { if (!_disposed) SetStatus("Str_Speed_Canceled"); }
            catch (SpeedTestException ex)
            {
                if (!_disposed) SetStatus(ex.Kind switch
                {
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
                if (!_disposed)
                {
                    TestProgress.IsIndeterminate = false;
                    StartButton.Visibility = Visibility.Visible;
                    StopButton.Visibility = Visibility.Collapsed;
                    StartButton.Focus();
                }
            }
        }

        private void ShowProgress(SpeedTestProgress progress)
        {
            bool download = progress.Phase is SpeedTestPhase.Download or SpeedTestPhase.DownloadWarmup;
            bool upload = progress.Phase is SpeedTestPhase.Upload or SpeedTestPhase.UploadWarmup;
            SetStatus(download ? "Str_Speed_Download" : upload ? "Str_Speed_Upload" : "Str_Speed_Latency");
            TestProgress.IsIndeterminate = progress.Phase is SpeedTestPhase.IdleLatency or SpeedTestPhase.DownloadWarmup or SpeedTestPhase.UploadWarmup;
            if (progress.Phase == SpeedTestPhase.IdleLatency) IdleValue.Text = Value(progress.LatencyMs);
            if (progress.Phase == SpeedTestPhase.Download)
            {
                DownloadValue.Text = Value(progress.Mbps);
                TestProgress.Value = Math.Min(50, 5 + progress.Elapsed.TotalSeconds / 8 * 45);
            }
            if (progress.Phase == SpeedTestPhase.Upload)
            {
                UploadValue.Text = Value(progress.Mbps);
                TestProgress.Value = Math.Min(99, 55 + progress.Elapsed.TotalSeconds / 8 * 44);
            }
        }
        private void ShowResult(SpeedTestResult result)
        {
            DownloadValue.Text = Value(result.Download.Mbps);
            UploadValue.Text = Value(result.Upload.Mbps);
            IdleValue.Text = Value(result.IdleLatencyMs);
        }
        private static string CompletionKey(SpeedTestResult result) =>
            result.Download.CompletedDuration && result.Upload.CompletedDuration ? "Str_Speed_Completed" : "Str_Speed_Limited";
        private void Stop_Click(object sender, RoutedEventArgs e) => Cancel();
        private void View_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsRunning) { Cancel(); e.Handled = true; }
        }
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            var text = new StringBuilder().AppendLine(L("Str_Speed_Title"))
                .AppendLine($"{L("Str_Speed_Download")}: {DownloadValue.Text} Mbps")
                .AppendLine($"{L("Str_Speed_Upload")}: {UploadValue.Text} Mbps")
                .AppendLine($"{L("Str_Speed_Latency")}: {IdleValue.Text} ms")
                .AppendLine(_result.Endpoint.Host).AppendLine(L(CompletionKey(_result)));
            try { Clipboard.SetText(text.ToString()); SetStatus(CompletionKey(_result)); }
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
