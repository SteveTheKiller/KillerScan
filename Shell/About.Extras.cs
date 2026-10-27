using System.Windows;
using KillerScan.Features.About;

namespace KillerScan.Shell
{
    /// <summary>
    /// APP-OWNED: KillerScan's About rows beside the shared core (Shell/About.cs): the vendor
    /// database line, its status and refresh link, and the frame dimmer behind both overlays.
    /// Kept out of the kit drift check (KillerHarness family/kit-owned.json).
    /// </summary>
    public partial class MainWindow : IAboutVendorDb
    {
        string IAboutVendorDb.DbInfo        { set => AboutDbBlock.Text = value; }
        string IAboutVendorDb.DbStatus      { set => AboutDbStatus.Text = value; }
        bool IAboutVendorDb.DbUpdateEnabled { set => AboutDbUpdateLink.IsEnabled = value; }

        private void AboutDbUpdate_Click(object sender, RoutedEventArgs e) => _about.UpdateVendorDb();

        // The dim backdrop behind the About and shortcuts overlays stays up while either shows.
        partial void OnOverlayShowing(UIElement overlay) => FrameDimmer.Visibility = Visibility.Visible;

        partial void OnOverlayHidden(UIElement overlay)
        {
            if (ShortcutsOverlay.Visibility != Visibility.Visible && AboutOverlay.Visibility != Visibility.Visible)
                FrameDimmer.Visibility = Visibility.Collapsed;
        }
    }
}
