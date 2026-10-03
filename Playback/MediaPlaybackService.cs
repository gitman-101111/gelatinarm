using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Playback
{
    /// <summary>
    ///     Playback reporting to the server, for the video and music players alike. Each report
    ///     says everything it reports; a play session is started, progressed and stopped once.
    /// </summary>
    public interface IMediaSessionService : IDisposable
    {
        Task ReportPlaybackStartAsync(PlaybackReport report);

        // Posts in the background and returns at once: a slow server must not hold the position timer
        void ReportPlaybackProgress(PlaybackReport report);

        Task ReportPlaybackStoppedAsync(PlaybackReport report);
    }

    public interface IMediaPlaybackService
    {
        /// <summary>
        ///     Asks the server how to play <paramref name="itemId" /> for this user and console, under
        ///     the Maximum Bitrate setting; <paramref name="configure" /> adds what the caller needs
        ///     (tracks, start position, what delivery to allow); <paramref name="hevcVideoCopyExpected" />,
        ///     <paramref name="source" /> and <paramref name="dolbyVisionAloneFailed" /> shape the
        ///     profile for this file (IDeviceProfileService). Null on failure.
        /// </summary>
        Task<PlaybackInfoResponse> GetPlaybackInfoAsync(Guid itemId, Action<PlaybackInfoDto> configure = null,
            bool hevcVideoCopyExpected = false, MediaSourceInfo source = null, bool dolbyVisionAloneFailed = false);
    }

    public class MediaPlaybackService : BaseService, IMediaPlaybackService, IMediaSessionService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IDeviceProfileService _deviceProfileService;
        private readonly IPreferencesService _preferencesService;
        private readonly IUserProfileService _userProfileService;

        // Kept per play session, not as flags: a track change restarts the stream under a new
        // play session id within one playback, and that session needs its own start and stop.
        private string _startReportedFor;
        private string _stopReportedFor;
        private Task _activeProgressReportTask;

        public MediaPlaybackService(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            IDeviceProfileService deviceProfileService,
            IPreferencesService preferencesService,
            ILogger<MediaPlaybackService> logger) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
            _deviceProfileService = deviceProfileService;
            _preferencesService = preferencesService;
        }

        public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(Guid itemId,
            Action<PlaybackInfoDto> configure = null, bool hevcVideoCopyExpected = false, MediaSourceInfo source = null,
            bool dolbyVisionAloneFailed = false)
        {
            var context = CreateErrorContext("GetPlaybackInfo", ErrorCategory.Media);
            try
            {
                if (!TryGetUserIdGuid(_userProfileService, out var userGuid))
                {
                    return null;
                }

                var preferences = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
                var deviceProfile = _deviceProfileService.GetDeviceProfile(hevcVideoCopyExpected, source,
                    preferences.PlayHdrOnAnyDisplay, dolbyVisionAloneFailed);

                // The server checks this against the file for direct play and uses it as the
                // transcode ceiling. Read per request so a settings change applies at once.
                var fromSetting = preferences.MaxStreamingBitrateMbps > 0;
                var request = new PlaybackInfoDto
                {
                    UserId = userGuid,
                    DeviceProfile = deviceProfile,
                    MaxStreamingBitrate = fromSetting
                        ? preferences.MaxStreamingBitrateMbps * 1000000
                        : deviceProfile.MaxStreamingBitrate,
                    AutoOpenLiveStream = true
                };
                configure?.Invoke(request);

                Logger.LogInformation(
                    "[PLAYBACK-INFO] ItemId={ItemId}, MaxStreamingBitrate={MaxStreamingBitrate} ({Source}), StartTimeTicks={StartTimeTicks}",
                    itemId, request.MaxStreamingBitrate, fromSetting ? "user setting" : "console maximum",
                    request.StartTimeTicks ?? 0);

                var response = await RetryAsync(
                    () => _apiClient.Items[itemId].PlaybackInfo.PostAsync(request)
                ).ConfigureAwait(false);

                Logger.LogInformation(
                    "[PLAYBACK-INFO] Response PlaySessionId={ResponsePlaySessionId}, MediaSources={MediaSourcesCount}", response?.PlaySessionId, response?.MediaSources?.Count ?? 0);

                return response;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                return null;
            }
        }

        public async Task ReportPlaybackStartAsync(PlaybackReport report)
        {
            if (string.IsNullOrEmpty(report?.PlaySessionId) || _startReportedFor == report.PlaySessionId)
            {
                Logger.LogDebug("Playback start already reported or missing session ID");
                return;
            }

            // Marked before the post: progress may follow at once, and a failed start report must
            // not block this session's progress and stop
            _startReportedFor = report.PlaySessionId;
            try
            {
                var playbackStartInfo = new PlaybackStartInfo
                {
                    ItemId = report.ItemId,
                    MediaSourceId = report.MediaSourceId,
                    AudioStreamIndex = report.AudioStreamIndex,
                    SubtitleStreamIndex = report.SubtitleStreamIndex,
                    PositionTicks = report.PositionTicks,
                    PlayMethod = (PlaybackStartInfo_PlayMethod)report.PlayMethod,
                    PlaySessionId = report.PlaySessionId,
                    CanSeek = true,
                    IsPaused = false
                };

                Logger.LogInformation(
                    "[PLAYBACK-START] ItemId={ItemId}, Session={PlaySessionId}, PositionTicks={PositionTicks}, " +
                    "PlayMethod={PlayMethod}, AudioStreamIndex={AudioStreamIndex}, SubtitleStreamIndex={SubtitleStreamIndex}",
                    report.ItemId, report.PlaySessionId, report.PositionTicks, report.PlayMethod,
                    report.AudioStreamIndex, report.SubtitleStreamIndex);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(PlaybackConstants.ApiCallTimeoutSeconds));
                await _apiClient.Sessions.Playing.PostAsync(playbackStartInfo, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("ReportPlaybackStart", ErrorCategory.Media, ErrorSeverity.Warning);
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void ReportPlaybackProgress(PlaybackReport report)
        {
            if (string.IsNullOrEmpty(report?.PlaySessionId) || _startReportedFor != report.PlaySessionId)
            {
                Logger.LogDebug("Cannot report progress - playback not started or missing session ID");
                return;
            }

            if (_activeProgressReportTask?.IsCompleted == false)
            {
                Logger.LogDebug("Previous progress report still in progress, skipping this interval");
                return;
            }

            var progressInfo = new PlaybackProgressInfo
            {
                ItemId = report.ItemId,
                MediaSourceId = report.MediaSourceId,
                PositionTicks = report.PositionTicks,
                PlaySessionId = report.PlaySessionId,
                IsPaused = report.IsPaused,
                PlayMethod = report.PlayMethod
            };

            Logger.LogDebug(
                "[PLAYBACK-PROGRESS] ItemId={ItemId}, Session={PlaySessionId}, PositionTicks={PositionTicks}, IsPaused={IsPaused}",
                report.ItemId, report.PlaySessionId, report.PositionTicks, report.IsPaused);

            _activeProgressReportTask = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(PlaybackConstants.ApiCallTimeoutSeconds));
                    await _apiClient.Sessions.Playing.Progress.PostAsync(progressInfo, cancellationToken: cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Timed out or superseded: the next interval sends the next report
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to report progress - will retry on next interval");
                }
            });
        }

        public async Task ReportPlaybackStoppedAsync(PlaybackReport report)
        {
            if (string.IsNullOrEmpty(report?.PlaySessionId) || _startReportedFor != report.PlaySessionId)
            {
                Logger.LogDebug("Cannot report stop - playback not started or missing session ID");
                return;
            }

            if (_stopReportedFor == report.PlaySessionId)
            {
                Logger.LogDebug("Playback stop already reported");
                return;
            }

            // Marked before the post: leaving the player and suspending the app can both report
            // the same session's stop
            _stopReportedFor = report.PlaySessionId;
            try
            {
                var stopInfo = new PlaybackStopInfo
                {
                    ItemId = report.ItemId,
                    MediaSourceId = report.MediaSourceId,
                    PositionTicks = report.PositionTicks,
                    PlaySessionId = report.PlaySessionId
                };

                Logger.LogInformation(
                    "[PLAYBACK-STOP] ItemId={ItemId}, Session={PlaySessionId}, PositionTicks={PositionTicks}",
                    report.ItemId, report.PlaySessionId, report.PositionTicks);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(PlaybackConstants.ApiCallTimeoutSeconds));
                await _apiClient.Sessions.Playing.Stopped.PostAsync(stopInfo, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ReportPlaybackStopped", ErrorCategory.Media), false);
            }
        }
    }
}
