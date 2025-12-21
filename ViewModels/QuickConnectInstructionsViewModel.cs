using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;
using Exception = System.Exception;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the QuickConnectInstructionsPage handling Quick Connect authentication flow
    /// </summary>
    public partial class QuickConnectInstructionsViewModel : BaseViewModel
    {
        // Services
        private readonly IAuthenticationService _authenticationService;
        private readonly INavigationService _navigationService;
        private readonly IUserProfileService _userProfileService;

        [ObservableProperty] private string _connectionStatus = "⏳ Waiting for authorization";

        [ObservableProperty] private bool _isConnectionProgressActive = true;

        [ObservableProperty] private bool _isLoadingOverlayVisible;

        private DispatcherTimer _pollingTimer;

        // Observable properties
        [ObservableProperty] private string _quickConnectCode = "Loading...";

        // Private fields
        private string _quickConnectSecret;

        [ObservableProperty] private string _quickConnectUrl = "Loading URL...";

        private string _serverUrl;

        public QuickConnectInstructionsViewModel(
            IAuthenticationService authenticationService,
            INavigationService navigationService,
            IUserProfileService userProfileService,
            ILogger<QuickConnectInstructionsViewModel> logger) : base(logger)
        {
            _authenticationService = authenticationService;
            _navigationService = navigationService;
            _userProfileService = userProfileService;
        }

        /// <summary>
        ///     Initialize the ViewModel with navigation parameters
        /// </summary>
        public Task InitializeAsync(object parameter)
        {
            var context = CreateErrorContext("Initialize", ErrorCategory.Configuration);
            FireAndForget(async () =>
            {
                try
                {
                    if (parameter is QuickConnectInstructionsParameters parameters)
                    {
                        _quickConnectSecret = parameters.Secret;
                        _serverUrl = parameters.ServerUrl;

                        QuickConnectCode = parameters.Code;
                        QuickConnectUrl = $"{_serverUrl}/web/index.html/#/quickconnect";

                        Logger?.LogInformation($"Displaying Quick Connect instructions for code: {parameters.Code}");
                    }

                    // Subscribe to events
                    SubscribeToEvents();

                    // Start polling for Quick Connect status
                    StartPolling();
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });

            if (IsError)
            {
                ConnectionStatus = "❌ Failed to initialize";
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Clean up when navigating away
        /// </summary>
        public void Cleanup()
        {
            StopPolling();
            UnsubscribeFromEvents();
        }

        private void SubscribeToEvents()
        {
            if (_authenticationService != null)
            {
                Logger?.LogInformation("Subscribing to QuickConnect events");
                _authenticationService.QuickConnectCompleted += OnQuickConnectCompleted;
                _authenticationService.QuickConnectError += OnQuickConnectError;
                _authenticationService.QuickConnectStatusChanged += OnQuickConnectStatusChanged;
                Logger?.LogInformation("QuickConnect event subscription completed");
            }
            else
            {
                Logger?.LogError("AuthenticationService is null, cannot subscribe to events");
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (_authenticationService != null)
            {
                Logger?.LogInformation("Unsubscribing from QuickConnect events");
                _authenticationService.QuickConnectCompleted -= OnQuickConnectCompleted;
                _authenticationService.QuickConnectError -= OnQuickConnectError;
                _authenticationService.QuickConnectStatusChanged -= OnQuickConnectStatusChanged;
                Logger?.LogInformation("QuickConnect event unsubscription completed");
            }
        }

        private async void OnQuickConnectCompleted(object sender, QuickConnectResult result)
        {
            var context = CreateErrorContext("HandleQuickConnectCompleted", ErrorCategory.User);
            try
            {
                Logger?.LogInformation("OnQuickConnectCompleted event handler called");

                await RunOnUIThreadAsync(async () =>
                {
                    var innerContext = CreateErrorContext("CompleteQuickConnect", ErrorCategory.User);
                    try
                    {
                        Logger?.LogInformation(
                            "Quick Connect completed successfully, authentication already processed");

                        IsConnectionProgressActive = false;
                        ConnectionStatus = "✅ Connected successfully! Redirecting";

                        Logger?.LogInformation("Waiting for storage operations to complete...");
                        await Task.Delay(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS);

                        if (!await EnsureUserProfileLoadedAsync("Quick Connect success"))
                        {
                            ConnectionStatus = "❌ Failed to load user profile";
                            return;
                        }

                        Logger?.LogInformation("Navigating to MainPage");
                        _navigationService.Navigate(typeof(MainPage), "FromLogin");
                        Logger?.LogInformation("Navigation to MainPage completed");
                    }
                    catch (Exception ex)
                    {
                        await ErrorHandler.HandleErrorAsync(ex, innerContext, true);
                        if (IsError)
                        {
                            ConnectionStatus = $"❌ Navigation failed: {ErrorMessage}";
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }

            if (IsError)
            {
                await RunOnUIThreadAsync(() =>
                {
                    IsConnectionProgressActive = false;
                    ConnectionStatus = $"❌ Event handler error: {ErrorMessage}";
                });
            }
        }

        private async void OnQuickConnectError(object sender, string errorMessage)
        {
            await RunOnUIThreadAsync(() =>
            {
                Logger?.LogError($"Quick Connect failed: {errorMessage}");
                IsConnectionProgressActive = false;
                ConnectionStatus = $"❌ Connection failed: {errorMessage}";
            });
        }

        private async void OnQuickConnectStatusChanged(object sender, QuickConnectState state)
        {
            await RunOnUIThreadAsync(() =>
            {
                if (state == QuickConnectState.Authorized)
                {
                    ConnectionStatus = "🔄 Authenticating";
                }
                else
                {
                    ConnectionStatus = "⏳ Waiting for authorization";
                }
            });
        }

        private void StartPolling()
        {
            if (string.IsNullOrEmpty(_quickConnectSecret))
            {
                Logger?.LogError("Cannot start polling: Quick Connect secret is missing");
                return;
            }

            Logger?.LogInformation("Starting Quick Connect polling");

            // Create a timer that polls every 5 seconds
            _pollingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(RetryConstants.QUICK_CONNECT_POLL_INTERVAL_SECONDS)
            };
            _pollingTimer.Tick += async (sender, e) => await PollQuickConnectStatus();
            _pollingTimer.Start();
        }

        private void StopPolling()
        {
            if (_pollingTimer != null)
            {
                Logger?.LogInformation("Stopping Quick Connect polling");
                _pollingTimer.Stop();
                _pollingTimer = null;
            }
        }

        private async Task PollQuickConnectStatus()
        {
            var context = CreateErrorContext("PollQuickConnectStatus", ErrorCategory.Network);
            try
            {
                Logger?.LogDebug($"Polling Quick Connect status for secret: {_quickConnectSecret}");

                var isAuthenticated =
                    await _authenticationService.CheckQuickConnectStatusAsync(_quickConnectSecret,
                        CancellationToken.None);

                if (isAuthenticated)
                {
                    Logger?.LogInformation("Quick Connect authentication detected via polling");

                    // Stop polling immediately
                    StopPolling();

                    // Update UI to show success
                    await RunOnUIThreadAsync(() =>
                    {
                        IsConnectionProgressActive = false;
                        ConnectionStatus = "✅ Connected successfully! Redirecting...";
                    });

                    // The authentication service should have already handled the login
                    // Ensure user profile is loaded before navigating
                    await EnsureUserProfileLoadedAsync("Quick Connect");

                    await Task.Delay(RetryConstants.QUICK_CONNECT_SUCCESS_DELAY_MS);

                    await RunOnUIThreadAsync(() =>
                    {
                        // Clear MainViewModel before navigating to ensure no old server data appears
                        ClearMainViewModelCache("before navigating to MainPage");

                        Logger?.LogInformation("Navigating to MainPage after Quick Connect success");
                        _navigationService.Navigate(typeof(MainPage), "FromLogin");
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }

            if (IsError)
            {
                // Don't stop polling on error - the user might still authenticate
                await RunOnUIThreadAsync(() =>
                {
                    ConnectionStatus = "⚠️ Checking connection";
                });
            }
        }

        // Commands
        [RelayCommand]
        private void GoBack()
        {
            Logger?.LogInformation("User clicked back button, cancelling Quick Connect");

            StopPolling();
            _authenticationService?.CancelQuickConnect();

            if (_navigationService.CanGoBack)
            {
                _navigationService.GoBack();
            }
            else
            {
                _navigationService.Navigate(typeof(LoginPage));
            }
        }

        private async Task<bool> EnsureUserProfileLoadedAsync(string context)
        {
            if (_userProfileService == null)
            {
                Logger?.LogWarning($"User profile service unavailable during {context}");
                return false;
            }

            var currentUserGuid = _userProfileService.GetCurrentUserGuid();
            if (currentUserGuid.HasValue)
            {
                Logger?.LogInformation($"User profile ready during {context} - UserId: '{currentUserGuid}'");
                return true;
            }

            Logger?.LogInformation($"Loading user profile during {context}");
            var profileLoaded = await _userProfileService.LoadUserProfileAsync(CancellationToken.None);
            if (!profileLoaded)
            {
                Logger?.LogError($"Failed to load user profile during {context}");
                return false;
            }

            currentUserGuid = _userProfileService.GetCurrentUserGuid();
            Logger?.LogInformation($"User profile loaded during {context} - UserId: '{currentUserGuid}'");
            return currentUserGuid.HasValue;
        }

        [RelayCommand]
        private async Task CancelAsync()
        {
            Logger?.LogInformation("User cancelled Quick Connect");

            StopPolling();
            _authenticationService?.CancelQuickConnect();

            IsConnectionProgressActive = false;
            ConnectionStatus = "❌ Quick Connect cancelled";

            // Wait before redirecting
            await Task.Delay(TimeSpan.FromSeconds(UIConstants.QUICK_CONNECT_CANCEL_REDIRECT_SECONDS));

            if (_navigationService.CanGoBack)
            {
                _navigationService.GoBack();
            }
            else
            {
                _navigationService.Navigate(typeof(LoginPage));
            }
        }

        protected override void DisposeManaged()
        {
            base.DisposeManaged();
            StopPolling();
            UnsubscribeFromEvents();
        }
    }
}
