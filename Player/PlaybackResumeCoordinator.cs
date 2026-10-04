using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Gelatinarm.Shared.Async;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    internal enum ResumeState
    {
        NotStarted,
        InProgress, // Initial resume seek applied
        Verifying, // Checking if position is advancing at target
        RecoveryNeeded, // Stuck, applying recovery techniques
        Succeeded,
        Cancelled, // The user seeked away; there is nothing left to resume to
        Failed
    }

    /// <summary>
    ///     How a resume behaves on one kind of stream; the steps are the same for both. Direct play
    ///     seeks exactly and cheaply. A server stream (HLS) lands on segment boundaries and a seek
    ///     can restart the server's transcode, so it gets a wider tolerance, slower checks and a
    ///     single seek. Direct play gives up early: a seek that works takes about 2 s, and one
    ///     that does not is handed to the server (MediaPlayerViewModel.HandleResumeFailureAsync).
    /// </summary>
    internal sealed class ResumeProfile
    {
        public static readonly ResumeProfile DirectPlay = new ResumeProfile("DirectPlay", 3.0, 1000, 8, 5, false);
        public static readonly ResumeProfile Hls = new ResumeProfile("HLS", 10.0, 5000, 15, 45, true);

        private ResumeProfile(string label, double toleranceSeconds, int checkDelayMs, int maxChecks,
            int timeoutSeconds, bool seekOnce)
        {
            Label = label;
            ToleranceSeconds = toleranceSeconds;
            CheckDelayMs = checkDelayMs;
            MaxChecks = maxChecks;
            Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            SeekOnce = seekOnce;
        }

        public string Label { get; }
        public double ToleranceSeconds { get; }
        public int CheckDelayMs { get; }
        public int MaxChecks { get; }
        public TimeSpan Timeout { get; }
        public bool SeekOnce { get; }
    }

    internal sealed class ResumeVerificationHelper
    {
        private TimeSpan _lastVerifiedPosition = TimeSpan.Zero;
        private DateTime _lastPositionCheckTime = DateTime.MinValue;
        private int _stuckPositionCount;

        public TimeSpan LastVerifiedPosition => _lastVerifiedPosition;

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
        private const int MaxStuckChecks = 5;
        private const double StuckPositionTolerance = 0.5;

        private readonly ILogger _logger;
        private ResumeState _resumeState = ResumeState.NotStarted;
        private DateTime _resumeStartTime = DateTime.MinValue;
        private bool _seekIssued;
        private readonly ResumeVerificationHelper _resumeVerification = new ResumeVerificationHelper();
        private int _recoveryAttemptLevel;

        public PlaybackResumeCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task HandleResumeOnPlaybackStartAsync(ResumeCoordinatorContext context)
        {
            var startPositionTicks = context.PlaybackParams?.StartPositionTicks;
            if (context.SessionState.HasPerformedInitialSeek ||
                !startPositionTicks.HasValue ||
                startPositionTicks.Value <= 0)
            {
                return;
            }

            _logger.LogDebug("Video playback started - checking for resume position");

            var profile = context.Profile;
            var streamLabel = profile.Label;
            var resumeResult = context.ApplyResumeIfNeeded();
            var retryCount = 0;

            if (!resumeResult)
            {
                var maxRetries = profile.MaxChecks;
                var retryDelay = profile.CheckDelayMs;

                while (!resumeResult && retryCount < maxRetries)
                {
                    if (!context.IsResumePending())
                    {
                        // Every Playing transition re-enters here until SeekCompleted sets
                        // HasPerformedInitialSeek; a rebuffer can get in first, after an earlier
                        // entry already finished the resume. That is not a failure, and neither
                        // is a user seek that cancelled the resume while this loop waited.
                        if (_resumeState == ResumeState.Succeeded || _resumeState == ResumeState.Cancelled)
                        {
                            _logger.LogDebug("Resume {ResumeState}, nothing to retry", _resumeState);
                            return;
                        }

                        _logger.LogDebug("Resume no longer pending, stopping retries");
                        break;
                    }

                    try
                    {
                        await Task.Delay(retryDelay, context.Cancellation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The playback ended, and its resume with it
                        return;
                    }

                    // While the player is still carrying out the resume seek there is nothing
                    // to verify: the position already reads as the target, and a recovery pause
                    // or seek only restarts the fetch (device: a 15 Mbps direct play was declared
                    // stuck 2 s after its seek, "recovered" twice and given up on at 7 s, with
                    // SeekCompleted never raised). The wait spends no check.
                    if (ResumeSeekRunning(context.SessionState, profile))
                    {
                        _logger.LogDebug("[{StreamLabel}] Waiting for the player to finish the resume seek", streamLabel);
                        continue;
                    }

                    retryCount++;
                    _logger.LogDebug("[{StreamLabel}] Check {RetryCount}/{MaxRetries}", streamLabel, retryCount, maxRetries);
                    resumeResult = context.ApplyResumeIfNeeded();
                }
            }

            var targetPosition = TimeSpan.FromTicks(startPositionTicks.Value);
            var actualPosition = context.GetCurrentPosition();

            if (resumeResult)
            {
                _logger.LogInformation("[{StreamLabel}] Successfully resumed after {RetryCount} retries", streamLabel, retryCount);

                var diff = Math.Abs((actualPosition - targetPosition).TotalSeconds);
                if (diff > 3.0)
                {
                    _logger.LogInformation("[{StreamLabel}] Accepted server position {ActualPosition:hh\\:mm\\:ss} " +
                                           "(target was {TargetPosition:hh\\:mm\\:ss}, diff: {Diff:F1}s)", streamLabel, actualPosition, targetPosition, diff);
                }

                var resumePos = context.GetManifestOffset();
                if (context.SessionState.IsHlsStream && resumePos > TimeSpan.Zero)
                {
                    _logger.LogInformation(
                        "[HLS-RESUME] PlaybackControlService applied manifest offset workaround at {ResumePos:hh\\:mm\\:ss}", resumePos);

                    context.SessionState.HlsManifestOffsetApplied = false;
                    context.OnHlsResumeFixCompleted();
                }
            }
            else if (!context.Cancellation.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "[{StreamLabel}] Failed to resume after {RetryCount} retries: at {ActualPosition:hh\\:mm\\:ss}, target {TargetPosition:hh\\:mm\\:ss}",
                    streamLabel, retryCount, actualPosition, targetPosition);
                await context.OnResumeFailedAsync().ConfigureAwait(false);
            }
        }

        // SeekCompleted sets HasPerformedInitialSeek. Past the profile's timeout the wait ends
        // and the next check reports the timeout.
        private bool ResumeSeekRunning(PlaybackSessionState sessionState, ResumeProfile profile)
        {
            return _seekIssued && !sessionState.HasPerformedInitialSeek &&
                   DateTime.UtcNow - _resumeStartTime <= profile.Timeout;
        }

        public void Reset()
        {
            _resumeState = ResumeState.NotStarted;
            _resumeStartTime = DateTime.MinValue;
            _seekIssued = false;
            _resumeVerification.Reset();
            _recoveryAttemptLevel = 0;
        }

        public bool IsInProgress(bool hasPending)
        {
            return hasPending && _resumeState != ResumeState.Succeeded && _resumeState != ResumeState.Cancelled &&
                   _resumeState != ResumeState.Failed;
        }

        public void CancelPendingResume(ref TimeSpan? pendingResumePosition, string reason)
        {
            if (!pendingResumePosition.HasValue)
            {
                return;
            }

            _logger.LogInformation(
                "[RESUME-CANCEL] {Reason} - clearing pending resume at {PendingResumePosition:hh\\:mm\\:ss}", reason, pendingResumePosition);
            pendingResumePosition = null;
            _resumeState = ResumeState.Cancelled;
            _resumeStartTime = DateTime.MinValue;
            _seekIssued = false;
        }

        /// <summary>
        ///     One resume check, called until it returns true: wait for the stream, seek to the
        ///     target, then confirm playback advances there (recovering a stuck start).
        /// </summary>
        public bool ApplyPendingResumePosition(MediaPlayer mediaPlayer, ref TimeSpan? pendingResumePosition,
            ResumeProfile profile, TimeSpan? originalTarget)
        {
            if (!pendingResumePosition.HasValue || mediaPlayer?.PlaybackSession == null ||
                _resumeState == ResumeState.Failed || _resumeState == ResumeState.Cancelled)
            {
                return false;
            }

            if (_resumeState == ResumeState.Succeeded)
            {
                return true;
            }

            var label = profile.Label;
            var resumePosition = pendingResumePosition.Value;

            if (_resumeState == ResumeState.NotStarted)
            {
                _resumeState = ResumeState.InProgress;
                _resumeStartTime = DateTime.UtcNow;
                _logger.LogInformation("[{Stream}] Starting resume to {ResumePosition:hh\\:mm\\:ss}", label, resumePosition);
            }

            var elapsed = DateTime.UtcNow - _resumeStartTime;
            if (elapsed > profile.Timeout)
            {
                _logger.LogError("[{Stream}-TIMEOUT] Resume timed out after {Elapsed:F1}s", label, elapsed.TotalSeconds);
                pendingResumePosition = null;
                _resumeState = ResumeState.Failed;
                return false;
            }

            try
            {
                var (playbackSession, currentState, currentPosition, naturalDuration) = ReadSession(mediaPlayer);

                if (currentState == MediaPlaybackState.Opening ||
                    (currentState == MediaPlaybackState.Buffering && currentPosition == TimeSpan.Zero))
                {
                    _logger.LogDebug("[{Stream}] Media still loading, deferring resume", label);
                    return false;
                }

                var positionDiff = Math.Abs((currentPosition - resumePosition).TotalSeconds);
                if (positionDiff <= profile.ToleranceSeconds)
                {
                    return VerifyResumeAdvancing(label,
                        _resumeState != ResumeState.Verifying && _resumeState != ResumeState.RecoveryNeeded,
                        mediaPlayer, playbackSession, currentState, currentPosition, ref pendingResumePosition,
                        originalTarget);
                }

                var target = ClampToDuration(label, resumePosition, naturalDuration);

                if (_seekIssued && profile.SeekOnce)
                {
                    // Playback runs on while we wait, so landing past the target is a resume too
                    var sinceTarget = (currentPosition - target).TotalSeconds;
                    if (sinceTarget >= -profile.ToleranceSeconds)
                    {
                        _logger.LogInformation(
                            "[{Stream}] Resume successful: {CurrentPosition:hh\\:mm\\:ss} is at/past target {Target:hh\\:mm\\:ss}", label, currentPosition, target);
                        _resumeState = ResumeState.Succeeded;
                        pendingResumePosition = null;
                        return true;
                    }

                    _logger.LogWarning(
                        "[{Stream}] Still {Behind:F1}s before target {Target:hh\\:mm\\:ss}", label, -sinceTarget, target);
                    return false;
                }

                // A stable stream first: the manifest (HLS) and the duration have to be known
                if (currentState != MediaPlaybackState.Playing && currentState != MediaPlaybackState.Paused)
                {
                    _logger.LogDebug("[{Stream}] Waiting for playback before seeking (state: {State})", label, currentState);
                    return false;
                }

                if (naturalDuration == TimeSpan.Zero)
                {
                    _logger.LogDebug("[{Stream}] Duration not known yet, deferring seek", label);
                    return false;
                }

                _logger.LogInformation(
                    "[{Stream}] Seeking from {CurrentPosition:hh\\:mm\\:ss} to {Target:hh\\:mm\\:ss}", label, currentPosition, target);
                playbackSession.Position = target;
                _seekIssued = true;
                return false; // Verified on the next check
            }
            catch (Exception ex)
            {
                // Pending position kept: the next check retries
                _logger.LogError(ex, "[{Stream}] Resume check failed", label);
                return false;
            }
        }

        /// <summary>
        ///     Once playback is at the resume target, confirms it is actually advancing before calling
        ///     the resume done: a stream can sit at the right position, stuck.
        /// </summary>
        private bool VerifyResumeAdvancing(string stream, bool firstReach, MediaPlayer mediaPlayer,
            MediaPlaybackSession playbackSession, MediaPlaybackState currentState, TimeSpan currentPosition,
            ref TimeSpan? pendingResumePosition, TimeSpan? originalTarget)
        {
            if (firstReach)
            {
                _resumeState = ResumeState.Verifying;
                _logger.LogDebug(
                    "[{Stream}] Reached target position {CurrentPosition:hh\\:mm\\:ss}, verifying playback is advancing...", stream, currentPosition);
                _resumeVerification.StartVerification(currentPosition);
                return false;
            }

            if (!_resumeVerification.TryGetPositionChange(currentPosition, out var positionChange))
            {
                return false; // Too soon to check
            }

            var stuck = positionChange < StuckPositionTolerance;
            // A change of about our own recovery seek is not playback advancing
            var recoverySeekOnly = !stuck && ResumeVerificationHelper.IsRecoverySeekOnly(_recoveryAttemptLevel, positionChange);
            if (stuck || recoverySeekOnly)
            {
                var stuckCount = _resumeVerification.IncrementStuckCount();
                if (stuck)
                {
                    _logger.LogWarning(
                        "[{Stream}-STUCK] Position not advancing at resume point {CurrentPosition:hh\\:mm\\:ss} (count: {StuckCount}/{MaxStuckChecks})", stream, currentPosition, stuckCount, MaxStuckChecks);
                }
                else
                {
                    _logger.LogWarning(
                        "[{Stream}-STUCK] Position change ({PositionChange:F1}s) appears to be from recovery seek, not actual playback", stream, positionChange);
                }

                _resumeVerification.UpdateLastCheck(currentPosition);
                if (stuckCount >= MaxStuckChecks)
                {
                    _logger.LogError(
                        "[{Stream}-STUCK] Playback is stuck at {CurrentPosition:hh\\:mm\\:ss} after resume. Giving up.", stream, currentPosition);
                    pendingResumePosition = null;
                    _resumeState = ResumeState.Failed;
                    return false;
                }

                if (stuck)
                {
                    RecoverStuckStart(stream, mediaPlayer, playbackSession, currentState, currentPosition);
                }
                else
                {
                    _recoveryAttemptLevel++;
                }

                return false;
            }

            _logger.LogInformation(
                "[{Stream}] Resume successful! Position advancing from {LastVerifiedPosition:hh\\:mm\\:ss} to {CurrentPosition:hh\\:mm\\:ss}", stream, _resumeVerification.LastVerifiedPosition, currentPosition);
            if (originalTarget.HasValue)
            {
                var acceptedDiff = Math.Abs((currentPosition - originalTarget.Value).TotalSeconds);
                if (acceptedDiff > 3.0)
                {
                    _logger.LogDebug(
                        "[{Stream}] Playback resumed at {CurrentPosition:hh\\:mm\\:ss} (originally requested {OriginalTarget:hh\\:mm\\:ss}, diff: {AcceptedDiff:F1}s)", stream, currentPosition, originalTarget.Value, acceptedDiff);
                }
            }

            _resumeState = ResumeState.Succeeded;
            pendingResumePosition = null;
            _resumeVerification.ClearStuckCount();
            _recoveryAttemptLevel = 0;
            return true;
        }

        /// <summary>
        ///     One step further each stuck check: pause/play, a 1 s seek forward, then (still
        ///     buffering) 5 s back.
        /// </summary>
        private void RecoverStuckStart(string stream, MediaPlayer mediaPlayer, MediaPlaybackSession playbackSession,
            MediaPlaybackState currentState, TimeSpan currentPosition)
        {
            if (_resumeState != ResumeState.RecoveryNeeded)
            {
                _resumeState = ResumeState.RecoveryNeeded;
                _recoveryAttemptLevel = 0;
            }

            _recoveryAttemptLevel++;
            if (_recoveryAttemptLevel == 1 && currentState == MediaPlaybackState.Playing)
            {
                _logger.LogInformation("[{Stream}-STUCK] Recovery 1: pause/play", stream);
                PauseThenPlay(mediaPlayer);
            }
            else if (_recoveryAttemptLevel == 2)
            {
                _logger.LogInformation("[{Stream}-STUCK] Recovery 2: seek forward 1s", stream);
                playbackSession.Position = currentPosition + TimeSpan.FromSeconds(1);
            }
            else if (_recoveryAttemptLevel == 3 && currentState == MediaPlaybackState.Buffering)
            {
                _logger.LogInformation("[{Stream}-STUCK] Recovery 3: seek back 5s", stream);
                var restartPosition = currentPosition - TimeSpan.FromSeconds(5);
                playbackSession.Position = restartPosition < TimeSpan.Zero ? TimeSpan.Zero : restartPosition;
            }
        }

        private static (MediaPlaybackSession Session, MediaPlaybackState State, TimeSpan Position, TimeSpan Duration)
            ReadSession(MediaPlayer mediaPlayer)
        {
            var session = mediaPlayer.PlaybackSession;
            return (session, session.PlaybackState, session.Position, session.NaturalDuration);
        }

        /// <summary>
        ///     A resume point at or past the end would finish the item at once; start ten seconds
        ///     before the end instead.
        /// </summary>
        private TimeSpan ClampToDuration(string stream, TimeSpan resumePosition, TimeSpan naturalDuration)
        {
            if (naturalDuration <= TimeSpan.Zero || resumePosition < naturalDuration)
            {
                return resumePosition;
            }

            var clamped = naturalDuration - TimeSpan.FromSeconds(10);
            _logger.LogWarning(
                "[{Stream}] Resume position adjusted from {ResumePosition:hh\\:mm\\:ss} to {Clamped:hh\\:mm\\:ss}", stream, resumePosition, clamped);
            return clamped;
        }

        private void PauseThenPlay(MediaPlayer mediaPlayer)
        {
            mediaPlayer.Pause();
            AsyncHelper.FireAndForget(async () =>
            {
                await Task.Delay(PlayerConstants.PauseResumeCycleDelayMs).ConfigureAwait(false);
                mediaPlayer.Play();
            }, _logger, GetType());
        }
    }

    internal sealed class ResumeCoordinatorContext
    {
        public PlaybackSessionState SessionState { get; set; }
        public MediaPlaybackParams PlaybackParams { get; set; }
        public ResumeProfile Profile { get; set; }
        public Func<TimeSpan> GetCurrentPosition { get; set; }
        public Func<bool> ApplyResumeIfNeeded { get; set; }
        public Func<bool> IsResumePending { get; set; }
        public Func<TimeSpan> GetManifestOffset { get; set; }
        public Action OnHlsResumeFixCompleted { get; set; }
        public Func<Task> OnResumeFailedAsync { get; set; }
        public CancellationToken Cancellation { get; set; }
    }
}
