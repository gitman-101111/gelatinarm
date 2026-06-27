using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    internal enum ResumeState
    {
        NotStarted, // Resume not yet attempted
        InProgress, // Initial resume seek applied
        Verifying, // Checking if position is advancing at target
        RecoveryNeeded, // Stuck, applying recovery techniques
        Succeeded, // Resume completed successfully
        Failed // Resume failed after all attempts
    }

    internal struct RetryConfig
    {
        public int MaxAttempts;
        public int DelayMs;
        public double ToleranceSeconds;

        public static RetryConfig ForHls => new RetryConfig
        {
            MaxAttempts = 8, // HLS needs more time for server to transcode
            DelayMs = 5000, // Give server time to restart transcode and generate segments
            ToleranceSeconds = 10.0 // Accounts for segment boundaries + keyframe alignment
        };

        public static RetryConfig ForDirectPlay => new RetryConfig
        {
            MaxAttempts = 5, DelayMs = 1000, ToleranceSeconds = 5.0 // Accounts for keyframe alignment and buffering
        };
    }

    internal interface IStreamResumePolicy
    {
        bool CanTrustStartTimeTicks { get; }
        bool NeedsClientSeek { get; }
        double AcceptableToleranceSeconds { get; }
        bool ShouldUseManifestOffset { get; }
    }

    internal sealed class DirectPlayResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => true;
        public bool NeedsClientSeek => false;
        public double AcceptableToleranceSeconds => 3.0;
        public bool ShouldUseManifestOffset => false;
    }

    internal sealed class HlsResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => false;
        public bool NeedsClientSeek => true;
        public double AcceptableToleranceSeconds => RetryConfig.ForHls.ToleranceSeconds;
        public bool ShouldUseManifestOffset => true;
    }

    internal sealed class HlsInitialResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => false;
        public bool NeedsClientSeek => true;
        public double AcceptableToleranceSeconds => RetryConfig.ForHls.ToleranceSeconds;
        public bool ShouldUseManifestOffset => false;
    }

    internal sealed class ResumeVerificationHelper
    {
        private TimeSpan _lastVerifiedPosition = TimeSpan.Zero;
        private DateTime _lastPositionCheckTime = DateTime.MinValue;
        private int _stuckPositionCount;

        public TimeSpan LastVerifiedPosition => _lastVerifiedPosition;
        public DateTime LastPositionCheckTime => _lastPositionCheckTime;
        public int StuckPositionCount => Volatile.Read(ref _stuckPositionCount);

        public void Reset()
        {
            _lastVerifiedPosition = TimeSpan.Zero;
            _lastPositionCheckTime = DateTime.MinValue;
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public void StartVerification(TimeSpan currentPosition)
        {
            _lastVerifiedPosition = currentPosition;
            _lastPositionCheckTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public bool TryGetPositionChange(TimeSpan currentPosition, out double positionChange)
        {
            positionChange = 0;
            var timeSinceLastCheck = DateTime.UtcNow - _lastPositionCheckTime;
            if (timeSinceLastCheck.TotalSeconds < 1)
            {
                return false;
            }

            positionChange = Math.Abs((currentPosition - _lastVerifiedPosition).TotalSeconds);
            return true;
        }

        public int IncrementStuckCount()
        {
            return Interlocked.Increment(ref _stuckPositionCount);
        }

        public void ClearStuckCount()
        {
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public void UpdateLastCheck(TimeSpan currentPosition)
        {
            _lastPositionCheckTime = DateTime.UtcNow;
            _lastVerifiedPosition = currentPosition;
        }

        public static bool IsRecoverySeekOnly(int recoveryAttemptLevel, double positionChange)
        {
            return recoveryAttemptLevel == 2 && positionChange <= 1.5;
        }
    }

    internal sealed class PlaybackResumeCoordinator
    {
        private const int MaxStuckChecks = 5; // If position doesn't change for 5 checks, it's stuck
        private const double StuckPositionTolerance = 0.5; // Position must advance by at least 0.5 seconds

        private readonly ILogger _logger;
        private ResumeState _resumeState = ResumeState.NotStarted;
        private int _hlsResumeAttempts;
        private DateTime _resumeStartTime = DateTime.MinValue;
        private readonly ResumeVerificationHelper _resumeVerification = new ResumeVerificationHelper();
        private int _recoveryAttemptLevel;

        public PlaybackResumeCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<ResumeFlowOutcome> HandleResumeOnPlaybackStartAsync(ResumeCoordinatorContext context)
        {
            if (context == null)
            {
                return ResumeFlowOutcome.Skipped();
            }

            var startPositionTicks = context.PlaybackParams?.StartPositionTicks;
            if (context.SessionState?.HasPerformedInitialSeek == true ||
                !startPositionTicks.HasValue ||
                startPositionTicks.Value <= 0)
            {
                return ResumeFlowOutcome.Skipped();
            }

            _logger.LogInformation("Video playback started - checking for resume position");

            var resumeResult = context.ApplyResumeIfNeeded?.Invoke() == true;
            var retryCount = 0;

            if (!resumeResult && startPositionTicks.Value > 0)
            {
                var isHlsStream = context.SessionState?.IsHlsStream == true;
                var maxRetries = isHlsStream ? 15 : 8;
                var retryDelay = isHlsStream ? 5000 : 1000;
                var streamType = isHlsStream ? "HLS-RESUME" : "DirectPlay";

                while (!resumeResult && retryCount < maxRetries)
                {
                    retryCount++;

                    if (context.IsResumePending != null && !context.IsResumePending())
                    {
                        _logger.LogInformation("Resume no longer pending, stopping retries");
                        break;
                    }

                    _logger.LogInformation($"[{streamType}] Retry {retryCount}/{maxRetries} in {retryDelay}ms");
                    await Task.Delay(retryDelay).ConfigureAwait(false);

                    resumeResult = context.ApplyResumeIfNeeded?.Invoke() == true;
                }
            }

            var targetPosition = TimeSpan.FromTicks(startPositionTicks.Value);
            var actualPosition = context.GetCurrentPosition?.Invoke() ?? TimeSpan.Zero;
            var streamLabel = context.SessionState?.IsHlsStream == true ? "HLS" : "DirectPlay";

            if (resumeResult)
            {
                _logger.LogInformation($"[{streamLabel}] Successfully resumed after {retryCount} retries");

                var diff = Math.Abs((actualPosition - targetPosition).TotalSeconds);
                if (diff > 3.0)
                {
                    _logger.LogInformation($"[{streamLabel}] Accepted server position {actualPosition:mm\\:ss} " +
                                           $"(target was {targetPosition:mm\\:ss}, diff: {diff:F1}s)");
                }

                if (context.SessionState?.IsHlsStream == true &&
                    (context.GetManifestOffset?.Invoke() ?? TimeSpan.Zero) > TimeSpan.Zero)
                {
                    var resumePos = context.GetManifestOffset?.Invoke() ?? TimeSpan.Zero;
                    _logger.LogInformation(
                        $"[HLS-RESUME] PlaybackControlService applied manifest offset workaround at {resumePos:hh\\:mm\\:ss}");

                    context.SessionState.HlsManifestOffsetApplied = false;
                    context.OnHlsResumeFixCompleted?.Invoke();
                }
            }
            else if (context.OnResumeFailedAsync != null)
            {
                _logger.LogWarning($"[{streamLabel}] Failed to resume after all retries");
                await context.OnResumeFailedAsync(new ResumeFailureContext
                {
                    RetryCount = retryCount,
                    CurrentPosition = actualPosition,
                    TargetPosition = targetPosition,
                    StreamType = streamLabel
                }).ConfigureAwait(false);
            }

            return new ResumeFlowOutcome(resumeResult, retryCount, streamLabel, targetPosition, actualPosition);
        }

        public void Reset()
        {
            _resumeState = ResumeState.NotStarted;
            _hlsResumeAttempts = 0;
            _resumeStartTime = DateTime.MinValue;
            _resumeVerification.Reset();
            _recoveryAttemptLevel = 0;
        }

        public bool ApplyPendingResumePosition(
            MediaPlayer mediaPlayer,
            ref TimeSpan? pendingResumePosition,
            IStreamResumePolicy resumePolicy,
            TimeSpan? originalTarget,
            Action<TimeSpan> setManifestOffset)
        {
            if (!pendingResumePosition.HasValue || mediaPlayer?.PlaybackSession == null)
            {
                return false;
            }

            if (resumePolicy.NeedsClientSeek)
            {
                return ApplyHlsResumePosition(mediaPlayer, ref pendingResumePosition, resumePolicy, originalTarget,
                    setManifestOffset);
            }

            return ApplySimpleResumePosition(mediaPlayer, ref pendingResumePosition, resumePolicy);
        }

        public bool IsInProgress(IStreamResumePolicy resumePolicy, bool hasPending)
        {
            return resumePolicy.NeedsClientSeek && hasPending &&
                   _resumeState != ResumeState.Succeeded && _resumeState != ResumeState.Failed;
        }

        public void CancelPendingResume(ref TimeSpan? pendingResumePosition, string reason)
        {
            if (!pendingResumePosition.HasValue)
            {
                return;
            }

            _logger.LogInformation(
                $"[RESUME-CANCEL] {reason} - clearing pending resume at {pendingResumePosition:hh\\:hh\\:mm\\:ss}");
            pendingResumePosition = null;
            _resumeState = ResumeState.Failed;
            _hlsResumeAttempts = 0;
            _resumeStartTime = DateTime.MinValue;
        }

        public (bool InProgress, int Attempts, TimeSpan? Target) GetStatus(IStreamResumePolicy resumePolicy,
            TimeSpan? pendingResumePosition)
        {
            return (IsInProgress(resumePolicy, pendingResumePosition.HasValue), _hlsResumeAttempts,
                pendingResumePosition);
        }

        private bool ApplySimpleResumePosition(MediaPlayer mediaPlayer, ref TimeSpan? pendingResumePosition,
            IStreamResumePolicy resumePolicy)
        {
            if (!pendingResumePosition.HasValue || mediaPlayer?.PlaybackSession == null)
            {
                return false;
            }

            var resumePosition = pendingResumePosition.Value;

            // Initialize tracking on first attempt (reuse same variables as HLS)
            if (_resumeVerification.LastPositionCheckTime == DateTime.MinValue && _resumeStartTime == DateTime.MinValue)
            {
                _resumeStartTime = DateTime.UtcNow; // Reuse for timeout tracking
            }

            // Check for overall timeout - don't try forever
            if (_resumeStartTime != DateTime.MinValue)
            {
                var totalElapsed = DateTime.UtcNow - _resumeStartTime;
                if (totalElapsed.TotalSeconds > 20) // 20 second timeout for DirectPlay (shorter than HLS)
                {
                    _logger.LogError(
                        $"[DirectPlay-TIMEOUT] Resume operation timed out after {totalElapsed.TotalSeconds:F1}s");
                    pendingResumePosition = null;
                    return false;
                }
            }

            try
            {
                var playbackSession = mediaPlayer.PlaybackSession;
                var currentState = playbackSession.PlaybackState;
                var currentPosition = playbackSession.Position;
                var naturalDuration = playbackSession.NaturalDuration;

                // Check if media is ready for seeking
                if (currentState == MediaPlaybackState.Opening)
                {
                    _logger.LogInformation(
                        $"[DirectPlay] Media still opening, deferring resume to {resumePosition:hh\\:hh\\:mm\\:ss}");
                    return false; // Will be retried
                }

                // For DirectPlay, wait for initial buffering to complete if at position 0
                if (currentState == MediaPlaybackState.Buffering && currentPosition == TimeSpan.Zero)
                {
                    _logger.LogInformation("[DirectPlay] Waiting for initial buffering to complete before resume");
                    return false; // Will be retried
                }

                // Check if we're already at the target position (within tolerance for drift)
                var positionDiff = Math.Abs((currentPosition - resumePosition).TotalSeconds);
                if (positionDiff <= resumePolicy.AcceptableToleranceSeconds)
                {
                    // For DirectPlay, also verify playback is actually advancing before marking complete
                    // This prevents marking as complete when stuck buffering at the resume position

                    if (_resumeVerification.LastPositionCheckTime == DateTime.MinValue)
                    {
                        // First time reaching target position - start monitoring
                        _logger.LogInformation(
                            $"[DirectPlay] Reached target position {currentPosition:hh\\:hh\\:mm\\:ss}, verifying playback is advancing...");
                        _resumeVerification.StartVerification(currentPosition);
                        return false; // Need to verify position advances
                    }

                    if (!_resumeVerification.TryGetPositionChange(currentPosition, out var positionChange))
                    {
                        return false; // Too soon to check
                    }

                    if (positionChange < StuckPositionTolerance)
                    {
                        var stuckCount = _resumeVerification.IncrementStuckCount();
                        _logger.LogWarning(
                            $"[DirectPlay-STUCK] Position not advancing at resume point {currentPosition:hh\\:hh\\:mm\\:ss} (count: {stuckCount}/{MaxStuckChecks})");

                        if (stuckCount >= MaxStuckChecks)
                        {
                            _logger.LogError(
                                $"[DirectPlay-STUCK] Playback is stuck at {currentPosition:hh\\:hh\\:mm\\:ss} after resume. Giving up.");
                            pendingResumePosition = null;
                            return false; // Report failure
                        }

                        // Try recovery techniques for DirectPlay
                        if (stuckCount == 2 && currentState == MediaPlaybackState.Playing)
                        {
                            _logger.LogInformation("[DirectPlay-STUCK] Attempting to unstick with pause/play");
                            mediaPlayer.Pause();
                            // Use async delay without blocking
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                                mediaPlayer.Play();
                            });
                        }
                        else if (stuckCount == 3)
                        {
                            _logger.LogInformation("[DirectPlay-STUCK] Attempting to unstick with small seek forward");
                            playbackSession.Position = currentPosition + TimeSpan.FromSeconds(1);
                            _recoveryAttemptLevel = 2; // Mark that we did a forward seek
                        }

                        _resumeVerification.UpdateLastCheck(currentPosition);
                        return false; // Continue checking
                    }

                    // Position changed, but need to verify it's not just our recovery seek
                    if (ResumeVerificationHelper.IsRecoverySeekOnly(_recoveryAttemptLevel, positionChange))
                    {
                        // Position only advanced due to our recovery seek, not actual playback
                        _logger.LogWarning(
                            $"[DirectPlay-STUCK] Position change ({positionChange:F1}s) appears to be from recovery seek, not actual playback");
                        _recoveryAttemptLevel = 0;
                        var stuckCount = _resumeVerification.IncrementStuckCount();

                        if (stuckCount >= MaxStuckChecks)
                        {
                            _logger.LogError(
                                $"[DirectPlay-STUCK] Playback is stuck at {currentPosition:hh\\:hh\\:mm\\:ss} after resume. Giving up.");
                            pendingResumePosition = null;
                            return false;
                        }

                        _resumeVerification.UpdateLastCheck(currentPosition);
                        return false;
                    }

                    // Position is truly advancing! Resume successful
                    _logger.LogInformation(
                        $"[DirectPlay] Resume successful! Position advancing from {_resumeVerification.LastVerifiedPosition:hh\\:hh\\:mm\\:ss} to {currentPosition:hh\\:hh\\:mm\\:ss}");
                    pendingResumePosition = null;
                    _resumeVerification.ClearStuckCount();
                    _recoveryAttemptLevel = 0;
                    return true;
                }

                // Validate resume position against duration if available
                if (naturalDuration > TimeSpan.Zero && resumePosition >= naturalDuration)
                {
                    // Adjust resume position to be 10 seconds before the end
                    resumePosition = naturalDuration - TimeSpan.FromSeconds(10);
                    _logger.LogWarning(
                        $"[DirectPlay] Resume position adjusted from {pendingResumePosition.Value:hh\\:hh\\:mm\\:ss} to {resumePosition:hh\\:hh\\:mm\\:ss}");
                }

                // Apply the seek
                _logger.LogInformation(
                    $"[DirectPlay] Seeking from {currentPosition:hh\\:hh\\:mm\\:ss} to {resumePosition:hh\\:hh\\:mm\\:ss}");
                playbackSession.Position = resumePosition;

                // Don't clear pending position yet - wait for verification that playback advances
                _logger.LogInformation(
                    $"[DirectPlay] Seek initiated to {resumePosition:hh\\:hh\\:mm\\:ss}, will verify on next check");
                return false; // Will verify on next attempt
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"[DirectPlay] Failed to set resume position to {resumePosition:hh\\:hh\\:mm\\:ss}");
                // Keep pending position so it can be retried
                return false;
            }
        }

        private bool ApplyHlsResumePosition(
            MediaPlayer mediaPlayer,
            ref TimeSpan? pendingResumePosition,
            IStreamResumePolicy resumePolicy,
            TimeSpan? originalTarget,
            Action<TimeSpan> setManifestOffset)
        {
            if (_resumeState == ResumeState.Failed)
            {
                return false;
            }

            if (_resumeState == ResumeState.Succeeded)
            {
                return true;
            }

            var resumePosition = pendingResumePosition.Value;

            // Get appropriate retry config
            var config = resumePolicy.NeedsClientSeek ? RetryConfig.ForHls : RetryConfig.ForDirectPlay;
            var toleranceSeconds = resumePolicy.AcceptableToleranceSeconds;

            // Initialize resume tracking on first attempt
            if (_resumeState == ResumeState.NotStarted)
            {
                _resumeState = ResumeState.InProgress;
                _resumeStartTime = DateTime.UtcNow;
                _logger.LogInformation($"[HLS-RESUME] Starting resume to {resumePosition:hh\\:mm\\:ss}");
            }

            Interlocked.Increment(ref _hlsResumeAttempts);

            // Check if we've exceeded max attempts
            if (_hlsResumeAttempts > config.MaxAttempts)
            {
                _logger.LogError($"[HLS-RESUME] Exceeded max attempts ({config.MaxAttempts}), giving up");
                pendingResumePosition = null;
                return false;
            }

            try
            {
                var playbackSession = mediaPlayer.PlaybackSession;
                var currentState = playbackSession.PlaybackState;
                var currentPosition = playbackSession.Position;
                var naturalDuration = playbackSession.NaturalDuration;
                var positionDiff = Math.Abs((currentPosition - resumePosition).TotalSeconds);

                _logger.LogInformation(
                    $"[HLS-RESUME] Attempt {_hlsResumeAttempts}: State={currentState}, Pos={currentPosition:hh\\:mm\\:ss}, Duration={naturalDuration:hh\\:mm\\:ss}");

                // Early accept: playback already starts near the target position
                if (currentState == MediaPlaybackState.Playing &&
                    positionDiff <= toleranceSeconds)
                {
                    _logger.LogInformation(
                        $"[HLS-RESUME] Already at resume position {currentPosition:hh\\:mm\\:ss} (target {resumePosition:hh\\:mm\\:ss}, diff {positionDiff:F1}s). Skipping resume workflow.");
                    _resumeState = ResumeState.Succeeded;
                    pendingResumePosition = null;
                    return true;
                }

                // For HLS, be extra careful about when to seek
                // Wait if media is still opening or initial buffering
                if (currentState == MediaPlaybackState.Opening ||
                    (currentState == MediaPlaybackState.Buffering && currentPosition == TimeSpan.Zero))
                {
                    _logger.LogInformation("[HLS-RESUME] Media still loading, will retry");
                    return false; // Will be retried by MediaPlayerViewModel
                }

                // For first attempt on HLS, wait for Playing state to ensure manifest is stable
                if (_hlsResumeAttempts == 1 && currentState != MediaPlaybackState.Playing)
                {
                    _logger.LogInformation($"[HLS-RESUME] Waiting for Playing state (current: {currentState})");
                    return false;
                }

                // Ensure we have a valid duration for HLS
                if (naturalDuration == TimeSpan.Zero)
                {
                    _logger.LogWarning("[HLS-RESUME] Duration not available yet, will retry");
                    return false;
                }

                // HLS WORKAROUND: Handle server creating new manifest at different position
                // Can be removed when Jellyfin server properly handles HLS resume
                if (resumePolicy.ShouldUseManifestOffset &&
                    _hlsResumeAttempts >= 2 && currentState == MediaPlaybackState.Buffering &&
                    positionDiff <= 15.0) // Within 15 seconds of target (typical keyframe distance)
                {
                    _logger.LogWarning(
                        $"[HLS-RESUME] Detected stuck buffering at {currentPosition:hh\\:mm\\:ss} (target was {resumePosition:hh\\:mm\\:ss})");
                    _logger.LogInformation(
                        "[HLS-RESUME] Server created new manifest with -noaccurate_seek. Applying HLS offset workaround.");

                    // The server has created a new manifest starting at roughly the resume position
                    // Store the offset so MediaPlayerViewModel can display correct position
                    setManifestOffset(currentPosition);

                    // Seek to position 0 to play from the start of this new manifest
                    playbackSession.Position = TimeSpan.Zero;

                    _logger.LogInformation(
                        $"[HLS-OFFSET] Set manifest offset to {currentPosition:hh\\:mm\\:ss}. Position 0 = {currentPosition:hh\\:mm\\:ss} in actual media time.");

                    // Mark as succeeded since we've applied the workaround
                    _resumeState = ResumeState.Succeeded;
                    pendingResumePosition = null;

                    return true; // Report success to stop retrying
                }

                if (positionDiff <= toleranceSeconds)
                {
                    // For HLS, we must verify the position is actually advancing before marking complete
                    // This prevents marking as complete when stuck buffering at the resume position

                    // Check for overall timeout - don't try forever
                    if (_resumeStartTime != DateTime.MinValue)
                    {
                        var totalElapsed = DateTime.UtcNow - _resumeStartTime;
                        if (totalElapsed.TotalSeconds > 30) // 30 second overall timeout
                        {
                            _logger.LogError(
                                $"[HLS-RESUME-TIMEOUT] Resume operation timed out after {totalElapsed.TotalSeconds:F1}s at position {currentPosition:hh\\:mm\\:ss}");
                            pendingResumePosition = null;
                            _resumeState = ResumeState.Failed;
                            return false;
                        }
                    }

                    if (_resumeState != ResumeState.Verifying && _resumeState != ResumeState.RecoveryNeeded)
                    {
                        // First time reaching target position - start monitoring
                        _resumeState = ResumeState.Verifying;
                        _logger.LogInformation(
                            $"[HLS-RESUME] Reached target position {currentPosition:hh\\:mm\\:ss}, verifying playback is advancing...");
                        _resumeVerification.StartVerification(currentPosition);
                        return false; // Need to verify position advances
                    }

                    // Add debug logging to track state transitions
                    if (!_resumeVerification.TryGetPositionChange(currentPosition, out var positionChange))
                    {
                        return false; // Too soon to check
                    }

                    if (positionChange < StuckPositionTolerance)
                    {
                        var stuckCount = _resumeVerification.IncrementStuckCount();
                        _logger.LogWarning(
                            $"[HLS-STUCK] Position not advancing at resume point {currentPosition:hh\\:mm\\:ss} (count: {stuckCount}/{MaxStuckChecks})");

                        // Update last check time and position for next comparison
                        _resumeVerification.UpdateLastCheck(currentPosition);

                        if (stuckCount >= MaxStuckChecks)
                        {
                            _logger.LogError(
                                $"[HLS-STUCK] Playback is stuck at {currentPosition:hh\\:mm\\:ss} after resume. Giving up.");
                            pendingResumePosition = null;
                            _resumeState = ResumeState.Failed;
                            return false;
                        }

                        // Try recovery techniques based on attempt level
                        if (_resumeState != ResumeState.RecoveryNeeded)
                        {
                            _resumeState = ResumeState.RecoveryNeeded;
                            _recoveryAttemptLevel = 0;
                        }

                        _recoveryAttemptLevel++;
                        if (_recoveryAttemptLevel == 1 && currentState == MediaPlaybackState.Playing)
                        {
                            _logger.LogInformation("[HLS-STUCK] Recovery level 1: Attempting pause/play toggle");
                            mediaPlayer.Pause();
                            // Use async delay without blocking
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                                mediaPlayer.Play();
                            });
                        }
                        else if (_recoveryAttemptLevel == 2)
                        {
                            _logger.LogInformation("[HLS-STUCK] Recovery level 2: Small seek forward (1s)");
                            playbackSession.Position = currentPosition + TimeSpan.FromSeconds(1);
                        }
                        else if (_recoveryAttemptLevel == 3 && currentState == MediaPlaybackState.Buffering)
                        {
                            _logger.LogInformation("[HLS-STUCK] Recovery level 3: Seek back 5 seconds");
                            var restartPosition = currentPosition - TimeSpan.FromSeconds(5);
                            if (restartPosition < TimeSpan.Zero)
                            {
                                restartPosition = TimeSpan.Zero;
                            }

                            playbackSession.Position = restartPosition;
                        }

                        _resumeVerification.UpdateLastCheck(currentPosition);
                        return false; // Continue checking
                    }

                    // Position changed, but need to verify it's not just our recovery seek
                    if (ResumeVerificationHelper.IsRecoverySeekOnly(_recoveryAttemptLevel, positionChange))
                    {
                        // Position only advanced due to our recovery seek, not actual playback
                        _logger.LogWarning(
                            $"[HLS-STUCK] Position change ({positionChange:F1}s) appears to be from recovery seek, not actual playback");
                        _recoveryAttemptLevel++; // Move to next recovery level
                        var stuckCount = _resumeVerification.IncrementStuckCount();

                        if (stuckCount >= MaxStuckChecks)
                        {
                            _logger.LogError(
                                $"[HLS-STUCK] Playback is stuck at {currentPosition:hh\\:mm\\:ss} after resume. Giving up.");
                            pendingResumePosition = null;
                            _resumeState = ResumeState.Failed;
                            return false;
                        }

                        _resumeVerification.UpdateLastCheck(currentPosition);
                        return false;
                    }

                    // Position is truly advancing! Resume successful
                    _logger.LogInformation(
                        $"[HLS-RESUME] Resume successful! Position advancing from {_resumeVerification.LastVerifiedPosition:hh\\:mm\\:ss} to {currentPosition:hh\\:mm\\:ss}");

                    // Log if we accepted an inaccurate seek
                    if (originalTarget.HasValue)
                    {
                        var acceptedDiff = Math.Abs((currentPosition - originalTarget.Value).TotalSeconds);
                        if (acceptedDiff > 3.0)
                        {
                            _logger.LogInformation(
                                $"[HLS-RESUME] Playback resumed at {currentPosition:hh\\:mm\\:ss} (originally requested {originalTarget.Value:hh\\:mm\\:ss}, diff: {acceptedDiff:F1}s)");
                        }
                    }

                    _resumeState = ResumeState.Succeeded;
                    pendingResumePosition = null;
                    _resumeVerification.ClearStuckCount();
                    _recoveryAttemptLevel = 0;
                    return true;
                }

                // Validate and adjust resume position
                var adjustedPosition = resumePosition;
                if (adjustedPosition >= naturalDuration)
                {
                    adjustedPosition = naturalDuration - TimeSpan.FromSeconds(10);
                    _logger.LogWarning(
                        $"[HLS-RESUME] Adjusted position from {resumePosition:hh\\:mm\\:ss} to {adjustedPosition:hh\\:mm\\:ss}");
                }

                // Only perform the seek on the first attempt, then verify on subsequent attempts
                if (_hlsResumeAttempts == 1)
                {
                    // For HLS, ensure we're not seeking during active buffering
                    if (currentState == MediaPlaybackState.Buffering)
                    {
                        _logger.LogInformation("[HLS-RESUME] Waiting for buffering to complete before seek");
                        Interlocked.Decrement(ref _hlsResumeAttempts); // Don't count this as an attempt
                        return false;
                    }

                    // Apply the seek (only on first attempt)
                    _logger.LogInformation(
                        $"[HLS-RESUME] Seeking from {currentPosition:hh\\:mm\\:ss} to {adjustedPosition:hh\\:mm\\:ss}");
                    playbackSession.Position = adjustedPosition;

                    _logger.LogInformation("[HLS-RESUME] Seek initiated, will verify on next check");
                    return false; // Will be verified on next call
                }

                // On subsequent attempts, verify the seek was successful
                _logger.LogInformation(
                    $"[HLS-RESUME] Verifying position (attempt {_hlsResumeAttempts}), current: {currentPosition:hh\\:mm\\:ss}, target: {adjustedPosition:hh\\:mm\\:ss}");

                // Check if we're at or past the resume position (within tolerance)
                // After a successful seek, we should be at or slightly past the target
                var timeSinceTarget = (currentPosition - adjustedPosition).TotalSeconds;

                // Calculate how much time has elapsed since we started the resume process
                // Each retry has a 5 second delay, plus time for processing
                var expectedElapsedTime = ((_hlsResumeAttempts - 1) * 5.0) + 10.0; // Add 10s buffer for processing
                _ = expectedElapsedTime;

                // Success conditions:
                // 1. We're within 10 seconds before the target (seek may have undershot)
                // 2. OR we're past the target by any reasonable amount
                //    (since playback continues during verification, being past the target is expected)
                if (timeSinceTarget >= -10.0)
                {
                    // We're at or past the target - resume successful!
                    // Don't worry about being "too far" past - that just means playback continued normally
                    _logger.LogInformation(
                        $"[HLS-RESUME] Resume successful! Position {currentPosition:hh\\:mm\\:ss} is at/past target {adjustedPosition:hh\\:mm\\:ss} (diff: {timeSinceTarget:F1}s)");
                    _resumeState = ResumeState.Succeeded;
                    pendingResumePosition = null;
                    return true;
                }

                // If we're still before the target by more than 10 seconds, the seek may have failed
                _logger.LogWarning(
                    $"[HLS-RESUME] Position {currentPosition:hh\\:mm\\:ss} is still {-timeSinceTarget:F1}s before target {adjustedPosition:hh\\:mm\\:ss}. Seek may have failed.");
                // Continue retrying
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[HLS-RESUME] Error during attempt {_hlsResumeAttempts}");

                // Continue retrying unless we've hit the limit
                if (_hlsResumeAttempts >= config.MaxAttempts)
                {
                    pendingResumePosition = null;
                    _resumeState = ResumeState.Failed;
                    return false;
                }

                return false; // Will retry
            }
        }
    }

    internal sealed class ResumeCoordinatorContext
    {
        public PlaybackSessionState SessionState { get; set; }
        public MediaPlaybackParams PlaybackParams { get; set; }
        public Func<TimeSpan> GetCurrentPosition { get; set; }
        public Func<bool> ApplyResumeIfNeeded { get; set; }
        public Func<bool> IsResumePending { get; set; }
        public Func<TimeSpan> GetManifestOffset { get; set; }
        public Action OnHlsResumeFixCompleted { get; set; }
        public Func<ResumeFailureContext, Task> OnResumeFailedAsync { get; set; }
    }

    public sealed class ResumeFailureContext
    {
        public int RetryCount { get; set; }
        public TimeSpan CurrentPosition { get; set; }
        public TimeSpan TargetPosition { get; set; }
        public string StreamType { get; set; }
    }

    public sealed class ResumeFlowOutcome
    {
        public ResumeFlowOutcome(bool success, int retryCount, string streamType, TimeSpan targetPosition,
            TimeSpan actualPosition)
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
