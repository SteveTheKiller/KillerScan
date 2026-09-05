using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Profiles share the history sidebar rather than a rail flyout, so both saved lists
        /// live in the same place and the panel cross-fades between them.
        /// </summary>
        private void ProfilesButton_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            ShowSidebarSection("profiles");
        }

        private void RefreshProfilesList()
        {
            // Read the selection before the source is replaced, which clears it, then put it back
            // only if that profile is still in the list.
            object? previous = ProfilesList.SelectedItem;
            var items = ScanProfiles.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ProfilesList.ItemsSource = items;
            if (previous is ScanProfile selected && items.Contains(selected)) ProfilesList.SelectedItem = selected;
            SaveProfileButton.IsEnabled = ActiveScan != null;
        }

        /// <summary>The profile a row's context menu was opened on.</summary>
        private static ScanProfile? ProfileFor(object sender) =>
            (sender as FrameworkElement)?.DataContext as ScanProfile;

        private void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            var scan = ActiveScan;
            if (scan == null) return;
            var dialog = new InputDialog(
                Loc("Str_Profiles_Save"), scan.Targets,
                Loc("Str_Rename_Label"), string.Empty,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.Confirmed || string.IsNullOrWhiteSpace(dialog.Value)) return;
            ScanProfiles.Save(new ScanProfile { Name = dialog.Value, Target = scan.Targets.Trim() });
            RefreshProfilesList();
        }

        private void ProfileLoad_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileFor(sender) is not { } profile) return;
            var scan = ActiveScan;
            if (scan == null || scan.IsScanning) NewScan(profile.Target);
            else scan.Targets = profile.Target;
        }

        private void ProfileRun_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileFor(sender) is not { } profile) return;
            var scan = ActiveScan;
            if (scan == null || scan.IsScanning) scan = NewScan(profile.Target);
            scan.RunProfile(profile);
        }

        private void ProfileDeep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || ProfileFor(sender) is not { } profile) return;
            profile.DeepScanAfter = item.IsChecked;
            ScanProfiles.Update();
        }

        private void ProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileFor(sender) is not { } profile) return;
            ScanProfiles.Delete(profile);
            RefreshProfilesList();
        }
    }
}
