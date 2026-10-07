using System;
using System.Windows;
using System.Windows.Threading;

namespace KillerScan.Controls
{
    internal static class StartupFade
    {
        internal static void In(UIElement content)
        {
            content.Opacity = 1;
            if (!SystemParameters.ClientAreaAnimation) return;

            Anim.FadeIn(content);
            // Finish on the dispatcher even if the composition animation never advances.
            // The main window must not remain invisible in a restricted desktop.
            var finish = new DispatcherTimer(DispatcherPriority.Normal, content.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Anim.FadeMs + 100)
            };
            finish.Tick += Complete;
            finish.Start();

            void Complete(object? sender, EventArgs e)
            {
                finish.Stop();
                finish.Tick -= Complete;
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Opacity = 1;
            }
        }
    }
}
