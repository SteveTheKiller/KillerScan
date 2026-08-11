using System.Windows;
using System.Windows.Input;

namespace KillerScan.Controls
{
    /// <summary>
    /// A themed one-line text prompt on the ConfirmDialog chrome. Enter confirms, Esc cancels, and
    /// the field is preselected so a remembered value can be replaced by typing over it.
    /// </summary>
    public partial class InputDialog : Window
    {
        public bool Confirmed { get; private set; }

        /// <summary>What was typed, trimmed. Meaningful only when <see cref="Confirmed"/> is true;
        /// an empty string is a legitimate answer, not a cancel.</summary>
        public string Value => ValueBox.Text.Trim();

        /// <param name="heading">The question, in one line.</param>
        /// <param name="detail">Optional explanation under it; pass an empty string for none.</param>
        /// <param name="fieldLabel">Small label above the box.</param>
        /// <param name="initial">Pre-filled value, selected so typing replaces it.</param>
        /// <param name="confirmText">Confirm button caption.</param>
        /// <param name="cancelText">Cancel button caption.</param>
        public InputDialog(string heading, string detail, string fieldLabel,
                           string initial, string confirmText, string cancelText)
        {
            InitializeComponent();

            HeadingText.Text = heading;
            DetailText.Text = detail;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            FieldLabel.Text = fieldLabel;
            ValueBox.Text = initial ?? string.Empty;
            OkButton.Content = confirmText;
            CancelButton.Content = cancelText;

            Loaded += (_, _) =>
            {
                Anim.FadeIn(RootBorder);
                // Focus and select from Loaded, not the constructor: the box has no presentation
                // source until the window is shown, so an earlier Focus is silently dropped.
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }

        // Enter confirms and Esc cancels from inside the box, which is where focus starts. Without
        // this the dialog would need a mouse for its only field.
        private void ValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)       { OK_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel_Click(this, new RoutedEventArgs()); e.Handled = true; }
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
