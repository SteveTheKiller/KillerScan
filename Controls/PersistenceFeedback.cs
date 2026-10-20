using System.Windows;
using KillerScan.Services;

namespace KillerScan.Controls
{
    internal static class PersistenceFeedback
    {
        internal static bool Check(bool saved, Func<bool> retry, Func<string?> error)
        {
            while (!saved)
            {
                string format = (string)Application.Current.FindResource("Str_SaveFailed");
                string message = string.Format(format, error());
                var owner = Application.Current.MainWindow;
                var answer = owner == null
                    ? MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    : MessageBox.Show(owner, message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return false;
                saved = retry();
            }
            return true;
        }
    }
}
