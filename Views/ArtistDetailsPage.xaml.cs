using System;
using System.Threading.Tasks;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Gelatinarm.Views
{
    public sealed partial class ArtistDetailsPage : BasePage
    {
        public ArtistDetailsPage() : base(typeof(ArtistDetailsPage))
        {
            InitializeComponent();

            // Handle gamepad menu button for context menus
            PreviewKeyDown += OnPreviewKeyDown;
        }

        protected override Type ViewModelType => typeof(ArtistDetailsViewModel);
        public ArtistDetailsViewModel ViewModel => (ArtistDetailsViewModel)base.ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            // Initialize the ViewModel with navigation parameters
            // If we have a parameter, use it (normal navigation)
            if (parameter != null)
            {
                await ViewModel.InitializeAsync(parameter);
            }
            // If no parameter, check if we have a saved parameter from navigation service (back navigation)
            else
            {
                var savedParameter = GetSavedNavigationParameter();

                if (savedParameter != null)
                {
                    Logger?.LogInformation("Using saved navigation parameter for back navigation");
                    await ViewModel.InitializeAsync(savedParameter);
                }
            }
        }

        /// <summary>
        ///     Handle album click
        /// </summary>
        private void OnAlbumClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BaseItemDto album && ViewModel != null)
            {
                ViewModel.NavigateToAlbumCommand.Execute(album);
            }
        }

        /// <summary>
        ///     Handle track button click to show context menu
        /// </summary>
        private void OnTrackButtonClick(object sender, RoutedEventArgs e)
        {
            var logger = Logger;
            Logger?.LogInformation("OnTrackButtonClick called - showing context menu");

            var button = sender as Button;
            if (button?.ContextFlyout is MenuFlyout flyout)
            {
                logger?.LogInformation("Found ContextFlyout on button, showing it");
                flyout.ShowAt(button);
            }
            else
            {
                logger?.LogWarning(
                    $"No ContextFlyout found on button. Button: {button != null}, ContextFlyout: {button?.ContextFlyout}");
            }
        }

        /// <summary>
        ///     Handle play all button click
        /// </summary>
        private async void OnPlayAllClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.PlayCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        ///     Handle shuffle all button click
        /// </summary>
        private async void OnShuffleAllClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.ShuffleCommand.ExecuteAsync(null);
            }
        }

        /// <summary>
        ///     Handle gamepad menu button to show context menu
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var logger = Logger;

            // Check for gamepad menu button
            if (e.Key == VirtualKey.GamepadMenu)
            {
                e.Handled = true;

                // Get the currently focused element
                var focusedElement = FocusManager.GetFocusedElement() as FrameworkElement;
                if (focusedElement == null)
                {
                    return;
                }

                // Look for a button in the focused element or its ancestors
                var button = focusedElement as Button ?? FindParent<Button>(focusedElement);
                if (button != null)
                {
                    // Try to find and show the context flyout
                    var grid = button.Content as Grid;
                    if (grid?.ContextFlyout is MenuFlyout flyout)
                    {
                        flyout.ShowAt(button);
                    }
                }
            }
            else if (e.Key == VirtualKey.GamepadA || e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
            {
                logger?.LogInformation(
                    $"A/Enter/Space pressed. Focused element: {FocusManager.GetFocusedElement()?.GetType().Name}");

                // Handle A button press to show context menu
                var focusedElement = FocusManager.GetFocusedElement() as FrameworkElement;
                if (focusedElement is ListViewItem listViewItem)
                {
                    // Find the button inside the ListViewItem
                    var button = FindChild<Button>(listViewItem);
                    if (button?.ContextFlyout is MenuFlyout flyout)
                    {
                        logger?.LogInformation("Showing context menu from A button press");
                        flyout.ShowAt(button);
                        e.Handled = true;
                    }
                }
            }
        }

        /// <summary>
        ///     Helper to find parent of specific type
        /// </summary>
        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as T;
        }

        /// <summary>
        ///     Helper to find child of specific type
        /// </summary>
        private T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

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
