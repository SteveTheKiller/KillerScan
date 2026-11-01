using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerFamily.Accessibility;
using KillerScan.Services;

internal static class ContrastTransitionTests
{
    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckNotifications(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "Contrast notification checks finish within fifteen seconds.");
        if (failure != null) throw new InvalidOperationException("Contrast transition regression failed.", failure);
        Console.WriteLine("PASS: contrast transition scheduling (1 selected).");
        return Task.CompletedTask;
    }

    private static void CheckNotifications()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HighContrastResources.Initialize();
        FilmGrain.Initialize();
        Drain();

        // Raise the real WPF event at the OS boundary without changing any Windows setting.
        var notification = typeof(SystemParameters).GetField("StaticPropertyChanged",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        int posted = 0;
        DispatcherHookEventHandler observe = (_, _) => posted++;
        app.Dispatcher.Hooks.OperationPosted += observe;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            ((PropertyChangedEventHandler)notification.GetValue(null)!).Invoke(null,
                new PropertyChangedEventArgs("WorkArea"));
        int queued = posted;
        Drain();
        watch.Stop();
        app.Dispatcher.Hooks.OperationPosted -= observe;
        Console.WriteLine("Contrast baseline: 100 unrelated notifications queued " + queued
            + " operations; drain " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms.");
        Require(queued == 0, "Unrelated Windows metrics must not queue contrast resource updates; actual " + queued + ".");

        posted = 0;
        app.Dispatcher.Hooks.OperationPosted += observe;
        watch.Restart();
        for (int i = 0; i < 100; i++)
            ((PropertyChangedEventHandler)notification.GetValue(null)!).Invoke(null,
                new PropertyChangedEventArgs("HighContrast"));
        queued = posted;
        Drain();
        watch.Stop();
        app.Dispatcher.Hooks.OperationPosted -= observe;
        Console.WriteLine("Contrast burst: 100 relevant notifications queued " + queued
            + " operations; drain " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms.");
        Require(queued <= 2, "A contrast burst must queue at most one update per resource subscriber; actual " + queued + ".");

        CheckBorderCommand(notification);
        CheckSharedBorderCommand(notification);
    }

    private static void CheckBorderCommand(FieldInfo notification)
    {
        var app = Application.Current;
        var palette = new SolidColorBrush(Color.FromRgb(20, 50, 80));
        app.Resources["WindowEdgeBrush"] = palette;
        app.Resources["AppBorderBrush"] = Brushes.Red;
        HighContrastResources.Refresh(true, Brushes.Black, Brushes.White, Brushes.Yellow, Brushes.Black);
        var window = new Window { ShowInTaskbar = false, Width = 1, Height = 1 };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            int border = -1;
            int genericWrites = 0;
            KillerScan.Controls.DwmChrome.SetThemeBorder(window, (_, _) => genericWrites++);
            KillerScan.Shell.MainWindow.ApplyThemeBorder(window, (target, color) =>
            {
                Require(target == hwnd, "The border command targets the owned hidden HWND.");
                border = color;
            });
            Require(border == 0xFFFFFF, "The hidden HWND starts with the contrast white border command.");
            HighContrastResources.Refresh(false, Brushes.Black, Brushes.White, Brushes.Yellow, Brushes.Black);
            ((PropertyChangedEventHandler)notification.GetValue(null)!).Invoke(null,
                new PropertyChangedEventArgs("HighContrast"));
            Drain();
            Require(ReferenceEquals(app.Resources["WindowEdgeBrush"], palette), "Contrast off restores the original border resource.");
            Require(border == 0x503214,
                "Contrast off must restore the HWND border command without changing the app theme; actual " + border.ToString("X6") + ".");
            Require(genericWrites == 1, "A custom border painter replaces the earlier generic painter for that window.");
            Console.WriteLine("PASS: contrast off publishes border command 503214 for the hidden HWND.");
        }
        finally { window.Close(); }
    }

    private static void CheckSharedBorderCommand(FieldInfo notification)
    {
        var app = Application.Current;
        var palette = new SolidColorBrush(Color.FromRgb(90, 60, 30));
        app.Resources["AppBorderBrush"] = palette;
        HighContrastResources.Refresh(true, Brushes.Black, Brushes.White, Brushes.Yellow, Brushes.Black);
        var window = new Window { ShowInTaskbar = false, Width = 1, Height = 1 };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            int border = -1;
            KillerScan.Controls.DwmChrome.SetThemeBorder(window, (target, color) =>
            {
                Require(target == hwnd, "The shared border command targets the owned hidden HWND.");
                border = color;
            });
            Require(border == 0xFFFFFF, "The shared border starts with the contrast white command.");
            HighContrastResources.Refresh(false, Brushes.Black, Brushes.White, Brushes.Yellow, Brushes.Black);
            ((PropertyChangedEventHandler)notification.GetValue(null)!).Invoke(null,
                new PropertyChangedEventArgs("HighContrast"));
            Drain();
            Require(ReferenceEquals(app.Resources["AppBorderBrush"], palette), "Contrast off restores the shared border resource.");
            Require(border == 0x1E3C5A,
                "Contrast off must restore the shared HWND border command; actual " + border.ToString("X6") + ".");
            foreach (int message in new[] { 0x031A, 0x0015 })
            {
                border = -1;
                SendMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
                Drain();
                Require(border == 0x1E3C5A, "The owned HWND's Windows theme/color message refreshes its border command.");
            }
            Console.WriteLine("PASS: shared border restores command 1E3C5A for the hidden HWND.");
        }
        finally { window.Close(); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
