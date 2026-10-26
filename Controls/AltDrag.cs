using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace KillerScan.Controls
{
    /// <summary>
    /// Linux-style Alt+drag: holding Alt and pressing the left button anywhere in a window moves
    /// it, as if the press had landed on the title bar. One class handler covers every window the
    /// app opens, dialogs included, so nothing is wired per window. Call <see cref="Register"/>
    /// once at startup.
    /// </summary>
    /// <remarks>
    /// The move is the system's own (DragMove, the same SC_MOVE loop a title-bar drag starts), so
    /// snapping and multi-monitor behaviour are Windows'. A maximized window restores first and
    /// keeps the cursor at the same relative spot across its width, as Windows' title-bar drag
    /// does. Letting go of Alt after a drag would normally open the window menu (SC_KEYMENU) or
    /// put WPF into access-key mode; both are swallowed for that one release.
    /// </remarks>
    public static class AltDrag
    {
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_KEYMENU = 0xF100;

        private static bool _registered;
        private static bool _swallowAltRelease;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyUpEvent,
                new KeyEventHandler(OnPreviewKeyUp), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.RemoveHook(SwallowKeyMenu);
                source.AddHook(SwallowKeyMenu);
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Window window || Keyboard.Modifiers != ModifierKeys.Alt || e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            _swallowAltRelease = true;
            if (window.WindowState == WindowState.Maximized)
            {
                // Restore under the cursor at the same fraction of the width, then carry on moving.
                Point inside = e.GetPosition(window);
                double fraction = window.ActualWidth > 0 ? inside.X / window.ActualWidth : 0.5;
                Point screen = window.PointToScreen(inside);
                var source = PresentationSource.FromVisual(window);
                if (source?.CompositionTarget != null) screen = source.CompositionTarget.TransformFromDevice.Transform(screen);
                window.WindowState = WindowState.Normal;
                window.Left = screen.X - window.RestoreBounds.Width * fraction;
                window.Top = screen.Y - inside.Y;
            }
            try { window.DragMove(); }
            catch (InvalidOperationException) { }   // the button was already up
        }

        private static void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_swallowAltRelease) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is not (Key.LeftAlt or Key.RightAlt)) return;
            e.Handled = true;   // keeps WPF out of access-key mode
            _swallowAltRelease = false;
        }

        private static IntPtr SwallowKeyMenu(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // The window menu Alt's release asks for, right after an Alt+drag.
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero && _swallowAltRelease)
            {
                _swallowAltRelease = false;
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
