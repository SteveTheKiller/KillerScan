using System.Windows;
using System.Windows.Input;

namespace KillerScan
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        public ConfirmDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
        }

        // Configurable variant for reusing the themed dialog beyond the install prompt
        // (e.g. the self-update confirmation). detail may contain newlines for multiple lines.
        public ConfirmDialog(string heading, string detail, string confirmText, string cancelText = "Cancel")
            : this()
        {
            HeadingText.Text = heading;
            HeadingText.Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(detail) ? 0 : 12);
            DetailText.Text = detail;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            OkButton.Content = confirmText;
            CancelButton.Content = cancelText;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();
    }
}
