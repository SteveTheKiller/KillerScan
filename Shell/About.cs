using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using KillerScan.Controls;
using KillerScan.Features;
using KillerScan.Services;

namespace KillerScan.Shell
{
    /// <summary>
    /// The About overlay's window half: the dim backdrop, the fade, the click handling, and the
    /// IAboutHost implementation that maps the controller's values onto the named XAML elements.
    /// Everything the card SAYS is decided in Features/About/AboutController.cs.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private readonly AboutController _about = null!;

        private void ShowAboutOverlay() => _about.Show();

        // ---- IAboutHost ----

        string IAboutHost.Version     { set => AboutVersionBlock.Text = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text = value; }
        string IAboutHost.Alias       { set => AboutAkaRun.Text = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text = value; }
        string IAboutHost.UpdateText  { set => AboutUpdateText.Text = value; }
        string IAboutHost.DbInfo      { set => AboutDbBlock.Text = value; }
        string IAboutHost.DbStatus    { set => AboutDbStatus.Text = value; }

        bool IAboutHost.AliasVisible
        {
            set => AboutAkaBlock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateVisible
        {
            set => AboutUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateEnabled   { set => AboutUpdateButton.IsEnabled = value; }
        bool IAboutHost.DbUpdateEnabled { set => AboutDbUpdateLink.IsEnabled = value; }

        void IAboutHost.ShowCard() => FadeOverlayIn(AboutOverlay);

        // ---- Overlay fade (shared with the shortcuts overlay, Shortcuts.cs) ----

        private static void FadeOverlayIn(UIElement o)
        {
            o.Visibility = Visibility.Visible;
            Anim.FadeIn(o);
        }

        private static void FadeOverlayOut(UIElement o)
        {
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Anim.FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            a.Completed += (_, _) => o.Visibility = Visibility.Collapsed;
            o.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ---- Handlers ----

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) => _about.OpenReleaseNotes();

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => _about.Update();

        private void AboutDbUpdate_Click(object sender, RoutedEventArgs e) => _about.UpdateVendorDb();

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            WebLink.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
