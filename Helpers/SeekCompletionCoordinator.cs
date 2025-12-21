using System;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Helpers
{
    internal sealed class SeekCompletionCoordinator
    {
        private readonly ILogger _logger;

        public SeekCompletionCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public void HandleSeekCompleted(SeekCompletionContext context)
        {
            if (context == null)
            {
                return;
            }

            if (context.PendingSeekCount > 0)
            {
                context.DecrementPendingSeek?.Invoke();
            }

            _logger.LogInformation($"SeekCompleted event fired. Position: {context.Position:hh\\:mm\\:ss}, " +
                                   $"State: {context.PlaybackState}, Pending seeks: {context.PendingSeekCount}");
            _logger.LogInformation($"NaturalDuration after seek: {context.NaturalDuration:hh\\:mm\\:ss}, " +
                                   $"MetadataDuration: {context.MetadataDuration:hh\\:mm\\:ss}");

            if (context.IsHlsStream && !context.HasPerformedInitialSeek)
            {
                context.SetActualResumePosition?.Invoke(context.Position);
                context.MarkInitialSeekPerformed?.Invoke();
            }

            HandleDurationMismatchAfterSeek(context);

            _logger.LogInformation("SeekCompleted");
        }

        private void HandleDurationMismatchAfterSeek(SeekCompletionContext context)
        {
            if (!context.NaturalDuration.HasValue || context.MetadataDuration <= TimeSpan.Zero)
            {
                return;
            }

            var durationDiff = Math.Abs((context.NaturalDuration.Value - context.MetadataDuration).TotalSeconds);
            if (durationDiff <= 10)
            {
                return;
            }

            _logger.LogWarning($"Duration mismatch after seek! Natural: {context.NaturalDuration:mm\\:ss}, " +
                               $"Metadata: {context.MetadataDuration:mm\\:ss}, Diff: {durationDiff:F1}s");

            if (context.IsHlsStream && context.NaturalDuration.Value < context.MetadataDuration * 0.5)
            {
                _logger.LogWarning("[HLS-MANIFEST] Manifest appears truncated after resume seek");
                _logger.LogWarning($"[HLS-MANIFEST] Natural duration is only " +
                                   $"{(context.NaturalDuration.Value.TotalSeconds / context.MetadataDuration.TotalSeconds * 100):F1}% of expected");

                if (context.PlaybackState == MediaPlaybackState.Paused)
                {
                    _logger.LogInformation("[HLS-RECOVERY] Attempting to recover by resuming playback");
                    context.AttemptHlsRecovery?.Invoke();
                }
            }

            if (context.IsHlsStream &&
                context.NaturalDuration.Value < TimeSpan.FromMinutes(1) &&
                context.Position > context.NaturalDuration.Value &&
                context.ActualResumePosition > TimeSpan.Zero)
            {
                _logger.LogError($"[HLS-CORRUPT-RESUME] Manifest corrupted after resume to {context.ActualResumePosition:mm\\:ss}");
                _logger.LogError($"[HLS-CORRUPT-RESUME] Natural duration is only {context.NaturalDuration.Value:mm\\:ss}, position is {context.Position:mm\\:ss}");
                return;
            }

            context.TryHandleHlsManifestChange?.Invoke(context.Position, context.NaturalDuration.Value, context.MetadataDuration);
        }
    }

    internal sealed class SeekCompletionContext
    {
        public TimeSpan Position { get; set; }
        public MediaPlaybackState? PlaybackState { get; set; }
        public TimeSpan? NaturalDuration { get; set; }
        public TimeSpan MetadataDuration { get; set; }
        public bool IsHlsStream { get; set; }
        public bool HasPerformedInitialSeek { get; set; }
        public int PendingSeekCount { get; set; }
        public TimeSpan ActualResumePosition { get; set; }
        public Action DecrementPendingSeek { get; set; }
        public Action<TimeSpan> SetActualResumePosition { get; set; }
        public Action MarkInitialSeekPerformed { get; set; }
        public Action AttemptHlsRecovery { get; set; }
        public Action<TimeSpan, TimeSpan, TimeSpan> TryHandleHlsManifestChange { get; set; }
    }
}
