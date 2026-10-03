using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Popups;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Details;
using Gelatinarm.Favorites;
using Gelatinarm.Home;
using Gelatinarm.Library;
using Gelatinarm.Music;
using Gelatinarm.Playback;
using Gelatinarm.Player;
using Gelatinarm.Settings;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using UnhandledExceptionEventArgs = Windows.UI.Xaml.UnhandledExceptionEventArgs;

namespace Gelatinarm
{
    public sealed partial class App : Application
    {
        private static readonly TimeSpan VideoStopReportTimeout = TimeSpan.FromSeconds(3);

        // The server shows this in its dashboard and logs: the real package version, not a constant
        private static readonly string AppVersion = FormatPackageVersion();

        private ILogger<App> _logger;
        private volatile IServiceProvider _serviceProvider;

        public App()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception)
            {
                // A broken App.xaml would otherwise end the app before anything could log it. The
                // pages then fail to load one by one, and OnNavigationFailed reports each
            }

            Suspending += OnSuspending;
            Resuming += OnResuming;
            EnteredBackground += OnEnteredBackground;
            LeavingBackground += OnLeavingBackground;
            UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;

            try
            {
                ConfigureServices();
            }
            catch (Exception)
            {
                // OnLaunched shows the fallback error page when the provider is missing
            }
        }

        public new static App Current => (App)Application.Current;

        public IServiceProvider Services
        {
            get
            {
                if (_serviceProvider == null)
                {
                    throw new InvalidOperationException(
                        "ServiceProvider has not been initialized. This usually means the App constructor has not completed.");
                }

                return _serviceProvider;
            }
        }

        private static string FormatPackageVersion()
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }

        private void ConfigureServices()
        {
            if (_serviceProvider != null)
            {
                return;
            }

            var services = new ServiceCollection();

            try
            {
                services.AddLogging(builder =>
                {
                    builder.AddDebug();
#if DEBUG
                    builder.SetMinimumLevel(LogLevel.Debug);
#else
                    builder.SetMinimumLevel(LogLevel.Warning);
#endif
                });
            }
            catch (Exception)
            {
                // Continue without logging configured
            }

            services.AddSingleton<IPreferencesService, PreferencesService>();

            services.AddSingleton<IUnifiedDeviceService, UnifiedDeviceService>();
            services.AddSingleton<IDisplayModeService, DisplayModeService>();

            services.AddHttpClient(SystemConstants.JellyfinHttpClientName, (serviceProvider, client) =>
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"{BrandingConstants.UserAgent}/{AppVersion}");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                    // ConnectionTimeoutHandler owns the timeout
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(10))
                .AddHttpMessageHandler(serviceProvider =>
                    new ConnectionTimeoutHandler(serviceProvider.GetRequiredService<IPreferencesService>()))
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                {
                    var handler = new HttpClientHandler();

                    var ignoreCertErrors = false;
                    try
                    {
                        ignoreCertErrors = serviceProvider.GetRequiredService<IPreferencesService>().GetValue(PreferenceConstants.IgnoreCertificateErrors, false);
                    }
                    catch (Exception ex)
                    {
                        serviceProvider.GetRequiredService<ILogger<App>>().LogWarning(ex,
                            "Could not read {Preference}; certificate validation stays enabled",
                            PreferenceConstants.IgnoreCertificateErrors);
                    }

                    if (ignoreCertErrors)
                    {
                        handler.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }

                    return handler;
                });

            services.AddSingleton(provider =>
            {
                var deviceService = provider.GetRequiredService<IUnifiedDeviceService>();
                var settings = new JellyfinSdkSettings();
                settings.Initialize(BrandingConstants.AppName, AppVersion, deviceService.GetDeviceName(), deviceService.GetDeviceId());
                return settings;
            });

            services.AddSingleton<IAuthenticationProvider, JellyfinAuthenticationProvider>();

            services.AddSingleton(provider =>
            {
                var settings = provider.GetRequiredService<JellyfinSdkSettings>();
                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SystemConstants.JellyfinHttpClientName);
                var requestAdapter = new JellyfinRequestAdapter(provider.GetRequiredService<IAuthenticationProvider>(), settings, httpClient);

                // The last server, so the first requests go to it before the auth service has run
                try
                {
                    var serverUrl = provider.GetRequiredService<IPreferencesService>().GetValue<string>(PreferenceConstants.ServerUrl);
                    if (!string.IsNullOrEmpty(serverUrl))
                    {
                        settings.SetServerUrl(serverUrl);
                    }
                }
                catch (Exception ex)
                {
                    provider.GetRequiredService<ILogger<App>>().LogWarning(ex, "Failed to set initial server URL");
                }

                return requestAdapter;
            });

            services.AddSingleton<IRequestAdapter>(sp => sp.GetRequiredService<JellyfinRequestAdapter>());
            services.AddSingleton<JellyfinApiClient>();

            services.AddSingleton<IAuthenticationService, AuthenticationService>();
            services.AddSingleton<IUserProfileService, UserProfileService>();
            services.AddSingleton<IMediaDiscoveryService, MediaDiscoveryService>();

            services.AddTransient<ServerSettingsViewModel>();
            services.AddTransient<PlaybackSettingsViewModel>();
            services.AddTransient<HomeSettingsViewModel>();
            services.AddTransient<SettingsViewModel>();

            services.AddSingleton<MainViewModel>();
            services.AddTransient<FavoritesViewModel>();
            services.AddTransient<LibraryViewModel>();
            services.AddSingleton<LibrarySelectionViewModel>();
            services.AddTransient<SeasonDetailsViewModel>();
            services.AddTransient<MovieDetailsViewModel>();
            services.AddTransient<ArtistDetailsViewModel>();
            services.AddTransient<AlbumDetailsViewModel>();
            services.AddTransient<PersonDetailsViewModel>();
            services.AddTransient<CollectionDetailsViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<ProfileSelectionViewModel>();
            services.AddTransient<ServerSelectionViewModel>();
            services.AddTransient<QuickConnectInstructionsViewModel>();
            services.AddTransient<MediaPlayerViewModel>();

            services.AddSingleton<IMediaPlaybackService, MediaPlaybackService>();

            // MediaPlaybackService implements IMediaSessionService as well; alias it to the
            // same singleton so consumers can depend on the session contract directly
            // instead of type-testing the playback service at every call site.
            services.AddSingleton<IMediaSessionService>(sp =>
                (IMediaSessionService)sp.GetRequiredService<IMediaPlaybackService>());

            services.AddSingleton<MediaQueueService>();
            services.AddSingleton<IEpisodeQueueService>(sp => sp.GetRequiredService<MediaQueueService>());

            services.AddSingleton<IImageLoadingService, ImageLoadingService>();
            services.AddSingleton<IUserDataService, UserDataService>();
            services.AddSingleton<IMediaOptimizationService, MediaOptimizationService>();

            services.AddSingleton<IDeviceProfileService, DeviceProfileService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IErrorHandlingService, ErrorHandlingService>();

            services.AddSingleton<NavigationService>();
            services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

            services.AddSingleton<ICacheManagerService, CacheManagerService>();

            services.AddSingleton<IPlaybackQueueService, MusicQueueService>();
            services.AddSingleton<IMediaControlService, MediaControlService>();
            services.AddSingleton<IPlaybackControlService, PlaybackControlService>();
            services.AddSingleton<IMediaNavigationService>(sp => sp.GetRequiredService<MediaQueueService>());
            services.AddTransient<IControllerInputService, ControllerInputService>();

            services.AddSingleton<IMusicPlayerService, MusicPlayerService>();

            try
            {
                _serviceProvider = services.BuildServiceProvider();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to build service provider. The application cannot start.",
                    ex);
            }
        }

        private T GetRequiredService<T>() where T : class
        {
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException("Service provider has not been initialized");
            }

            return _serviceProvider.GetRequiredService<T>();
        }

        private T GetService<T>() where T : class
        {
            return _serviceProvider?.GetService<T>();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                _logger = GetService<ILogger<App>>();

                if (Window.Current == null)
                {
                    return;
                }

                if (Window.Current.Content is not RootContainer rootContainer)
                {
                    rootContainer = new RootContainer();
                    rootContainer.MainFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootContainer;
                }

                var rootFrame = rootContainer.MainFrame;

                if (!args.PrelaunchActivated)
                {
                    if (rootFrame.Content == null)
                    {
                        INavigationService navigationService = null;
                        try
                        {
                            if (_serviceProvider == null)
                            {
                                return;
                            }

                            navigationService = GetRequiredService<INavigationService>();
                            navigationService.Initialize(rootFrame);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to initialize NavigationService with the root frame");
                        }

                        try
                        {
                            GetRequiredService<IUnifiedDeviceService>();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to construct UnifiedDeviceService during startup");
                        }

                        IAuthenticationService authService = null;
                        try
                        {
                            authService = GetService<IAuthenticationService>();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to resolve AuthenticationService; continuing unauthenticated");
                        }

                        if (authService == null)
                        {
                            try
                            {
                                if (navigationService != null)
                                {
                                    navigationService.Navigate(typeof(ServerSelectionPage), args.Arguments);
                                }
                                else
                                {
                                    CreateBasicErrorPage(rootFrame,
                                        "Navigation service is unavailable. Please restart the application.");
                                }
                            }
                            catch (Exception)
                            {
                                CreateBasicErrorPage(rootFrame,
                                    "Authentication service is unavailable. Please restart the application.");
                            }
                        }
                        else
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(authService.ServerUrl) &&
                                    (authService.HasMultipleSavedProfiles ||
                                     (string.IsNullOrEmpty(authService.AccessToken) && authService.GetSavedProfiles().Count > 0)))
                                {
                                    // Profiles on this device: several, ask who's watching; one and
                                    // nobody signed in (the other signed out), offer it rather than
                                    // the server page. No active token needed; the picker routes
                                    // tokenless profiles through the login flow.
                                    navigationService.Navigate(typeof(ProfileSelectionPage), args.Arguments);
                                }
                                else if (string.IsNullOrEmpty(authService.ServerUrl) ||
                                         string.IsNullOrEmpty(authService.AccessToken))
                                {
                                    navigationService.Navigate(typeof(ServerSelectionPage), args.Arguments);
                                }
                                else
                                {
                                    // Navigate immediately — token looks valid from stored credentials
                                    navigationService.Navigate(typeof(MainPage), args.Arguments);

                                    // Validate token in background; redirect to login only if actually invalid
                                    var launchArgs = args.Arguments;
                                    var capturedNavService = navigationService;
                                    var capturedAuthService = authService;
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var sessionTask = capturedAuthService.RestoreLastSessionAsync();
                                            var timeoutTask = Task.Delay(RetryConstants.SessionRestoreTimeoutMs);
                                            var completedTask = await Task.WhenAny(sessionTask, timeoutTask)
                                                .ConfigureAwait(false);

                                            if (completedTask == timeoutTask)
                                            {
                                                // Server slow or unreachable — not a confirmed invalid token.
                                                // Leave the user on MainPage; content requests will surface their own errors.
                                                return;
                                            }

                                            await sessionTask.ConfigureAwait(false);

                                            // A 401 is the only case that clears the token (via ClearInvalidCredentials).
                                            // Any other failure (network error, exception) leaves it intact — don't redirect.
                                            if (string.IsNullOrEmpty(capturedAuthService.AccessToken))
                                            {
                                                await UiHelper.RunOnUIThreadAsync(() =>
                                                    capturedNavService.Navigate(typeof(ServerSelectionPage),
                                                        launchArgs), _logger);
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            // Transient error — not a confirmed revocation. Don't redirect.
                                        }
                                    });
                                }
                            }
                            catch (Exception)
                            {
                                if (!navigationService.Navigate(typeof(ServerSelectionPage), args.Arguments))
                                {
                                    CreateBasicErrorPage(rootFrame,
                                        "Failed to initialize. Please restart the application.");
                                }
                            }
                        }
                    }

                    try
                    {
                        var applicationView = ApplicationView.GetForCurrentView();
                        applicationView.SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
                    }
                    catch (Exception)
                    {
                        // Non-critical, continue
                    }

                    Window.Current.Activate();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize application");

                try
                {
                    if (Window.Current.Content is not RootContainer rootContainer)
                    {
                        rootContainer = new RootContainer();
                        Window.Current.Content = rootContainer;
                    }

                    CreateBasicErrorPage(rootContainer.MainFrame,
                        $"The application failed to start properly.\n\nError: {ex.Message}\n\nPlease restart the application.");
                    Window.Current.Activate();
                }
                catch (Exception)
                {
                    try
                    {
                        var dialog = new MessageDialog(
                            "Failed to initialize application. Please try restarting.",
                            "Critical Error");
                        await dialog.ShowAsync();
                    }
                    catch (Exception)
                    {
                        // Nothing left that can show anything
                    }
                }
            }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                _logger?.LogInformation("App suspending");
                await ReportVideoStoppedAsync("suspension");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to handle Suspending");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e)
        {
            // Only music keeps the app alive in the background (EnteredBackground exits otherwise)
            _logger?.LogInformation("App resuming from suspension");
        }

        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                // The player page pauses itself when its window is hidden
                _logger?.LogInformation("App entered background");
                if (!IsMusicPlaying())
                {
                    _logger?.LogInformation("No music playing - exiting for background transition");
                    await ReportVideoStoppedAsync("exit");
                    await ExitAppAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to handle EnteredBackground");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            _logger?.LogInformation("App leaving background");
        }

        // Leaving the player page is the only other place a video stop is reported. Without
        // this, exiting or suspending mid-video leaves the server holding the session until
        // its idle timeout. Bounded: both callers are on a deadline and the report is best
        // effort. A second report for the same session is ignored by the service.
        private async Task ReportVideoStoppedAsync(string reason)
        {
            try
            {
                var rootFrame = (Window.Current?.Content as RootContainer)?.MainFrame;
                if (rootFrame?.Content is MediaPlayerPage mediaPlayerPage)
                {
                    _logger?.LogInformation("Reporting video playback stopped before {Reason}", reason);
                    var report = mediaPlayerPage.ReportPlaybackStoppedAsync();
                    if (await Task.WhenAny(report, Task.Delay(VideoStopReportTimeout)) != report)
                    {
                        _logger?.LogWarning("Video stop report did not finish within {Timeout} before {Reason}",
                            VideoStopReportTimeout, reason);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to report video playback stopped before {Reason}", reason);
            }
        }

        private bool IsMusicPlaying()
        {
            var musicPlayerService = GetService<IMusicPlayerService>();
            return musicPlayerService?.IsPlaying == true;
        }

        private Task ExitAppAsync()
        {
            return UiHelper.RunOnUIThreadAsync(
                () => Application.Current.Exit(),
                logger: _logger);
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            // The unhandled-exception handler logs it, inner exception included
            throw new InvalidOperationException($"Failed to load page {e.SourcePageType.FullName}", e.Exception);
        }

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var logger = GetService<ILogger<App>>();

                try
                {
                    AppMemory.Log(logger, "At crash", LogLevel.Critical);
                }
                catch
                {
                    // Intentionally silent: this runs inside the unhandled-exception
                    // handler, where logging the failure could itself throw and mask
                    // the original crash. The memory reading is diagnostic only.
                }

                logger?.LogCritical("Exception Type: {ExceptionGetType}", e.Exception?.GetType().FullName);
                logger?.LogCritical("Exception Message: {ExceptionMessage}", e.Exception?.Message);
                logger?.LogCritical("Exception Source: {ExceptionSource}", e.Exception?.Source);
                logger?.LogCritical("Exception HResult: 0x{ExceptionHResult:X8}", e.Exception?.HResult);

                if (e.Exception is XamlParseException xamlEx)
                {
                    logger?.LogCritical("XAML Parse Exception: {ErrorMessage}", xamlEx.Message);
                    if (xamlEx.InnerException != null)
                    {
                        logger?.LogCritical("Inner Exception: {InnerExceptionMessage}", xamlEx.InnerException.Message);
                        logger?.LogCritical("Inner Exception Type: {InnerExceptionGetType}", xamlEx.InnerException.GetType().FullName);
                        logger?.LogCritical("Stack Trace: {InnerExceptionStackTrace}", xamlEx.InnerException.StackTrace);
                    }

                    logger?.LogCritical("XAML Stack Trace: {XamlExStackTrace}", xamlEx.StackTrace);
                }

                // Media playback fails as COM exceptions; the HResult is what names the failure
                if (e.Exception is COMException comEx)
                {
                    logger?.LogCritical("COM Exception HResult: 0x{ComExHResult:X8}", comEx.HResult);
                    logger?.LogCritical("COM Exception ErrorCode: {ComExErrorCode}", comEx.ErrorCode);
                }

                if (e.Exception?.InnerException != null)
                {
                    logger?.LogCritical("Inner Exception Type: {InnerExceptionGetType}", e.Exception.InnerException.GetType().FullName);
                    logger?.LogCritical("Inner Exception: {InnerExceptionMessage}", e.Exception.InnerException.Message);
                    logger?.LogCritical("Inner Exception HResult: 0x{InnerExceptionHResult:X8}", e.Exception.InnerException.HResult);
                }

                logger?.LogCritical(e.Exception, "Unhandled exception occurred - Full stack trace");

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to log unhandled exception: {ex}");
                Debug.WriteLine($"Original exception: {e?.Exception}");
                e.Handled = true;
            }
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                var logger = GetService<ILogger<App>>();
                logger?.LogCritical(e.Exception, "Unobserved task exception occurred");

                e.SetObserved();
            }
            catch (Exception)
            {
                e.SetObserved();
            }
        }

        private void CreateBasicErrorPage(Frame frame, string message)
        {
            try
            {
                var errorPage = new Page();
                var stackPanel = new StackPanel
                {
                    Margin = new Thickness(20),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var titleText = new TextBlock
                {
                    Text = "Application Error",
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 20),
                    TextAlignment = TextAlignment.Center
                };

                var messageText = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20),
                    MaxWidth = 600,
                    TextAlignment = TextAlignment.Center
                };

                var restartButton = new Button
                {
                    Content = "Exit Application",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(20, 10, 20, 10)
                };
                restartButton.Click += (s, e) => Application.Current.Exit();

                stackPanel.Children.Add(titleText);
                stackPanel.Children.Add(messageText);
                stackPanel.Children.Add(restartButton);
                errorPage.Content = stackPanel;

                frame.Content = errorPage;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to render the fallback error page");
            }
        }
    }
}
