using System.Windows;
using System.Windows.Media;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private HistoryWorkspace? _historyWorkspace;
        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            _historyWorkspace ??= new HistoryWorkspace { LayoutTransform = new ScaleTransform(_appScale, _appScale) };
            _historyWorkspace.RefreshHistory();
            ShowWorkspaceContent(_historyWorkspace, "history");
        }
    }
}
