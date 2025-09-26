using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using AudioTrack = Gelatinarm.Models.AudioTrack;

namespace Gelatinarm.Services
{
    public interface IAuthenticationService
    {
        string ServerUrl { get; }
        string AccessToken { get; }
        string UserId { get; }
        string Username { get; }
        bool IsAuthenticated { get; }

        Task<bool> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
        Task<bool> AuthenticateWithQuickConnectAsync(string secret, CancellationToken cancellationToken = default);
        void SetServerUrl(string serverUrl);
        Task LogoutAsync(CancellationToken cancellationToken = default);
        Task<QuickConnectResult> InitiateQuickConnectAsync(CancellationToken cancellationToken = default);
        Task<bool> CheckQuickConnectStatusAsync(string secret, CancellationToken cancellationToken = default);
        Task<bool> ValidateTokenAsync(CancellationToken cancellationToken = default);
        string GetAuthorizationHeader();
        Task<bool> RestoreLastSessionAsync(CancellationToken cancellationToken = default);
        void CancelQuickConnect();

        // Events
        event EventHandler<QuickConnectResult> QuickConnectCompleted;
        event EventHandler<string> QuickConnectError;
        event EventHandler<QuickConnectState> QuickConnectStatusChanged;
    }

    public interface IUserProfileService
    {
        string CurrentUserId { get; }
        string CurrentUserName { get; }
        bool IsAdmin { get; }

        Task<bool> LoadUserProfileAsync(CancellationToken cancellationToken = default);
        Task<UserDto> GetUserAsync(string userId, CancellationToken cancellationToken = default);
        Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default);


