using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class PickerSelectionTests
{
    internal static void Run()
    {
        foreach (string rowStyle in new[] { "PickerPlaceRow", "PickerRow" })
        {
            var content = new StackPanel { Style = (Style)Application.Current.FindResource("PickerRowContent") };
            content.Children.Add(new TextBlock { Text = "Demo folder" });
            var item = new ListBoxItem { Content = content, Style = (Style)Application.Current.FindResource(rowStyle) };
            var list = new ListBox();
            list.Items.Add(item);
            var window = new Window { Content = list, Width = 320, Height = 180, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                list.SelectedItem = item;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (!item.IsSelected) throw new InvalidOperationException("Picker selection must be exercised.");
                if (content.Effect != null) throw new InvalidOperationException(rowStyle + " selection must not add a shadow to its content.");
                list.SelectedItem = null;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (content.Effect != null) throw new InvalidOperationException(rowStyle + " unselected content must remain shadow-free.");
            }
            finally { window.Close(); }
        }
        Console.WriteLine("PASS: selected and unselected picker places and file rows are shadow-free (4 checks).");
    }
}
