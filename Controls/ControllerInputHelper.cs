using System;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Microsoft.Extensions.Logging;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Gelatinarm.Controls
{
    /// <summary>
    ///     Helper class for consistent controller input handling across all pages
    /// </summary>
    public static class ControllerInputHelper
    {
        /// <summary>
        ///     Configures a TextBox for optimal controller input
        /// </summary>
        /// <param name="textBox">The TextBox to configure</param>
        /// <param name="inputScope">The input scope (default: Default)</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void ConfigureTextBoxForController(TextBox textBox,
            InputScopeNameValue inputScope = InputScopeNameValue.Default, ILogger logger = null)
        {
            if (textBox == null)
            {
                return;
            }

            try
            {
                textBox.InputScope = new InputScope();
                textBox.InputScope.Names.Add(new InputScopeName { NameValue = inputScope });
                textBox.IsSpellCheckEnabled = false;
                textBox.IsTextPredictionEnabled = false;
                textBox.AcceptsReturn = false;

                // Let Xbox system handle keyboard display when user presses A button
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to configure TextBox for controller input");
            }
        }

        /// <summary>
        ///     Configures a PasswordBox for optimal controller input
        /// </summary>
        /// <param name="passwordBox">The PasswordBox to configure</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void ConfigurePasswordBoxForController(PasswordBox passwordBox, ILogger logger = null)
        {
            if (passwordBox == null)
            {
                return;
            }

            try
            {
                // Let Xbox system handle keyboard display when user presses A button
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to configure PasswordBox for controller input");
            }
        }

        /// <summary>
        ///     Sets initial focus on a control with improved focus management
        /// </summary>
        /// <param name="control">The control to focus</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void SetInitialFocus(Control control, ILogger logger = null)
        {
            if (control == null)
            {
                return;
            }

            try
            {
                var focusResult = control.Focus(FocusState.Programmatic);
                logger?.LogDebug($"Set initial focus to {control.GetType().Name}");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, $"Could not set initial focus to {control.GetType().Name}");

                // Try alternative focus method as last resort
                try
                {
                    control.Focus(FocusState.Keyboard);
                }
                catch (Exception fallbackEx)
                {
                    logger?.LogWarning(fallbackEx, "Fallback focus method also failed");
                }
            }
        }

        /// <summary>
        ///     Shows the virtual keyboard for controller input
        /// </summary>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void ShowVirtualKeyboard(ILogger logger = null)
        {
            try
            {
                var inputPane = InputPane.GetForCurrentView();
                inputPane.TryShow();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not show virtual keyboard");
            }
        }

        /// <summary>
        ///     Hides the virtual keyboard
        /// </summary>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void HideVirtualKeyboard(ILogger logger = null)
        {
            try
            {
                var inputPane = InputPane.GetForCurrentView();
                inputPane.TryHide();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not hide virtual keyboard");
            }
        }

        /// <summary>
        ///     Configures a page for optimal controller navigation
        /// </summary>
        /// <param name="page">The page to configure</param>
        /// <param name="initialFocusControl">The control to receive initial focus</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void ConfigurePageForController(Page page, Control initialFocusControl = null,
            ILogger logger = null)
        {
            if (page == null)
            {
                return;
            }

            try
            {
                page.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
                page.UseSystemFocusVisuals = true;

                page.Loaded += async (sender, e) =>
                {
                    try
                    {
                        ConfigureControlsRecursively(page, logger);

                        if (initialFocusControl != null)
                        {
                            await Task.Delay(UIConstants.UI_SETTLE_DELAY_MS);
                            SetInitialFocus(initialFocusControl, logger);
                        }
                        else
                        {
                            var firstFocusable = FindFirstFocusableControl(page);
                            if (firstFocusable != null)
                            {
                                await Task.Delay(UIConstants.UI_SETTLE_DELAY_MS);
                                SetInitialFocus(firstFocusable, logger);
                            }
                        }

                        await Task.Delay(UIConstants.UI_SETTLE_DELAY_MS);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to configure page controls on load");
                    }
                };
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to configure page for controller input");
                throw;
            }
        }

        /// <summary>
        ///     Enables XY focus navigation for controller input
        /// </summary>
        /// <param name="element">The element to configure</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void EnableXYFocusNavigation(FrameworkElement element, ILogger logger = null)
        {
            if (element == null)
            {
                return;
            }

            try
            {
                // Enable XY focus navigation mode for controller input
                element.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

                // Set the page to use directional navigation
                if (element is Page page)
                {
                    page.UseSystemFocusVisuals = true;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to enable XY focus navigation");
            }
        }

        /// <summary>
        ///     Finds the first focusable control in a container
        /// </summary>
        /// <param name="container">The container to search</param>
        /// <returns>The first focusable control, or null if none found</returns>
        public static Control FindFirstFocusableControl(DependencyObject container)
        {
            if (container == null)
            {
                return null;
            }

            try
            {
                var childCount = VisualTreeHelper.GetChildrenCount(container);
                for (var i = 0; i < childCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(container, i);

                    if (child is Control control && control.IsTabStop && control.Visibility == Visibility.Visible)
                    {
                        return control;
                    }

                    // Recursively search children
                    var result = FindFirstFocusableControl(child);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
            catch (Exception)
            {
                // Log error but don't throw
            }

            return null;
        }

        /// <summary>
        ///     Recursively configures all input controls in a container for controller input
        /// </summary>
        /// <param name="container">The container to search</param>
        /// <param name="logger">Optional logger for error reporting</param>
        private static void ConfigureControlsRecursively(DependencyObject container, ILogger logger = null)
        {
            if (container == null)
            {
                return;
            }

            try
            {
                var childCount = VisualTreeHelper.GetChildrenCount(container);

                for (var i = 0; i < childCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(container, i);

                    if (child is TextBox textBox)
                    {
                        ConfigureTextBoxForController(textBox, InputScopeNameValue.Default, logger);
                    }
                    else if (child is PasswordBox passwordBox)
                    {
                        ConfigurePasswordBoxForController(passwordBox, logger);
                    }
                    else if (child is Button button)
                    {
                        ConfigureButtonForController(button, logger);
                    }
                    else if (child is FrameworkElement element)
                    {
                        // Enable XY focus navigation for all framework elements
                        EnableXYFocusNavigation(element, logger);
                    }

                    // Recursively check children
                    ConfigureControlsRecursively(child, logger);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to configure controls recursively");
                throw;
            }
        }

        /// <summary>
        ///     Configures a Button for optimal controller input
        /// </summary>
        /// <param name="button">The Button to configure</param>
        /// <param name="logger">Optional logger for error reporting</param>
        public static void ConfigureButtonForController(Button button, ILogger logger = null)
        {
            if (button == null)
            {
                return;
            }

            try
            {
                // Ensure button is focusable and accessible via controller
                button.IsTabStop = true;
                button.IsEnabled = true;

                // Enable XY focus navigation for gamepad input
                button.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

                // Use system focus visuals for better controller experience
                button.UseSystemFocusVisuals = true;

                // Let Xbox system handle button activation naturally
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to configure Button for controller input");
            }
        }
    }
}
