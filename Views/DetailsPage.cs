using System;
using System.Threading.Tasks;
using Gelatinarm.Controls;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    /// <summary>
    ///     Common functionality for detail pages
    /// </summary>
    public abstract class DetailsPage : BasePage
    {
        // Additional Services (beyond those in BasePage)
        protected readonly JellyfinApiClient ApiClient;
        protected readonly IUnifiedDeviceService DeviceService;
        protected readonly INavigationStateService NavigationStateService;

        // Common Properties

        protected DetailsPage(Type loggerType) : base(loggerType)
        {
            ApiClient = GetService<JellyfinApiClient>();
            DeviceService = GetService<IUnifiedDeviceService>();
            NavigationStateService = GetService<INavigationStateService>();

        }

        // Common UI Elements (expected to be defined in derived XAML)
        protected LoadingOverlay LoadingOverlay => FindName("LoadingOverlay") as LoadingOverlay;
        protected ScrollViewer ContentScrollViewer => FindName("ContentScrollViewer") as ScrollViewer;
        protected ListView TabsListView => FindName("TabsListView") as ListView;

        // Common Buttons
        protected Button PlayButton => FindName("PlayButton") as Button;
        protected Button ResumeButton => FindName("ResumeButton") as Button;
        protected ToggleButton FavoriteButton => FindName("FavoriteButton") as ToggleButton;
        protected ToggleButton WatchedButton => FindName("WatchedButton") as ToggleButton;

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Configure controller input
            ConfigureControllerInput();

            // Handle navigation based on mode
            if (e.NavigationMode == NavigationMode.Back)
            {
                await OnNavigatedBackAsync();
                // Also call InitializeViewModelAsync with null parameter to allow pages to restore state
                await InitializeViewModelAsync(null);
            }
            else
            {
                await InitializeViewModelAsync(e.Parameter);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            OnNavigatingAway();
        }

        /// <summary>
        ///     Configure Xbox controller input handling
        /// </summary>
        protected virtual void ConfigureControllerInput()
        {
            if (ContentScrollViewer != null)
            {
                ControllerInputHelper.ConfigurePageForController(
                    this,
                    ContentScrollViewer);
            }

            // Set up tab navigation if tabs exist
            if (TabsListView != null)
            {
                TabsListView.PreviewKeyDown += OnTabsPreviewKeyDown;
                TabsListView.GotFocus += OnTabsGotFocus;
            }
        }

        /// <summary>
        ///     Initialize the view model with navigation parameter
        /// </summary>
        protected virtual async Task InitializeViewModelAsync(object parameter)
        {
            // Override in derived classes to initialize their specific ViewModel
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Called when navigating back to this page
        /// </summary>
        protected override async Task OnNavigatedBackAsync()
        {
            // Override in derived classes to handle refresh logic
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Called when navigating away from this page
        /// </summary>
        protected override void OnNavigatingAway()
        {
            // Clean up event handlers
            if (TabsListView != null)
            {
                TabsListView.PreviewKeyDown -= OnTabsPreviewKeyDown;
                TabsListView.GotFocus -= OnTabsGotFocus;
            }
        }

        #region Common UI Methods

        /// <summary>
        ///     Show or hide loading overlay
        /// </summary>
        protected virtual void ShowLoading(bool show)
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.IsLoading = show;
                if (show)
                {
                    LoadingOverlay.LoadingText = "Loading...";
                }
            }
        }

        /// <summary>
        ///     Show error message and navigate back
        /// </summary>
        protected virtual async Task ShowErrorAndNavigateBackAsync(string message, Exception ex = null)
        {
            // Use ErrorHandlingService if available and we have an exception
            var errorHandler = ErrorHandlingService;
            if (errorHandler != null && ex != null)
            {
                var context = new ErrorContext(GetType().Name, "LoadDetails", ErrorCategory.User);
                await errorHandler.HandleErrorAsync(ex, context, true, message);
            }
            else if (DialogService != null)
            {
                // Fallback to DialogService
                await DialogService.ShowErrorAsync("Error", message);
            }

            NavigationService.GoBack();
        }

        /// <summary>
        ///     Show a message dialog
        /// </summary>
        protected virtual async Task ShowMessageAsync(string title, string message)
        {
            if (DialogService != null)
            {
                await DialogService.ShowMessageAsync(message, title);
            }
        }

        #endregion

        #region Xbox Controller Navigation

        /// <summary>
        ///     Handle tab navigation with controller
        /// </summary>
        protected virtual async void OnTabsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!(sender is ListView tabsList))
            {
                return;
            }

            if (e.Key == VirtualKey.Left || e.Key == VirtualKey.GamepadDPadLeft ||
                e.Key == VirtualKey.Right || e.Key == VirtualKey.GamepadDPadRight)
            {
                // Let the key event process normally first
                await Task.Delay(100);

                // Ensure selected item is in view
                if (tabsList.SelectedItem != null)
                {
                    var selectedItem = tabsList.ContainerFromItem(tabsList.SelectedItem) as ListViewItem;
                    if (selectedItem != null)
                    {
                        await EnsureItemInView(selectedItem);
                    }
                }
            }
            else if (e.Key == VirtualKey.Down || e.Key == VirtualKey.GamepadDPadDown)
            {
                // Move focus to content area
                MoveToContentArea();
                e.Handled = true;
            }
        }

        /// <summary>
        ///     Handle tab focus to ensure visibility
        /// </summary>
        protected virtual async void OnTabsGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ListView tabsList && tabsList.SelectedItem != null)
            {
                var selectedItem = tabsList.ContainerFromItem(tabsList.SelectedItem) as ListViewItem;
                if (selectedItem != null)
                {
                    await EnsureItemInView(selectedItem);
                }
            }
        }

        /// <summary>
        ///     Ensure item is visible in scroll viewer
        /// </summary>
        protected virtual async Task EnsureItemInView(ListViewItem item)
        {
            if (item == null)
            {
                return;
            }

            // Find the parent ScrollViewer
            DependencyObject parent = item;
            ScrollViewer scrollViewer = null;
            while (parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
                scrollViewer = parent as ScrollViewer;
                if (scrollViewer != null)
                {
                    break;
                }
            }

            if (scrollViewer != null)
            {
                // Get the position of the item relative to the ScrollViewer
                var transform = item.TransformToVisual(scrollViewer);
                var position = transform.TransformPoint(new Point(0, 0));

                // Calculate if we need to scroll
                var itemHeight = item.ActualHeight;
                var viewportHeight = scrollViewer.ViewportHeight;
                var currentOffset = scrollViewer.VerticalOffset;

                if (position.Y < 0)
                {
                    // Item is above viewport, scroll up
                    scrollViewer.ChangeView(null, currentOffset + position.Y - 20, null);
                }
                else if (position.Y + itemHeight > viewportHeight)
                {
                    // Item is below viewport, scroll down
                    var newOffset = currentOffset + (position.Y + itemHeight - viewportHeight) + 20;
                    scrollViewer.ChangeView(null, newOffset, null);
                }
            }
        }

        /// <summary>
        ///     Move focus to the main content area
        /// </summary>
        protected virtual void MoveToContentArea()
        {
            // Try common buttons first
            if (PlayButton?.Visibility == Visibility.Visible)
            {
                PlayButton.Focus(FocusState.Programmatic);
            }
            else if (ResumeButton?.Visibility == Visibility.Visible)
            {
                ResumeButton.Focus(FocusState.Programmatic);
            }
            else
            {
                // Let derived classes handle specific content focus
                OnMoveToContentArea();
            }
        }

        /// <summary>
        ///     Override in derived classes to handle specific content focus
        /// </summary>
        protected virtual void OnMoveToContentArea()
        {
            // Derived classes can override to focus on specific content
        }

        #endregion

        #region Common Button Handlers

        /// <summary>
        ///     Common play button click handler
        /// </summary>
        protected virtual async void OnPlayButtonClick(object sender, RoutedEventArgs e)
        {
            // Override in derived classes to handle play action
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Common resume button click handler
        /// </summary>
        protected virtual async void OnResumeButtonClick(object sender, RoutedEventArgs e)
        {
            // Override in derived classes to handle resume action
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Common favorite toggle handler
        /// </summary>
        protected virtual async void OnFavoriteToggled(object sender, RoutedEventArgs e)
        {
            // Override in derived classes to handle favorite toggle
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Common watched toggle handler
        /// </summary>
        protected virtual async void OnWatchedToggled(object sender, RoutedEventArgs e)
        {
            // Override in derived classes to handle watched toggle
            await Task.CompletedTask;
        }

        #endregion
    }

    #region ViewModel Interfaces

    /// <summary>
    ///     Interface for ViewModels that can be initialized with a parameter
    /// </summary>
    public interface IInitializableViewModel
    {
        Task InitializeAsync(object parameter);
    }


    #endregion
}
