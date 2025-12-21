using System;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Helpers
{
    internal sealed class ResumeFlowCoordinator
    {
        private readonly ILogger _logger;
        private readonly IPlaybackControlService _playbackControlService;
        private readonly ResumeRetryCoordinator _resumeRetryCoordinator;

        public ResumeFlowCoordinator(
            ILogger logger,
            IPlaybackControlService playbackControlService,
            ResumeRetryCoordinator resumeRetryCoordinator)
        {
            _logger = logger;
            _playbackControlService = playbackControlService;
            _resumeRetryCoordinator = resumeRetryCoordinator;
        }

        public async Task<ResumeFlowOutcome> HandleResumeOnPlaybackStartAsync(ResumeFlowContext context)
        {
            var startPositionTicks = context.PlaybackParams?.StartPositionTicks;
            if (context.SessionState.HasPerformedInitialSeek ||
                !startPositionTicks.HasValue ||
                startPositionTicks.Value <= 0)
            {
                return ResumeFlowOutcome.Skipped();
            }

            _logger.LogInformation("Video playback started - checking for resume position");
            context.SessionState.HasPerformedInitialSeek = true;

            bool resumeResult = await TryApplyResumePositionAsync(context).ConfigureAwait(false);
            int retryCount = 0;

            if (!resumeResult && context.PlaybackParams?.StartPositionTicks > 0)
            {
                var retryOutcome = await _resumeRetryCoordinator.ExecuteAsync(
                    () => TryApplyResumePositionAsync(context),
                    () => context.SessionState.IsHlsStream ?
                        _playbackControlService.IsHlsResumeInProgress() :
                        !context.SessionState.HasPerformedInitialSeek,
                    context.SessionState.IsHlsStream).ConfigureAwait(false);

                resumeResult = retryOutcome.Success;
                retryCount = retryOutcome.Retries;
            }

            var streamType = context.SessionState.IsHlsStream ? "HLS" : "DirectPlay";
            var targetPosition = TimeSpan.FromTicks(context.PlaybackParams?.StartPositionTicks ?? 0);
            var actualPosition = context.GetCurrentPosition?.Invoke() ?? TimeSpan.Zero;

            if (resumeResult)
            {
                _logger.LogInformation($"[{streamType}] Successfully resumed after {retryCount} retries");

                var diff = Math.Abs((actualPosition - targetPosition).TotalSeconds);
                if (diff > 3.0)
                {
                    _logger.LogInformation($"[{streamType}] Accepted server position {actualPosition:mm\\:ss} " +
                                           $"(target was {targetPosition:mm\\:ss}, diff: {diff:F1}s)");
                }

                if (context.SessionState.IsHlsStream)
                {
                    _logger.LogInformation($"HLS stream - resume to {targetPosition:hh\\:mm\\:ss} completed");
                }
            }
            else if (context.OnResumeFailedAsync != null)
            {
                _logger.LogWarning($"[{streamType}] Failed to resume after all retries");
                await context.OnResumeFailedAsync(new ResumeFailureContext
                {
                    RetryCount = retryCount,
                    CurrentPosition = actualPosition,
                    TargetPosition = targetPosition,
                    StreamType = streamType
                }).ConfigureAwait(false);
            }

            return new ResumeFlowOutcome(resumeResult, retryCount, streamType, targetPosition, actualPosition);
        }

        private Task<bool> TryApplyResumePositionAsync(ResumeFlowContext context)
        {
            _logger.LogInformation($"TryApplyResumePositionAsync called. HasPerformedInitialSeek: {context.SessionState.HasPerformedInitialSeek}");

            bool resumeApplied = false;

            if (_playbackControlService != null)
            {
                var pendingSeekPositionTicks = context.SessionState.PendingSeekPositionAfterQualitySwitch;
                var result = _playbackControlService.ApplyResumeIfNeeded(ref pendingSeekPositionTicks);
                context.SessionState.PendingSeekPositionAfterQualitySwitch = pendingSeekPositionTicks;
                if (result)
                {
                    _logger.LogInformation("Applied pending resume position from PlaybackControlService");
                    resumeApplied = true;

                    if (context.SessionState.IsHlsStream && _playbackControlService.HlsManifestOffset > TimeSpan.Zero)
                    {
                        var resumePos = _playbackControlService.HlsManifestOffset;
                        _logger.LogInformation($"[HLS-RESUME] PlaybackControlService applied manifest offset workaround at {resumePos:hh\\:mm\\:ss}");

                        context.SessionState.HlsManifestOffset = TimeSpan.Zero;
                        context.SessionState.HlsManifestOffsetApplied = false;
                        context.OnHlsResumeFixCompleted?.Invoke();
                    }
                }
            }

            if (resumeApplied)
            {
                context.SessionState.HasPerformedInitialSeek = true;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    internal sealed class ResumeFlowContext
    {
        public PlaybackSessionState SessionState { get; set; }
        public MediaPlaybackParams PlaybackParams { get; set; }
        public Func<TimeSpan> GetCurrentPosition { get; set; }
        public Action OnHlsResumeFixCompleted { get; set; }
        public Func<ResumeFailureContext, Task> OnResumeFailedAsync { get; set; }
    }

    internal sealed class ResumeFailureContext
    {
        public int RetryCount { get; set; }
        public TimeSpan CurrentPosition { get; set; }
        public TimeSpan TargetPosition { get; set; }
        public string StreamType { get; set; }
    }

    internal sealed class ResumeFlowOutcome
    {
        public ResumeFlowOutcome(bool success, int retryCount, string streamType, TimeSpan targetPosition, TimeSpan actualPosition)
        {
            Success = success;
            RetryCount = retryCount;
            StreamType = streamType;
            TargetPosition = targetPosition;
            ActualPosition = actualPosition;
        }

        public bool Success { get; }
        public int RetryCount { get; }
        public string StreamType { get; }
        public TimeSpan TargetPosition { get; }
        public TimeSpan ActualPosition { get; }

        public static ResumeFlowOutcome Skipped()
        {
            return new ResumeFlowOutcome(false, 0, string.Empty, TimeSpan.Zero, TimeSpan.Zero);
        }
    }
}
