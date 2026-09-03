using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerScan.Terminal;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private readonly Grid _terminalPanel = new();
        private readonly TextBlock _terminalPanelTitle = new();
        private readonly ContentControl _terminalPanelBody = new();
        private GridSplitter? _terminalPanelGrip;
        private TerminalControl? _terminalControl;
        private bool _terminalPanelOpen;
        private bool _terminalPanelDisposed;
        private bool _terminalExited;
        private double _terminalPanelWidth = 340;
        private int _terminalSlideVersion;
        private string? _terminalTitle;
        private string? _terminalStatusKey;
        private object? _terminalStatusArgument;

        private void InitializeTerminalPanel()
        {
            if (double.TryParse(App.GetSetting("TerminalPanelWidth"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double width) && width >= 160 && width <= 4000)
                _terminalPanelWidth = width;

            TerminalHost.ClipToBounds = true;
            TerminalHost.Visibility = Visibility.Collapsed;
            TerminalColumn.Width = new GridLength(0);
            _terminalPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _terminalPanel.RowDefinitions.Add(new RowDefinition());
            _terminalPanel.SetResourceReference(Panel.BackgroundProperty, "PaneBrush");

            var header = new Grid();
            header.SetResourceReference(Panel.BackgroundProperty, "PaneBrush");
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            header.Children.Add(grain);
            var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 3, 3, 3) };
            var close = new Button { Content = "\u00D7", Width = 24, Height = 24, Padding = new Thickness(0) };
            close.SetResourceReference(StyleProperty, "TabCloseBtn");
            var closeTip = new StackPanel { Orientation = Orientation.Horizontal };
            var closeLabel = new TextBlock();
            closeLabel.SetResourceReference(TextBlock.TextProperty, "Str_Workspace_Close");
            closeTip.Children.Add(closeLabel);
            closeTip.Children.Add(new TextBlock { Text = " (Ctrl+Shift+T)" });
            close.ToolTip = closeTip;
            close.Click += (_, _) => SetTerminalPanelOpen(false);
            DockPanel.SetDock(close, Dock.Right);
            heading.Children.Add(close);
            _terminalPanelTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            _terminalPanelTitle.FontSize = 12;
            _terminalPanelTitle.VerticalAlignment = VerticalAlignment.Center;
            _terminalPanelTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            heading.Children.Add(_terminalPanelTitle);
            header.Children.Add(heading);
            _terminalPanel.Children.Add(header);
            Grid.SetRow(_terminalPanelBody, 1);
            _terminalPanel.Children.Add(_terminalPanelBody);
            TerminalHost.Children.Add(_terminalPanel);

            _terminalPanelGrip = new GridSplitter
            {
                Width = 7, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Visibility = Visibility.Collapsed, ShowsPreview = true,
                Cursor = Cursors.SizeWE
            };
            _terminalPanelGrip.Background = Brushes.Transparent;
            var gripSurface = new FrameworkElementFactory(typeof(Grid));
            gripSurface.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var gripLine = new FrameworkElementFactory(typeof(Border));
            gripLine.SetValue(WidthProperty, 1.0);
            gripLine.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            gripLine.SetResourceReference(Border.BackgroundProperty, "PaneBorderBrush");
            gripSurface.AppendChild(gripLine);
            _terminalPanelGrip.Template = new ControlTemplate(typeof(GridSplitter)) { VisualTree = gripSurface };
            _terminalPanelGrip.DragCompleted += TerminalPanelGrip_DragCompleted;
            Grid.SetColumn(_terminalPanelGrip, 1);
            TerminalLayout.Children.Add(_terminalPanelGrip);
            TerminalLayout.SizeChanged += TerminalLayout_SizeChanged;
            RefreshTerminalPanelTheme();
        }

        private void ToggleTerminalPanel()
        {
            if (_terminalPanelOpen) SetTerminalPanelOpen(false);
            else NewTerminal();
        }

        private void NewTerminal(string? command = null, string? title = null, bool beside = false)
        {
            if (_terminalPanelDisposed) return;
            if (_terminalControl == null || command != null || _terminalExited)
            {
                _terminalControl?.Dispose();
                var terminal = _terminalControl = new TerminalControl();
                _terminalPanelBody.Content = terminal;
                _terminalTitle = title;
                _terminalExited = false;
                _terminalStatusKey = null;
                _terminalStatusArgument = null;
                terminal.GotKeyboardFocus += (_, _) => UpdateTerminalPanelStatus();
                terminal.Exited += code =>
                {
                    if (_terminalControl != terminal) return;
                    _terminalExited = true;
                    _terminalStatusKey = "Str_Workspace_Exited";
                    _terminalStatusArgument = code;
                    UpdateTerminalPanelStatus();
                };
                terminal.StartFailed += error =>
                {
                    if (_terminalControl != terminal) return;
                    _terminalExited = true;
                    _terminalStatusKey = "Str_Workspace_StartFailed";
                    _terminalStatusArgument = error.Message;
                    UpdateTerminalPanelStatus();
                };
                ApplyTerminalPanelScale(_appScale);
                RefreshTerminalPanelTheme();
                SetTerminalPanelOpen(true);
                string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                terminal.Start(command ?? "\"" + shell + "\" -NoLogo",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            else SetTerminalPanelOpen(true);
        }

        private void SetTerminalPanelOpen(bool open)
        {
            if (_terminalPanelDisposed) return;
            if (_terminalPanelOpen == open)
            {
                if (open) _terminalControl?.Focus();
                return;
            }
            _terminalPanelOpen = open;
            double width = TerminalPanelOpenWidth();
            double from = TerminalColumn.ActualWidth;
            int version = ++_terminalSlideVersion;
            TerminalColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            TerminalColumn.MinWidth = 0;
            TerminalColumn.MaxWidth = double.PositiveInfinity;
            TerminalHost.Visibility = Visibility.Visible;
            _terminalPanel.Width = open ? width : Math.Max(width, from);
            _terminalPanel.HorizontalAlignment = HorizontalAlignment.Left;
            if (_terminalPanelGrip != null)
            {
                _terminalPanelGrip.Visibility = Visibility.Visible;
                _terminalPanelGrip.IsEnabled = false;
            }
            var animation = new TerminalColumnAnimation
            {
                From = from, To = open ? width : 0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn }
            };
            animation.Completed += (_, _) =>
            {
                if (version != _terminalSlideVersion || _terminalPanelDisposed) return;
                SettleTerminalPanel();
                if (open) _terminalControl?.Focus();
            };
            TerminalColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);
            if (!open)
            {
                if (_terminalControl?.IsKeyboardFocusWithin == true) ActiveScan?.FocusTargets();
                ActivatePane(CurrentPane);
            }
        }

        private double TerminalPanelOpenWidth()
        {
            double available = TerminalLayout.ActualWidth;
            if (available <= 0) available = Math.Max(1, ActualWidth - 40);
            double maximum = Math.Max(1, available * 0.45);
            return Math.Max(Math.Min(200, maximum), Math.Min(_terminalPanelWidth, maximum));
        }

        private void SettleTerminalPanel()
        {
            TerminalColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            _terminalPanel.ClearValue(WidthProperty);
            _terminalPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            double maximum = TerminalPanelOpenWidth();
            TerminalColumn.MinWidth = _terminalPanelOpen ? Math.Min(200, maximum) : 0;
            TerminalColumn.MaxWidth = _terminalPanelOpen ? Math.Max(1, TerminalLayout.ActualWidth * 0.45) : double.PositiveInfinity;
            TerminalColumn.Width = new GridLength(_terminalPanelOpen ? maximum : 0);
            TerminalHost.Visibility = _terminalPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            if (_terminalPanelGrip != null)
            {
                _terminalPanelGrip.Visibility = _terminalPanelOpen ? Visibility.Visible : Visibility.Collapsed;
                _terminalPanelGrip.IsEnabled = true;
            }
        }

        private void TerminalLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_terminalPanelOpen || !e.WidthChanged || _terminalPanelGrip?.IsEnabled == false) return;
            SettleTerminalPanel();
        }

        private void TerminalPanelGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!_terminalPanelOpen || e.Canceled) return;
            _terminalPanelWidth = TerminalColumn.Width.IsAbsolute
                ? TerminalColumn.Width.Value : TerminalColumn.ActualWidth;
            TerminalLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SettleTerminalPanel();
            App.SetSetting("TerminalPanelWidth", _terminalPanelWidth.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void ApplyTerminalPanelScale(double scale)
        {
            if (_terminalControl != null) _terminalControl.LayoutTransform = new ScaleTransform(scale, scale);
            _terminalPanelTitle.FontSize = 12 * scale;
        }

        private void RefreshTerminalPanelTheme()
        {
            _terminalPanelTitle.Text = _terminalTitle ?? Loc("Str_Workspace_Terminal");
            _terminalControl?.RefreshTheme();
            UpdateTerminalPanelStatus();
        }

        private void UpdateTerminalPanelStatus()
        {
            if (!_terminalPanelOpen || _terminalControl?.IsKeyboardFocusWithin != true) return;
            StatusText.Text = _terminalStatusKey == null ? _terminalPanelTitle.Text
                : string.Format(Loc(_terminalStatusKey), _terminalStatusArgument);
        }

        private void DisposeTerminalPanel()
        {
            _terminalPanelDisposed = true;
            _terminalSlideVersion++;
            TerminalColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            TerminalLayout.SizeChanged -= TerminalLayout_SizeChanged;
            _terminalControl?.Dispose();
            _terminalControl = null;
            _terminalPanelBody.Content = null;
        }

        private sealed class TerminalColumnAnimation : AnimationTimeline
        {
            public override Type TargetPropertyType => typeof(GridLength);
            protected override Freezable CreateInstanceCore() => new TerminalColumnAnimation();
            public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
                nameof(From), typeof(double), typeof(TerminalColumnAnimation));
            public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
                nameof(To), typeof(double), typeof(TerminalColumnAnimation));
            public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
            public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
            public IEasingFunction? EasingFunction { get; set; }
            public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
            {
                double progress = clock.CurrentProgress ?? 0;
                if (EasingFunction != null) progress = EasingFunction.Ease(progress);
                return new GridLength(From + (To - From) * progress);
            }
        }
    }
}
