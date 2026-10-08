using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerScan.Controls
{
    public partial class NetworkToolsWindow
    {
        private void WatchCard_Focus(object sender, MouseButtonEventArgs e) => ((UIElement)sender).Focus();

        public bool HandleShortcut(Key key, ModifierKeys modifiers)
        {
            if (Keyboard.FocusedElement is TextBox) return false;
            if (Keyboard.FocusedElement is not FrameworkElement element || element.DataContext is not WatchCard) return false;
            Action? action = (key, modifiers) switch
            {
                (Key.C, ModifierKeys.Control) => () => CardCopy_Click(element, new RoutedEventArgs()),
                (Key.F3, ModifierKeys.None) => () => CardDiagnose_Click(element, new RoutedEventArgs()),
                (Key.R, ModifierKeys.Control) => () => CardReset_Click(element, new RoutedEventArgs()),
                (Key.Delete, ModifierKeys.None) => () => CardRemove_Click(element, new RoutedEventArgs()),
                _ => null
            };
            if (action == null) return false;
            action(); return true;
        }
    }
}
