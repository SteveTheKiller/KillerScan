using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace KillerScan.Controls
{
    /// <summary>Opens menu surfaces immediately while retaining their existing closing fade.</summary>
    public static class MenuAnimation
    {
        private static readonly MethodInfo? Hookup = typeof(ContextMenu).GetMethod(
            "HookupParentPopup", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ParentPopup = typeof(ContextMenu).GetField(
            "_parentPopup", BindingFlags.Instance | BindingFlags.NonPublic);

        public static readonly DependencyProperty OpenImmediatelyProperty =
            DependencyProperty.RegisterAttached("OpenImmediately", typeof(bool), typeof(MenuAnimation),
                new PropertyMetadata(false, OnOpenImmediatelyChanged));

        private static readonly DependencyProperty CloseAnimationProperty =
            DependencyProperty.RegisterAttached("CloseAnimation", typeof(PopupAnimation), typeof(MenuAnimation),
                new PropertyMetadata(PopupAnimation.None));

        private static readonly DependencyProperty IsPopupOpenProperty =
            DependencyProperty.RegisterAttached("IsPopupOpen", typeof(bool), typeof(MenuAnimation),
                new PropertyMetadata(false, OnPopupOpenChanged));

        public static bool GetOpenImmediately(DependencyObject element) =>
            (bool)element.GetValue(OpenImmediatelyProperty);

        public static void SetOpenImmediately(DependencyObject element, bool value) =>
            element.SetValue(OpenImmediatelyProperty, value);

        private static void OnOpenImmediatelyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is Popup templatePopup && !templatePopup.IsInitialized && (bool)e.NewValue)
            {
                templatePopup.Initialized += OnPopupInitialized;
                templatePopup.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                    new System.Action(() => ConfigureTemplatePopup(templatePopup)));
                return;
            }
            var popup = element as Popup;
            if (element is ContextMenu menu)
            {
                popup = ParentPopup?.GetValue(menu) as Popup;
                if (popup == null && (bool)e.NewValue)
                {
                    // Creating the host during style evaluation would reenter that evaluation.
                    menu.Initialized += OnMenuInitialized;
                    menu.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                        new System.Action(() => ConfigureMenu(menu)));
                    return;
                }
            }
            if (popup == null) return;
            ConfigurePopup(popup, (bool)e.NewValue);
        }

        private static void OnMenuInitialized(object? sender, System.EventArgs e)
        {
            if (sender is ContextMenu menu) ConfigureMenu(menu);
        }

        private static void ConfigureMenu(ContextMenu menu)
        {
            menu.Initialized -= OnMenuInitialized;
            if (!GetOpenImmediately(menu)) return;
            if (ParentPopup?.GetValue(menu) == null)
                Hookup?.Invoke(menu, null);
            if (ParentPopup?.GetValue(menu) is Popup popup)
                ConfigurePopup(popup, true);
        }

        private static void OnPopupInitialized(object? sender, System.EventArgs e)
        {
            if (sender is Popup popup) ConfigureTemplatePopup(popup);
        }

        private static void ConfigureTemplatePopup(Popup popup)
        {
            popup.Initialized -= OnPopupInitialized;
            if (GetOpenImmediately(popup)) ConfigurePopup(popup, true);
        }

        private static void ConfigurePopup(Popup popup, bool enabled)
        {
            if (enabled && BindingOperations.IsDataBound(popup, IsPopupOpenProperty)) return;

            if (enabled)
            {
                popup.SetValue(CloseAnimationProperty, popup.PopupAnimation);
                popup.PopupAnimation = PopupAnimation.None;
                popup.Opened += OnPopupOpened;
                BindingOperations.SetBinding(popup, IsPopupOpenProperty,
                    new Binding(nameof(Popup.IsOpen)) { Source = popup });
            }
            else
            {
                popup.Opened -= OnPopupOpened;
                BindingOperations.ClearBinding(popup, IsPopupOpenProperty);
                popup.PopupAnimation = (PopupAnimation)popup.GetValue(CloseAnimationProperty);
                popup.ClearValue(CloseAnimationProperty);
            }
        }

        private static void OnPopupOpened(object? sender, System.EventArgs e)
        {
            if (sender is Popup popup &&
                (PopupAnimation)popup.GetValue(CloseAnimationProperty) == PopupAnimation.Fade)
                popup.PopupAnimation = PopupAnimation.Fade;
        }

        private static void OnPopupOpenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            // WPF has already started the closing fade. Reset the next opening now,
            // including when the popup is reopened before that fade finishes.
            if (!(bool)e.NewValue) ((Popup)element).PopupAnimation = PopupAnimation.None;
        }
    }
}
