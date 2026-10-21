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
                    (host, padding) => ((InputContentHost)host).CompactPadding ?? padding));
        }
    }
}
