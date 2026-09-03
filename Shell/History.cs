using System.Windows;
using System.Windows.Media;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            foreach (var pane in WorkspacePanes())
            {
                var existing = pane.Tabs.FirstOrDefault(tab => tab.Content is HistoryWorkspace);
                if (existing?.Content is HistoryWorkspace history)
                {
                    history.RefreshHistory();
                    SelectWorkspaceTab(pane, existing);
                    return;
                }
            }
            AddWorkspaceTab(new WorkspaceTab
            {
                Content = new HistoryWorkspace { LayoutTransform = new ScaleTransform(_appScale, _appScale) },
                TitleKey = "Str_History_Title"
            }, false);
        }
    }
}
