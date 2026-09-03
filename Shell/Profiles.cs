using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {

        private void ProfilesButton_Click(object sender, RoutedEventArgs e)
        {
            BuildProfilesMenu();
            ProfilesMenu.PlacementTarget = sender as FrameworkElement ??
                (FixedProfilesButton.IsVisible ? FixedProfilesButton : ProfilesButton);
            ProfilesMenu.IsOpen = true;
        }

        private void BuildProfilesMenu()
        {
            ProfilesMenu.Items.Clear();
            var save = new MenuItem { Header = Loc("Str_Profiles_Save"), IsEnabled = ActiveScan != null };
            save.Click += SaveProfile_Click;
            ProfilesMenu.Items.Add(save);
            if (ScanProfiles.Items.Count == 0) return;
            ProfilesMenu.Items.Add(new Separator());

            foreach (var profile in ScanProfiles.Items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var parent = new MenuItem { Header = profile.Name, ToolTip = profile.Target };
                var run = new MenuItem { Header = Loc("Str_Profiles_Run"), Tag = profile };
                run.Click += RunProfile_Click;
                var load = new MenuItem { Header = Loc("Str_Profiles_Load"), Tag = profile };
                load.Click += LoadProfile_Click;
                var deep = new MenuItem
                {
                    Header = Loc("Str_Profiles_Deep"), Tag = profile,
                    IsCheckable = true, IsChecked = profile.DeepScanAfter
                };
                deep.Click += ToggleProfileDeep_Click;
                var delete = new MenuItem { Header = Loc("Str_Profiles_Delete"), Tag = profile };
                delete.Click += DeleteProfile_Click;
                parent.Items.Add(run);
                parent.Items.Add(load);
                parent.Items.Add(deep);
                parent.Items.Add(new Separator());
                parent.Items.Add(delete);
                ProfilesMenu.Items.Add(parent);
            }
        }

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
        }

        private void LoadProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ScanProfile profile }) return;
            var scan = ActiveScan;
            if (scan == null || scan.IsScanning) scan = NewScan(profile.Target);
            else scan.Targets = profile.Target;
        }

        private void RunProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ScanProfile profile }) return;
            var scan = ActiveScan;
            if (scan == null || scan.IsScanning) scan = NewScan(profile.Target);
            scan.RunProfile(profile);
        }

        private void ToggleProfileDeep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ScanProfile profile } item) return;
            profile.DeepScanAfter = item.IsChecked;
            ScanProfiles.Update();
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: ScanProfile profile }) ScanProfiles.Delete(profile);
        }
    }
}
