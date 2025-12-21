using System;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Helpers
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
                ExpectedHlsSeekTarget = request.ExpectedHlsSeekTarget,
                HlsManifestOffset = request.HlsManifestOffset,
                HlsManifestOffsetApplied = request.HlsManifestOffsetApplied
            };

            if (request.IsBuffering && !request.WasBuffering)
            {
                _logger.LogInformation($"Buffering started at position {request.Position:hh\\:mm\\:ss}, HLS: {request.IsHls}");

                if (request.IsHls && request.ExpectedHlsSeekTarget > TimeSpan.Zero)
                {
                    var naturalDuration = request.NaturalDuration;
                    var metadataDuration = request.MetadataDuration;

                    if (naturalDuration > TimeSpan.Zero && metadataDuration > TimeSpan.Zero)
                    {
                        var durationDiff = Math.Abs((naturalDuration - metadataDuration).TotalSeconds);
                        if (durationDiff > 10 && naturalDuration < metadataDuration)
                        {
                            _logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected during buffering. Natural: {naturalDuration:hh\\:mm\\:ss}, Metadata: {metadataDuration:hh\\:mm\\:ss}");
                            result.HlsManifestOffset = request.ExpectedHlsSeekTarget;
                            result.HlsManifestOffsetApplied = false;
                            result.ExpectedHlsSeekTarget = TimeSpan.Zero;
                            _logger.LogInformation($"[HLS-MANIFEST-CHANGE] Position 0 in new manifest = {result.HlsManifestOffset:hh\\:mm\\:ss}");
                        }
                    }
                }

                result.BufferingStartTime = DateTime.UtcNow;
                result.TriggerHlsBufferingFix = true;
                result.StartTimeoutTimer = true;
                _logger.LogInformation($"[BUFFERING-TIMEOUT] Started {_timeoutSeconds}s timeout timer for {(request.IsHls ? "HLS" : "direct")} stream");
                return result;
            }

            if (!request.IsBuffering && request.WasBuffering)
            {
                _logger.LogInformation($"Buffering ended at position {request.Position:hh\\:mm\\:ss}, transitioning to {request.NewState}");

                if (request.BufferingStartTime.HasValue)
                {
                    var bufferingDuration = DateTime.UtcNow - request.BufferingStartTime.Value;
                    _logger.LogInformation($"[BUFFERING-END] Buffering completed after {bufferingDuration.TotalSeconds:F1}s");
                }

                result.BufferingStartTime = null;
                result.StopTimeoutTimer = true;
                result.ResetHlsTrackChange = true;
            }

            return result;
        }
    }

    public sealed class BufferingStateRequest
    {
        public bool IsBuffering { get; set; }
        public bool WasBuffering { get; set; }
        public bool IsHls { get; set; }
        public MediaPlaybackState NewState { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public TimeSpan NaturalDuration { get; set; }
        public TimeSpan MetadataDuration { get; set; }
        public DateTime? BufferingStartTime { get; set; }
        public TimeSpan HlsManifestOffset { get; set; }
        public bool HlsManifestOffsetApplied { get; set; }
    }

    public sealed class BufferingStateResult
    {
        public DateTime? BufferingStartTime { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public TimeSpan HlsManifestOffset { get; set; }
        public bool HlsManifestOffsetApplied { get; set; }
        public bool StartTimeoutTimer { get; set; }
        public bool StopTimeoutTimer { get; set; }
        public bool TriggerHlsBufferingFix { get; set; }
        public bool ResetHlsTrackChange { get; set; }
    }
}
