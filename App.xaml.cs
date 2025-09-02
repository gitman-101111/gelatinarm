using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Controls;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions.Authentication;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using UnhandledExceptionEventArgs = Windows.UI.Xaml.UnhandledExceptionEventArgs;

namespace Gelatinarm
{
    public sealed partial class App : Application
    {
        private IAuthenticationService _authService;
        private IUnifiedDeviceService _deviceInfo;
        private ILogger<App> _logger;
        private volatile IServiceProvider _serviceProvider;


        public App()
        {
            try
            {
                // App constructor started

                // Pre-init diagnostics
                try
                {
                    InitializeComponent();
                }
                catch (Exception)
                {
                    // Don't try to access Resources - it may throw
                }
            }
            catch (Exception)
            {
                // Don't throw - try to continue with basic functionality
            }

            // Set up event handlers - these are critical for app lifecycle
            try
            {
                Suspending += OnSuspending;
                Resuming += OnResuming;
                UnhandledException += App_UnhandledException;
                TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
                // Event handlers registered
            }
            catch (Exception)
            {
                // Continue anyway
            }

            // Set up UI preferences
            try
            {
                RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            }
            catch (Exception)
            {
                // Failed to set UI preferences
                // Non-critical - continue
            }

            // Configure services - this is critical but we'll handle failures in OnLaunched
            try
            {
                ConfigureServices();
                // Services configured
            }
            catch (Exception)
            {
                // We'll handle this in OnLaunched
            }
        }

        public static new App Current => (App)Application.Current;

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

