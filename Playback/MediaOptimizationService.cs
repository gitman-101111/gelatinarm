using System;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Playback
{
    public interface IMediaOptimizationService : IDisposable
    {
        Task ApplyNormalizationAsync(MediaPlayer player, float? normalizationGainDb);

        /// <summary>
        ///     Opens a stream for either player: an HLS URL as an adaptive source, anything
        ///     else as the file it names
        /// </summary>
        Task<MediaSource> CreateStreamSourceAsync(string url);
    }

    /// <summary>
    ///     Opens playback streams for both players, and sets the music player's normalized volume
    /// </summary>
    public class MediaOptimizationService : BaseService, IMediaOptimizationService
    {
        private readonly IPreferencesService _preferencesService;

        public MediaOptimizationService(
            ILogger<MediaOptimizationService> logger,
            IPreferencesService preferencesService) : base(logger)
        {
            _preferencesService = preferencesService;
        }

        /// <summary>
        ///     Opens a stream for either player: an HLS URL as an adaptive source, anything else as the
        ///     file it names (Jellyfin streams no DASH). Both carry the API key, so no headers are set.
        /// </summary>
        public async Task<MediaSource> CreateStreamSourceAsync(string url)
        {
            if (!UrlHelper.IsHls(url))
            {
                return MediaSource.CreateFromUri(new Uri(url));
            }

            return await CreateAdaptiveSourceAsync(url);
        }

        private async Task<MediaSource> CreateAdaptiveSourceAsync(string streamUrl)
        {
            LogAdaptiveMediaSourceRequest(streamUrl);

            var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(streamUrl));

            LogAdaptiveMediaSourceResult(result);

            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                throw new InvalidOperationException($"AdaptiveMediaSource creation failed: {result.Status}");
            }

            ConfigureAdaptiveMediaSource(result.MediaSource);
            return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
        }

        private void ConfigureAdaptiveMediaSource(AdaptiveMediaSource adaptiveSource)
        {
            if (adaptiveSource.AvailableBitrates.Count > 0)
            {
                Logger.LogDebug("HLS bitrates: initial {InitialBitrate}kbps, {AvailableBitratesCount} available",
                    adaptiveSource.InitialBitrate / 1000, adaptiveSource.AvailableBitrates.Count);
            }

            adaptiveSource.DownloadBitrateChanged += OnDownloadBitrateChanged;
            adaptiveSource.PlaybackBitrateChanged += OnPlaybackBitrateChanged;
        }

        private void LogAdaptiveMediaSourceRequest(string streamUrl)
        {
            if (!UrlHelper.HasApiKey(streamUrl))
            {
                Logger.LogWarning(
                    "[HLS-DEBUG] Stream URL does not include ApiKey parameter; auth headers are not used for AdaptiveMediaSource");
            }

            // A manifest without StartTimeTicks starts at 0, whatever the resume point
            var playSessionId = UrlHelper.GetQueryParameter(streamUrl, "PlaySessionId");
            var startTimeTicks = UrlHelper.GetQueryParameter(streamUrl, "StartTimeTicks");
            var transcodeReasons = UrlHelper.GetQueryParameter(streamUrl, "TranscodeReasons");
            Logger.LogDebug("[HLS-DEBUG] Opening HLS stream: PlaySessionId={PlaySessionId}, StartTimeTicks={StartTimeTicks}, TranscodeReasons={TranscodeReasons}",
                playSessionId, startTimeTicks, transcodeReasons);
        }

        private void LogAdaptiveMediaSourceResult(AdaptiveMediaSourceCreationResult result)
        {
            var http = result.HttpResponseMessage;
            var level = result.Status == AdaptiveMediaSourceCreationStatus.Success ? LogLevel.Information : LogLevel.Error;
            Logger.Log(level,
                "[HLS-DEBUG] AdaptiveMediaSource: {Status}, HTTP {HttpStatus} {HttpReason}, extended error {ExtendedError} ({HResult:X8})",
                result.Status, http?.StatusCode, http?.ReasonPhrase, result.ExtendedError?.Message ?? "none",
                result.ExtendedError?.HResult ?? 0);
        }

        private void OnDownloadBitrateChanged(AdaptiveMediaSource sender,
            AdaptiveMediaSourceDownloadBitrateChangedEventArgs args)
        {
            Logger.LogInformation(
                "[BITRATE] Download bitrate changed from {ArgsOldValue}kbps to {ArgsNewValue}kbps", args.OldValue / 1000, args.NewValue / 1000);
            AppMemory.Log(Logger, "After bitrate change", LogLevel.Debug);
        }

        private void OnPlaybackBitrateChanged(AdaptiveMediaSource sender,
            AdaptiveMediaSourcePlaybackBitrateChangedEventArgs args)
        {
            Logger.LogInformation(
                "[BITRATE] Playback bitrate changed from {ArgsOldValue}kbps to {ArgsNewValue}kbps", args.OldValue / 1000, args.NewValue / 1000);
        }

        public async Task ApplyNormalizationAsync(MediaPlayer player, float? normalizationGainDb)
        {
            var prefs = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            if (!prefs.AudioNormalizationEnabled || normalizationGainDb == null)
            {
                player.Volume = 1.0;
                return;
            }

            // NormalizationGain is the adjustment required to reach the target loudness level
            var linear = Math.Pow(10.0, normalizationGainDb.Value / 20.0);
            player.Volume = Math.Max(0.0, Math.Min(1.0, linear));
            Logger.LogInformation(
                "[NORMALIZATION] Applied gain {NormalizationGainDb:F2} dB → volume {PlayerVolume:F3}", normalizationGainDb, player.Volume);
        }
    }
}
