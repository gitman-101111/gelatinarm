using System;
using Windows.Media.Playback;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public sealed class BufferingStateCoordinator
    {
        private readonly ILogger _logger;
        private readonly int _timeoutSeconds;

        public BufferingStateCoordinator(ILogger logger, int timeoutSeconds)
        {
            _logger = logger;
            _timeoutSeconds = timeoutSeconds;
        }

        public BufferingStateResult Handle(BufferingStateRequest request)
        {
            var result = new BufferingStateResult
            {
                BufferingStartTime = request.BufferingStartTime,
                ExpectedHlsSeekTarget = request.ExpectedHlsSeekTarget
            };

            if (request.IsBuffering && !request.BufferingStartTime.HasValue)
            {
                var isRecentSeek = request.PendingSeekCount > 0 ||
                                   (request.LastSeekTime != DateTime.MinValue &&
                                    DateTime.UtcNow - request.LastSeekTime < TimeSpan.FromSeconds(2));
                var seekLabel = isRecentSeek ? " (after seek)" : string.Empty;
                var lastSeekAge = request.LastSeekTime == DateTime.MinValue
                    ? "n/a"
                    : $"{(DateTime.UtcNow - request.LastSeekTime).TotalSeconds:F1}s";
                _logger.LogInformation(
                    "Buffering started at position {RequestPosition:hh\\:mm\\:ss}, HLS: {RequestIsHls}{SeekLabel}, " +
                    "PendingSeeks: {RequestPendingSeekCount}, LastSeekAge: {LastSeekAge}", request.Position, request.IsHls, seekLabel, request.PendingSeekCount, lastSeekAge);

                if (request.IsHls && request.ExpectedHlsSeekTarget > TimeSpan.Zero)
                {
                    var naturalDuration = request.NaturalDuration;
                    var metadataDuration = request.MetadataDuration;

                    if (naturalDuration > TimeSpan.Zero && metadataDuration > TimeSpan.Zero)
                    {
                        var durationDiff = Math.Abs((naturalDuration - metadataDuration).TotalSeconds);
                        if (durationDiff > 10 && naturalDuration < metadataDuration)
                        {
                            result.HlsManifestOffset = request.ExpectedHlsSeekTarget;
                            result.ExpectedHlsSeekTarget = TimeSpan.Zero;
                            _logger.LogInformation(
                                "[HLS-MANIFEST-CHANGE] Detected during buffering: natural {NaturalDuration:hh\\:mm\\:ss}, metadata {MetadataDuration:hh\\:mm\\:ss}; " +
                                "position 0 in the new manifest = {ResultHlsManifestOffset:hh\\:mm\\:ss}",
                                naturalDuration, metadataDuration, result.HlsManifestOffset);
                        }
                    }
                }

                result.BufferingStartTime = DateTime.UtcNow;
                result.TriggerHlsBufferingFix =
                    request.IsHls && request.HasManifestOffset;
                result.StartTimeoutTimer = true;
                _logger.LogDebug(
                    "[BUFFERING-TIMEOUT] Started {TimeoutSeconds}s timeout timer for {StreamKind} stream", _timeoutSeconds, request.IsHls ? "HLS" : "direct");
                return result;
            }

            if (!request.IsBuffering && request.BufferingStartTime.HasValue)
            {
                var bufferingDuration = DateTime.UtcNow - request.BufferingStartTime.Value;
                _logger.LogInformation(
                    "Buffering ended at position {RequestPosition:hh\\:mm\\:ss} after {BufferingDurationTotalSeconds:F1}s, transitioning to {RequestNewState}",
                    request.Position, bufferingDuration.TotalSeconds, request.NewState);

                result.BufferingStartTime = null;
                result.StopTimeoutTimer = true;
            }

            return result;
        }
    }

    public sealed class BufferingStateRequest
    {
        public bool IsBuffering { get; set; }
        public bool IsHls { get; set; }
        public bool HasManifestOffset { get; set; }
        public MediaPlaybackState NewState { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public TimeSpan NaturalDuration { get; set; }
        public TimeSpan MetadataDuration { get; set; }
        public DateTime LastSeekTime { get; set; }
        public int PendingSeekCount { get; set; }
        public DateTime? BufferingStartTime { get; set; }
    }

    public sealed class BufferingStateResult
    {
        public DateTime? BufferingStartTime { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public TimeSpan HlsManifestOffset { get; set; }
        public bool StartTimeoutTimer { get; set; }
        public bool StopTimeoutTimer { get; set; }
        public bool TriggerHlsBufferingFix { get; set; }
    }
}
