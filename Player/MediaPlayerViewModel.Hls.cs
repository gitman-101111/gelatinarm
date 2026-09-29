using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Media.Playback;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public partial class MediaPlayerViewModel
    {
        private void CancelResumeForUserSeek(string reason)
        {
            if (_playbackParams?.StartPositionTicks == null || _playbackParams.StartPositionTicks == 0)
            {
                return;
            }

            Logger.LogDebug("[RESUME-CANCEL] {Reason} - clearing resume target", reason);
            _playbackParams.StartPositionTicks = null;
            _playbackControlService.CancelPendingResume(reason);
        }

        private void NoteSeekStarted(string reason)
        {
            _sessionState.LastSeekTime = DateTime.UtcNow;
            ResetBufferingTimeout();
            Logger.LogDebug("[BUFFERING] Reset timeout for seek ({Reason})", reason);
        }

        private async Task<bool> TryHandleHlsBackwardSeekBeforeManifestStartAsync(int skipSeconds)
        {
            if (_playbackControlService.HlsManifestOffset <= TimeSpan.Zero)
            {
                return false;
            }

            var currentRawPosition = GetPlayerPosition();
            var targetRawPosition = currentRawPosition - TimeSpan.FromSeconds(skipSeconds);

            if (targetRawPosition >= TimeSpan.Zero)
            {
                return false;
            }

            var actualTargetPosition = BackwardSeekTarget(GetCurrentPlaybackPosition(), skipSeconds);

            Logger.LogInformation(
                "[HLS-SEEK] Backward seek would go before manifest start. Restarting at {ActualTargetPosition:hh\\:mm\\:ss}", actualTargetPosition);

            await RestartStreamAsync("seek before the HLS manifest start", actualTargetPosition);
            return true;
        }

        private static TimeSpan BackwardSeekTarget(TimeSpan from, int skipSeconds)
        {
            var target = from - TimeSpan.FromSeconds(skipSeconds);
            return target < TimeSpan.Zero ? TimeSpan.Zero : target;
        }

        private void LogHlsLargeBackwardSeek(int skipSeconds)
        {
            if (!_sessionState.IsHlsStream || skipSeconds < 60)
            {
                return;
            }

            var currentPosWithOffset = GetCurrentPlaybackPosition();
            var targetPos = BackwardSeekTarget(currentPosWithOffset, skipSeconds);

            Logger.LogDebug(
                "[HLS] Large backward seek: {SkipSeconds}s from {CurrentPosWithOffset:hh\\:mm\\:ss} to {TargetPos:hh\\:mm\\:ss} - server may create new manifest", skipSeconds, currentPosWithOffset, targetPos);
        }

        private bool TryPrepareHlsForwardSeek(ref int skipSeconds)
        {
            if (!_sessionState.IsHlsStream)
            {
                return true;
            }

            var rawPosition = GetPlayerPosition();
            var hlsManifestOffset = _playbackControlService.HlsManifestOffset;
            var currentPosWithOffset = rawPosition + hlsManifestOffset;
            var targetPos = currentPosWithOffset + TimeSpan.FromSeconds(skipSeconds);

            var metadataDuration = GetMetadataDuration();

            if (metadataDuration > TimeSpan.Zero && targetPos >= metadataDuration - TimeSpan.FromSeconds(30))
            {
                Logger.LogWarning(
                    "[HLS] Preventing seek to {TargetPos:hh\\:mm\\:ss} - too close to end ({MetadataDuration:hh\\:mm\\:ss}). This could corrupt the HLS manifest.", targetPos, metadataDuration);

                var safeEndPosition = metadataDuration - TimeSpan.FromSeconds(35);
                if (currentPosWithOffset < safeEndPosition)
                {
                    var adjustedSkip = (int)(safeEndPosition - currentPosWithOffset).TotalSeconds;
                    Logger.LogInformation("[HLS] Adjusted skip to {AdjustedSkip}s to avoid end-of-stream issues", adjustedSkip);
                    skipSeconds = adjustedSkip;
                    targetPos = currentPosWithOffset + TimeSpan.FromSeconds(skipSeconds);
                }
                else
                {
                    Logger.LogInformation("[HLS] Already close to end, skipping forward disabled");
                    return false;
                }
            }

            if (skipSeconds >= 60 && hlsManifestOffset > TimeSpan.Zero)
            {
                Logger.LogDebug(
                    "[HLS-MANIFEST-OFFSET] Large seek with offset. Current raw: {RawPosition:hh\\:mm\\:ss}, offset: {HlsManifestOffset:hh\\:mm\\:ss}, target absolute: {TargetPos:hh\\:mm\\:ss}", rawPosition, hlsManifestOffset, targetPos);
                var session = Player?.PlaybackSession;
                if (session != null)
                {
                    session.Position = rawPosition + TimeSpan.FromSeconds(skipSeconds);
                }

                _sessionState.RecordLargeSeek(targetPos);
                return false;
            }

            if (skipSeconds >= 60)
            {
                _sessionState.RecordLargeSeek(targetPos);
                Logger.LogDebug(
                    "[HLS] Large seek detected: {SkipSeconds}s forward from {CurrentPosWithOffset:hh\\:mm\\:ss} to {TargetPos:hh\\:mm\\:ss} (pending seeks: {SessionStatePendingSeekCount})", skipSeconds, currentPosWithOffset, targetPos, _sessionState.PendingSeekCount);
            }

            return true;
        }

        private bool TryRecoverHlsBuffering()
        {
            if (!_sessionState.IsHlsStream)
            {
                return false;
            }

            Logger.LogWarning("[BUFFERING-TIMEOUT] HLS stream stuck - restarting playback at current position");

            ResetBufferingTimeout();
            FireAndForget(() => RestartStreamAsync("buffering timeout recovery"));
            return true;
        }

        private async Task HandleResumeOnPlaybackStartAsync()
        {
            if (_resumeAttemptInProgress)
            {
                Logger.LogDebug("Resume attempt already in progress, skipping duplicate call");
                return;
            }

            _resumeAttemptInProgress = true;
            try
            {
                // Restarts (track change, buffering recovery, direct-play retry) open a new stream
                SyncCurrentStream();

                await _playbackControlService.HandleResumeOnPlaybackStartAsync(
                    _sessionState,
                    _playbackParams,
                    () => GetCurrentPlaybackPosition(),
                    CompleteHlsResumeFix,
                    HandleResumeFailureAsync);
            }
            finally
            {
                _resumeAttemptInProgress = false;
            }
        }

        private async Task HandleResumeFailureAsync()
        {
            var context = CreateErrorContext("ResumePlayback", ErrorCategory.Media);
            ErrorHandler.HandleError(new ResumeStuckException(), context, true);

            await LeaveAfterPlaybackFailureAsync();
        }

        private async void OnSeekCompleted(MediaPlayer sender, object args)
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                await RunOnUIThreadAsync(() =>
                {
                    var position = sender?.PlaybackSession?.Position ?? TimeSpan.Zero;
                    var state = sender?.PlaybackSession?.PlaybackState;
                    var naturalDuration = sender?.PlaybackSession?.NaturalDuration;
                    var metadataDuration = GetMetadataDuration();

                    _seekCompletionCoordinator.HandleSeekCompleted(new SeekCompletionContext
                    {
                        Position = position,
                        PlaybackState = state,
                        NaturalDuration = naturalDuration,
                        MetadataDuration = metadataDuration,
                        IsHlsStream = _sessionState.IsHlsStream,
                        HasPerformedInitialSeek = _sessionState.HasPerformedInitialSeek,
                        PendingSeekCount = _sessionState.PendingSeekCount,
                        ActualResumePosition = _actualResumePosition,
                        DecrementPendingSeek = _sessionState.DecrementPendingSeek,
                        SetActualResumePosition = resumePosition => _actualResumePosition = resumePosition,
                        MarkInitialSeekPerformed = () => _sessionState.HasPerformedInitialSeek = true,
                        AttemptHlsRecovery = AttemptHlsRecoveryAfterSeek,
                        TryHandleHlsManifestChange = TryHandleHlsManifestChange
                    });
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnSeekCompleted", ErrorCategory.Media), false);
            }
        }

        private void AttemptHlsRecoveryAfterSeek()
        {
            FireAndForget(async () =>
            {
                await Task.Delay(PlayerConstants.HlsManifestLoadDelayMs);
                await RunOnUIThreadAsync(() =>
                {
                    if (!IsPlaying)
                    {
                        Logger.LogInformation("[HLS-RECOVERY] Auto-playing to recover from manifest issue");
                        Player?.Play();
                    }
                });
            });
        }

        private void TryHandleHlsManifestChange(TimeSpan position, TimeSpan naturalDuration, TimeSpan metadataDuration)
        {
            if (!_sessionState.IsHlsStream || naturalDuration >= metadataDuration ||
                _sessionState.ExpectedHlsSeekTarget <= TimeSpan.Zero)
            {
                return;
            }

            var percentageOfOriginal = naturalDuration.TotalSeconds / metadataDuration.TotalSeconds * 100;
            Logger.LogInformation(
                "[HLS-MANIFEST-CHANGE] Detected new HLS manifest after seek: natural duration is {PercentageOfOriginal:F1}% of metadata duration; " +
                "the new manifest starts at {SessionStateExpectedHlsSeekTarget:hh\\:mm\\:ss} and lasts {NaturalDuration:hh\\:mm\\:ss}",
                percentageOfOriginal, _sessionState.ExpectedHlsSeekTarget, naturalDuration);

            var timeSinceLastSeek = DateTime.UtcNow - _sessionState.LastSeekTime;
            var shouldProcessManifest = _sessionState.PendingSeekCount == 0 || timeSinceLastSeek.TotalSeconds > 2;

            if (!shouldProcessManifest)
            {
                Logger.LogDebug(
                    "[HLS-MANIFEST-CHANGE] Skipping manifest offset due to {SessionStatePendingSeekCount} pending seeks", _sessionState.PendingSeekCount);
                return;
            }

            Logger.LogInformation(
                "[HLS-MANIFEST-CHANGE] Processing manifest change (pending seeks: {SessionStatePendingSeekCount}, time since last seek: {TimeSinceLastSeekTotalSeconds:F1}s)", _sessionState.PendingSeekCount, timeSinceLastSeek.TotalSeconds);

            _sessionState.PendingSeekCount = 0;
            SetHlsManifestOffset(_sessionState.ExpectedHlsSeekTarget, false, "Setting up offset tracking.");

            Logger.LogDebug("[HLS-MANIFEST-CHANGE] Applying immediate seek to position 0 of new manifest");
            if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
            {
                NoteSeekStarted("manifest change");
                MediaPlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                CompleteHlsResumeFix();
                Logger.LogDebug(
                    "[HLS-MANIFEST-CHANGE] Seeked to position 0, playback should continue from {PlaybackControlServiceHlsManifestOffset:hh\\:mm\\:ss}", _playbackControlService.HlsManifestOffset);
            }
        }

        private void TryApplyHlsManifestChangeAfterBackwardSeek(TimeSpan currentPosition)
        {
            if (!_sessionState.IsHlsStream || _sessionState.ExpectedHlsSeekTarget == TimeSpan.Zero)
            {
                return;
            }

            var timeSinceSeek = (DateTime.UtcNow - _sessionState.LastSeekTime).TotalSeconds;
            if (timeSinceSeek >= 5 || _sessionState.PendingSeekCount <= 0 || currentPosition.TotalSeconds >= 1)
            {
                return;
            }

            _sessionState.DecrementPendingSeek();
            Logger.LogInformation(
                "[HLS-MANIFEST-CHANGE] Detected new manifest creation after backward seek (pending: {SessionStatePendingSeekCount})", _sessionState.PendingSeekCount);
            SetHlsManifestOffset(_sessionState.ExpectedHlsSeekTarget, true,
                "Detected new manifest creation after backward seek.");
        }

        // Where the player is in its current stream, before the manifest offset
        private TimeSpan GetPlayerPosition(MediaPlaybackSession session = null)
        {
            TimeSpan rawPosition;

            try
            {
                // Use provided session first (safe from any thread)
                if (session != null)
                {
                    rawPosition = session.Position;
                }
                // Only access MediaPlayerElement if we're on UI thread
                else if (CoreApplication.MainView.CoreWindow.Dispatcher.HasThreadAccess)
                {
                    rawPosition = MediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                }
                else
                {
                    // If called from background thread without session, use cached position
                    rawPosition = _position;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[GetPlayerPosition] Error getting position");
                rawPosition = _position;
            }

            return rawPosition;
        }

        // The player counts from the start of its current manifest; after the server restarts a
        // transcode further in, the offset turns that into a position in the item
        private TimeSpan WithManifestOffset(TimeSpan playerPosition)
        {
            return playerPosition + _playbackControlService.HlsManifestOffset;
        }

        private void SetHlsManifestOffset(TimeSpan offset, bool markApplied, string context)
        {
            _sessionState.HlsManifestOffsetApplied = markApplied;
            _sessionState.ExpectedHlsSeekTarget = TimeSpan.Zero;
            _playbackControlService.HlsManifestOffset = offset;

            Logger.LogInformation("[HLS-MANIFEST-CHANGE] {Context} Position 0 in new manifest = {Offset:hh\\:mm\\:ss}", context, offset);
        }

        private void HandleHlsBufferingFix(MediaPlaybackSession session)
        {
            if (!ShouldApplyHlsResumeFix(session))
            {
                return;
            }

            Logger.LogInformation("[HLS-RESUME] Buffering at resume position, will try seeking to manifest start");

            FireAndForget(async () =>
            {
                await Task.Delay(PlayerConstants.HlsManifestLoadDelayMs);

                if (!IsBuffering)
                {
                    return;
                }

                await UiHelper.RunOnUIThreadAsync(() => ApplyHlsManifestOffsetSeek(MediaPlayerElement?.MediaPlayer?.PlaybackSession), logger: Logger);
            });
        }

        private bool ShouldApplyHlsResumeFix(MediaPlaybackSession session)
        {
            // Only a restarted transcode (track change or large seek) has a manifest offset; an
            // initial resume is a client-side seek
            var offset = _playbackControlService.HlsManifestOffset;
            if (!_sessionState.IsHlsStream || offset <= TimeSpan.Zero || _sessionState.HlsManifestOffsetApplied)
            {
                return false;
            }

            var rawPosition = session?.Position ?? TimeSpan.Zero;
            return Math.Abs((rawPosition - offset).TotalSeconds) < 10;
        }

        private void ApplyHlsManifestOffsetSeek(MediaPlaybackSession session)
        {
            if (session == null)
            {
                return;
            }

            var currentState = session.PlaybackState;
            Logger.LogInformation(
                "[HLS-RESUME] Applying fix - current state: {CurrentState}, position before: {SessionPosition:hh\\:mm\\:ss}", currentState, session.Position);

            // Pause briefly to ensure clean audio buffer transition
            var wasPlaying = currentState == MediaPlaybackState.Playing || currentState == MediaPlaybackState.Buffering;
            if (wasPlaying)
            {
                Logger.LogDebug("[HLS-RESUME] Pausing playback for clean seek");
                session.MediaPlayer.Pause();
            }

            NoteSeekStarted("manifest offset fix");
            session.Position = TimeSpan.Zero;
            Logger.LogDebug("[HLS-RESUME] Seeked to position 0, new position: {SessionPosition:hh\\:mm\\:ss}", session.Position);
            CompleteHlsResumeFix();

            if (wasPlaying)
            {
                Logger.LogDebug("[HLS-RESUME] Resuming playback");
                session.MediaPlayer.Play();
            }
        }

        private void CompleteHlsResumeFix()
        {
            _sessionState.HlsManifestOffsetApplied = true;
            OnPropertyChanged(nameof(Position));
        }

        private void ResetHlsState()
        {
            _sessionState.Reset();
            _actualResumePosition = TimeSpan.Zero;
        }
    }
}
