using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing skip intro/outro functionality
    /// </summary>
    public class SkipSegmentService : BaseService, ISkipSegmentService
    {
        private const double INTRO_BUFFER_TIME_SECONDS = 2.0;
        private readonly IAuthenticationService _authenticationService;
        private readonly IPreferencesService _preferencesService;
        private readonly JellyfinApiClient _sdkClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private BaseItemDto _currentItem;
        private bool _hasAutoSkippedIntro;
        private bool _hasAutoSkippedOutro;
        private TimeSpan? _introEndTime;
        private TimeSpan? _introStartTime;

        private MediaPlayer _mediaPlayer;
        private TimeSpan? _outroEndTime;
        private TimeSpan? _outroStartTime;
        private AppPreferences _preferences;

        public SkipSegmentService(
            JellyfinApiClient sdkClient,
            IPreferencesService preferencesService,
            IAuthenticationService authenticationService,
            IHttpClientFactory httpClientFactory,
            ILogger<SkipSegmentService> logger) : base(logger)
        {
            _sdkClient = sdkClient ?? throw new ArgumentNullException(nameof(sdkClient));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _authenticationService =
                authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public event EventHandler SegmentAvailabilityChanged;
        public event EventHandler<SkipSegmentType> SegmentSkipped;

        public async Task InitializeAsync(MediaPlayer mediaPlayer, BaseItemDto item)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
            _currentItem = item ?? throw new ArgumentNullException(nameof(item));

            _preferences = await _preferencesService.GetAppPreferencesAsync(); _hasAutoSkippedIntro = false;
            _hasAutoSkippedOutro = false; if (item.Id.HasValue)
            {
                await LoadSegmentsAsync(item.Id.Value.ToString());
            }

            Logger.LogInformation($"SkipSegmentService initialized for item: {item.Name}");
        }

        public async Task LoadSegmentsAsync(string itemId)
        {
            try
            {
                _introStartTime = null;
                _introEndTime = null;
                _outroStartTime = null;
                _outroEndTime = null;

                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    Logger.LogWarning($"Invalid item ID: {itemId}");
                    return;
                }

                // Fetch media segments from API
                // Using custom HTTP request as MediaSegments is not in the standard SDK
                var httpClient = _httpClientFactory.CreateClient("JellyfinClient");
                var serverUrl = _authenticationService.ServerUrl;

                // Add authorization header for this request
                if (!string.IsNullOrEmpty(_authenticationService.AccessToken))
                {
                    httpClient.DefaultRequestHeaders.Remove("Authorization");
                    httpClient.DefaultRequestHeaders.Add("Authorization",
                        $"MediaBrowser Token=\"{_authenticationService.AccessToken}\"");
                }

                // MediaSegments endpoint requires the intro detection plugin
                var segmentsUrl = $"{serverUrl}/MediaSegments/{itemGuid}";
                Logger.LogInformation($"Fetching media segments from: {segmentsUrl}");

                var response = await httpClient.GetAsync(segmentsUrl);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning($"MediaSegments API returned {response.StatusCode} for item {itemId}");
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.LogInformation(
                            "MediaSegments endpoint not found - server may not have intro detection plugin installed");
                    }

                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                Logger.LogDebug($"MediaSegments response: {json}");

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var segmentsResponse = JsonSerializer.Deserialize<MediaSegmentsResponse>(json, options);

                if (segmentsResponse?.Items != null && segmentsResponse.Items.Any())
                {
                    foreach (var segment in segmentsResponse.Items)
                    {
                        if (segment.Type == "Intro" && segment.StartTicks.HasValue && segment.EndTicks.HasValue)
                        {
                            _introStartTime = TimeSpan.FromTicks(segment.StartTicks.Value);
                            _introEndTime = TimeSpan.FromTicks(segment.EndTicks.Value);
                            Logger.LogInformation($"Found intro segment: {_introStartTime} - {_introEndTime}");
                        }
                        else if (segment.Type == "Outro" && segment.StartTicks.HasValue && segment.EndTicks.HasValue)
                        {
                            _outroStartTime = TimeSpan.FromTicks(segment.StartTicks.Value);
                            _outroEndTime = TimeSpan.FromTicks(segment.EndTicks.Value);
                            Logger.LogInformation($"Found outro segment: {_outroStartTime} - {_outroEndTime}");
                        }
                    }

                    SegmentAvailabilityChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Logger.LogInformation("No media segments found for this item");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading media segments");
            }
        }

        public bool IsIntroSkipAvailable()
        {
            return _introStartTime.HasValue && _introEndTime.HasValue;
        }

        public bool IsOutroSkipAvailable()
        {
            return _outroStartTime.HasValue && _outroEndTime.HasValue;
        }

        public (TimeSpan? start, TimeSpan? end) GetIntroSegment()
        {
            return (_introStartTime, _introEndTime);
        }

        public (TimeSpan? start, TimeSpan? end) GetOutroSegment()
        {
            return (_outroStartTime, _outroEndTime);
        }

        public async Task SkipIntroAsync()
        {
            try
            {
                if (!IsIntroSkipAvailable() || _mediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                var currentPosition = _mediaPlayer.PlaybackSession.Position;
                Logger.LogInformation($"Manual skip intro from {currentPosition} to {_introEndTime.Value}");

                // Seek to end of intro
                _mediaPlayer.PlaybackSession.Position = _introEndTime.Value;

                // Analytics reporting would go here when SkipIntro endpoint is available in SDK

                SegmentSkipped?.Invoke(this, SkipSegmentType.Intro);
                _hasAutoSkippedIntro = true; // Prevent auto-skip after manual skip
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error skipping intro");
            }
        }

        public async Task SkipOutroAsync()
        {
            try
            {
                if (!IsOutroSkipAvailable() || _mediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                // For episodes, skip outro means play next episode
                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    Logger.LogInformation("Skipping outro by triggering next episode");
                    SegmentSkipped?.Invoke(this, SkipSegmentType.Outro);
                    return;
                }

                // For other content, skip to end of outro
                Logger.LogInformation($"Manual skip outro to {_outroEndTime.Value}");
                _mediaPlayer.PlaybackSession.Position = _outroEndTime.Value;

                SegmentSkipped?.Invoke(this, SkipSegmentType.Outro);
                _hasAutoSkippedOutro = true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error skipping outro");
            }
        }

        public async Task HandleAutoSkipAsync(TimeSpan currentPosition)
        {
            try
            {
                // Check for auto-skip intro
                if (_preferences?.AutoSkipIntroEnabled == true &&
                    IsIntroSkipAvailable() &&
                    currentPosition >= _introStartTime.Value &&
                    currentPosition < _introEndTime.Value &&
                    !_hasAutoSkippedIntro)
                {
                    _hasAutoSkippedIntro = true;
                    Logger.LogInformation($"Auto-skipping intro from {currentPosition} to {_introEndTime.Value}");

                    // Auto-skip the intro
                    _mediaPlayer.PlaybackSession.Position = _introEndTime.Value;
                    SegmentSkipped?.Invoke(this, SkipSegmentType.Intro);
                }

                // Check for auto-skip outro (if enabled in future)
                if (_preferences?.AutoSkipOutroEnabled == true &&
                    IsOutroSkipAvailable() &&
                    currentPosition >= _outroStartTime.Value &&
                    currentPosition < _outroEndTime.Value &&
                    !_hasAutoSkippedOutro)
                {
                    _hasAutoSkippedOutro = true;
                    Logger.LogInformation($"Auto-skipping outro from {currentPosition}");
                    await SkipOutroAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling auto-skip");
            }
        }

        public SkipSegmentType? GetCurrentSegmentType(TimeSpan position)
        {
            // Check if we're in intro range (with buffer time)
            if (IsIntroSkipAvailable())
            {
                // Show button slightly before intro starts, but handle case where intro starts at 0
                var showButtonTime = _introStartTime.Value > TimeSpan.FromSeconds(INTRO_BUFFER_TIME_SECONDS)
                    ? _introStartTime.Value - TimeSpan.FromSeconds(INTRO_BUFFER_TIME_SECONDS)
                    : TimeSpan.Zero;

                if (position >= showButtonTime && position < _introEndTime.Value)
                {
                    Logger.LogTrace(
                        $"Position {position:mm\\:ss} is within intro range ({showButtonTime:mm\\:ss} - {_introEndTime.Value:mm\\:ss})");
                    return SkipSegmentType.Intro;
                }
            }

            // Check if we're in outro range
            if (IsOutroSkipAvailable() &&
                position >= _outroStartTime.Value &&
                position < _outroEndTime.Value)
            {
                return SkipSegmentType.Outro;
            }

            return null;
        }

        public void Dispose()
        {
            _mediaPlayer = null;
            _currentItem = null;
            _preferences = null;
        }
    }

    // Response classes for MediaSegments API
    internal class MediaSegmentsResponse
    {
        public List<MediaSegment> Items { get; set; }
    }

    internal class MediaSegment
    {
        public string Type { get; set; }
        public long? StartTicks { get; set; }
        public long? EndTicks { get; set; }
    }
}
