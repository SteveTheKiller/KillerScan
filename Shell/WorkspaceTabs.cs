using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void BuildWorkspaceNavigation()
        {
            _workspaceNavigation.Margin = new Thickness(8, 2, 8, 2);
            AddViewButton("scan", "Str_View_Scan", "Ctrl+T", () => ShowScanView("devices"));
            AddViewButton("topology", "Str_View_Topology", "F9", () => ShowScanView("topology"));
            AddViewButton("watch", "Str_View_KeepAlive", "F2", () => Watch_Click(this, new RoutedEventArgs()));
            AddViewButton("terminal", "Str_Workspace_Terminal", "Ctrl+Shift+T", NewTerminalView);
        }

        private void AddViewButton(string view, string key, string shortcut, Action action)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(6, 4, 6, 4),
                Height = 52, VerticalAlignment = VerticalAlignment.Center
            };
            button.SetResourceReference(StyleProperty, "ViewToolbarButton");
            string glyph = view switch
            {
                "scan" => "\uE8FD",
                "topology" => "\uE968",
                "watch" => "\uE9D9",
                _ => "\uE756"
            };
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center
            });
            var label = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            label.SetResourceReference(TextBlock.TextProperty, key);
            content.Children.Add(label);
            button.Content = content;
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
                pair.Value.Tag = selected ? "on" : null;
            }
        }

        private void NewTerminalView() => NewTerminal();
    }
}
