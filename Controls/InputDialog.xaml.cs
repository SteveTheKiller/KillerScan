using System;
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
        private (string Key, string Glyph, string NameKey)[] _icons = [];

        /// <summary>The icon picked, when <see cref="UseIcons"/> showed the icon button.</summary>
        public string SelectedIcon { get; private set; } = string.Empty;

        /// <summary>Shows the icon button after the field, starting on <paramref name="selected"/>.</summary>
        public void UseIcons((string Key, string Glyph, string NameKey)[] icons, string selected)
        {
            _icons = icons;
            IconButton.Visibility = Visibility.Visible;
            SetIcon(Array.Exists(icons, icon => icon.Key == selected) ? selected : icons[0].Key);
        }

        private void SetIcon(string key)
        {
            SelectedIcon = key;
            var icon = Array.Find(_icons, item => item.Key == key);
            IconButton.Content = icon.Glyph;
            string name = TryFindResource(icon.NameKey) as string ?? key;
            IconButton.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(IconButton, name);
        }

        private void IconButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = IconButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            foreach (var icon in _icons)
            {
                var captured = icon.Key;
                var item = new System.Windows.Controls.MenuItem
                {
                    IsCheckable = true, IsChecked = icon.Key == SelectedIcon,
                    Icon = MenuGlyph.Create(char.ConvertToUtf32(icon.Glyph, 0))
                };
                item.SetResourceReference(System.Windows.Controls.HeaderedItemsControl.HeaderProperty, icon.NameKey);
                item.Click += (_, _) => SetIcon(captured);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        public InputDialog(string heading, string detail, string fieldLabel,
                           string initial, string confirmText, string cancelText)
        {
            InitializeComponent();
            if (!DialogChrome.SetTitleIcon(TitleIcon)) TitleIcon.Visibility = Visibility.Collapsed;

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
            if (e.Key == Key.Enter && !ValueBox.AcceptsReturn) { OK_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        /// <summary>A taller box that keeps line breaks, for free text such as Location notes.</summary>
        /// <summary>Shows a read-only labelled line under the fields.</summary>
        public void UseInfo(string label, string text)
        {
            InfoLabel.Text = label;
            InfoText.Text = text;
            InfoLabel.Visibility = InfoText.Visibility = Visibility.Visible;
        }

        /// <summary>The notes box's text, when <see cref="UseNotes"/> showed it.</summary>
        public string Notes => NotesBox.Text;

        /// <summary>Shows the notes box under the field. Enter in it is a new line; Ctrl+Enter saves.</summary>
        public void UseNotes(string label, string notes)
        {
            Width = 380;   // room for notes; a lone name field stays at 300
            NotesLabel.Text = label;
            NotesLabel.Visibility = Visibility.Visible;
            NotesBox.Text = notes ?? string.Empty;
            NotesBox.Visibility = Visibility.Visible;
            NotesBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                OK_Click(this, new RoutedEventArgs());
                e.Handled = true;
            };
        }

        internal void UseMultipleLines()
        {
            ValueBox.AcceptsReturn = true;
            ValueBox.TextWrapping = TextWrapping.Wrap;
            ValueBox.MinHeight = 140;
            ValueBox.MaxHeight = 320;
            ValueBox.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            ValueBox.VerticalContentAlignment = VerticalAlignment.Top;
            Width = 440;
            // Enter is a new line here, so Ctrl+Enter confirms. The box would take Enter first.
            ValueBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    OK_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            };
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
