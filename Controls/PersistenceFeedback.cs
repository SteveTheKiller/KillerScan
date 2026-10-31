using System.Windows;
using KillerScan.Services;

namespace KillerScan.Controls
{
    internal static class PersistenceFeedback
    {
        /// <summary>Answers the retry prompt. Tests answer it without a dialog.</summary>
        internal static Func<string, bool>? Answer { get; set; }

        internal static bool Check(bool saved, Func<bool> retry, Func<string?> error)
        {
            while (!saved)
            {
                string format = (string)Application.Current.FindResource("Str_SaveFailed");
                string message = string.Format(format, error());
                if (!Ask(message)) return false;
                saved = retry();
            }
            return true;
        }

        /// <summary>Makes a write that is safe to repeat, offering to repeat it while it fails.</summary>
        internal static bool Attempt(Func<bool> write, Func<string?> error) => Check(write(), write, error);

        private static bool Ask(string message)
        {
            if (Answer != null) return Answer(message);
            var owner = Application.Current.MainWindow;
            var answer = owner == null
                ? MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }
    }
}