        private void ConfigureServices()
        {
            if (_serviceProvider != null)
            {
                return;
            }

            var services = new ServiceCollection();

            // Add logging first - this is critical for error reporting
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

            // Add preferences service - critical for app functionality
            services.AddSingleton<IPreferencesService, PreferencesService>();

            // Register IUnifiedDeviceService early as it's needed by multiple services
            // Register IUnifiedDeviceService early as it's needed by multiple services
            try
            {
                services.AddSingleton<IUnifiedDeviceService>(provider =>
                {
                    CoreDispatcher dispatcher = null;
                    // Dispatcher will be set later after UI initialization

                    var logger =
                        provider.GetService<ILogger<UnifiedDeviceService>>(); // Use GetService to avoid failure
                    var service = new UnifiedDeviceService(logger, dispatcher);
                    return service;
                });
            }
            catch (Exception)
            {
                // Continue - some features may not work but app can still run
            }

            // Configure HttpClientFactory for proper connection pooling and socket management
            services.AddHttpClient("JellyfinClient", (serviceProvider, client) =>
            {
                // Configure default headers
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"{BrandingConstants.USER_AGENT}/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

                // Set timeout from preferences
                var preferencesService = serviceProvider.GetService<IPreferencesService>();
                var timeoutSeconds = SystemConstants.DEFAULT_TIMEOUT_SECONDS;
                if (preferencesService != null)
                {
                    try
                    {
                        timeoutSeconds = preferencesService.GetValue(PreferenceConstants.ConnectionTimeout,
                            SystemConstants.DEFAULT_TIMEOUT_SECONDS);
                    }
                    catch { }
                }
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var handler = new HttpClientHandler();

                // Check certificate validation preference
                var preferencesService = serviceProvider.GetService<IPreferencesService>();
                var ignoreCertErrors = false;
                if (preferencesService != null)
                {
                    try
                    {
                        ignoreCertErrors = preferencesService.GetValue("IgnoreCertificateErrors", false);
                    }
                    catch { }
                }

                if (ignoreCertErrors)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            });

            // Register JellyfinSdkSettings - needed for proper SDK initialization
            services.AddSingleton(provider =>
            {
                var deviceService = provider.GetService<IUnifiedDeviceService>();
                var settings = new JellyfinSdkSettings();

                var deviceId = deviceService?.GetDeviceId() ?? Guid.NewGuid().ToString();
                var deviceName = deviceService?.GetDeviceName() ?? "Xbox";

                settings.Initialize(
                    BrandingConstants.APP_NAME,
                    "1.0.0",
                    deviceName,
                    deviceId
                );

                return settings;
            });

            // Register authentication provider - critical for API access
            services.AddSingleton<IAuthenticationProvider>(provider =>
            {
                var settings = provider.GetRequiredService<JellyfinSdkSettings>();
                return new JellyfinAuthenticationProvider(settings);
            });

            // Register Jellyfin SDK Request Adapter - critical for API communication
            services.AddSingleton<JellyfinRequestAdapter>(provider =>
            {
                var logger = provider.GetService<ILogger<App>>();
                try
                {
                    var authProvider = provider.GetRequiredService<IAuthenticationProvider>();
                    var settings = provider.GetRequiredService<JellyfinSdkSettings>();
                    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                    var preferencesService = provider.GetService<IPreferencesService>();

                    // Get HttpClient from factory - this properly manages connection pooling
                    var httpClient = httpClientFactory.CreateClient("JellyfinClient");
                    logger?.LogInformation("Using HttpClient from factory with connection pooling");

                    // Create JellyfinRequestAdapter with settings
                    var requestAdapter = new JellyfinRequestAdapter(authProvider, settings, httpClient);

                    // Try to set server URL if available
                    try
                    {
                        if (preferencesService != null)
                        {
                            var serverUrl = preferencesService.GetValue<string>("ServerUrl");
                            if (!string.IsNullOrEmpty(serverUrl))
                            {
                                logger?.LogInformation($"Setting initial server URL from preferences: {serverUrl}");
                                settings.SetServerUrl(serverUrl);
                            }
                            else
                            {
                                logger?.LogDebug("No server URL found in preferences");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to set initial server URL");
                    }

                    return requestAdapter;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to create JellyfinRequestAdapter");
                    throw; // This is critical - cannot continue without request adapter
                }
            });

            // Register Jellyfin SDK API client - critical for API operations
            services.AddSingleton(provider =>
            {
                var requestAdapter = provider.GetRequiredService<JellyfinRequestAdapter>();
                return new JellyfinApiClient(requestAdapter);
            });

            // JellyfinApiClient is already registered above - services should use it directly

            // Register IAuthenticationService AFTER JellyfinApiClient - critical for app functionality
            try
            {
                services.AddSingleton<IAuthenticationService>(provider =>
                {
                    var logger = provider.GetService<ILogger<AuthenticationService>>();

                    var preferencesService = provider.GetRequiredService<IPreferencesService>();

                    var deviceInfoService = provider.GetRequiredService<IUnifiedDeviceService>();

                    var cacheManagerService = provider.GetRequiredService<ICacheManagerService>();

                    var sdkSettings = provider.GetRequiredService<JellyfinSdkSettings>();

                    var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                    var authService = new AuthenticationService(logger, preferencesService, deviceInfoService,
                        cacheManagerService, sdkSettings, apiClient);

                    return authService;
                });
            }
            catch (Exception)
            {
                // Continue - app can function in limited mode
            }

            // Register UserProfileService AFTER AuthenticationService
            try
            {
                services.AddSingleton<IUserProfileService>(provider =>
                {
                    var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                    var authService = provider.GetRequiredService<IAuthenticationService>();
                    var logger = provider.GetService<ILogger<UserProfileService>>();
                    return new UserProfileService(logger, apiClient, authService);
                });
            }
            catch (Exception)
            {
                // Continue - some features may not work
            }


            // Register MediaDiscoveryService - important for content discovery
            try
            {
                services.AddSingleton<IMediaDiscoveryService>(provider =>
                {
                    var logger = provider.GetService<ILogger<MediaDiscoveryService>>();
                    var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                    var authService = provider.GetRequiredService<IAuthenticationService>();
                    var userProfileService = provider.GetRequiredService<IUserProfileService>();
                    var navigationStateService = provider.GetRequiredService<INavigationStateService>();
                    var cacheManager = provider.GetRequiredService<ICacheManagerService>();
                    return new MediaDiscoveryService(logger, apiClient, authService, userProfileService,
                        navigationStateService, cacheManager);
                });
            }
            catch (Exception)
            {
                // Continue - content discovery may be limited
            }

            // Register Settings ViewModels
            services.AddTransient<ServerSettingsViewModel>();
            services.AddTransient<PlaybackSettingsViewModel>();
            services.AddTransient<NetworkSettingsViewModel>();
            services.AddTransient<SettingsViewModel>();

            // Register other ViewModels
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
            services.AddTransient<ServerSelectionViewModel>();
            services.AddTransient<QuickConnectInstructionsViewModel>();
            services.AddTransient<MediaPlayerViewModel>();

            // Register MediaPlaybackService with careful dependency resolution
            try
            {
                services.AddSingleton<IMediaPlaybackService>(provider =>
                {
                    var logger = provider.GetService<ILogger<MediaPlaybackService>>();
                    try
                    {
                        // Critical dependencies
                        var userProfileService = provider.GetRequiredService<IUserProfileService>();
                        var preferencesService = provider.GetRequiredService<IPreferencesService>();

                        // Optional dependencies - continue without them if they fail
                        IDeviceProfileService deviceProfileService = null;
                        IMediaOptimizationService mediaOptimizationService = null;
                        IAuthenticationService authService = null;
                        IMemoryMonitor memoryMonitor = null;
                        INetworkMonitor networkMonitor = null;


                        try
                        {
                            deviceProfileService = provider.GetService<IDeviceProfileService>();
                            if (deviceProfileService != null)
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to get DeviceProfileService - continuing without it");
                        }

                        try
                        {
                            memoryMonitor = provider.GetService<IMemoryMonitor>();
                            if (memoryMonitor != null)
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to get MemoryMonitor - continuing without it");
                        }

                        try
                        {
                            networkMonitor = provider.GetService<INetworkMonitor>();
                            if (networkMonitor != null)
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to get NetworkMonitor - continuing without it");
                        }

                        try
                        {
                            mediaOptimizationService = provider.GetService<IMediaOptimizationService>();
                            if (mediaOptimizationService != null)
                            {
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to get MediaOptimizationService - continuing without it");
                        }

                        try
                        {
                            authService = provider.GetService<IAuthenticationService>();
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to get AuthenticationService - continuing without it");
                        }


                        var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                        var deviceServiceInterface = provider.GetRequiredService<IUnifiedDeviceService>();
                        logger?.LogInformation("Using SDK-based MediaPlaybackService");

                        // Create MediaPlaybackService without musicPlayerService to avoid circular dependency
                        return new MediaPlaybackService(apiClient, userProfileService, preferencesService,
                            deviceServiceInterface, deviceProfileService, mediaOptimizationService,
                            authService, logger);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Failed to create MediaPlaybackService");
                        throw;
                    }
                });
            }
            catch (Exception)
            {
                // Continue without media playback service - app can still function for browsing
            }


            // Register unified SystemMonitorService for all monitoring needs
            services.AddSingleton(provider =>
            {
                CoreDispatcher dispatcher = null;
                try
                {
                    if (CoreApplication.Views.Any())
                    {
                        dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
                    }
                }
                catch (Exception)
                {
                }

                var logger = provider.GetRequiredService<ILogger<SystemMonitorService>>();
                var preferencesService = provider.GetRequiredService<IPreferencesService>();
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var userProfileService = provider.GetService<IUserProfileService>();
                return new SystemMonitorService(dispatcher, logger, preferencesService, httpClientFactory, userProfileService);
            });
            services.AddSingleton<ISystemMonitorService>(sp => sp.GetRequiredService<SystemMonitorService>());
            services.AddSingleton<IMemoryMonitor>(sp => sp.GetRequiredService<SystemMonitorService>());
            services.AddSingleton<INetworkMonitor>(sp => sp.GetRequiredService<SystemMonitorService>());

            // Register consolidated MediaOptimizationService
            services.AddSingleton<IEpisodeQueueService>(provider =>
            {
                var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                var logger = provider.GetRequiredService<ILogger<EpisodeQueueService>>();
                var userProfileService = provider.GetRequiredService<IUserProfileService>();
                return new EpisodeQueueService(apiClient, logger, userProfileService);
            });

            services.AddSingleton<IImageLoadingService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<ImageLoadingService>>();
                return new ImageLoadingService(logger);
            });

            services.AddSingleton<IUserDataService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<UserDataService>>();
                var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                var userProfileService = provider.GetRequiredService<IUserProfileService>();
                return new UserDataService(logger, apiClient, userProfileService);
            });

