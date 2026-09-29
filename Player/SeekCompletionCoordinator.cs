using System;
using Windows.Media.Playback;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
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
            context.DecrementPendingSeek();

            _logger.LogInformation("SeekCompleted at {ContextPosition:hh\\:mm\\:ss}, State: {ContextPlaybackState}, " +
                                   "pending seeks before it: {ContextPendingSeekCount}, NaturalDuration: " +
                                   "{ContextNaturalDuration:hh\\:mm\\:ss}, MetadataDuration: {ContextMetadataDuration:hh\\:mm\\:ss}",
                context.Position, context.PlaybackState, context.PendingSeekCount, context.NaturalDuration,
                context.MetadataDuration);

            if (!context.HasPerformedInitialSeek)
            {
                if (context.IsHlsStream)
                {
                    context.SetActualResumePosition(context.Position);
                }

                context.MarkInitialSeekPerformed();
            }

            HandleDurationMismatchAfterSeek(context);
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

            _logger.LogWarning("Duration mismatch after seek! Natural: {ContextNaturalDuration:hh\\:mm\\:ss}, " +
                               "Metadata: {ContextMetadataDuration:hh\\:mm\\:ss}, Diff: {DurationDiff:F1}s", context.NaturalDuration, context.MetadataDuration, durationDiff);

            if (context.IsHlsStream && context.NaturalDuration.Value < context.MetadataDuration * 0.5)
            {
                _logger.LogWarning("[HLS-MANIFEST] Manifest appears truncated after resume seek: natural duration is only " +
                                   "{NaturalDurationTotalSeconds:F1}% of expected", context.NaturalDuration.Value.TotalSeconds / context.MetadataDuration.TotalSeconds * 100);

                if (context.PlaybackState == MediaPlaybackState.Paused)
                {
                    _logger.LogInformation("[HLS-RECOVERY] Attempting to recover by resuming playback");
                    context.AttemptHlsRecovery();
                }
            }

            if (context.IsHlsStream &&
                context.NaturalDuration.Value < TimeSpan.FromMinutes(1) &&
                context.Position > context.NaturalDuration.Value &&
                context.ActualResumePosition > TimeSpan.Zero)
            {
                _logger.LogError(
                    "[HLS-CORRUPT-RESUME] Manifest corrupted after resume to {ContextActualResumePosition:hh\\:mm\\:ss}: natural duration is only {ContextNaturalDuration:hh\\:mm\\:ss}, position is {ContextPosition:hh\\:mm\\:ss}",
                    context.ActualResumePosition, context.NaturalDuration.Value, context.Position);
                return;
            }

            context.TryHandleHlsManifestChange(context.Position, context.NaturalDuration.Value, context.MetadataDuration);
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
