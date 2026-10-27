using System.Windows;
using KillerScan.Features;

// The three seams every feature shares. Implemented once here; each feature's own interface extends
// IShellServices, so all of them are satisfied by these.
//
// Loc goes through Services/LocaleManager, which returns the key when it is missing, so a gap shows
// up as itself rather than as blank UI.
namespace KillerScan.Shell
{
    public partial class MainWindow : IShellServices
    {
        Window IShellServices.Window => this;

        string IShellServices.Loc(string key) => Services.LocaleManager.Loc(key);

        void IShellServices.SetStatus(string text)
        {
            bool handled = false;
            SetStatusText(text, ref handled);
            if (!handled) StatusText.Text = text;
        }

        /// <summary>App hook: show a feature's status line the app's own way (a held message that
        /// routine updates must not replace, a crash-report breadcrumb). Set
        /// <paramref name="handled"/> to skip the kit's plain StatusText write.</summary>
        partial void SetStatusText(string text, ref bool handled);
    }
}