        void ClearUserData();
        Guid? GetCurrentUserGuid();
    }

    public interface IMediaPlaybackService
    {
        // Properties
        string CurrentItemId { get; }

        Task<PlaybackInfoResponse> GetPlaybackInfoAsync(string itemId, long? startTimeTicks = null,
            CancellationToken cancellationToken = default);

        Task ReportPlaybackStartAsync(string itemId, string mediaSourceId, long positionTicks, string playSessionId,
            CancellationToken cancellationToken = default);

        Task ReportPlaybackProgressAsync(string itemId, string mediaSourceId, long positionTicks, string playSessionId,
            bool isPaused, CancellationToken cancellationToken = default);

        Task ReportPlaybackStoppedAsync(string itemId, string mediaSourceId, long positionTicks, string playSessionId,
            CancellationToken cancellationToken = default);

        Task<BaseItemDto> GetItemAsync(string itemId, CancellationToken cancellationToken = default);

        Task<BaseItemDtoQueryResult> GetEpisodesAsync(string seriesId, string seasonId,
            CancellationToken cancellationToken = default);

        string GetStreamUrl(MediaSourceInfo mediaSource, string playSessionId);
        Task<bool> MarkWatchedAsync(string itemId, CancellationToken cancellationToken = default);
        Task<bool> MarkUnwatchedAsync(string itemId, CancellationToken cancellationToken = default);

        // High-level playback orchestration
        Task<bool> PlayMediaAsync(BaseItemDto item, long? startPositionTicks = null,
            CancellationToken cancellationToken = default);

        // Events
        event EventHandler<string> PlaybackError;
        event EventHandler<PlaybackInfoResponse> PlaybackInfoUpdated;
    }

    public interface IMediaDiscoveryService
    {
        Task<IEnumerable<BaseItemDto>> GetRecentlyAddedAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetContinueWatchingAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetRecommendedAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetNextUpEpisodesAsync(string seriesId, int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetLatestMoviesAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetLatestShowsAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetFavoriteItemsAsync(int limit = 20,
            CancellationToken cancellationToken = default);

        Task<SearchHintResult> SearchMediaAsync(string searchTerm, int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetByGenreAsync(string genreId, int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetSimilarItemsAsync(string itemId, int limit = 20,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetSuggestionsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<BaseItemDto>> GetNextUpAsync(CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> SearchAsync(string searchTerm, string[] includeItemTypes = null,
            string[] includeMediaTypes = null, bool includeGenres = true, bool includePeople = true,
            bool includeStudios = true, CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetPersonItemsAsync(string personId, string[] includeItemTypes = null,
            CancellationToken cancellationToken = default);

        Task<IEnumerable<BaseItemDto>> GetLatestMediaAsync(string[] includeItemTypes = null, string parentId = null,
            int limit = 20, CancellationToken cancellationToken = default);

        IEnumerable<KeyValuePair<string, BaseItemDto[]>> GetRecentSearches();
        void ClearRecentSearches();

        event EventHandler<BaseItemDto[]> RecommendationsUpdated;
        event EventHandler<BaseItemDto[]> ContinueWatchingUpdated;
    }


    public interface IMusicPlayerService : IDisposable
    {
        MediaPlayer MediaPlayer { get; }
        bool IsPlaying { get; }
        BaseItemDto CurrentItem { get; }
        List<BaseItemDto> Queue { get; }
        bool IsRepeatOne { get; }
        bool IsRepeatAll { get; }
        bool IsShuffleMode { get; }
        bool IsShuffleEnabled { get; }
        RepeatMode RepeatMode { get; }
        int CurrentQueueIndex { get; }

        Task PlayItem(BaseItemDto item, MediaSourceInfo mediaSource = null);
        Task PlayItems(List<BaseItemDto> items, int startIndex = 0);
        void SetQueue(List<BaseItemDto> items, int startIndex = 0);
        void AddToQueue(BaseItemDto item);
        void AddToQueueNext(BaseItemDto item);
        void ClearQueue();
        void Stop();
        void Play();
        void Pause();
        void SkipNext();
        void SkipPrevious();
        void SeekForward(int seconds);
        void SeekBackward(int seconds);
        void ToggleRepeatMode();
        void CycleRepeatMode();
        void ToggleShuffleMode();
        void SetShuffle(bool enabled);
        Task<bool> EnableBackgroundPlayback();
        Task<bool> DisableBackgroundPlayback();

        event EventHandler<BaseItemDto> NowPlayingChanged;
        event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        event EventHandler<List<BaseItemDto>> QueueChanged;
        event EventHandler<bool> ShuffleStateChanged;
        event EventHandler<RepeatMode> RepeatModeChanged;
    }

    public interface IMediaOptimizationService : IDisposable
    {
        // Enhancement Features
        bool IsEnhancementEnabled { get; }

        // Performance Optimization
        bool IsOptimizationEnabled { get; }
        Task ApplyVideoEnhancementsAsync(MediaPlayer player, MediaSourceInfo mediaSourceInfo);
        Task ApplyAudioEnhancementsAsync(MediaPlayer player);
        Task ConfigureForXboxAsync(MediaPlayer player, MediaSourceInfo mediaSourceInfo);
        Task ResetEnhancementsAsync(MediaPlayer player);

        // Audio Enhancement Settings
        void SetNightMode(bool enabled);
        Task StartOptimizationAsync();
        Task StopOptimizationAsync();
        int GetOptimalBitrate();

        // Pre-loading and Caching
        Task OptimizeForItemAsync(BaseItemDto item,
            Func<string, Task<PlaybackInfoResponse>> getPlaybackInfoCallback,
            Func<Uri, string, string, bool, Task<MediaSource>> createOptimizedMediaSourceCallback);

        Task PreloadNextItemAsync(BaseItemDto nextItem,
            Func<string, Task<PlaybackInfoResponse>> getPlaybackInfoCallback,
            Func<BaseItemDto, ImageType, Task<string>> getImageUrlCallback);

        MediaSource GetCachedMediaSource(string itemId);
        Task ClearOptimizationsAsync();

        // Preference Getters (for UI binding)
        bool GetNightModePreference();
        bool GetSpatialAudioPreference();
        bool GetHDROutputEnabledPreference();

        // Events
        event EventHandler<OptimizationStateChangedEventArgs> OptimizationStateChanged;

        // Media Source Creation
        Task<MediaSource> CreateAdaptiveMediaSourceAsync(
            string mediaUrl,
            string accessToken,
            bool isAudio,
            IPreferencesService preferences = null);

        Task<MediaSource> CreateSimpleMediaSourceAsync(
            string mediaUrl,
            string accessToken,
            bool isAudio,
            IPreferencesService preferences = null);
    }

    // Supporting classes
    public class QuickConnectResult
    {
        public string Code { get; set; }
        public string Secret { get; set; }
    }

    public enum QuickConnectState
    {
        Unavailable,
        Active,
        Authorized
    }

    /// <summary>
    ///     Service for managing application-wide caching with memory pressure awareness
    /// </summary>
    public interface ICacheManagerService
    {
        /// <summary>
        ///     Add or update an item in the cache
        /// </summary>
        void Set<T>(string key, T value, TimeSpan? expiration = null) where T : class;

        /// <summary>
        ///     Get an item from the cache
        /// </summary>
        T Get<T>(string key) where T : class;

        /// <summary>
        ///     Remove an item from the cache
        /// </summary>
        bool Remove(string key);

        /// <summary>
        ///     Clear all cached items
        /// </summary>
        void Clear();

        /// <summary>
        ///     Get current cache statistics
        /// </summary>
        CacheStatistics GetStatistics();

        /// <summary>
        ///     Manually trigger cache eviction
        /// </summary>
        void TriggerEviction();

        /// <summary>
        ///     Set the maximum memory limit for the cache
        /// </summary>
        void SetMemoryLimit(long maxSizeInBytes);

        /// <summary>
        ///     Register a cache provider for specific functionality
        /// </summary>
        void RegisterCacheProvider(string name, ICacheProvider provider);

        /// <summary>
        ///     Get a registered cache provider
        /// </summary>
        ICacheProvider GetCacheProvider(string name);

        /// <summary>
        ///     Remove a cache provider
        /// </summary>
        bool RemoveCacheProvider(string name);

        /// <summary>
        ///     Set data using a specific cache provider
        /// </summary>
        Task SetWithProviderAsync(string providerName, string key, byte[] data, TimeSpan? expiration = null);

        /// <summary>
        ///     Get data using a specific cache provider
        /// </summary>
        Task<byte[]> GetWithProviderAsync(string providerName, string key);
    }

    /// <summary>
    ///     Interface for cache providers to support different caching strategies
    /// </summary>
    public interface ICacheProvider
    {
        /// <summary>
        ///     Set an item in the cache
        /// </summary>
        Task SetAsync(string key, byte[] data, TimeSpan? expiration = null);

        /// <summary>
        ///     Get an item from the cache
        /// </summary>
        Task<byte[]> GetAsync(string key);

        /// <summary>
        ///     Remove an item from the cache
        /// </summary>
        Task<bool> RemoveAsync(string key);

        /// <summary>
        ///     Clear all items from the cache
        /// </summary>
        Task ClearAsync();

        /// <summary>
        ///     Check if an item exists in the cache
        /// </summary>
        Task<bool> ExistsAsync(string key);

        /// <summary>
        ///     Get the size of cached data in bytes
        /// </summary>
        Task<long> GetSizeAsync();
    }

    /// <summary>
    ///     Cache statistics
    /// </summary>
    public class CacheStatistics
    {
        public int ItemCount { get; set; }
        public long EstimatedSizeInBytes { get; set; }
        public int HitCount { get; set; }
        public int MissCount { get; set; }
        public int EvictionCount { get; set; }
        public double HitRate => HitCount + MissCount > 0 ? (double)HitCount / (HitCount + MissCount) : 0;
        public int MaxSize { get; set; }
    }

    /// <summary>
    ///     Service for managing navigation state across the application
    /// </summary>
    public interface INavigationStateService
    {
        /// <summary>
        ///     Save the current playback session state
        /// </summary>
        void SavePlaybackSession(PlaybackSession session);

        /// <summary>
        ///     Get the current playback session
        /// </summary>
        PlaybackSession GetCurrentPlaybackSession();

        /// <summary>
        ///     Clear the current playback session
        /// </summary>
        void ClearPlaybackSession();

        /// <summary>
        ///     Save state for returning to a specific page
        /// </summary>
        void SaveReturnState(Type pageType, object state);

        /// <summary>
        ///     Get and clear return state for a page
        /// </summary>
        object GetAndClearReturnState(Type pageType);

        /// <summary>
        ///     Check if there's a return state for a page
        /// </summary>
        bool HasReturnState(Type pageType);

        /// <summary>
        ///     Save the current library selection
        /// </summary>
        void SaveLibrarySelection(Guid? libraryId, string libraryName, string libraryType);

        /// <summary>
        ///     Get the current library selection
        /// </summary>
        (Guid? libraryId, string libraryName, string libraryType) GetLibrarySelection();
    }

    /// <summary>
    ///     Represents a media playback session with navigation context
    /// </summary>
    public class PlaybackSession
    {
        public string SessionId { get; set; }
        public Type OriginatingPage { get; set; }
        public object OriginatingPageState { get; set; }
        public List<BaseItemDto> Queue { get; set; }
        public int CurrentIndex { get; set; }
        public bool IsShuffled { get; set; }
        public bool IsFromContinueWatching { get; set; }
        public BaseItemDto CurrentItem { get; set; }
        public BaseItemDto NextEpisode { get; set; }
        public string SeriesId { get; set; }
        public string SeasonId { get; set; }
        public DateTime StartTime { get; set; }

        // For multi-episode sessions
        public bool IsMultiEpisodeSession { get; set; }
        public int EpisodesWatched { get; set; }
        public List<string> WatchedEpisodeIds { get; set; } = new();
    }

    /// <summary>
    ///     Service for building and managing episode queues
    /// </summary>
    public interface IEpisodeQueueService
    {
        /// <summary>
        ///     Get all episodes for a series
        /// </summary>
        Task<List<BaseItemDto>> GetAllSeriesEpisodesAsync(Guid seriesId, Guid userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Build an episode queue starting from a target episode
        /// </summary>
        Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(BaseItemDto targetEpisode, Guid seriesId,
            Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        ///     Build an episode queue starting from a target episode (simplified - gets user ID internally)
        /// </summary>
        Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(BaseItemDto targetEpisode,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Sort episodes by season and episode number
        /// </summary>
        List<BaseItemDto> SortEpisodesBySeasonAndNumber(IEnumerable<BaseItemDto> episodes);

        /// <summary>
        ///     Shuffle episodes for random playback
        /// </summary>
        List<BaseItemDto> ShuffleEpisodes(IEnumerable<BaseItemDto> episodes, Random random = null);

        /// <summary>
        ///     Build a shuffled episode queue for an entire series
        /// </summary>
        Task<(List<BaseItemDto> queue, int startIndex)> BuildShuffledSeriesQueueAsync(Guid seriesId,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Build episode queue for continue watching scenario with session management
        /// </summary>
        Task<(List<BaseItemDto> queue, int startIndex, bool success)> BuildContinueWatchingQueueAsync(
            BaseItemDto episode, CancellationToken cancellationToken = default);
    }

    /// <summary>
    ///     Service for centralized image loading with retry logic
    /// </summary>
    public interface IImageLoadingService
    {
        /// <summary>
        ///     Load an image from a BaseItemDto
        /// </summary>
        Task<ImageSource> LoadImageAsync(BaseItemDto item, string imageType, int? width = null, int? height = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Get an image by item ID and image type
        /// </summary>
        Task<ImageSource> GetImageAsync(Guid itemId, ImageType imageType, string imageTag, int? width = null,
            int? height = null, CancellationToken cancellationToken = default);

        /// <summary>
        ///     Load an image from a URL
        /// </summary>
        Task<BitmapImage> LoadImageByUrlAsync(string url, CancellationToken cancellationToken = default);

        /// <summary>
        ///     Load an image and set it to a target using the provided action
        /// </summary>
        Task LoadImageIntoTargetAsync(BaseItemDto item, string imageType, Action<ImageSource> setImageAction,
            CoreDispatcher dispatcher, int? width = null, int? height = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Load an image by URL and set it to a target using the provided action
        /// </summary>
        Task LoadImageByUrlIntoTargetAsync(string url, Action<BitmapImage> setImageAction, CoreDispatcher dispatcher,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    ///     Service for controlling media playback operations
    /// </summary>
    public interface IPlaybackControlService : IDisposable
    {
        /// <summary>
        ///     Gets the HLS manifest offset when server creates a new manifest at a different position
        ///     This should be added to the playback position to get the actual media position
        /// </summary>
        TimeSpan HlsManifestOffset { get; }

        /// <summary>
        ///     Initialize the service with a media player instance
        /// </summary>
        Task InitializeAsync(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams);

        /// <summary>
        ///     Get playback information for a media item
        /// </summary>
        Task<PlaybackInfoResponse> GetPlaybackInfoAsync(BaseItemDto item, int? maxBitrate = null);

        /// <summary>
        ///     Create a media source from playback info
        /// </summary>
        Task<MediaSource> CreateMediaSourceAsync(PlaybackInfoResponse playbackInfo);

        /// <summary>
        ///     Start playback of a media source with adaptive streaming support
        /// </summary>
        Task StartPlaybackAsync(MediaSource mediaSource, long? startPositionTicks = null);

        /// <summary>
        ///     Apply pending resume position if one exists
        /// </summary>
        bool ApplyPendingResumePosition();

        /// <summary>
        ///     Check if HLS resume is still in progress
        /// </summary>
        bool IsHlsResumeInProgress();

        /// <summary>
        ///     Get HLS resume status for diagnostics
        /// </summary>
        (bool InProgress, int Attempts, TimeSpan? Target) GetHlsResumeStatus();

        /// <summary>
        ///     Get available audio tracks
        /// </summary>
        Task<List<AudioTrack>> GetAudioTracksAsync(PlaybackInfoResponse playbackInfo);

        /// <summary>
        ///     Change to a different audio track
        /// </summary>
        Task ChangeAudioTrackAsync(AudioTrack audioTrack);

        /// <summary>
        ///     Get the currently selected media source info
        /// </summary>
        MediaSourceInfo GetCurrentMediaSource();

        /// <summary>
        ///     Restart playback with current position (for stream switching)
        /// </summary>
        /// <param name="maxBitrate">Optional max bitrate for quality changes</param>
        /// <param name="restartReason">Reason for restart (for logging)</param>
        /// <param name="audioStreamIndex">Optional audio stream index for audio track changes</param>
        /// <param name="subtitleStreamIndex">Optional subtitle stream index for subtitle changes</param>
        Task RestartPlaybackWithCurrentPositionAsync(int? maxBitrate = null, string restartReason = "stream change", int? audioStreamIndex = null, int? subtitleStreamIndex = null);

        /// <summary>
        ///     Event raised when playback state changes
        /// </summary>
        event EventHandler<MediaPlaybackState> PlaybackStateChanged;

        /// <summary>
        ///     Event raised when playback position changes
        /// </summary>
        event EventHandler<TimeSpan> PositionChanged;
    }

    /// <summary>
    ///     Service for managing buffering state and health
    /// </summary>


    /// <summary>
    ///     Service for managing subtitle tracks
    /// </summary>
    public interface ISubtitleService : IDisposable
    {
        /// <summary>
        ///     Initialize the service
        /// </summary>
        Task InitializeAsync(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams);

        /// <summary>
        ///     Get available subtitle tracks
        /// </summary>
        Task<List<SubtitleTrack>> GetSubtitleTracksAsync(PlaybackInfoResponse playbackInfo);

        /// <summary>
        ///     Change to a different subtitle track
        /// </summary>
        Task ChangeSubtitleTrackAsync(SubtitleTrack subtitle);

        /// <summary>
        ///     Disable subtitles
        /// </summary>
        Task DisableSubtitlesAsync();

        /// <summary>
        ///     Get the current subtitle track
        /// </summary>
        SubtitleTrack GetCurrentSubtitle();

        /// <summary>
        ///     Event raised when subtitle track changes
        /// </summary>
        event EventHandler<SubtitleTrack> SubtitleChanged;
    }

    /// <summary>
    ///     Service for managing media session and progress reporting
    /// </summary>
    public interface IMediaSessionService : IDisposable
    {
        /// <summary>
        ///     Initialize the service
        /// </summary>
        Task InitializeAsync(MediaPlaybackParams playbackParams);

        /// <summary>
        ///     Update the current item (used when item is loaded later)
        /// </summary>
        void UpdateCurrentItem(BaseItemDto item);

        /// <summary>
        ///     Report playback has started
        /// </summary>
        Task ReportPlaybackStartAsync(string playSessionId, long positionTicks);

        /// <summary>
        ///     Report playback progress
        /// </summary>
        Task ReportPlaybackProgressAsync(string playSessionId, long positionTicks, bool isPaused);

        /// <summary>
        ///     Report playback has stopped
        /// </summary>
        Task ReportPlaybackStoppedAsync(string playSessionId, long positionTicks);

        /// <summary>
        ///     Mark the current item as watched
        /// </summary>
        Task MarkAsWatchedAsync(string itemId);

        /// <summary>
        ///     Get the current play session ID
        /// </summary>
        string GetPlaySessionId();
    }

    /// <summary>
    ///     Service for managing media navigation (episodes, shuffle, queue)
    /// </summary>
    public interface IMediaNavigationService : IDisposable
    {
        /// <summary>
        ///     Initialize the service with playback parameters
        /// </summary>
        Task InitializeAsync(MediaPlaybackParams playbackParams, BaseItemDto currentItem);

        /// <summary>
        ///     Get the next episode in the queue
        /// </summary>
        Task<BaseItemDto> GetNextEpisodeAsync();

        /// <summary>
        ///     Get the previous episode in the queue
        /// </summary>
        Task<BaseItemDto> GetPreviousEpisodeAsync();

        /// <summary>
        ///     Navigate to the next item
        /// </summary>
        Task NavigateToNextAsync();

        /// <summary>
        ///     Navigate to the previous item
        /// </summary>
        Task NavigateToPreviousAsync();

        /// <summary>
        ///     Check if there's a next item available
        /// </summary>
        bool HasNextItem();

        /// <summary>
        ///     Check if there's a previous item available
        /// </summary>
        bool HasPreviousItem();

        /// <summary>
        ///     Enable or disable shuffle mode
        /// </summary>
        Task SetShuffleModeAsync(bool enabled);

        /// <summary>
        ///     Get the current shuffle state
        /// </summary>
        bool IsShuffleEnabled();

        /// <summary>
        ///     Preload the next item for faster transitions
        /// </summary>
        Task PreloadNextItemAsync();

        /// <summary>
        ///     Event raised when navigation state changes
        /// </summary>
        event EventHandler NavigationStateChanged;
    }

    /// <summary>
    ///     Service for managing playback statistics display
    /// </summary>
    public interface IPlaybackStatisticsService : IDisposable
    {
        /// <summary>
        ///     Check if stats are currently visible
        /// </summary>
        bool IsVisible { get; }

        /// <summary>
        ///     Initialize the service
        /// </summary>
        Task InitializeAsync(MediaPlayer mediaPlayer);

        /// <summary>
        ///     Get formatted statistics for display
        /// </summary>
        PlaybackStats GetCurrentStats();

        /// <summary>
        ///     Start updating statistics
        /// </summary>
        void StartUpdating();

        /// <summary>
        ///     Stop updating statistics
        /// </summary>
        void StopUpdating();

        /// <summary>
        ///     Set the current media source info for accurate playback method display
        /// </summary>
        void SetMediaSourceInfo(MediaSourceInfo mediaSource);

        /// <summary>
        ///     Toggle stats visibility
        /// </summary>
        void ToggleVisibility();

        /// <summary>
        ///     Event raised when statistics update
        /// </summary>
        event EventHandler<PlaybackStats> StatsUpdated;
    }

    /// <summary>
    ///     Service for managing media controller input
    /// </summary>
    public interface IMediaControllerService : IDisposable
    {
        /// <summary>
        ///     Check if controller input is enabled
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        ///     Initialize the service
        /// </summary>
        Task InitializeAsync(MediaPlayer mediaPlayer);

        /// <summary>
        ///     Handle controller button press
        /// </summary>
        Task<bool> HandleButtonPressAsync(ControllerButton button);

        /// <summary>
        ///     Handle controller analog input
        /// </summary>

        /// <summary>
        ///     Handle KeyDown events from the MediaPlayerPage
        /// </summary>
        Task<bool> HandleKeyDownAsync(VirtualKey key);

        /// <summary>
        ///     Set button mapping configuration
        /// </summary>
        void SetButtonMapping(Dictionary<ControllerButton, MediaAction> mapping);

        /// <summary>
        ///     Get current button mapping
        /// </summary>
        Dictionary<ControllerButton, MediaAction> GetButtonMapping();

        /// <summary>
        ///     Enable or disable controller input
        /// </summary>
        void SetEnabled(bool enabled);

        /// <summary>
        ///     Event raised when a media action is triggered
        /// </summary>
        event EventHandler<MediaAction> ActionTriggered;

        /// <summary>
        ///     Event raised when a media action with parameter is triggered
        /// </summary>
        event EventHandler<(MediaAction action, object parameter)> ActionWithParameterTriggered;

        /// <summary>
        ///     Update the visibility state of the media controls
        /// </summary>
        void SetControlsVisible(bool visible);
    }

    /// <summary>
    ///     Service for managing skip intro/outro functionality
    /// </summary>
    public interface ISkipSegmentService : IDisposable
    {
        /// <summary>
        ///     Initialize the service
        /// </summary>
        Task InitializeAsync(MediaPlayer mediaPlayer, BaseItemDto item);

        /// <summary>
        ///     Load media segments for the current item
        /// </summary>
        Task LoadSegmentsAsync(string itemId);

        /// <summary>
        ///     Check if intro skip is available
        /// </summary>
        bool IsIntroSkipAvailable();

        /// <summary>
        ///     Check if outro skip is available
        /// </summary>
        bool IsOutroSkipAvailable();

        /// <summary>
        ///     Get intro segment timing
        /// </summary>
        (TimeSpan? start, TimeSpan? end) GetIntroSegment();

        /// <summary>
        ///     Get outro segment timing
        /// </summary>
        (TimeSpan? start, TimeSpan? end) GetOutroSegment();

        /// <summary>
        ///     Skip the intro
        /// </summary>
        Task SkipIntroAsync();

        /// <summary>
        ///     Skip the outro
        /// </summary>
        Task SkipOutroAsync();

        /// <summary>
        ///     Handle auto-skip if enabled
        /// </summary>
        Task HandleAutoSkipAsync(TimeSpan currentPosition);

        /// <summary>
        ///     Check if position is within a skippable segment
        /// </summary>
        SkipSegmentType? GetCurrentSegmentType(TimeSpan position);

        /// <summary>
        ///     Event raised when segment availability changes
        /// </summary>
        event EventHandler SegmentAvailabilityChanged;

        /// <summary>
        ///     Event raised when a segment is skipped
        /// </summary>
        event EventHandler<SkipSegmentType> SegmentSkipped;
    }

    /// <summary>
    ///     Service for displaying dialogs and messages to the user
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        ///     Show an error dialog
        /// </summary>
        Task ShowErrorAsync(string title, string message);

        /// <summary>
        ///     Show a message dialog
        /// </summary>
        Task ShowMessageAsync(string message, string title);

        /// <summary>
        ///     Show a confirmation dialog
        /// </summary>
        Task<bool> ShowConfirmationAsync(string title, string message);

        /// <summary>
        ///     Show a dialog with custom buttons
        /// </summary>
        Task<ContentDialogResult> ShowCustomAsync(string title, string message, string primaryButtonText = null,
            string secondaryButtonText = null, string closeButtonText = null);
    }

    /// <summary>
    ///     Unified error handling service for standardized error processing
    /// </summary>
    public interface IErrorHandlingService
    {
        /// <summary>
        ///     Handle an exception with appropriate logging and user notification
        /// </summary>
        /// <param name="exception">The exception to handle</param>
        /// <param name="context">Context information about where the error occurred</param>
        /// <param name="showUserMessage">Whether to show a message to the user</param>
        /// <param name="userMessage">Optional custom user message</param>
        /// <returns>Task</returns>
        Task HandleErrorAsync(Exception exception, ErrorContext context, bool showUserMessage = true,
            string userMessage = null);

        /// <summary>
        ///     Handle an exception and return a default value
        /// </summary>
        /// <typeparam name="T">Return type</typeparam>
        /// <param name="exception">The exception to handle</param>
        /// <param name="context">Context information about where the error occurred</param>
        /// <param name="defaultValue">Default value to return</param>
        /// <param name="showUserMessage">Whether to show a message to the user</param>
        /// <returns>The default value</returns>
        Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue,
            bool showUserMessage = false);

        /// <summary>
        ///     Determine if an error should be shown to the user based on its type and context
        /// </summary>
        bool ShouldShowUserMessage(Exception exception, ErrorContext context);

        /// <summary>
        ///     Get a user-friendly message for an exception
        /// </summary>
        string GetUserFriendlyMessage(Exception exception, ErrorContext context);

        /// <summary>
        ///     Check if the operation should be retried based on the exception
        /// </summary>
        bool ShouldRetry(Exception exception, int attemptNumber);

        /// <summary>
        ///     Handle an exception with appropriate logging and user notification (non-async)
        ///     This method uses fire-and-forget for the dialog display, making it suitable for use in synchronous contexts
        /// </summary>
        /// <param name="exception">The exception to handle</param>
        /// <param name="context">Context information about where the error occurred</param>
        /// <param name="showUserMessage">Whether to show a message to the user (fires and forgets the dialog)</param>
        void HandleError(Exception exception, ErrorContext context, bool showUserMessage = false);
    }
}
