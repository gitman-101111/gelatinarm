using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Unified service for media enhancements and playback optimization
    ///     Combines functionality from MediaEnhancementService and PlaybackOptimizerService
    /// </summary>
    public class MediaOptimizationService : BaseService, IMediaOptimizationService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IUnifiedDeviceService _deviceService;
        private readonly ConcurrentDictionary<string, MediaSource> _mediaSourceCache = new();
        private readonly IMemoryMonitor _memoryMonitor;
        private readonly INetworkMonitor _networkMonitor;
        private readonly IPreferencesService _preferencesService;
        private readonly ConcurrentDictionary<string, Task<MediaSource>> _preloadTasks = new();
        private readonly Task _initializationTask;
        private int _currentBandwidthKbps = 0;
        private bool _hdrOutputEnabled = false;

        // Enhancement state

        // Optimization state
        private volatile int _isOptimizing = 0; // 0 = not optimizing, 1 = optimizing
        private bool _nightModeEnabled = false;
        private bool _spatialAudioEnabled = false;

        public MediaOptimizationService(
            ILogger<MediaOptimizationService> logger,
            IHttpClientFactory httpClientFactory,
            IPreferencesService preferencesService,
            IUnifiedDeviceService deviceService,
            IMemoryMonitor memoryMonitor,
            INetworkMonitor networkMonitor) : base(logger)
        {
            _httpClientFactory = httpClientFactory;
            _preferencesService = preferencesService;
            _deviceService = deviceService;
            _memoryMonitor = memoryMonitor;
            _networkMonitor = networkMonitor;

            // Start initialization but don't await - will be awaited when needed
            _initializationTask = InitializeAsync();
        }

        public bool IsEnhancementEnabled { get; private set; }

        public bool IsOptimizationEnabled { get; private set; }

        public event EventHandler<OptimizationStateChangedEventArgs> OptimizationStateChanged;

        public async Task<MediaSource> CreateAdaptiveMediaSourceAsync(
            string mediaUrl,
            string accessToken,
            bool isAudio,
            IPreferencesService preferences = null)
        {
            await _initializationTask.ConfigureAwait(false);
            
            var context = CreateErrorContext("CreateAdaptiveMediaSource", ErrorCategory.Media);
            try
            {
                Logger.LogInformation(
                    $"Creating AdaptiveMediaSource for {(isAudio ? "audio" : "video")} from URL: {mediaUrl}");

                // For audio files, check if this is a direct file URL (not HLS)
                if (isAudio && !mediaUrl.Contains(".m3u8") && !mediaUrl.Contains("transcodingProtocol=hls"))
                {
                    Logger.LogInformation(
                        "Audio URL appears to be a direct file, using MediaSource.CreateFromUri instead of AdaptiveMediaSource");
                    return MediaSource.CreateFromUri(new Uri(mediaUrl));
                }

                // Create Windows.Web.Http.HttpClient for AdaptiveMediaSource
                var windowsHttpClient = new Windows.Web.Http.HttpClient();

                // Add authorization header if available
                if (!string.IsNullOrEmpty(accessToken))
                {
                    windowsHttpClient.DefaultRequestHeaders.Add("Authorization", $"MediaBrowser Token=\"{accessToken}\"");
                }
                windowsHttpClient.DefaultRequestHeaders.Add("User-Agent", $"{BrandingConstants.USER_AGENT}/1.0");

                var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(mediaUrl), windowsHttpClient).AsTask()
                    .ConfigureAwait(false);

                if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
                {
                    // Configure adaptive streaming properties
                    var bitrateContext = CreateErrorContext("ConfigureBitrates", ErrorCategory.Media);
                    try
                    {
                        var availableBitrates = result.MediaSource.AvailableBitrates;
                        Logger.LogInformation($"Available bitrates count: {availableBitrates?.Count ?? 0}");

                        if (availableBitrates?.Any() == true)
                        {
                            // Always use maximum available bitrate
                            var targetBitrate = 120000000u; // 120Mbps max
                            var selectedBitrate = availableBitrates.FirstOrDefault(); // Default to lowest

                            if (selectedBitrate == 0)
                            {
                                Logger.LogWarning("Available bitrates list is empty when it shouldn't be");
                                selectedBitrate = targetBitrate; // Use target as fallback
                            }

                            // Find the closest available bitrate to our target
                            foreach (var bitrate in availableBitrates)
                            {
                                if (bitrate <= targetBitrate && bitrate > selectedBitrate)
                                {
                                    selectedBitrate = bitrate;
                                }
                            }

                            result.MediaSource.InitialBitrate = selectedBitrate;
                            Logger.LogInformation(
                                $"Set initial bitrate to {selectedBitrate} from available: {string.Join(", ", availableBitrates)}");
                        }

                        // Set desired bitrate range based on available bitrates
                        if (availableBitrates?.Any() == true)
                        {
                            var minAvailableBitrate = availableBitrates.Min();
                            var maxAvailableBitrate = availableBitrates.Max();

                            // Always use max available bitrate range
                            var preferredMax = 120000000u; // Always default to 120Mbps max

                            // Ensure desired max is at least as high as the highest available bitrate
                            var desiredMaxBitrate = Math.Max(preferredMax, maxAvailableBitrate);
                            // Ensure desired min is no higher than the lowest available bitrate
                            var desiredMinBitrate = Math.Min(1000000u, minAvailableBitrate);

                            result.MediaSource.DesiredMaxBitrate = desiredMaxBitrate;
                            result.MediaSource.DesiredMinBitrate = desiredMinBitrate;

                            Logger.LogInformation(
                                $"Configured adaptive streaming: MaxBitrate={result.MediaSource.DesiredMaxBitrate}, MinBitrate={result.MediaSource.DesiredMinBitrate}, Available range: {minAvailableBitrate}-{maxAvailableBitrate}");
                        }
                        else
                        {
                            Logger.LogInformation("No bitrate configuration applied - no available bitrates reported");
                        }
                    }
                    catch (Exception ex)
                    {
                        await ErrorHandler.HandleErrorAsync(ex, bitrateContext, false);
                    }

                    var mediaSource = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
                    Logger.LogInformation(
                        $"Created adaptive media source successfully for {(isAudio ? "audio" : "video")}");
                    return mediaSource;
                }

                Logger.LogError($"AdaptiveMediaSource creation failed with status: {result.Status}");
                throw new Exception($"Failed to create AdaptiveMediaSource: {result.Status}");
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<MediaSource>(ex, context, null, true);
            }
        }

        public async Task<MediaSource> CreateSimpleMediaSourceAsync(
            string mediaUrl,
            string accessToken,
            bool isAudio,
            IPreferencesService preferences = null)
        {
            var context = CreateErrorContext("CreateSimpleMediaSource", ErrorCategory.Media);
            try
            {
                Logger.LogInformation(
                    $"Creating simple MediaSource for {(isAudio ? "audio" : "video")} from URL: {mediaUrl}");

                // Use HttpClient from factory with proper configuration
                var httpClient = _httpClientFactory.CreateClient("JellyfinClient");

                // Create MediaSource directly from URI without adaptive streaming
                var uri = new Uri(mediaUrl);
                var mediaSource = MediaSource.CreateFromUri(uri);

                // Note: MediaSource doesn't have ProtectionManager or direct HttpClient support like AdaptiveMediaSource
                // Authentication must be handled via URL parameters (ApiKey) since we can't set headers
                Logger.LogInformation("Created simple MediaSource successfully (authentication via URL parameters)");

                return mediaSource;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<MediaSource>(ex, context, null, true);
            }
        }

        public void Dispose()
        {
            if (_memoryMonitor != null)
            {
                _memoryMonitor.MemoryPressureChanged -= OnMemoryPressureChanged;
            }

            if (_networkMonitor != null)
            {
                _networkMonitor.BandwidthChanged -= OnBandwidthChanged;
                _networkMonitor.ConnectionQualityChanged -= OnConnectionQualityChanged;
            }

            FireAndForget(() => ClearOptimizationsAsync(), "ClearOptimizationsOnMemoryPressure");
        }

        private async Task InitializeAsync()
        {
            var context = CreateErrorContext("Initialize", ErrorCategory.Media);
            try
            {
                await LoadPreferencesAsync().ConfigureAwait(false);
                SubscribeToMonitors();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadPreferencesAsync()
        {
            var context = CreateErrorContext("LoadPreferences", ErrorCategory.Configuration);
            try
            {
                // Load enhancement preferences
                IsEnhancementEnabled = _preferencesService.GetValue(PreferenceConstants.EnableMediaEnhancements, true);
                _nightModeEnabled = _preferencesService.GetValue(PreferenceConstants.NightModeEnabled, false);
                _spatialAudioEnabled = _preferencesService.GetValue(PreferenceConstants.SpatialAudioEnabled, false);
                // HDR is always enabled if the display supports it
                _hdrOutputEnabled = _deviceService.SupportsHDR;

                // Load optimization preferences
                IsOptimizationEnabled =
                    _preferencesService.GetValue(PreferenceConstants.IsPlaybackOptimizationEnabled, true);

                Logger.LogInformation(
                    $"MediaOptimizationService initialized - Enhancement: {IsEnhancementEnabled}, Optimization: {IsOptimizationEnabled}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void SubscribeToMonitors()
        {
            if (_memoryMonitor != null)
            {
                _memoryMonitor.MemoryPressureChanged += OnMemoryPressureChanged;
            }

            if (_networkMonitor != null)
            {
                _networkMonitor.BandwidthChanged += OnBandwidthChanged;
                _networkMonitor.ConnectionQualityChanged += OnConnectionQualityChanged;
            }
        }

        #region Enhancement Features

        public async Task ApplyVideoEnhancementsAsync(MediaPlayer player, MediaSourceInfo mediaSourceInfo)
        {
            await _initializationTask.ConfigureAwait(false);
            
            if (!IsEnhancementEnabled || player == null)
            {
                return;
            }

            var context = CreateErrorContext("ApplyVideoEnhancements", ErrorCategory.Media);
            try
            {
                // Configure HDR if supported and enabled
                if (_hdrOutputEnabled && _deviceService.SupportsHDR && IsHDRContent(mediaSourceInfo))
                {
                    // HDR configuration would typically be done through VideoEffects                    Logger.LogInformation("HDR output enabled for content");
                }

                // Enable hardware acceleration if available
                if (_deviceService.SupportsHardwareDecoding)
                {
                    // Hardware acceleration is typically enabled by default on Xbox
                    Logger.LogInformation("Hardware acceleration enabled");
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task ApplyAudioEnhancementsAsync(MediaPlayer player)
        {
            if (!IsEnhancementEnabled || player == null)
            {
                return;
            }

            var context = CreateErrorContext("ApplyAudioEnhancements", ErrorCategory.Media);
            try
            {
                // Configure audio category for enhancements
                if (_nightModeEnabled)
                {
                    player.AudioCategory = MediaPlayerAudioCategory.Movie;
                }
                else
                {
                    // Set default category when no enhancements are enabled
                    // Media category has no compression
                    player.AudioCategory = MediaPlayerAudioCategory.Media;
                }

                // Apply spatial audio if supported
                if (_spatialAudioEnabled && _deviceService.IsXboxEnvironment)
                {
                    player.AudioDeviceType = MediaPlayerAudioDeviceType.Multimedia;
                }

                Logger.LogInformation($"Audio enhancements applied - Night: {_nightModeEnabled}");
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task ConfigureForXboxAsync(MediaPlayer player, MediaSourceInfo mediaSourceInfo)
        {
            if (!_deviceService.IsXboxEnvironment || player == null)
            {
                return;
            }

            var context = CreateErrorContext("ConfigureForXbox", ErrorCategory.Media);
            try
            {
                // Apply Xbox-specific optimizations
                player.RealTimePlayback = true;

                // Apply both video and audio enhancements
                await ApplyVideoEnhancementsAsync(player, mediaSourceInfo).ConfigureAwait(false);
                await ApplyAudioEnhancementsAsync(player).ConfigureAwait(false);

                Logger.LogInformation("Xbox-specific optimizations applied");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task ResetEnhancementsAsync(MediaPlayer player)
        {
            if (player == null)
            {
                return;
            }

            var context = CreateErrorContext("ResetEnhancements", ErrorCategory.Media);
            try
            {
                player.Volume = 1.0;
                player.AudioCategory = MediaPlayerAudioCategory.Other;
                player.AudioDeviceType = MediaPlayerAudioDeviceType.Console;
                player.RealTimePlayback = false;

                Logger.LogInformation("Media enhancements reset");
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }


        public void SetNightMode(bool enabled)
        {
            _nightModeEnabled = enabled;
            _preferencesService.SetValue(PreferenceConstants.NightModeEnabled, enabled);
        }

        #endregion

        #region Performance Optimization

        public async Task StartOptimizationAsync()
        {
            if (Interlocked.CompareExchange(ref _isOptimizing, 1, 0) != 0)
            {
                return; // Already optimizing
            }

            var context = CreateErrorContext("StartOptimization", ErrorCategory.Media);
            try
            {
                // Already set by CompareExchange above
                IsOptimizationEnabled = true;
                _preferencesService.SetValue(PreferenceConstants.IsPlaybackOptimizationEnabled, true);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    OptimizationStateChanged?.Invoke(this,
                        new OptimizationStateChangedEventArgs
                        {
                            IsEnabled = true,
                            CurrentBitrate = GetOptimalBitrate()
                        });
                }, logger: Logger);

                Logger.LogInformation("Playback optimization started");
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task StopOptimizationAsync()
        {
            var context = CreateErrorContext("StopOptimization", ErrorCategory.Media);
            try
            {
                Interlocked.Exchange(ref _isOptimizing, 0);
                IsOptimizationEnabled = false;
                _preferencesService.SetValue(PreferenceConstants.IsPlaybackOptimizationEnabled, false);

                await ClearOptimizationsAsync().ConfigureAwait(false);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    OptimizationStateChanged?.Invoke(this,
                        new OptimizationStateChangedEventArgs { IsEnabled = false, CurrentBitrate = 0 });
                }, logger: Logger);

                Logger.LogInformation("Playback optimization stopped");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public int GetOptimalBitrate()
        {
            if (!IsOptimizationEnabled)
            {
                return 0; // Let server decide
            }

            // Get current bandwidth from network monitor
            var availableBandwidth = _currentBandwidthKbps > 0 ? _currentBandwidthKbps : 20000; // Default 20 Mbps

            // Apply conservative factor for stability (use 80% of available bandwidth)
            var optimalBitrate = (int)(availableBandwidth * 0.8 * 1000); // Convert to bps

            // Respect device maximum
            optimalBitrate = Math.Min(optimalBitrate, _deviceService.MaxSupportedBitrate);

            Logger.LogInformation($"Optimal bitrate calculated: {optimalBitrate / 1000000.0:F1} Mbps");
            return optimalBitrate;
        }

        public async Task OptimizeForItemAsync(
            BaseItemDto item,
            Func<string, Task<PlaybackInfoResponse>> getPlaybackInfoCallback,
            Func<Uri, string, string, bool, Task<MediaSource>> createOptimizedMediaSourceCallback)
        {
            if (!IsOptimizationEnabled || item?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("OptimizeForItem", ErrorCategory.Media);
            try
            {
                var itemId = item.Id.ToString();

                // Check if already cached
                if (_mediaSourceCache.ContainsKey(itemId))
                {
                    return;
                }

                // Check if already being preloaded
                if (_preloadTasks.ContainsKey(itemId))
                {
                    return;
                }

                var preloadTask =
                    PreloadMediaSourceAsync(item, getPlaybackInfoCallback, createOptimizedMediaSourceCallback);
                _preloadTasks.TryAdd(itemId, preloadTask);

                try
                {
                    var mediaSource = await preloadTask.ConfigureAwait(false);
                    if (mediaSource != null)
                    {
                        _mediaSourceCache.TryAdd(itemId, mediaSource);
                        Logger.LogInformation($"Pre-cached media source for: {item.Name}");
                    }
                }
                finally
                {
                    _preloadTasks.TryRemove(itemId, out _);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task PreloadNextItemAsync(
            BaseItemDto nextItem,
            Func<string, Task<PlaybackInfoResponse>> getPlaybackInfoCallback,
            Func<BaseItemDto, ImageType, Task<string>> getImageUrlCallback)
        {
            if (!IsOptimizationEnabled || nextItem == null)
            {
                return;
            }

            var context = CreateErrorContext("PreloadNextItem", ErrorCategory.Media);
            try
            {
                // Preload media source
                await OptimizeForItemAsync(nextItem, getPlaybackInfoCallback, null).ConfigureAwait(false);

                // Preload images
                if (getImageUrlCallback != null)
                {
                    var imageTypes = new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Thumb };
                    foreach (var imageType in imageTypes)
                    {
                        var imageContext = CreateErrorContext($"PreloadImage_{imageType}", ErrorCategory.Media);
                        try
                        {
                            var imageUrl = await getImageUrlCallback(nextItem, imageType).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(imageUrl))
                            {
                                // Preload image by URL - IImageCacheService.PreloadImagesAsync takes BaseItemDto
                                await ImageHelper.GetImageByUrlAsync(imageUrl).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await ErrorHandler.HandleErrorAsync(ex, imageContext, false);
                        }
                    }
                }

                Logger.LogInformation($"Pre-loaded next item: {nextItem.Name}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public MediaSource GetCachedMediaSource(string itemId)
        {
            return _mediaSourceCache.TryGetValue(itemId, out var mediaSource) ? mediaSource : null;
        }

        public async Task ClearOptimizationsAsync()
        {
            var context = CreateErrorContext("ClearOptimizations", ErrorCategory.Media);
            try
            {
                // Cancel pending preload tasks
                foreach (var task in _preloadTasks.Values.ToList())
                {
                    // Wait for tasks to complete or be cancelled - errors are expected and can be ignored
                    await task.ContinueWith(t =>
                    {
                        /* Task completed or cancelled */
                    }, TaskContinuationOptions.ExecuteSynchronously).ConfigureAwait(false);
                }

                _preloadTasks.Clear();

                // Clear media source cache
                foreach (var mediaSource in _mediaSourceCache.Values.ToList())
                {
                    mediaSource?.Dispose();
                }

                _mediaSourceCache.Clear();

                // Clear image cache if memory constrained
                if (_memoryMonitor?.IsMemoryConstrained == true)
                {
                    await ImageHelper.ClearCacheAsync().ConfigureAwait(false);
                }

                Logger.LogInformation("Optimization caches cleared");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        #endregion

        #region Preference Getters

        public bool GetNightModePreference()
        {
            return _nightModeEnabled;
        }

        public bool GetSpatialAudioPreference()
        {
            return _spatialAudioEnabled;
        }

        public bool GetHDROutputEnabledPreference()
        {
            return _hdrOutputEnabled;
        }

        #endregion

        #region Monitor Event Handlers

        private void OnMemoryPressureChanged(object sender, MemoryPressureEventArgs e)
        {
            if (e.Pressure >= MemoryPressure.Medium)
            {
                FireAndForget(() => ClearOptimizationsAsync(), "ClearOptimizationsOnMemoryPressure");
            }
        }

        private void OnBandwidthChanged(object sender, BandwidthEventArgs e)
        {
            _currentBandwidthKbps = e.BandwidthKbps;

            if (IsOptimizationEnabled)
            {
                AsyncHelper.FireAndForget(async () => await UIHelper.RunOnUIThreadAsync(() =>
                {
                    OptimizationStateChanged?.Invoke(this,
                        new OptimizationStateChangedEventArgs
                        {
                            IsEnabled = true,
                            CurrentBitrate = GetOptimalBitrate()
                        });
                }, logger: Logger));
            }
        }

        private void OnConnectionQualityChanged(object sender, ConnectionQualityEventArgs e)
        {
            if (e.Quality == ConnectionQuality.Poor)
            {
                // Could implement additional logic for poor connections
                Logger.LogWarning("Connection quality is poor, playback may be affected");
            }
        }

        #endregion

        #region Helper Methods

        private bool IsHDRContent(MediaSourceInfo mediaSourceInfo)
        {
            if (mediaSourceInfo?.MediaStreams == null)
            {
                return false;
            }

            var videoStream = mediaSourceInfo.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            // Check if VideoRange indicates HDR content
            // Note: The exact enum values depend on the SDK version
            return videoStream?.VideoRange == MediaStream_VideoRange.HDR;
        }

        private async Task<MediaSource> PreloadMediaSourceAsync(
            BaseItemDto item,
            Func<string, Task<PlaybackInfoResponse>> getPlaybackInfoCallback,
            Func<Uri, string, string, bool, Task<MediaSource>> createOptimizedMediaSourceCallback)
        {
            var context = CreateErrorContext("PreloadMediaSource", ErrorCategory.Media);
            try
            {
                if (getPlaybackInfoCallback == null)
                {
                    return null;
                }

                var playbackInfo = await getPlaybackInfoCallback(item.Id.ToString()).ConfigureAwait(false);
                if (playbackInfo?.MediaSources?.FirstOrDefault() == null)
                {
                    return null;
                }

                var mediaSourceInfo = playbackInfo.MediaSources.FirstOrDefault();
                if (mediaSourceInfo == null)
                {
                    Logger.LogWarning($"No media sources available for {item.Name}");
                    return null;
                }

                if (createOptimizedMediaSourceCallback != null && !string.IsNullOrEmpty(mediaSourceInfo.Path))
                {
                    var uri = new Uri(mediaSourceInfo.Path);
                    return await createOptimizedMediaSourceCallback(
                        uri,
                        mediaSourceInfo.Container,
                        mediaSourceInfo.Id,
                        mediaSourceInfo.SupportsDirectPlay ?? false).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<MediaSource>(ex, context, null);
            }
        }

        #endregion
    }

    // Event args for optimization state changes
    public class OptimizationStateChangedEventArgs : EventArgs
    {
        public bool IsEnabled { get; set; }
        public int CurrentBitrate { get; set; }
    }

    // Memory pressure event args
    public class MemoryPressureEventArgs : EventArgs
    {
        public MemoryPressure Pressure { get; set; }
        public long AvailableMemory { get; set; }
        public long TotalMemory { get; set; }
    }

    // Bandwidth event args
    public class BandwidthEventArgs : EventArgs
    {
        public int BandwidthKbps { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Connection quality event args
    public class ConnectionQualityEventArgs : EventArgs
    {
        public ConnectionQuality Quality { get; set; }
        public int LatencyMs { get; set; }
        public double PacketLoss { get; set; }
    }

    // Enums
    public enum MemoryPressure
    {
        Low,
        Medium,
        High
    }

    public enum ConnectionQuality
    {
        Excellent,
        Good,
        Fair,
        Poor
    }
}
