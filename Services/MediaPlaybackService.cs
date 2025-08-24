using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     SDK-based implementation of the media playback service with integrated session management
    /// </summary>
    public class MediaPlaybackService : BaseService, IMediaPlaybackService, IMediaSessionService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IDeviceProfileService _deviceProfileService;
        private readonly IUnifiedDeviceService _deviceService;
        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IPreferencesService _preferencesService;
        private readonly IUserProfileService _userProfileService;
        private BaseItemDto _currentItem;
        private bool _hasReportedStart;
        private bool _hasReportedStop;
        private IMusicPlayerService _musicPlayerService;
        private string _playSessionId;

        // Session management state
        private MediaPlaybackParams _playbackParams;
        private int _positionReportCounter;
        private Task _activeProgressReportTask;

        public MediaPlaybackService(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            IPreferencesService preferencesService,
            IUnifiedDeviceService deviceService,
            IDeviceProfileService deviceProfileService,
            IMediaOptimizationService mediaOptimizationService,
            IAuthenticationService authService,
            ILogger<MediaPlaybackService> logger) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
            _preferencesService = preferencesService;
            _deviceService = deviceService;
            _deviceProfileService = deviceProfileService;
            _mediaOptimizationService = mediaOptimizationService;
            _authService = authService;
        }

        public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string itemId, long? startTimeTicks = null,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetPlaybackInfo", ErrorCategory.Media);
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    Logger.LogError($"Invalid item ID format: {itemId}");
                    return null;
                }

                var userId = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger.LogError($"Invalid user ID format: {userId}");
                    return null;
                }

                var deviceProfile = _deviceProfileService.GetDeviceProfile();

                var playbackInfoRequest = new PlaybackInfoDto
                {
                    UserId = userGuid,
                    MaxStreamingBitrate = _mediaOptimizationService.GetOptimalBitrate(),
                    StartTimeTicks = startTimeTicks,
                    AutoOpenLiveStream = true,
                    DeviceProfile = deviceProfile,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex
                };

                if (_playbackParams?.SubtitleStreamIndex.HasValue == true)
                {
                    Logger.LogInformation($"Requesting playback info with SubtitleStreamIndex: {_playbackParams.SubtitleStreamIndex}");
                }
                if (_playbackParams?.AudioStreamIndex.HasValue == true)
                {
                    Logger.LogInformation($"Requesting playback info with AudioStreamIndex: {_playbackParams.AudioStreamIndex}");
                }

                // The SDK uses a POST request to get playback info
                var response = await RetryAsync(
                    async () => await _apiClient.Items[itemGuid].PlaybackInfo
                        .PostAsync(playbackInfoRequest, null, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                return response;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return null;
            }
        }

        // IMediaPlaybackService interface method with different signature
        public async Task ReportPlaybackStartAsync(string itemId, string mediaSourceId, long positionTicks,
            string playSessionId, CancellationToken cancellationToken = default)
        {
            // Update the current item if the ID changed
            if (_currentItem == null || _currentItem.Id?.ToString() != itemId)
            {
                _currentItem = await GetItemAsync(itemId);
                _playbackParams = _playbackParams ?? new MediaPlaybackParams();
                _playbackParams.MediaSourceId = mediaSourceId;
            }

            // Delegate to the session-aware method
            await ReportPlaybackStartAsync(playSessionId, positionTicks);
        }

        // IMediaPlaybackService interface method with different signature
        public async Task ReportPlaybackProgressAsync(string itemId, string mediaSourceId, long positionTicks,
            string playSessionId, bool isPaused, CancellationToken cancellationToken = default)
        {
            // Update the current item if the ID changed
            if (_currentItem == null || _currentItem.Id?.ToString() != itemId)
            {
                _currentItem = await GetItemAsync(itemId);
                _playbackParams = _playbackParams ?? new MediaPlaybackParams();
                _playbackParams.MediaSourceId = mediaSourceId;
            }

            // Delegate to the session-aware method
            await ReportPlaybackProgressAsync(playSessionId, positionTicks, isPaused);
        }

        // IMediaPlaybackService interface method with different signature
        public async Task ReportPlaybackStoppedAsync(string itemId, string mediaSourceId, long positionTicks,
            string playSessionId, CancellationToken cancellationToken = default)
        {
            // Update the current item if the ID changed
            if (_currentItem == null || _currentItem.Id?.ToString() != itemId)
            {
                _currentItem = await GetItemAsync(itemId);
                _playbackParams = _playbackParams ?? new MediaPlaybackParams();
                _playbackParams.MediaSourceId = mediaSourceId;
            }

            // Delegate to the session-aware method
            await ReportPlaybackStoppedAsync(playSessionId, positionTicks);
        }

        public async Task<BaseItemDto> GetItemAsync(string itemId, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                Logger.LogError($"Invalid item ID format: {itemId}");
                return null;
            }

            var userId = _userProfileService.CurrentUserId;
            if (!Guid.TryParse(userId, out var userGuid))
            {
                Logger.LogError($"Invalid user ID format: {userId}");
                return null;
            }

            var context = CreateErrorContext("GetItem", ErrorCategory.Media);
            try
            {
                return await RetryAsync(
                    async () => await _apiClient.Items[itemGuid].GetAsync(config =>
                    {
                        config.QueryParameters.UserId = userGuid;
                    }, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return null;
            }
        }

        public async Task<BaseItemDtoQueryResult> GetEpisodesAsync(string seriesId, string seasonId,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetEpisodes", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger.LogError($"Invalid user ID format: {userId}");
                    return null;
                }

                return await RetryAsync(
                    async () => await _apiClient.Items.GetAsync(config =>
                    {
                        config.QueryParameters.UserId = userGuid;
                        config.QueryParameters.ParentId = Guid.Parse(seasonId);
                        config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                        config.QueryParameters.Fields = new[]
                        {
                            ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSources
                        };
                    }, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return null;
            }
        }


        public string GetStreamUrl(MediaSourceInfo mediaSource, string playSessionId)
        {
            if (mediaSource == null)
            {
                Logger.LogError("MediaSource is null");
                return null;
            }

            // For direct play, use the direct stream path
            if (mediaSource.SupportsDirectPlay == true && !string.IsNullOrEmpty(mediaSource.Path))
            {
                // For remote items, we need to build the stream URL using SDK
                if (mediaSource.Protocol != MediaSourceInfo_Protocol.File)
                {
                    // Parse the item ID from the media source
                    if (!Guid.TryParse(mediaSource.Id, out var itemId))
                    {
                        Logger.LogError($"Invalid media source ID: {mediaSource.Id}");
                        return null;
                    }

                    // Build the stream URL using SDK
                    var requestInfo = _apiClient.Videos[itemId].Stream.ToGetRequestInformation(config =>
                    {
                        config.QueryParameters.Static = true;
                        config.QueryParameters.MediaSourceId = mediaSource.Id;
                        config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                        if (!string.IsNullOrEmpty(playSessionId))
                        {
                            config.QueryParameters.PlaySessionId = playSessionId;
                        }

                        // Add SubtitleStreamIndex if specified
                        if (_playbackParams?.SubtitleStreamIndex >= 0)
                        {
                            config.QueryParameters.SubtitleStreamIndex = _playbackParams.SubtitleStreamIndex.Value;
                            // Note: SubtitleMethod is set via enum in SDK
                            Logger.LogInformation($"Added SubtitleStreamIndex={_playbackParams.SubtitleStreamIndex.Value} to stream URL");
                        }

                        // Add AudioStreamIndex if specified
                        if (_playbackParams?.AudioStreamIndex >= 0)
                        {
                            config.QueryParameters.AudioStreamIndex = _playbackParams.AudioStreamIndex.Value;
                            Logger.LogInformation($"Added AudioStreamIndex={_playbackParams.AudioStreamIndex.Value} to stream URL");
                        }
                    });

                    var uri = _apiClient.BuildUri(requestInfo);
                    var url = uri.ToString();

                    // SDK-generated URLs already include authentication via headers
                    return url;
                }

                return mediaSource.Path;
            }

            // For transcoding, use the transcoding URL from media source
            if (mediaSource.SupportsTranscoding == true && !string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                var serverUrl = _authService.ServerUrl?.TrimEnd('/') ?? "";
                var transcodingUrl = mediaSource.TranscodingUrl;

                // Add SubtitleStreamIndex if specified and not already in URL
                if (_playbackParams?.SubtitleStreamIndex.HasValue == true &&
                    _playbackParams.SubtitleStreamIndex.Value >= 0 &&
                    !transcodingUrl.Contains("SubtitleStreamIndex="))
                {
                    var separator = transcodingUrl.Contains("?") ? "&" : "?";
                    transcodingUrl += $"{separator}SubtitleStreamIndex={_playbackParams.SubtitleStreamIndex.Value}";
                    Logger.LogInformation($"Added SubtitleStreamIndex={_playbackParams.SubtitleStreamIndex.Value} to transcoding URL");
                }

                // Add AudioStreamIndex if specified and not already in URL
                if (_playbackParams?.AudioStreamIndex.HasValue == true &&
                    _playbackParams.AudioStreamIndex.Value >= 0 &&
                    !transcodingUrl.Contains("AudioStreamIndex="))
                {
                    var separator = transcodingUrl.Contains("?") ? "&" : "?";
                    transcodingUrl += $"{separator}AudioStreamIndex={_playbackParams.AudioStreamIndex.Value}";
                    Logger.LogInformation($"Added AudioStreamIndex={_playbackParams.AudioStreamIndex.Value} to transcoding URL");
                }

                var url = string.Concat(serverUrl, transcodingUrl);

                // Server-provided transcoding URLs already include api_key parameter
                return url;
            }

            Logger.LogError("No suitable stream URL found in media source");
            return null;
        }

        public async Task<bool> MarkWatchedAsync(string itemId, CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("MarkWatched", ErrorCategory.Media);
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    Logger.LogError($"Invalid item ID format: {itemId}");
                    return false;
                }

                var userId = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger.LogError($"Invalid user ID format: {userId}");
                    return false;
                }

                await RetryAsync(
                    async () => await _apiClient.UserPlayedItems[itemGuid].PostAsync(null, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                Logger.LogInformation($"Item marked as watched: {itemId}");
                return true;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return false;
            }
        }

        public async Task<bool> MarkUnwatchedAsync(string itemId, CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("MarkUnwatched", ErrorCategory.Media);
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    Logger.LogError($"Invalid item ID format: {itemId}");
                    return false;
                }

                var userId = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger.LogError($"Invalid user ID format: {userId}");
                    return false;
                }

                await RetryAsync(
                    async () => await _apiClient.UserPlayedItems[itemGuid].DeleteAsync(null, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                Logger.LogInformation($"Item marked as unwatched: {itemId}");
                return true;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return false;
            }
        }

        // Playback control methods
        public async Task<bool> PlayMediaAsync(BaseItemDto item, long? startPositionTicks = null,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("PlayMedia", ErrorCategory.Media);
            try
            {
                if (item == null)
                {
                    Logger.LogError("Cannot play null item");
                    return false;
                }

                Logger.LogInformation($"PlayMediaAsync called for item: {item.Name} (Type: {item.Type})");

                // Update current item info
                CurrentItemId = item.Id?.ToString();

                // For video items, navigate to MediaPlayerPage
                if (item.Type == BaseItemDto_Type.Movie || item.Type == BaseItemDto_Type.Episode)
                {
                    // Stop any currently playing music and clear the queue
                    if (_musicPlayerService != null)
                    {
                        _musicPlayerService.Stop();
                        _musicPlayerService.ClearQueue();
                        Logger.LogInformation("Stopped music playback and cleared queue before starting video");
                    }

                    // Get navigation service from the app
                    var navigationService = App.Current.Services.GetService<INavigationService>();
                    if (navigationService != null)
                    {
                        var playbackParams = new MediaPlaybackParams
                        {
                            Item = item,
                            ItemId = item.Id?.ToString(),
                            StartPositionTicks = startPositionTicks
                        };
                        navigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
                        return true;
                    }
                }
                // For audio items, use mini player
                else if (item.Type == BaseItemDto_Type.Audio)
                {
                    if (_musicPlayerService != null)
                    {
                        await _musicPlayerService.PlayItem(item).ConfigureAwait(false);
                        return true;
                    }

                    Logger.LogWarning("Cannot play audio item - MusicPlayerService is not available");
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        PlaybackError?.Invoke(this, "Audio playback service is not available");
                    }, logger: Logger);
                    return false;
                }

                return false;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                return false;
            }
        }

        // Note: Low-level playback control methods (Play/Pause/Stop/Seek) have been removed
        // from this service. Use MediaControlService for direct MediaPlayer control,
        // or MusicPlayerService for audio playback control.
        // This service focuses on high-level playback orchestration (navigation, reporting)

        // Properties
        public string CurrentItemId { get; private set; }

        // Events
        public event EventHandler<string> PlaybackError;
