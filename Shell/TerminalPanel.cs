using System.IO;
using System.Windows;
using System.Windows.Media;
using KillerScan.Terminal;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private TerminalControl? _terminalControl;
        private bool _terminalPanelDisposed;
        private bool _terminalExited;
        private bool _terminalIsPing;
        private string? _terminalTitle;
        private string? _terminalStatusKey;
        private object? _terminalStatusArgument;

        private void ToggleTerminalPanel() => NewTerminal();

        /// <param name="command">
        /// An executable and its arguments, launched directly with no shell between. This is
        /// how anything carrying user input has to arrive.
        /// </param>
        /// <param name="shellCommand">
        /// Text run inside the styled PowerShell session once its prompt is up. Only ever code
        /// this app composed itself.
        /// </param>
        /// <summary>The scripted session for demo mode, built from the network on screen.</summary>
        private static string DemoTerminalTranscript() =>
            Services.DemoData.Current is { } scan ? Services.DemoData.TerminalTranscript(scan) : string.Empty;

        private void NewTerminal(string? command = null, string? title = null, string? shellCommand = null)
        {
            if (_terminalPanelDisposed) return;
            if (_terminalControl == null || command != null || shellCommand != null || _terminalExited)
            {
                if (_terminalControl != null)
                {
                    _workspaceBody.Children.Remove(_terminalControl);
                    _terminalControl.Dispose();
                }
                var terminal = _terminalControl = new TerminalControl();
                _terminalTitle = title;
                _terminalExited = false;
                _terminalIsPing = shellCommand?.StartsWith("ping.exe ", StringComparison.OrdinalIgnoreCase) == true;
                _terminalStatusKey = null;
                _terminalStatusArgument = null;
                terminal.GotKeyboardFocus += (_, _) => UpdateTerminalPanelStatus();
                terminal.SpeedTestRequested += () => SpeedTestButton_Click(this, new RoutedEventArgs());
                terminal.Exited += code =>
                {
                    if (_terminalControl != terminal) return;
                    _terminalExited = true;
                    _terminalStatusKey = "Str_Workspace_Exited";
                    _terminalStatusArgument = code;
                    UpdateTerminalPanelStatus();
                };
                terminal.StartFailed += error =>
                {
                    if (_terminalControl != terminal) return;
                    _terminalExited = true;
                    _terminalStatusKey = "Str_Workspace_StartFailed";
                    _terminalStatusArgument = error.Message;
                    UpdateTerminalPanelStatus();
                };
                terminal.LayoutTransform = new ScaleTransform(_appScale, _appScale);
                ShowWorkspaceContent(terminal, "terminal");
                // Demo mode paints a scripted session instead of starting a shell, so a screenshot
                // never carries the real machine's name, paths, or history.
                if (Services.DemoData.Enabled)
                {
                    terminal.ShowScript(DemoTerminalTranscript());
                    _terminalControl.Focus();
                    UpdateTerminalPanelStatus();
                    return;
                }
                EnsureBundledModules();
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (command != null) terminal.Start(command, home);
                else
                {
                    string shell = ResolveTerminalShell();
                    terminal.Start(QuoteArgument(shell) + " -NoLogo" + PromptArgs(shellCommand), home);
                }
            }
            else ShowWorkspaceContent(_terminalControl, "terminal");
            _terminalControl.Focus();
            UpdateTerminalPanelStatus();
        }

        /// <summary>
        /// A continuous ping, tinted as it streams. ping.exe emits no color of its own, so the
        /// lines are matched and wrapped here: a reply green, a loss red, everything else (the
        /// banner, the blank lines, the summary) left alone. TTL= and the timeout words are what
        /// the two states have in common across the ping builds we see; an unmatched line simply
        /// prints as it always did rather than being colored wrongly.
        ///
        /// Single quotes throughout: this whole pipeline rides inside the double-quoted -Command
        /// that also carries the prompt.
        /// </summary>
        private static string PingCommand(string ip) =>
            "ping.exe -t " + ip + " | ForEach-Object { $e = [char]27; " +
            "if ($_ -match 'TTL=') { $e + '[32m' + $_ + $e + '[0m' } " +
            "elseif ($_ -match 'timed out|unreachable|transmit failed|General failure') " +
            "{ $e + '[31m' + $_ + $e + '[0m' } else { $_ } }";

        private bool InterruptTerminalPing()
        {
            if (_workspaceView != "terminal" || !_terminalIsPing || _terminalExited) return false;
            _terminalControl?.Send("\u0003");
            return true;
        }

        private static string ResolveTerminalShell()
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), "pwsh.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                foreach (string version in new[] { "7", "7-preview" })
                {
                    string candidate = Path.Combine(Environment.GetFolderPath(folder), "PowerShell", version, "pwsh.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        private void RefreshTerminalPanelTheme()
        {
            _terminalControl?.RefreshTheme();
            UpdateTerminalPanelStatus();
        }

        private void UpdateTerminalPanelStatus()
        {
            if (_workspaceView != "terminal") return;
            string text = _terminalStatusKey == null ? _terminalTitle ?? Loc("Str_Workspace_Terminal")
                : string.Format(Loc(_terminalStatusKey), _terminalStatusArgument);
            // A continuous ping never ends on its own, so the way out belongs on screen while it
            // runs rather than only in the shortcuts overlay.
            if (_terminalIsPing && !_terminalExited) text += "   " + Loc("Str_Workspace_EscStop");
            StatusText.Text = text;
        }

        private void DisposeTerminalPanel()
        {
            _terminalPanelDisposed = true;
            _terminalControl?.Dispose();
        }
    }
}
