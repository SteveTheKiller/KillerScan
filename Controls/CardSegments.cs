using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace KillerScan.Controls
{
    /// <summary>
    /// A card segment row for a menu that is built once and kept: the kit's
    /// CardBuilder.SegmentRow fixes its checked segment when it is built, while these follow a
    /// setting that shortcuts change too. Labels and tooltips are resource references, so a
    /// language switch reaches a menu that is never rebuilt. Set each segment's IsChecked from
    /// the setting whenever it changes (Tag carries the choice).
    /// </summary>
    internal static class CardSegments
    {
        internal sealed record Choice(object Tag, string Key, string? Gesture, Action Run);

        internal static MenuItem Row(string labelKey, IEnumerable<Choice> choices, out List<RadioButton> segments)
        {
            segments = [];
            var track = new StackPanel { Orientation = Orientation.Horizontal };
            string group = "CardSegments" + Guid.NewGuid().ToString("N");
            foreach (var choice in choices)
            {
                var segment = new RadioButton { GroupName = group, Tag = choice.Tag };
                segment.SetResourceReference(FrameworkElement.StyleProperty, "CardSegment");
                segment.SetResourceReference(ContentControl.ContentProperty, choice.Key);
                segment.SetResourceReference(AutomationProperties.NameProperty, choice.Key);
                ToolTipService.SetShowOnDisabled(segment, true);
                var tip = new TextBlock();
                var name = new Run();
                name.SetResourceReference(Run.TextProperty, choice.Key);
                tip.Inlines.Add(name);
                if (!string.IsNullOrEmpty(choice.Gesture))
                {
                    AutomationProperties.SetAcceleratorKey(segment, choice.Gesture);
                    tip.Inlines.Add(new Run(" (" + choice.Gesture + ")"));
                }
                segment.ToolTip = tip;
                var run = choice.Run;
                segment.Click += (_, _) => { KillerFamily.Controls.CardBuilder.Close(segment); run(); };
                segments.Add(segment);
                track.Children.Add(segment);
            }
            var border = new Border { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2), Child = track };
            border.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            border.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var row = KillerFamily.Controls.CardBuilder.Labeled("", border);
            if (row.Header is Panel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
                label.SetResourceReference(TextBlock.TextProperty, labelKey);
            row.SetResourceReference(AutomationProperties.NameProperty, labelKey);
            return row;
        }

        /// <summary>A card title slot whose text follows the language.</summary>
        internal static MenuItem Header(string titleKey)
        {
            var header = KillerFamily.Controls.CardBuilder.Header("");
            if (header.Header is Panel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock title)
                title.SetResourceReference(TextBlock.TextProperty, titleKey);
            header.SetResourceReference(AutomationProperties.NameProperty, titleKey);
            return header;
        }
    }
}
