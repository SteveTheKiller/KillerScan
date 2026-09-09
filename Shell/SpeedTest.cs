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
                NewTerminal(title: Loc("Str_Speed_Title"), managed: true);
            }
            else if (_terminalControl == null || _terminalExited)
                NewTerminal(title: Loc("Str_Speed_Title"), managed: true);
            else
            {
                _terminalControl.BeginManagedSession();
                ShowWorkspaceContent(_terminalControl, "terminal");
                _terminalControl.Focus();
            }
            var terminal = _speedTestTerminal = _terminalControl!;
            terminal.ManagedInput += input =>
            {
                if (input == "\u001b" || input == "\u0003") _speedTestRun?.Cancel();
            };
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
            terminal.WriteManaged("\r\n\u001b[1;36m" + Loc("Str_Speed_Title") + "\u001b[0m\r\n");
            Status("Str_Speed_Run");
            bool downloadPrinted = false, uploadPrinted = false;
            var progress = new Progress<SpeedTestProgress>(p =>
            {
                if (!Current() || p.Phase == SpeedTestPhase.Completed) return;
                bool down = p.Phase is SpeedTestPhase.Download or SpeedTestPhase.DownloadWarmup;
                bool up = p.Phase is SpeedTestPhase.Upload or SpeedTestPhase.UploadWarmup;
                string key = down ? "Str_Speed_Download" : up ? "Str_Speed_Upload" : "Str_Speed_Latency";
                Status(key);
                double? value = down || up ? p.Mbps : p.LatencyMs;
                string line = Loc(key) + ": " + (value?.ToString("N1") ?? "...") + (down || up ? " Mbps" : " ms");
                if (p.IsPhaseComplete && p.Mbps.HasValue &&
                    ((p.Phase == SpeedTestPhase.Download && !downloadPrinted) ||
                     (p.Phase == SpeedTestPhase.Upload && !uploadPrinted)))
                {
                    if (down) downloadPrinted = true; else uploadPrinted = true;
                    Line(line + "\r\n", 32);
                    return;
                }
                int width = Math.Max(1, terminal.Buffer.Cols - 1);
                Line(line.Length > width ? line.Substring(0, width) : line);
            });
            try
            {
                var result = await new SpeedTestEngine().RunAsync(new SpeedTestOptions(), progress, cancellation.Token);
                if (!Current()) return;
                if (!downloadPrinted) Line(Loc("Str_Speed_Download") + ": " + (result.Download.Mbps?.ToString("N1") ?? Loc("Str_Speed_Unavailable")) + " Mbps\r\n", 32);
                if (!uploadPrinted) Line(Loc("Str_Speed_Upload") + ": " + (result.Upload.Mbps?.ToString("N1") ?? Loc("Str_Speed_Unavailable")) + " Mbps\r\n", 32);
                Line(Loc("Str_Speed_Latency") + ": " + (result.IdleLatencyMs?.ToString("N1") ?? Loc("Str_Speed_Unavailable")) + " ms\r\n", 36);
                string key = result.Download.CompletedDuration && result.Upload.CompletedDuration ? "Str_Speed_Completed" : "Str_Speed_Limited";
                Status(key);
                Line(Loc(key) + "\r\n", 32);
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
                if (Current())
                {
                    _speedTestRun = null;
                    _speedTestTerminal = null;
                    terminal.EndManagedSession();
                    if (!terminal.HasShell)
                    {
                        EnsureBundledModules();
                        string shell = ResolveTerminalShell();
                        terminal.Start(QuoteArgument(shell) + " -NoLogo" + PromptArgs(),
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    }
                    _terminalStatusKey = null;
                    _terminalTitle = null;
                    _terminalIsPing = false;
                    UpdateTerminalPanelStatus();
                }
                cancellation.Dispose();
            }
        }
    }
}
