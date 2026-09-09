using System;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Services.SpeedTest;
using KillerScan.Terminal;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private TerminalControl? _speedTestTerminal;
        private CancellationTokenSource? _speedTestRun;

        private void SpeedTestButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_terminalPanelDisposed) return;
            if (_terminalControl?.HasRunningCommand == true)
            {
                var dialog = new Controls.ConfirmDialog(Loc("Str_Speed_Title"), Loc("Str_Speed_ReplaceRunning"),
                    Loc("Str_Speed_Start"), Loc("Str_Btn_Cancel")) { Owner = this };
                dialog.ShowDialog();
                if (!dialog.Confirmed) return;
                NewTerminal(title: Loc("Str_Speed_Title"), shellCommand: string.Empty);
            }
            else if (_terminalControl == null || _terminalExited)
                NewTerminal(title: Loc("Str_Speed_Title"), shellCommand: string.Empty);
            else
            {
                ShowWorkspaceContent(_terminalControl, "terminal");
                _terminalControl.Focus();
            }
            var terminal = _speedTestTerminal = _terminalControl!;
            terminal.Disposed += () =>
            {
                if (!ReferenceEquals(_speedTestTerminal, terminal)) return;
                _speedTestTerminal = null;
                var run = _speedTestRun;
                _speedTestRun = null;
                run?.Cancel();
            };
            _ = RunTerminalSpeedTestAsync(terminal);
        }

        private async Task RunTerminalSpeedTestAsync(TerminalControl terminal)
        {
            if (_speedTestRun != null || !ReferenceEquals(_speedTestTerminal, terminal)) return;
            var cancellation = _speedTestRun = new CancellationTokenSource();
            bool Current() => ReferenceEquals(_speedTestTerminal, terminal) && ReferenceEquals(_speedTestRun, cancellation);
            void Status(string key)
            {
                _terminalStatusKey = key;
                UpdateTerminalPanelStatus();
            }
            void Line(string text, int color = 36) => terminal.WriteManaged($"\r\u001b[2K\u001b[{color}m{text}\u001b[0m");
            var presentation = new SpeedTestPresentation(Loc, () => terminal.Buffer.Cols);
            Status("Str_Speed_Title");
            var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var consentCancellation = cancellation.Token.Register(() => consent.TrySetCanceled());
            terminal.ManagedInput += input =>
            {
                if (input == "\u001b" || input == "\u0003") cancellation.Cancel();
                else if (input == "\r" || input == "\n") consent.TrySetResult(true);
            };
            bool acceptingProgress = true;
            var progress = new Progress<SpeedTestProgress>(p =>
            {
                if (!Current() || !acceptingProgress || p.Phase == SpeedTestPhase.Completed) return;
                bool down = p.Phase is SpeedTestPhase.Download or SpeedTestPhase.DownloadWarmup;
                bool up = p.Phase is SpeedTestPhase.Upload or SpeedTestPhase.UploadWarmup;
                string key = down ? "Str_Speed_Download" : up ? "Str_Speed_Upload" : "Str_Speed_Latency";
                Status(key);
                terminal.WriteManaged(presentation.Progress(p));
            });
            try
            {
                await terminal.BeginShellManagedSessionAsync(cancellation.Token);
                var options = new SpeedTestOptions();
                terminal.WriteManaged(presentation.Header(options.Endpoint));
                terminal.WriteManaged("\u001b[37m" + Loc("Str_Speed_Consent") + "\u001b[0m\r\n\r\n");
                await consent.Task;
                cancellation.Token.ThrowIfCancellationRequested();
                Status("Str_Speed_Run");
                var result = await new SpeedTestEngine().RunAsync(options, progress, cancellation.Token);
                acceptingProgress = false;
                if (!Current()) return;
                terminal.WriteManaged(presentation.Result(result));
                string key = result.Download.CompletedDuration && result.Upload.CompletedDuration ? "Str_Speed_Completed" : "Str_Speed_Limited";
                Status(key);
            }
            catch (OperationCanceledException)
            {
                if (Current()) { Status("Str_Speed_Canceled"); Line(Loc("Str_Speed_Canceled") + "\r\n", 33); }
            }
            catch (Exception ex)
            {
                if (!Current()) return;
                string key = ex is SpeedTestException failure ? failure.Kind switch
                {
                    SpeedTestFailureKind.Timeout => "Str_Speed_Timeout",
                    SpeedTestFailureKind.EndpointUnavailable => "Str_Speed_Unreachable",
                    SpeedTestFailureKind.TlsFailure => "Str_Speed_Tls",
                    SpeedTestFailureKind.RateLimited => "Str_Speed_RateLimited",
                    SpeedTestFailureKind.InvalidResponse or SpeedTestFailureKind.HttpError => "Str_Speed_BadResponse",
                    _ => "Str_Speed_TransferFailed"
                } : "Str_Speed_TransferFailed";
                Status(key);
                Line(Loc(key) + "\r\n", 31);
            }
            finally
            {
                acceptingProgress = false;
                if (Current())
                {
                    try { await terminal.EndShellManagedSessionAsync(); }
                    catch (System.IO.IOException) { /* The shell closed while output was draining. */ }
                    catch (ObjectDisposedException) { /* The terminal was replaced during completion. */ }
                    if (Current())
                    {
                        _speedTestRun = null;
                        _speedTestTerminal = null;
                        _terminalTitle = null;
                        _terminalIsPing = false;
                        UpdateTerminalPanelStatus();
                    }
                }
                cancellation.Dispose();
            }
        }
    }
}