#pragma warning disable CS0067 // The event is never used (required by interface)
        public event EventHandler<PlaybackInfoResponse> PlaybackInfoUpdated;
#pragma warning restore CS0067

        // New session-aware method from IMediaSessionService
        public async Task ReportPlaybackStartAsync(string playSessionId, long positionTicks)
        {
            try
            {
                if (_hasReportedStart)
                {
                    Logger.LogDebug("Playback start already reported");
                    return;
                }

                if (_currentItem == null)
                {
                    Logger.LogWarning("Cannot report playback start - current item is null");
                    return;
                }

                _playSessionId = playSessionId;

                if (!_currentItem.Id.HasValue)
                {
                    Logger.LogWarning("Cannot report playback start - item has no ID");
                    return;
                }

                var playbackStartInfo = new PlaybackStartInfo
                {
                    ItemId = _currentItem.Id.Value,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    PositionTicks = positionTicks,
                    PlayMethod = (PlaybackStartInfo_PlayMethod)DeterminePlayMethod(),
                    PlaySessionId = playSessionId,
                    CanSeek = true,
                    IsPaused = false
                };

                using (var cts = new CancellationTokenSource(
                           TimeSpan.FromSeconds(MediaPlayerConstants.API_CALL_TIMEOUT_SECONDS)))
                {
                    await _apiClient.Sessions.Playing.PostAsync(playbackStartInfo, cancellationToken: cts.Token)
                        .ConfigureAwait(false);
                }

                _hasReportedStart = true;
                Logger.LogInformation(
                    $"Reported playback start for {_currentItem.Name} at position {TimeSpan.FromTicks(positionTicks):mm\\:ss}");
            }
            catch (Exception ex)
            {
                // Use ErrorHandlingService for consistent error handling
                var context = CreateErrorContext("ReportPlaybackStart", ErrorCategory.Media, ErrorSeverity.Warning);
                if (ErrorHandler != null)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger.LogError(ex, "Error reporting playback start");
                }

                // Still set the flag to allow progress reporting even if the initial report failed
                // This prevents blocking all subsequent progress reports
                _hasReportedStart = true;
                Logger.LogWarning("Setting playback started flag despite error to allow progress reporting");
                // Don't throw - not critical to playback
            }
        }

        // Session-aware method from IMediaSessionService
        public async Task ReportPlaybackProgressAsync(string playSessionId, long positionTicks, bool isPaused)
        {
            try
            {
                if (!_hasReportedStart || string.IsNullOrEmpty(playSessionId))
                {
                    Logger.LogDebug("Cannot report progress - playback not started or missing session ID");
                    return;
                }

                if (_currentItem == null || !_currentItem.Id.HasValue)
                {
                    Logger.LogDebug("Cannot report progress - current item is null or has no ID");
                    return;
                }

                // Throttle progress reports
                _positionReportCounter++;
                if (_positionReportCounter % MediaPlayerConstants.POSITION_REPORT_INTERVAL_TICKS != 0)
                {
                    return;
                }

                var progressInfo = new PlaybackProgressInfo
                {
                    ItemId = _currentItem.Id.Value,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    PositionTicks = positionTicks,
                    PlaySessionId = playSessionId,
                    IsPaused = isPaused,
                    PlayMethod = DeterminePlayMethod()
                };

                // Check if a previous progress report is still running
                if (_activeProgressReportTask != null && !_activeProgressReportTask.IsCompleted)
                {
                    Logger.LogDebug("Previous progress report still in progress, skipping this interval");
                    return;
                }

                // Store and execute the progress report task on background thread
                _activeProgressReportTask = Task.Run(async () =>
                {
                    try
                    {
                        // Create a timeout using Task.Delay
                        using var cts = new CancellationTokenSource();
                        var progressTask = _apiClient.Sessions.Playing.Progress.PostAsync(progressInfo);
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(MediaPlayerConstants.API_CALL_TIMEOUT_SECONDS), cts.Token);

                        // Wait for either completion or timeout
                        var completedTask = await Task.WhenAny(progressTask, timeoutTask).ConfigureAwait(false);

                        if (completedTask == timeoutTask)
                        {
                            // Timeout occurred
                            Logger.LogDebug("Progress report timed out - will retry on next interval");
                            return;
                        }

                        // Cancel the timeout since we completed
                        cts.Cancel();

                        // Await the progress task to get any exceptions
                        await progressTask.ConfigureAwait(false);

                        var position = TimeSpan.FromTicks(positionTicks);
                        var timeFormat = position.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss";
                        Logger.LogDebug(
                            $"Reported playback progress at {position.ToString(timeFormat)} for {_currentItem.Name}");
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogDebug("Progress report cancelled");
                    }
                    catch (Exception ex)
                    {
                        // Log other exceptions but don't let them break progress reporting
                        Logger.LogDebug(ex, "Failed to report progress - will retry on next interval");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to report playback progress");
                // Don't throw - this is not critical
            }
        }

        // Session-aware method from IMediaSessionService
        public async Task ReportPlaybackStoppedAsync(string playSessionId, long positionTicks)
        {
            try
            {
                if (!_hasReportedStart || string.IsNullOrEmpty(playSessionId))
                {
                    Logger.LogDebug("Cannot report stop - playback not started or missing session ID");
                    return;
                }

                if (_hasReportedStop)
                {
                    Logger.LogDebug("Playback stop already reported");
                    return;
                }

                // Capture current item reference to avoid race conditions
                var currentItem = _currentItem;

                if (currentItem == null || !currentItem.Id.HasValue)
                {
                    Logger.LogDebug("Cannot report stop - current item is null or has no ID");
                    return;
                }

                var stopInfo = new PlaybackStopInfo
                {
                    ItemId = currentItem.Id.Value,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    PositionTicks = positionTicks,
                    PlaySessionId = playSessionId
                };

                using (var cts = new CancellationTokenSource(
                           TimeSpan.FromSeconds(MediaPlayerConstants.API_CALL_TIMEOUT_SECONDS)))
                {
                    await _apiClient.Sessions.Playing.Stopped.PostAsync(stopInfo, cancellationToken: cts.Token)
                        .ConfigureAwait(false);
                }

                _hasReportedStop = true;
                var itemName = currentItem?.Name ?? "Unknown Item";
                Logger.LogInformation(
                    $"Reported playback stop at {TimeSpan.FromTicks(positionTicks):mm\\:ss} for {itemName}");

                // Check if we should mark as watched
                await CheckAndMarkAsWatched(positionTicks);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to report playback stop");
                // Don't throw - we don't want to interrupt navigation
            }
        }

        // IMediaSessionService implementation
        async Task IMediaSessionService.MarkAsWatchedAsync(string itemId)
        {
            await MarkWatchedAsync(itemId);
        }

        public string GetPlaySessionId()
        {
            return _playSessionId;
        }

        public void Dispose()
        {
            // Cleanup any resources
            CleanupAsync().GetAwaiter().GetResult();
        }

        public void SetMusicPlayerService(IMusicPlayerService musicPlayerService)
        {
            _musicPlayerService = musicPlayerService;
        }

        public async Task CleanupAsync()
        {
            var context = CreateErrorContext("Cleanup");
            try
            {
                // Report playback stopped if we haven't already
                if (_hasReportedStart && !_hasReportedStop && !string.IsNullOrEmpty(_playSessionId))
                {
                    await ReportPlaybackStoppedAsync(_playSessionId, 0);
                }

                // Reset session state
                _playbackParams = null;
                _currentItem = null;
                _playSessionId = null;
                _hasReportedStart = false;
                _hasReportedStop = false;
                _positionReportCounter = 0;
                _activeProgressReportTask = null;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        #region IMediaSessionService Implementation

        public async Task InitializeAsync(MediaPlaybackParams playbackParams)
        {
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));
            _currentItem = playbackParams.Item;

            // Reset state for new session
            _hasReportedStart = false;
            _hasReportedStop = false;
            _positionReportCounter = 0;
            _activeProgressReportTask = null;

            await Task.CompletedTask;
        }

        public void UpdateCurrentItem(BaseItemDto item)
        {
            _currentItem = item;
            Logger.LogInformation($"Updated current item: {item?.Name}");
        }

        #endregion

        #region Helper Methods

        private PlaybackProgressInfo_PlayMethod DeterminePlayMethod()
        {
            // In the future, we could determine if transcoding is being used
            return PlaybackProgressInfo_PlayMethod.DirectPlay;
        }

        private async Task CheckAndMarkAsWatched(long positionTicks)
        {
            var context = CreateErrorContext("CheckAndMarkAsWatched", ErrorCategory.Media);
            try
            {
                if (_currentItem == null || !_currentItem.RunTimeTicks.HasValue)
                {
                    return;
                }

                var runtime = _currentItem.RunTimeTicks.Value;
                var percentWatched = (double)positionTicks / runtime * 100;

                // Mark as watched if > 90% complete
                if (percentWatched > 90 && _currentItem.Id.HasValue)
                {
                    await MarkWatchedAsync(_currentItem.Id.Value.ToString());
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        #endregion
    }
}
