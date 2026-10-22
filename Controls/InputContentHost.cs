using System;
using System.Windows;
using System.Windows.Controls;

namespace KillerFamily.Controls
{
    public sealed class InputContentHost : ScrollViewer
    {
        public static readonly DependencyProperty CompactPaddingProperty = DependencyProperty.Register(
            nameof(CompactPadding), typeof(Thickness?), typeof(InputContentHost),
            new FrameworkPropertyMetadata(null, (host, args) => host.CoerceValue(PaddingProperty)));

        public Thickness? CompactPadding
        {
            get => (Thickness?)GetValue(CompactPaddingProperty);
            set => SetValue(CompactPaddingProperty, value);
        }

        static InputContentHost()
        {
            // TextBox assigns its padding directly to the content host. Coercion keeps the
            // compact theme inset effective without changing the field's original padding.
            PaddingProperty.OverrideMetadata(typeof(InputContentHost),
                new FrameworkPropertyMetadata(default(Thickness), null,
                    (host, padding) => Compact(((InputContentHost)host).CompactPadding, (Thickness)padding)));
        }

        /// <summary>The compact inset, plus any room the field reserves on one side beyond the other
        /// (the file dialog's path box keeps clear of its recent-locations arrow that way).</summary>
        private static object Compact(Thickness? compact, Thickness field)
        {
            if (compact is not Thickness inset) return field;
            return new Thickness(inset.Left + Math.Max(0, field.Left - field.Right), inset.Top,
                inset.Right + Math.Max(0, field.Right - field.Left), inset.Bottom);
        }
    }
}
