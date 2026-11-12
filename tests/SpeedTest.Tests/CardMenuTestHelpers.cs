using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

/// <summary>
/// The card menus (the kit's CardMenu) hold some actions as quick buttons and chips inside
/// CardSlot rows instead of as MenuItems. These helpers let a test find an action by name
/// wherever it lives: a row's Tag, or a button's CommandParameter.
/// </summary>
internal static class CardMenuTestHelpers
{
    /// <summary>True for a CardSlot: a MenuItem that only hosts a card header, strip or chip row.</summary>
    internal static bool IsCardSlot(MenuItem item) =>
        ReferenceEquals(item.Style, Application.Current.TryFindResource("CardSlot"));

    /// <summary>Every quick button and chip inside the menu's card slots.</summary>
    internal static IEnumerable<ButtonBase> CardButtons(ItemsControl menu) => CardButtons(menu.Items.Cast<object>());

    /// <summary>Every quick button and chip inside the card slots among these rows.</summary>
    internal static IEnumerable<ButtonBase> CardButtons(IEnumerable<object> rows)
    {
        foreach (var slot in rows.OfType<MenuItem>().Where(IsCardSlot))
        {
            var pending = new Stack<object>();
            if (slot.Header != null) pending.Push(slot.Header);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current is ButtonBase button) { yield return button; continue; }
                if (current is DependencyObject d)
                    foreach (var child in LogicalTreeHelper.GetChildren(d)) pending.Push(child);
            }
        }
    }

    /// <summary>The menu's actions by name: ordinary rows by Tag, card buttons by CommandParameter.</summary>
    internal static Dictionary<string, FrameworkElement> Actions(ItemsControl menu)
    {
        var actions = new Dictionary<string, FrameworkElement>();
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => !IsCardSlot(item) && item.Tag is string))
            actions[(string)item.Tag] = item;
        foreach (var button in CardButtons(menu).Where(button => button.CommandParameter is string))
            actions[(string)button.CommandParameter] = button;
        return actions;
    }

    /// <summary>Runs an action the way a click does, for a row or a card button.</summary>
    internal static void Click(FrameworkElement action) =>
        action.RaiseEvent(new RoutedEventArgs(action is MenuItem ? MenuItem.ClickEvent : ButtonBase.ClickEvent, action));
}
