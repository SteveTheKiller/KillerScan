using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class StartupFadeTests
{
    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var start = typeof(KillerScan.App).Assembly.GetType("KillerScan.Controls.StartupFade", true)!
                    .GetMethod("In", BindingFlags.Static | BindingFlags.NonPublic)!;
                foreach (bool stalled in new[] { false, true })
                {
                    var content = new Border { Width = 40, Height = 40, Background = Brushes.Orange, Opacity = 0 };
                    start.Invoke(null, new object[] { content });
                    if (stalled && SystemParameters.ClientAreaAnimation)
                        content.BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(0, 0, new Duration(TimeSpan.FromHours(1))));
                    var frame = new DispatcherFrame();
                    var deadline = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
                    deadline.Start();
                    Dispatcher.PushFrame(frame);
                    Require(content.Opacity == 1, "Startup must finish visible, including a stalled animation.");
                    Require(!content.HasAnimatedProperties, "Startup must release the opacity animation.");
                    content.Measure(new Size(40, 40));
                    content.Arrange(new Rect(0, 0, 40, 40));
                    var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var pixels = new byte[40 * 40 * 4];
                    bitmap.CopyPixels(pixels, 40 * 4, 0);
                    Require(pixels[(20 * 40 + 20) * 4 + 3] == 255, "Startup content must render opaque pixels.");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(10)), "Startup fade check must finish within ten seconds.");
        if (failure != null) throw new InvalidOperationException("Startup fade regression check failed.", failure);
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
