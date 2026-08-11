using System.Windows;
using System.Windows.Input;

namespace KillerScan.Controls
{
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        /// <summary>True when the user ticked "Install for all users". Always false for the
        /// reusable variant below, which hides the checkbox.</summary>
        public bool AllUsers => AllUsersCheck.IsChecked == true;

        public ConfirmDialog()
        {
            InitializeComponent();

            // Already installed machine-wide (by an admin, winget, choco or an RMM)? Then the
            // only sane move is to update that copy in place: tick the box and lock it. Offering
            // a per-user install here would either leave two copies on the PC, or let one user
            // delete the app for every other account on a shared machine.
            if (App.MachineInstallExists())
            {
                AllUsersCheck.IsChecked = true;
                AllUsersCheck.IsEnabled = false;
                AllUsersNote.Text = Loc("Str_Install_AlreadyAllUsers");
            }

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
            // The all-users choice belongs to the install prompt only, not to reused dialogs
            // like the self-update confirmation.
            AllUsersCheck.Visibility = Visibility.Collapsed;
            AllUsersNote.Visibility = Visibility.Collapsed;
        }

        // Ticking the box changes where the app lands, so the heading follows. The note under it
        // stays put either way - it describes what ticking would do, which is worth reading
        // before you tick, and a note that appears on tick makes the dialog jump.
        private void AllUsers_Changed(object sender, RoutedEventArgs e)
            => HeadingText.Text = Loc(AllUsers ? "Str_Install_HeadAll" : "Str_Install_HeadUser");

        private static string Loc(string key) =>
            Application.Current.TryFindResource(key) as string ?? key;

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
