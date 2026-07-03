using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace KillerScan
{
    // Shared fade used across the whole app so every surface - the main window,
    // dialogs and flyouts - fades in with the same timing and easing. KillerScan
    // previously only faded the main window and About overlay (at different
    // durations); the theme flyout, language menu and install dialog appeared
    // instantly. This keeps them all in step.
    internal static class Anim
    {
        // Standard fade duration in milliseconds, shared by all surfaces.
        public const int FadeMs = 150;

        // Fades an element's opacity from 0 to 1 over FadeMs with an ease-out curve.
        public static void FadeIn(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }
}
