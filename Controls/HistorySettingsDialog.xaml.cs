using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class HistorySettingsDialog : Window
    {
        private readonly HistoryRetention _initial;
        private readonly Func<HistoryRetention, bool> _apply;
        private string Loc(string key) => TryFindResource(key) as string ?? key;

        internal HistorySettingsDialog(HistoryRetention initial, Func<HistoryRetention, bool> apply)
        {
            InitializeComponent();
            // Reuse the family radio template without rasterizing its label through an effect.
            foreach (var selection in new[] { TimeMode, CountMode, AllMode })
            {
                selection.ApplyTemplate();
                if (selection.Template.FindName("label", selection) is ContentPresenter label) label.Effect = null;
            }
            _initial = initial;
            _apply = apply;
            CountBox.Text = initial.Count.ToString(CultureInfo.CurrentCulture);
            DaysBox.Text = initial.Days.ToString(CultureInfo.CurrentCulture);
            TimeMode.IsChecked = initial.Mode == HistoryRetentionMode.Time;
            CountMode.IsChecked = initial.Mode == HistoryRetentionMode.Count;
            AllMode.IsChecked = initial.Mode == HistoryRetentionMode.SaveAll;
            var frames = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/ks-icon.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            void RefreshIcon() => TitleIcon.Source = frames.OrderBy(frame =>
                Math.Abs(frame.PixelWidth - TitleIcon.ActualWidth * VisualTreeHelper.GetDpi(TitleIcon).DpiScaleX)).First();
            TitleIcon.Loaded += (_, _) => RefreshIcon();
            TitleIcon.SizeChanged += (_, _) => RefreshIcon();
            TaskbarIdentity.Track(this);
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
        }

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (DaysBox == null || CountBox == null) return;
            DaysBox.IsEnabled = TimeMode.IsChecked == true;
            CountBox.IsEnabled = CountMode.IsChecked == true;
        }

        internal HistoryRetention? ReadPolicy()
        {
            var mode = TimeMode.IsChecked == true ? HistoryRetentionMode.Time :
                CountMode.IsChecked == true ? HistoryRetentionMode.Count : HistoryRetentionMode.SaveAll;
            int count = _initial.Count, days = _initial.Days;
            if (mode == HistoryRetentionMode.Count && (!int.TryParse(CountBox.Text, out count) || count < 1)) return null;
            if (mode == HistoryRetentionMode.Time && (!int.TryParse(DaysBox.Text, out days) || days is < 1 or > 36500)) return null;
            return new(mode, count, days);
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            var policy = ReadPolicy();
            if (policy == null)
            {
                ValidationText.Text = Loc("Str_History_InvalidRetention");
                ValidationText.Visibility = Visibility.Visible;
                return;
            }
            try { if (_apply(policy)) DialogResult = true; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ValidationText.Text = string.Format(Loc("Str_History_SaveFailed"), ex.Message);
                ValidationText.Visibility = Visibility.Visible;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
