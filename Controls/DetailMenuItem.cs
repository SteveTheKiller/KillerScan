using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace KillerScan.Controls
{
    /// <summary>
    /// A menu item that shows a detail, such as a saved scan's target or an adapter's address,
    /// in the right-hand column where shortcuts usually go. Screen readers would announce that
    /// column as the item's keyboard shortcut, so this item reports no shortcut and reads the
    /// detail as help text instead.
    /// </summary>
    internal sealed class DetailMenuItem : MenuItem
    {
        public DetailMenuItem()
        {
            // The app's MenuItem style is implicit, which does not reach a subclass by itself.
            SetResourceReference(StyleProperty, typeof(MenuItem));
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DetailPeer(this);

        private sealed class DetailPeer(DetailMenuItem owner) : MenuItemAutomationPeer(owner)
        {
            protected override string GetAcceleratorKeyCore() => string.Empty;

            protected override string GetHelpTextCore()
            {
                string help = base.GetHelpTextCore();
                return help.Length > 0 ? help : ((MenuItem)Owner).InputGestureText ?? string.Empty;
            }
        }
    }
}
