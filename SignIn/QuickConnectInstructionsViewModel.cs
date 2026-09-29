using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Home;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public partial class QuickConnectInstructionsViewModel : BaseViewModel, IPageViewModel
    {
        private readonly IAuthenticationService _authenticationService;
        private readonly INavigationService _navigationService;

        private const string WaitingStatus = "⏳ Waiting for authorization";

        [ObservableProperty] private string _connectionStatus = WaitingStatus;

        [ObservableProperty] private bool _isConnectionProgressActive = true;

        private DispatcherTimer _pollingTimer;

        // A poll retries with backoff and can outlast the interval; the timer keeps ticking, and
        // overlapping polls would pile up on a down server and could both redeem the secret
        private bool _isPollInFlight;

        [ObservableProperty] private string _quickConnectCode = "Loading...";

        private string _quickConnectSecret;

        [ObservableProperty] private string _quickConnectUrl = "Loading URL...";

        public QuickConnectInstructionsViewModel(
            IAuthenticationService authenticationService,
            INavigationService navigationService,
            ILogger<QuickConnectInstructionsViewModel> logger) : base(logger)
        {
            _authenticationService = authenticationService;
            _navigationService = navigationService;
        }

        // Reached from the sign-in page alone, which always passes the parameters
        public void Initialize(object parameter)
        {
            var parameters = (QuickConnectInstructionsParameters)parameter;
            _quickConnectSecret = parameters.Secret;

            QuickConnectCode = parameters.Code;
            QuickConnectUrl = $"{parameters.ServerUrl}/web/index.html/#/quickconnect";

            Logger.LogDebug("Displaying Quick Connect instructions for code: {ParametersCode}", parameters.Code);
            StartPolling();
        }

        private void StartPolling()
        {
            Logger.LogDebug("Starting Quick Connect polling");

            _pollingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(SignInConstants.QuickConnectPollIntervalSeconds)
            };
            _pollingTimer.Tick += async (sender, e) => await PollQuickConnectStatusAsync();
            _pollingTimer.Start();
        }

        private void StopPolling()
        {
            if (_pollingTimer != null)
            {
                Logger.LogDebug("Stopping Quick Connect polling");
                _pollingTimer.Stop();
                _pollingTimer = null;
            }
        }

        // A DispatcherTimer tick: it starts, and resumes after each await, on the UI thread
        private async Task PollQuickConnectStatusAsync()
        {
            if (_isPollInFlight)
            {
                return;
            }

            _isPollInFlight = true;
            try
            {
                Logger.LogDebug("Polling Quick Connect status");

                var isAuthenticated =
                    await _authenticationService.CheckQuickConnectStatusAsync(_quickConnectSecret,
                        CancellationToken.None);

                if (!isAuthenticated)
                {
                    ConnectionStatus = WaitingStatus;
                    return;
                }

                Logger.LogInformation("Quick Connect authentication detected via polling");

                StopPolling();

                IsConnectionProgressActive = false;
                ConnectionStatus = "✅ Connected successfully! Redirecting...";

                await Task.Delay(SignInConstants.QuickConnectSuccessDelayMs);
                _navigationService.Navigate(typeof(MainPage));
            }
            catch (Exception ex)
            {
                // Polling goes on: the user may still approve the code once the server answers
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("PollQuickConnectStatus", ErrorCategory.Network), false);
                ConnectionStatus = "⚠️ Checking connection";
            }
            finally
            {
                _isPollInFlight = false;
            }
        }

        [RelayCommand]
        private async Task CancelAsync()
        {
            Logger.LogInformation("User cancelled Quick Connect");

            StopPolling();

            IsConnectionProgressActive = false;
            ConnectionStatus = "❌ Quick Connect cancelled";

            await Task.Delay(TimeSpan.FromSeconds(SignInConstants.QuickConnectCancelRedirectSeconds));

            Leave();
        }

        // Back to the sign-in page the code came from
        private void Leave()
        {
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
        }
    }
}
