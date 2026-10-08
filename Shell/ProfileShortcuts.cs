using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private bool HandleProfileShortcut(KeyEventArgs e, ModifierKeys modifiers)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.FocusedElement is KillerScan.Terminal.TerminalControl || Keyboard.FocusedElement is TextBox) return false;
            if (key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && ActiveScan != null)
            { SaveProfile_Click(this, new RoutedEventArgs()); e.Handled = true; return true; }
            if (!ProfilesList.IsKeyboardFocusWithin || ProfilesList.SelectedItem is not ScanProfile profile) return false;
            var source = new MenuItem { DataContext = profile };
            if (key == Key.Apps && modifiers == ModifierKeys.None || key == Key.F10 && modifiers == ModifierKeys.Shift)
            {
                var item = ProfilesList.ItemContainerGenerator.ContainerFromItem(profile) as DependencyObject;
                var target = FindProfileMenu(item);
                if (target?.ContextMenu == null) return false;
                target.ContextMenu.PlacementTarget = target;
                target.ContextMenu.Placement = PlacementMode.Bottom;
                target.ContextMenu.IsOpen = true;
                e.Handled = true;
                return true;
            }
            Action? action = (key, modifiers) switch
            {
                (Key.Enter, ModifierKeys.None) => () => ProfileRun_Click(source, new RoutedEventArgs()),
                (Key.L, ModifierKeys.Control) => () => ProfileLoad_Click(source, new RoutedEventArgs()),
                (Key.Delete, ModifierKeys.None) => () => ProfileDelete_Click(source, new RoutedEventArgs()),
                (Key.D, ModifierKeys.Control | ModifierKeys.Alt) => () =>
                { source.IsChecked = !profile.DeepScanAfter; ProfileDeep_Click(source, new RoutedEventArgs()); },
                _ => null
            };
            if (action == null) return false;
            action(); e.Handled = true; return true;
        }

        private void ProfileList_RightClick(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(ProfilesList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) { ProfilesList.SelectedItem = null; return; }
            ProfilesList.SelectedItem = item.DataContext;
            item.Focus();
        }

        private static FrameworkElement? FindProfileMenu(DependencyObject? parent)
        {
            if (parent is FrameworkElement element && element.ContextMenu != null) return element;
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var found = FindProfileMenu(VisualTreeHelper.GetChild(parent, i));
                if (found != null) return found;
            }
            return null;
        }
    }
}
