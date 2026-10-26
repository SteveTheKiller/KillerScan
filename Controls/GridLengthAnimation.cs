using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace KillerScan.Controls
{
    /// <summary>
    /// Animates a <see cref="GridLength"/> in pixels. WPF ships no such timeline, and the family's
    /// sliding panels (sidebar collapse and expand, history and detail panes) are all column-width
    /// tweens. Set From/To in device-independent pixels and an optional EasingFunction.
    ///
    /// EasingFunction is a dependency property, not a plain CLR property: WPF freezes and clones a
    /// timeline when it starts, and a clone copies dependency properties only, so a CLR easing
    /// would silently drop and the slide would run linear.
    /// </summary>
    internal sealed class GridLengthAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

        public double From
        {
            get => (double)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public double To
        {
            get => (double)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
            AnimationClock clock)
        {
            double progress = clock.CurrentProgress ?? 0;
            if (EasingFunction != null) progress = EasingFunction.Ease(progress);
            return new GridLength(From + (To - From) * progress, GridUnitType.Pixel);
        }
    }
}