            services.AddSingleton<IMediaOptimizationService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<MediaOptimizationService>>();
                var preferencesService = provider.GetRequiredService<IPreferencesService>();
                var deviceService = provider.GetRequiredService<IUnifiedDeviceService>();
                var memoryMonitor = provider.GetRequiredService<IMemoryMonitor>();
                var networkMonitor = provider.GetRequiredService<INetworkMonitor>();
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                return new MediaOptimizationService(logger, httpClientFactory, preferencesService, deviceService, memoryMonitor,
                    networkMonitor);
            });

            services.AddSingleton<IDeviceProfileService, DeviceProfileService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IErrorHandlingService, ErrorHandlingService>();

            // Register SDK infrastructure services
            // UnifiedDeviceService now handles all device-related functionality
            // JellyfinApiClient is already registered above - services should use it directly

            // Register NavigationService - critical for app navigation
            services.AddSingleton<NavigationService>();
            services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
            services.AddSingleton<INavigationStateService>(sp => sp.GetRequiredService<NavigationService>());

            // Register CacheManagerService
            services.AddSingleton<ICacheManagerService, CacheManagerService>();


            // Register new decomposed services for MusicPlayer
            services.AddSingleton<IPlaybackQueueService, PlaybackQueueService>();
            services.AddSingleton<IMediaControlService, MediaControlService>();
            services.AddSingleton<ISystemMediaIntegrationService, SystemMediaIntegrationService>();
            services.AddSingleton<IPlaybackControlService, PlaybackControlService>();
            services.AddSingleton<ISubtitleService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<SubtitleService>>();
                var playbackControlService = provider.GetRequiredService<IPlaybackControlService>();
                var preferencesService = provider.GetRequiredService<IPreferencesService>();
                var mediaControlService = provider.GetRequiredService<IMediaControlService>();
                var authService = provider.GetRequiredService<IAuthenticationService>();
                var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                return new SubtitleService(logger, playbackControlService, preferencesService,
                    mediaControlService, authService, apiClient);
            });
            services.AddSingleton<IMediaNavigationService, MediaNavigationService>();
            services.AddSingleton<IPlaybackStatisticsService, PlaybackStatisticsService>();
            services.AddTransient<IMediaControllerService, MediaControllerService>();
            services.AddSingleton<ISkipSegmentService, SkipSegmentService>();

            services.AddSingleton<IMusicPlayerService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<MusicPlayerService>>();
                var authService = provider.GetRequiredService<IAuthenticationService>();
                var userProfileService = provider.GetRequiredService<IUserProfileService>();
                var mediaPlaybackService = provider.GetRequiredService<IMediaPlaybackService>();
                var deviceService = provider.GetRequiredService<IUnifiedDeviceService>();
                var preferencesService = provider.GetRequiredService<IPreferencesService>();
                var mediaOptimizationService = provider.GetRequiredService<IMediaOptimizationService>();
                var queueService = provider.GetRequiredService<IPlaybackQueueService>();
                var mediaControlService = provider.GetRequiredService<IMediaControlService>();
                var systemMediaService = provider.GetRequiredService<ISystemMediaIntegrationService>();
                var apiClient = provider.GetRequiredService<JellyfinApiClient>();
                return new MusicPlayerService(logger, provider, apiClient, authService, userProfileService, mediaPlaybackService,
                    deviceService, preferencesService, mediaOptimizationService, queueService, mediaControlService,
                    systemMediaService);
            });

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

        private void ConnectControllerServices()
        {
            try
            {
                var unifiedDeviceService = _serviceProvider.GetRequiredService<IUnifiedDeviceService>();
                var logger = _serviceProvider.GetService<ILogger<App>>();
                AsyncHelper.FireAndForget(() => unifiedDeviceService.StartMonitoringAsync(), logger, typeof(App));
            }
            catch (Exception)
            {
            }
        }

        private void InitializeCoreServices()
        {
            if (_serviceProvider == null)
            {
                return;
            }


            var failedServices = new List<string>();

            // Try to initialize authentication service
            try
            {
                _authService = _serviceProvider.GetService<IAuthenticationService>();
                if (_authService == null)
                {
                    failedServices.Add("AuthenticationService");
                }
                else
                {
                    // SDK client is now injected directly through constructor

                    // Wire up MusicPlayerService to MediaPlaybackService
                    try
                    {
                        var mediaPlaybackService = _serviceProvider.GetService<IMediaPlaybackService>();
                        var musicPlayerService = _serviceProvider.GetService<IMusicPlayerService>();
                        if (mediaPlaybackService is MediaPlaybackService mediaPlaybackImpl && musicPlayerService != null)
                        {
                            mediaPlaybackImpl.SetMusicPlayerService(musicPlayerService);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                failedServices.Add("AuthenticationService");
            }

            // Try to initialize device info service
            try
            {
                _deviceInfo = _serviceProvider.GetService<IUnifiedDeviceService>();
                if (_deviceInfo == null)
                {
                    failedServices.Add("UnifiedDeviceService");
                }
            }
            catch (Exception)
            {
                failedServices.Add("UnifiedDeviceService");
            }

            // Try to initialize logging service (non-critical)
            try
            {
                _logger = _serviceProvider.GetService<ILogger<App>>();
            }
            catch (Exception)
            {
                _logger = null;
            }

            // Try to verify navigation service is available
            try
            {
                var navigationService = _serviceProvider.GetService<INavigationService>();
                if (navigationService == null)
                {
                    failedServices.Add("NavigationService");
                }
            }
            catch (Exception)
            {
                failedServices.Add("NavigationService");
            }

            // If critical services failed, log but continue - we'll handle in OnLaunched
            if (failedServices.Any())
            {
                var failedList = string.Join(", ", failedServices);
                _logger?.LogWarning($"Core services initialization incomplete. Failed services: {failedList}");
            }
        }


        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                try
                {
                    InitializeCoreServices();
                }
                catch (Exception)
                {
                    // Continue - we'll handle missing services gracefully
                }

                try
                {
                    if (_logger != null)
                    {
                        var loggingTask = Task.CompletedTask;
                        var timeoutTask = Task.Delay(RetryConstants.LOGGING_INIT_TIMEOUT_MS);
                        var completedTask = await Task.WhenAny(loggingTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                        }
                        else
                        {
                            await loggingTask;
                        }
                    }
                }
                catch (Exception)
                {
                }

                if (Window.Current == null)
                {
                    return;
                }

                var rootContainer = Window.Current.Content as RootContainer;
                Frame rootFrame = null;

                if (rootContainer == null)
                {
                    rootContainer = new RootContainer();

                    rootFrame = rootContainer?.MainFrame;
                    if (rootFrame == null)
                    {
                        return;
                    }

                    rootFrame.NavigationFailed += OnNavigationFailed;

                    if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                    {
                        try
                        {
                            var preferencesService = _serviceProvider.GetRequiredService<IPreferencesService>();
                            var state = preferencesService.GetValue<string>("AppState");
                            if (!string.IsNullOrEmpty(state))
                            {
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }

                    Window.Current.Content = rootContainer;
                }
                else
                {
                    rootFrame = rootContainer.MainFrame;
                    if (rootFrame == null)
                    {
                        return;
                    }
                }

                if (e.PrelaunchActivated == false)
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

                            navigationService = _serviceProvider.GetRequiredService<INavigationService>();
                            if (navigationService == null)
                            {
                                return;
                            }

                            navigationService.Initialize(rootFrame);
                        }
                        catch (Exception)
                        {
                        }

                        try
                        {
                            var unifiedDeviceService = _serviceProvider.GetRequiredService<IUnifiedDeviceService>();
                        }
                        catch (Exception)
                        {
                        }

                        // Check authentication and navigate accordingly
                        IAuthenticationService authService = null;
                        try
                        {
                            authService = _serviceProvider.GetService<IAuthenticationService>();
                        }
                        catch (Exception)
                        {
                        }

                        if (authService == null)
                        {
                            try
                            {
                                if (navigationService != null)
                                {
                                    navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                }
                                else
                                {
                                    CreateBasicErrorPage(rootFrame,
                                        "Navigation service is unavailable. Please restart the application.");
                                }
                            }
                            catch (Exception)
                            {
                                // As a last resort, try to navigate to a basic error page
                                CreateBasicErrorPage(rootFrame,
                                    "Authentication service is unavailable. Please restart the application.");
                            }
                        }
                        else
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(authService.ServerUrl) ||
                                    string.IsNullOrEmpty(authService.AccessToken))
                                {
                                    navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                }
                                else
                                {
                                    try
                                    {
                                        var sessionTask = authService.RestoreLastSessionAsync();
                                        var timeoutTask = Task.Delay(RetryConstants.SESSION_RESTORE_TIMEOUT_MS);
                                        var completedTask = await Task.WhenAny(sessionTask, timeoutTask);

                                        if (completedTask == timeoutTask)
                                        {
                                            navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                        }
                                        else
                                        {
                                            var sessionRestored = await sessionTask;
                                            if (sessionRestored)
                                            {
                                                navigationService.Navigate(typeof(MainPage), e.Arguments);
                                            }
                                            else
                                            {
                                                navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                            }
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                try
                                {
                                    navigationService.Navigate(typeof(ServerSelectionPage), e.Arguments);
                                }
                                catch (Exception)
                                {
                                    CreateBasicErrorPage(rootFrame,
                                        "Failed to initialize. Please restart the application.");
                                }
                            }
                        }
                    }

                    if (Window.Current != null)
                    {
                        // Skip resource check on Xbox to avoid potential crashes

                        // Configure Xbox-specific view settings
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

                        // Set up system navigation manager for back button
                        try
                        {
                            var systemNavigationManager = SystemNavigationManager.GetForCurrentView();
                            systemNavigationManager.BackRequested += OnBackRequested;
                        }
                        catch (Exception)
                        {
                            // Non-critical, continue
                        }

                        // Add a small delay to let XAML fully render
                        // Wait for XAML to stabilize
                        await Task.Delay(RetryConstants.XAML_STABILIZATION_DELAY_MS);
                        // XAML stabilization complete

                        // Add more debugging to isolate when errors occur
                        // Check visual tree
                        try
                        {
                            var visualTreeHelper = VisualTreeHelper.GetChildrenCount(Window.Current.Content);
                            // Visual tree checked
                        }
                        catch (Exception)
                        {
                            // Visual tree check failed
                        }
                    }

                    // Window.Current is null
                    try
                    {
                        // Call ConnectControllerServices
                        ConnectControllerServices();
                        // ConnectControllerServices completed
                    }
                    catch (Exception)
                    {
                        // ConnectControllerServices failed
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.LogError(ex, "Failed to initialize application");
                }

                try
                {
                    var rootContainer = Window.Current.Content as RootContainer;
                    if (rootContainer == null)
                    {
                        rootContainer = new RootContainer();
                        Window.Current.Content = rootContainer;
                    }

                    var rootFrame = rootContainer.MainFrame;
                    if (rootFrame == null)
                    {
                        // MainFrame is null
                        return;
                    }

                    CreateBasicErrorPage(rootFrame,
                        $"The application failed to start properly.\n\nError: {ex.Message}\n\nPlease restart the application.");
                    Window.Current.Activate();
                }
                catch (Exception)
                {
                    // Failed to show error page
                    try
                    {
                        var dialog = new MessageDialog(
                            "Failed to initialize application. Please try restarting.",
                            "Critical Error");
                        await dialog.ShowAsync();
                    }
                    catch (Exception)
                    {
                        // Ultimate fallback - we can't even show a dialog
                        // Failed to show error dialog
                    }
                }
            }
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                var state = new Dictionary<string, object>();
                var preferencesService = _serviceProvider.GetRequiredService<IPreferencesService>();
                var rootFrame = Window.Current.Content as Frame;

                if (rootFrame != null)
                {
                    // Only save the current page type, not the full back stack
                    state["CurrentPage"] = rootFrame.Content?.GetType().Name;

                    // Check if we're currently on the MediaPlayerPage (video playback)
                    if (rootFrame.Content is MediaPlayerPage mediaPlayerPage)
                    {
                        _logger?.LogInformation("App suspending while video is playing - video will be paused");
                        // The MediaPlayerPage window deactivation handler will pause the video
                        // We just need to save the state that we were on this page
                        state["WasPlayingVideo"] = true;
                    }
                }

                // Check if audio is playing via MusicPlayerService
                var musicPlayerService = _serviceProvider.GetRequiredService<IMusicPlayerService>();
                if (musicPlayerService.IsPlaying)
                {
                    var playbackState = new Dictionary<string, object>
                    {
                        ["ItemId"] = musicPlayerService.CurrentItem?.Id?.ToString(),
                        ["Position"] = musicPlayerService.MediaPlayer?.PlaybackSession?.Position.ToString(),
                        ["IsPlaying"] = musicPlayerService.IsPlaying
                    };
                    state["PlaybackState"] = playbackState;

                    _logger?.LogInformation("Audio playback state saved, music will continue in background");
                }

                // Don't save preferences - they're already persisted automatically

                // Use optimized serialization options
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = false, // Compact JSON
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var serializedState = JsonSerializer.Serialize(state, jsonOptions);
                preferencesService.SetValue("AppState", serializedState);

                _logger?.LogInformation("Saved application state");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save application state");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            try
            {
                _logger?.LogInformation("App resuming from suspension");

                // Check if we're on the MediaPlayerPage (video playback)
                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame?.Content is MediaPlayerPage mediaPlayerPage)
                {
                    _logger?.LogInformation(
                        "App resuming while on MediaPlayerPage - video playback will resume if it was playing before");
                    // The MediaPlayerPage window activation handler will resume the video if needed
                }

                var musicPlayerService = _serviceProvider.GetService<IMusicPlayerService>();
                if (musicPlayerService?.IsPlaying == true && musicPlayerService.MediaPlayer?.PlaybackSession != null)
                {
                    var currentPosition = musicPlayerService.MediaPlayer.PlaybackSession.Position;
                    _logger?.LogInformation($"Music is still playing at position: {currentPosition}");
                }

                _logger?.LogInformation("App resumed, network monitoring continues automatically");
            }
            catch (Exception ex)
            {
                var logger = _logger ?? _serviceProvider?.GetService<ILogger<App>>();
                logger?.LogError(ex, "Error during app resume");
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            _logger?.LogError(new Exception($"Failed to load {e.SourcePageType.FullName}"), "Navigation");
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var logger = _serviceProvider?.GetService<ILogger<App>>();

                // Log memory usage at time of crash
                try
                {
                    var memoryUsage = Windows.System.MemoryManager.AppMemoryUsage / (1024.0 * 1024.0);
                    var memoryLimit = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024.0 * 1024.0);
                    logger?.LogCritical($"Memory at crash: {memoryUsage:F2} MB / {memoryLimit:F2} MB");
                }
                catch { }

                // Log exception details
                logger?.LogCritical($"Exception Type: {e.Exception?.GetType().FullName}");
                logger?.LogCritical($"Exception Message: {e.Exception?.Message}");
                logger?.LogCritical($"Exception Source: {e.Exception?.Source}");
                logger?.LogCritical($"Exception HResult: 0x{e.Exception?.HResult:X8}");

                if (e.Exception is XamlParseException xamlEx)
                {
                    logger?.LogCritical($"XAML Parse Exception: {xamlEx.Message}");
                    if (xamlEx.InnerException != null)
                    {
                        logger?.LogCritical($"Inner Exception: {xamlEx.InnerException.Message}");
                        logger?.LogCritical($"Inner Exception Type: {xamlEx.InnerException.GetType().FullName}");
                        logger?.LogCritical($"Stack Trace: {xamlEx.InnerException.StackTrace}");
                    }

                    logger?.LogCritical($"XAML Stack Trace: {xamlEx.StackTrace}");
                }

                // Check for COM exceptions which are common with media playback
                if (e.Exception is System.Runtime.InteropServices.COMException comEx)
                {
                    logger?.LogCritical($"COM Exception HResult: 0x{comEx.HResult:X8}");
                    logger?.LogCritical($"COM Exception ErrorCode: {comEx.ErrorCode}");
                }

                if (e?.Exception?.InnerException != null)
                {
                    logger?.LogCritical($"Inner Exception Type: {e.Exception.InnerException.GetType().FullName}");
                    logger?.LogCritical($"Inner Exception: {e.Exception.InnerException.Message}");
                    logger?.LogCritical($"Inner Exception HResult: 0x{e.Exception.InnerException.HResult:X8}");
                }

                logger?.LogCritical(e.Exception, "Unhandled exception occurred - Full stack trace");

                // Try to handle the exception to prevent crash
                e.Handled = true;
            }
            catch (Exception ex)
            {
                // Last resort - log to debug output
                System.Diagnostics.Debug.WriteLine($"Failed to log unhandled exception: {ex}");
                System.Diagnostics.Debug.WriteLine($"Original exception: {e?.Exception}");
                e.Handled = true;
            }
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                var logger = _serviceProvider?.GetService<ILogger<App>>();
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
            catch (Exception)
            {
            }
        }


        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            try
            {
                // Don't handle if already handled
                if (e.Handled)
                {
                    return;
                }

                // Check if current page is BasePage - if so, let it handle navigation
                var rootContainer = Window.Current?.Content as RootContainer;
                var frame = rootContainer?.MainFrame;
                if (frame?.Content is BasePage)
                {
                    return;
                }

                // Otherwise, handle as fallback
                var navigationService = _serviceProvider?.GetService<INavigationService>();
                if (navigationService != null && navigationService.CanGoBack)
                {
                    e.Handled = true;
                    navigationService.GoBack();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
