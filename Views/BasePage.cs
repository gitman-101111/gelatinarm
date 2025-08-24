using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Gelatinarm.Controls;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    /// <summary>
    ///     Base class for all pages that provides standard lifecycle management,
    ///     controller input support, and common functionality to reduce code duplication.
    /// </summary>
    public abstract class BasePage : Page
    {
        private readonly Type _loggerType;
        private bool _hasInitialized;

        // State tracking
        private bool _isPageLoaded;
        private bool _servicesInitialized;

        protected BasePage() : this(typeof(BasePage))
        {
        }

        protected BasePage(Type loggerType)
        {
            _loggerType = loggerType;

            // Try to initialize services, but don't fail if they're not ready
            TryInitializeServices();

            // Subscribe to common events
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        // Core services
        protected ILogger Logger { get; private set; }
        protected INavigationService NavigationService { get; private set; }

        // Services commonly used across pages
        protected IErrorHandlingService ErrorHandlingService { get; private set; }
        protected IPreferencesService PreferencesService { get; private set; }
        protected IUserProfileService UserProfileService { get; private set; }

        private void TryInitializeServices()
        {
            try
            {
                var services = App.Current?.Services;
                if (services != null)
                {
                    var loggerGenericType = typeof(ILogger<>).MakeGenericType(_loggerType);
                    Logger = services.GetService(loggerGenericType) as ILogger;

                    // Get common services
                    NavigationService = services.GetService<INavigationService>();
                    ErrorHandlingService = services.GetService<IErrorHandlingService>();
                    PreferencesService = services.GetService<IPreferencesService>();
                    UserProfileService = services.GetService<IUserProfileService>();

                    // Configure basic controller support
                    ControllerInputHelper.ConfigurePageForController(this, null, Logger);

                    // Initialize ViewModel if specified
                    InitializeViewModel();

                    _servicesInitialized = true;
                }
            }
            catch (Exception ex)
            {
                // Services not ready yet, will try again in OnNavigatedTo
                Debug.WriteLine($"BasePage: Services not ready in constructor: {ex.Message}");
            }
        }

        #region Lifecycle Methods

        /// <summary>
        ///     Standard OnNavigatedTo implementation with error handling and state management
        /// </summary>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Initialize services if not already done
            if (!_servicesInitialized)
            {
                TryInitializeServices();
            }

            // Subscribe to back button events
            try
            {
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
                Logger?.LogInformation($"BasePage: Subscribed to BackRequested for {GetType().Name}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to subscribe to back button events");
            }

            Logger?.LogInformation($"{GetType().Name}: OnNavigatedTo called (Mode: {e.NavigationMode})");

            try
            {
                // Handle navigation based on mode
                if (e.NavigationMode == NavigationMode.Back)
                {
                    await OnNavigatedBackAsync();
                }
                else if (e.NavigationMode == NavigationMode.New)
                {
                    _hasInitialized = false;
                }

                // Initialize page if needed
                if (!_hasInitialized)
                {
                    await InitializePageAsync(e.Parameter);
                    _hasInitialized = true;
                }

                // Always refresh data
                await RefreshDataAsync(e.NavigationMode == NavigationMode.New ||
                                       e.NavigationMode == NavigationMode.Forward);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.OnNavigatedTo");
                var errorContext = new ErrorContext(GetType().Name, "OnNavigatedTo");
                await ErrorHandlingService?.HandleErrorAsync(ex, errorContext);
            }
        }

        /// <summary>
        ///     Standard OnNavigatedFrom implementation with cleanup
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Unsubscribe from back button events
            try
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to unsubscribe from back button events");
            }

            Logger?.LogInformation($"{GetType().Name}: OnNavigatedFrom called");

            try
            {
                // Cancel any ongoing operations
                CancelOngoingOperations();

                // Cleanup resources
                CleanupResources();

                // Allow derived classes to perform additional cleanup
                OnNavigatingAway();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.OnNavigatedFrom");
            }
        }

        /// <summary>
        ///     Called when the page is loaded
        /// </summary>
        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_isPageLoaded)
            {
                return;
            }

            _isPageLoaded = true;

            Logger?.LogInformation($"{GetType().Name}: Page loaded");

            try
            {
                await OnPageLoadedAsync();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.OnPageLoaded");
                var errorContext = new ErrorContext(GetType().Name, "OnPageLoaded");
                await ErrorHandlingService?.HandleErrorAsync(ex, errorContext);
            }
        }

        /// <summary>
        ///     Called when the page is unloaded
        /// </summary>
        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _isPageLoaded = false;
            _hasInitialized = false;

            Logger?.LogInformation($"{GetType().Name}: Page unloaded");

            try
            {
                // Unsubscribe from events
                Loaded -= OnPageLoaded;
                Unloaded -= OnPageUnloaded;

                // Perform additional cleanup
                OnPageUnloadedCore();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.OnPageUnloaded");
            }
        }

        #endregion

        #region Virtual Methods for Customization

        /// <summary>
        ///     Override to initialize the page with navigation parameter
        /// </summary>
        protected virtual async Task InitializePageAsync(object parameter)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Override to refresh page data
        /// </summary>
        protected virtual async Task RefreshDataAsync(bool forceRefresh)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Override to handle navigation back to this page
        /// </summary>
        protected virtual async Task OnNavigatedBackAsync()
        {
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Override to handle page loaded event
        /// </summary>
        protected virtual async Task OnPageLoadedAsync()
        {
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Override to perform cleanup when navigating away
        /// </summary>
        protected virtual void OnNavigatingAway()
        {
            // Override in derived classes
        }

        /// <summary>
        ///     Override to cancel ongoing operations
        /// </summary>
        protected virtual void CancelOngoingOperations()
        {
            // Override in derived classes
        }

        /// <summary>
        ///     Override to cleanup resources
        /// </summary>
        protected virtual void CleanupResources()
        {
            // Override in derived classes
        }

        /// <summary>
        ///     Override to perform additional cleanup when page is unloaded
        /// </summary>
        protected virtual void OnPageUnloadedCore()
        {
            // Override in derived classes
        }

        #endregion

        #region Helper Methods

        /// <summary>
        ///     Execute an async operation with standard error handling
        /// </summary>
        protected async Task ExecuteAsync(Func<Task> operation, string operationName = null)
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Error in {operationName ?? "operation"} on {GetType().Name}");
                var errorContext = new ErrorContext(GetType().Name, operationName ?? "ExecuteAsync");
                await ErrorHandlingService?.HandleErrorAsync(ex, errorContext);
            }
        }

        /// <summary>
        ///     Execute a fire-and-forget async operation with error handling
        /// </summary>
        protected void FireAndForget(Func<Task> asyncAction, string operationName = null)
        {
            AsyncHelper.FireAndForget(asyncAction, Logger, GetType(), operationName ?? "Unknown");
        }

        /// <summary>
        ///     Gets a service from the dependency injection container
        /// </summary>
        protected T GetService<T>() where T : class
        {
            return App.Current?.Services?.GetService<T>();
        }

        /// <summary>
        ///     Gets a required service from the dependency injection container
        /// </summary>
        protected T GetRequiredService<T>() where T : class
        {
            return App.Current?.Services?.GetRequiredService<T>() ??
                   throw new InvalidOperationException($"Service {typeof(T).Name} not found");
        }

        #endregion

        #region ViewModel Support

        /// <summary>
        ///     Override to specify the ViewModel type for automatic initialization
        /// </summary>
        protected virtual Type ViewModelType => null;

        /// <summary>
        ///     The page's ViewModel, automatically initialized if ViewModelType is specified
        /// </summary>
        protected object ViewModel { get; private set; }

        /// <summary>
        ///     Initialize ViewModel if ViewModelType is specified
        /// </summary>
        protected virtual void InitializeViewModel()
        {
            if (ViewModelType != null)
            {
                try
                {
                    ViewModel = GetRequiredService(ViewModelType);
                    DataContext = ViewModel;
                    Logger?.LogInformation($"{GetType().Name}: ViewModel {ViewModelType.Name} initialized");
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, $"Failed to initialize ViewModel {ViewModelType.Name}");
                    throw;
                }
            }
        }

        /// <summary>
        ///     Helper to get a service using runtime type
        /// </summary>
        private object GetRequiredService(Type serviceType)
        {
            return App.Current?.Services?.GetRequiredService(serviceType) ??
                   throw new InvalidOperationException($"Service {serviceType.Name} not found");
        }

        #endregion

        #region Controller Support

        /// <summary>
        ///     Handles the back button press using standard UWP navigation
        /// </summary>
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            Logger?.LogInformation($"BasePage.OnBackRequested called from {GetType().Name}");

            // Allow derived classes to handle back navigation
            if (HandleBackNavigation(e))
            {
                return;
            }

            // Use NavigationService for consistent navigation handling
            if (NavigationService?.CanGoBack == true)
            {
                e.Handled = true;
                NavigationService?.GoBack();
                Logger?.LogInformation($"BasePage: Navigated back from {GetType().Name} using NavigationService");
            }
            else
            {
                Logger?.LogInformation($"BasePage: Cannot go back from {GetType().Name} - letting system handle it");
                // Don't handle - let the system decide what to do
                e.Handled = false;
            }
        }

        /// <summary>
        ///     Virtual method that derived classes can override to handle custom back navigation
        /// </summary>
        /// <returns>True if the back navigation was handled, false to use default behavior</returns>
        protected virtual bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            // Default implementation does nothing
            return false;
        }

        /// <summary>
        ///     Sets the initial focus control for controller navigation
        /// </summary>
        /// <param name="control">The control to receive initial focus</param>
        protected void SetInitialFocus(Control control)
        {
            ControllerInputHelper.SetInitialFocus(control, Logger);
        }

        /// <summary>
        ///     Shows the virtual keyboard
        /// </summary>
        protected void ShowVirtualKeyboard()
        {
            ControllerInputHelper.ShowVirtualKeyboard(Logger);
        }

        /// <summary>
        ///     Hides the virtual keyboard
        /// </summary>
        protected void HideVirtualKeyboard()
        {
            ControllerInputHelper.HideVirtualKeyboard(Logger);
        }

        #endregion

        #region Common Navigation Helpers

        /// <summary>
        ///     Navigate to item details based on item type
        /// </summary>
        protected void NavigateToItemDetails(BaseItemDto item)
        {
            NavigationService?.NavigateToItemDetails(item);
        }

        /// <summary>
        ///     Get saved navigation parameter for back navigation
        /// </summary>
        protected object GetSavedNavigationParameter()
        {
            return NavigationService?.GetLastNavigationParameter();
        }

        #endregion
    }
}
