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

        void IShellServices.SetStatus(string text) => StatusText.Text = text;
    }
}
