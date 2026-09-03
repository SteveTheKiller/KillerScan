using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void BuildWorkspaceNavigation()
        {
            _workspaceNavigation.Margin = new Thickness(8, 6, 8, 6);
            AddViewButton("scan", "Str_View_Scan", "Ctrl+T", () => ShowScanView("devices"));
            AddViewButton("topology", "Str_View_Topology", "F9", () => ShowScanView("topology"));
            AddViewButton("watch", "Str_View_KeepAlive", "F2", () => Watch_Click(this, new RoutedEventArgs()));
            AddViewButton("terminal", "Str_Workspace_Terminal", "Ctrl+Shift+T", NewTerminalView);
        }

        private void AddViewButton(string view, string key, string shortcut, Action action)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 6, 2), Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12, MinHeight = 30, Tag = view
            };
            button.SetResourceReference(ContentControl.ContentProperty, key);
            var tip = new TextBlock();
            var caption = new System.Windows.Documents.Run();
            caption.SetResourceReference(System.Windows.Documents.Run.TextProperty, key);
            tip.Inlines.Add(caption);
            tip.Inlines.Add(" (" + shortcut + ")");
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            _viewButtons.Add(view, button);
            _workspaceNavigation.Children.Add(button);
        }

        private void UpdateWorkspaceNavigation()
        {
            foreach (var pair in _viewButtons)
            {
                bool selected = pair.Key == _workspaceView;
                pair.Value.SetResourceReference(StyleProperty, selected ? "PrimaryButton" : "OutlineButton");
            }
        }

        private void NewTerminalView() => NewTerminal();
    }
}
