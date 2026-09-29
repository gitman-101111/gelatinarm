using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Ui
{
    public static class ControllerInputHelper
    {
        /// <summary>
        ///     Keeps left/right inside a horizontal row: on its first tile Left, and on its last tile
        ///     Right, do nothing. Otherwise the controller's direction finds the nearest tile in the
        ///     row above or below, which reads as the row "wrapping". Up and down still leave the
        ///     row. Set by MediaRowGridViewStyle.
        /// </summary>
        public static readonly DependencyProperty KeepsSidewaysFocusProperty =
            DependencyProperty.RegisterAttached("KeepsSidewaysFocus", typeof(bool), typeof(ControllerInputHelper),
                new PropertyMetadata(false, OnKeepsSidewaysFocusChanged));

        public static bool GetKeepsSidewaysFocus(DependencyObject element)
        {
            return (bool)element.GetValue(KeepsSidewaysFocusProperty);
        }

        public static void SetKeepsSidewaysFocus(DependencyObject element, bool value)
        {
            element.SetValue(KeepsSidewaysFocusProperty, value);
        }

        private static ILogger RowLogger => ServiceLocator.GetService<ILoggerFactory>()?.CreateLogger("MediaRow");

        private static void OnKeepsSidewaysFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ListViewBase row)
            {
                // Preview: the row's own key handling and the directional focus move come after it
                row.PreviewKeyDown -= KeepSidewaysFocus;
                if ((bool)e.NewValue)
                {
                    row.PreviewKeyDown += KeepSidewaysFocus;
                    RowLogger?.LogDebug("A media row keeps left/right focus at its ends ({ItemCount} items)", row.Items.Count);
                }
            }
        }

        private static void KeepSidewaysFocus(object sender, KeyRoutedEventArgs e)
        {
            var left = e.Key == VirtualKey.Left || e.Key == VirtualKey.GamepadDPadLeft ||
                       e.Key == VirtualKey.GamepadLeftThumbstickLeft;
            var right = e.Key == VirtualKey.Right || e.Key == VirtualKey.GamepadDPadRight ||
                        e.Key == VirtualKey.GamepadLeftThumbstickRight;
            if ((!left && !right) || sender is not ListViewBase row)
            {
                return;
            }

            // The focused element is the tile's container or something inside it
            var index = -1;
            for (var element = FocusManager.GetFocusedElement() as DependencyObject;
                 element != null && element != row && index < 0;
                 element = VisualTreeHelper.GetParent(element))
            {
                index = row.IndexFromContainer(element);
            }

            if (index >= 0 && ((left && index == 0) || (right && index == row.Items.Count - 1)))
            {
                e.Handled = true;
                RowLogger?.LogDebug("{Key} stopped at tile {Index} of {ItemCount}", e.Key, index + 1, row.Items.Count);
            }
        }

        // Server addresses, user names and search terms: the on-screen keyboard's spelling and
        // prediction only get in the way. The keyboard itself opens when the user presses A.
        private static void ConfigureTextBoxForController(TextBox textBox)
        {
            textBox.IsSpellCheckEnabled = false;
            textBox.IsTextPredictionEnabled = false;
        }

        private static void SetInitialFocus(Control control, ILogger logger)
        {
            control.Focus(FocusState.Programmatic);
            logger.LogDebug("Set initial focus to {ControlGetType}", control.GetType().Name);
        }

        // For every page, from BasePage
        public static void ConfigurePageForController(Page page, Func<Control> initialFocusControl, ILogger logger)
        {
            // Arrow-key (and D-pad) focus moves for everything on the page: descendants left at
            // Auto inherit it
            page.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            page.UseSystemFocusVisuals = true;

            page.Loaded += async (sender, e) =>
            {
                try
                {
                    ConfigureTextBoxesRecursively(page);

                    var target = initialFocusControl?.Invoke() ?? FindFirstFocusableControl(page);
                    if (target != null)
                    {
                        // A control takes focus once the page has laid out, unless the page has put focus
                        // on a control of its own by then (a tile on return); the loading overlay's capture
                        // button is not the page's choice
                        await UiHelper.WhenIdleAsync(page.Dispatcher);
                        if (FocusManager.GetFocusedElement() is DependencyObject focused && FindParent<Page>(focused) == page &&
                            FindParent<LoadingOverlay>(focused) == null)
                        {
                            logger.LogDebug("Initial focus left on {ControlGetType}", focused.GetType().Name);
                            return;
                        }

                        SetInitialFocus(target, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to configure page controls on load");
                }
            };
        }

        // The platform's search: a control under a collapsed ancestor, or disabled, is not focusable. An
        // items control or scroll viewer that is itself a tab stop is stepped into: its first item is the
        // control a user wants, and focus on the container shows nothing
        public static Control FindFirstFocusableControl(DependencyObject container)
        {
            var element = FocusManager.FindFirstFocusableElement(container) as Control;
            while ((element is ItemsControl || element is ScrollViewer) && FocusManager.FindFirstFocusableElement(element) is Control inner)
            {
                element = inner;
            }

            return element;
        }

        private static void ConfigureTextBoxesRecursively(DependencyObject container)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(container);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(container, i);
                if (child is TextBox textBox)
                {
                    ConfigureTextBoxForController(textBox);
                }

                ConfigureTextBoxesRecursively(child);
            }
        }

        // A track button's Click: open its context menu
        public static void ShowContextFlyout(object sender)
        {
            if (sender is Button { ContextFlyout: MenuFlyout flyout } button)
            {
                flyout.ShowAt(button);
            }
        }

        /// <summary>
        ///     Opens the context flyout for whatever currently has focus, for pages whose
        ///     rows carry a MenuFlyout. Handles the gamepad Menu button, and A/Enter/Space
        ///     when the focus is on a ListViewItem.
        /// </summary>
        public static void HandleContextMenuKey(KeyRoutedEventArgs e, ILogger logger)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            Button button;
            if (e.Key == VirtualKey.GamepadMenu)
            {
                // Menu opens the focused track's flyout or nothing; it never falls through. Focus may
                // sit on the row, on its button, or on something inside the button.
                e.Handled = true;
                button = focused == null ? null : focused as Button ?? FindChild<Button>(focused) ?? FindParent<Button>(focused);
            }
            else if ((e.Key == VirtualKey.GamepadA || e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space) && focused is ListViewItem row)
            {
                // A on the row itself; on the button, its Click opens the flyout
                button = FindChild<Button>(row);
            }
            else
            {
                return;
            }

            if (button?.ContextFlyout is MenuFlyout flyout)
            {
                logger.LogDebug("Showing context menu for {Key}", e.Key);
                flyout.ShowAt(button);
                e.Handled = true;
            }
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && parent is not T)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as T;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild)
                {
                    return typedChild;
                }

                var result = FindChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
