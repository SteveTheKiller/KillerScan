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
        private string? _terminalTitle;
        private string? _terminalStatusKey;
        private object? _terminalStatusArgument;

        private void ToggleTerminalPanel() => NewTerminal();

        private void NewTerminal(string? command = null, string? title = null)
        {
            if (_terminalPanelDisposed) return;
            if (_terminalControl == null || command != null || _terminalExited)
            {
                if (_terminalControl != null)
                {
                    _workspaceBody.Children.Remove(_terminalControl);
                    _terminalControl.Dispose();
                }
                var terminal = _terminalControl = new TerminalControl();
                _terminalTitle = title;
                _terminalExited = false;
                _terminalStatusKey = null;
                _terminalStatusArgument = null;
                terminal.GotKeyboardFocus += (_, _) => UpdateTerminalPanelStatus();
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
                string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                terminal.Start(command ?? QuoteArgument(shell) + " -NoLogo",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            else ShowWorkspaceContent(_terminalControl, "terminal");
            _terminalControl.Focus();
        }

        private void RefreshTerminalPanelTheme()
        {
            _terminalControl?.RefreshTheme();
            UpdateTerminalPanelStatus();
        }

        private void UpdateTerminalPanelStatus()
        {
            if (_workspaceView != "terminal") return;
            StatusText.Text = _terminalStatusKey == null ? _terminalTitle ?? Loc("Str_Workspace_Terminal")
                : string.Format(Loc(_terminalStatusKey), _terminalStatusArgument);
        }

        private void DisposeTerminalPanel()
        {
            _terminalPanelDisposed = true;
            _terminalControl?.Dispose();
        }
    }
}
