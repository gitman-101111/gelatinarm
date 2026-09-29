using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Base
{
    public abstract class BasePage : Page
    {
        private readonly Type _loggerType;
        private bool _hasInitialized;
        private bool _isPageLoaded;

        protected BasePage(Type loggerType)
        {
            _loggerType = loggerType;

            // The XAML designer has no container; the app always has one by the time a page exists
            if (!DesignMode.DesignModeEnabled)
            {
                InitializeServices();
            }

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        // Focused when the page loads; without one, the first focusable control. Read then, not
        // here in the constructor, where the page's named controls do not exist yet.
        protected virtual Control InitialFocusControl => null;

        protected ILogger Logger { get; private set; }
        protected INavigationService NavigationService { get; private set; }
        protected IDialogService DialogService { get; private set; }
        protected IErrorHandlingService ErrorHandler { get; private set; }
        protected IPreferencesService PreferencesService { get; private set; }
        protected IUserProfileService UserProfileService { get; private set; }

        private void InitializeServices()
        {
            Logger = GetService(typeof(ILogger<>).MakeGenericType(_loggerType)) as ILogger
                     ?? throw new InvalidOperationException($"No logger for {_loggerType.Name}");
            NavigationService = GetRequiredService<INavigationService>();
            DialogService = GetRequiredService<IDialogService>();
            ErrorHandler = GetRequiredService<IErrorHandlingService>();
            PreferencesService = GetRequiredService<IPreferencesService>();
            UserProfileService = GetRequiredService<IUserProfileService>();

            ControllerInputHelper.ConfigurePageForController(this, () => InitialFocusControl, Logger);

            InitializeViewModel();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

            Logger.LogDebug("{GetType}: OnNavigatedTo called (Mode: {ENavigationMode})", GetType().Name, e.NavigationMode);

            try
            {
                if (e.NavigationMode == NavigationMode.Back)
                {
                    _hasInitialized = true; // InitializePageAsync ran on first visit; don't repeat on back navigation
                    await OnNavigatedBackAsync();
                }
                else if (e.NavigationMode == NavigationMode.New)
                {
                    _hasInitialized = false;
                }

                if (!_hasInitialized)
                {
                    await InitializePageAsync(e.Parameter);
                    _hasInitialized = true;
                }

                await RefreshDataAsync(e.NavigationMode == NavigationMode.New ||
                                       e.NavigationMode == NavigationMode.Forward);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnNavigatedTo"));
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;

            Logger.LogDebug("{GetType}: OnNavigatedFrom called", GetType().Name);

            try
            {
                CancelOngoingOperations();

                CleanupResources();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnNavigatedFrom"));
            }
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_isPageLoaded)
            {
                return;
            }

            _isPageLoaded = true;

            Logger.LogDebug("{GetType}: Page loaded", GetType().Name);

            try
            {
                await OnPageLoadedAsync();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnPageLoaded"));
            }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _isPageLoaded = false;
            _hasInitialized = false;

            Logger.LogDebug("{GetType}: Page unloaded", GetType().Name);

            // The page's own events hold nothing: a cached page loads again on return and must
            // run its hooks again
            try
            {
                OnPageUnloadedCore();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnPageUnloaded"));
            }
        }

        protected virtual Task InitializePageAsync(object parameter)
        {
            (ViewModel as IPageViewModel)?.Initialize(parameter);
            return Task.CompletedTask;
        }

        protected virtual Task RefreshDataAsync(bool forceRefresh)
        {
            return Task.CompletedTask;
        }

        protected virtual Task OnNavigatedBackAsync()
        {
            return Task.CompletedTask;
        }

        protected virtual Task OnPageLoadedAsync()
        {
            return Task.CompletedTask;
        }

        protected virtual void CancelOngoingOperations()
        {
        }

        protected virtual void CleanupResources()
        {
            (ViewModel as IPageViewModel)?.Dispose();
        }

        protected virtual void OnPageUnloadedCore()
        {
        }

        protected void FireAndForget(Func<Task> asyncAction, [CallerMemberName] string memberName = "")
        {
            AsyncHelper.FireAndForget(asyncAction, Logger, GetType(), memberName);
        }

        /// <summary>
        ///     BaseService, BaseViewModel and BaseControl have the same helper: the four bases share
        ///     no class to hold it
        /// </summary>
        protected ErrorContext CreateErrorContext(string operation, ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }

        private static object GetService(Type serviceType)
        {
            return ServiceLocator.GetService(serviceType);
        }

        protected static T GetRequiredService<T>() where T : class
        {
            return ServiceLocator.GetRequiredService<T>();
        }

        protected virtual Type ViewModelType => null;

        protected object ViewModel { get; set; }

        private void InitializeViewModel()
        {
            if (ViewModelType != null)
            {
                ViewModel = GetService(ViewModelType)
                            ?? throw new InvalidOperationException($"{ViewModelType.Name} is not registered");
                DataContext = ViewModel;
            }
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (HandleBackNavigation(e))
            {
                return;
            }

            if (NavigationService.IsNavigating)
            {
                Logger.LogDebug("BasePage: Back requested during navigation on {GetType}, ignoring", GetType().Name);
                e.Handled = true;
                return;
            }

            if (NavigationService.CanGoBack)
            {
                e.Handled = true;
                NavigationService.GoBack();
                Logger.LogDebug("BasePage: Navigated back from {GetType} using NavigationService", GetType().Name);
            }
            else
            {
                Logger.LogDebug("BasePage: Cannot go back from {GetType} - letting system handle it", GetType().Name);
            }
        }

        protected virtual bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            return false;
        }

        protected void NavigateToItemDetails(BaseItemDto item)
        {
            NavigationService.NavigateToItemDetails(item);
        }

        protected object ResolveNavigationParameter(object parameter)
        {
            if (parameter != null)
            {
                return parameter;
            }

            var savedParameter = NavigationService.GetLastNavigationParameter();
            if (savedParameter != null)
            {
                Logger.LogDebug("Using saved navigation parameter for back navigation");
            }

            return savedParameter;
        }
    }
}
