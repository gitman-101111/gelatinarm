using System;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Helpers
{
    internal sealed class PlaybackStateCoordinator
    {
        private const double PositionEpsilonSeconds = 0.5;
        private static readonly TimeSpan _duplicateWindow = TimeSpan.FromMilliseconds(250);
        private readonly ILogger _logger;
        private readonly BufferingStateCoordinator _bufferingStateCoordinator;
        private MediaPlaybackState _lastState = MediaPlaybackState.None;
        private TimeSpan _lastPosition = TimeSpan.Zero;
        private DateTime _lastProcessedAt = DateTime.MinValue;
        private bool _hasLastSnapshot;

        public PlaybackStateCoordinator(
            ILogger logger,
            BufferingStateCoordinator bufferingStateCoordinator)
        {
            _logger = logger;
            _bufferingStateCoordinator = bufferingStateCoordinator;
        }

        public async Task HandlePlaybackStateChangedAsync(PlaybackStateChangeContext context)
        {
            if (context.IsDisposed)
            {
                _logger.LogDebug("[VM-PLAYBACK-STATE] Event fired after disposal, ignoring");
                return;
            }

            try
            {
                await context.RunOnUiThreadAsync(async () =>
                {
                    try
                    {
                        var snapshot = PlaybackSessionSnapshot.Capture(
                            context.Session,
                            context.SessionState?.IsHlsStream == true);
                        if (!snapshot.HasSession)
                        {
                            _logger.LogWarning("[VM-PLAYBACK-STATE] Playback session missing, ignoring event");
                            return;
                        }

                        if (ShouldSkipSnapshot(snapshot))
                        {
                            return;
                        }

                        _logger.LogInformation("[VM-PLAYBACK-STATE] Handler entered");
                        var newState = snapshot.State;
                        _logger.LogInformation($"[VM-PLAYBACK-STATE] State changed to: {newState}, " +
                                               $"Position: {snapshot.Position.TotalSeconds:F2}s, " +
                                               $"BufferingProgress: {snapshot.BufferingProgress:P}");

                        RememberSnapshot(snapshot);
                        context.SetPlaybackState?.Invoke(snapshot.State);

                        var rawPosition = snapshot.Position;

                        context.SetRawPosition(rawPosition);

                        var position = context.GetDisplayPosition();
                        var bufferingProgress = snapshot.BufferingProgress;
                        var canSeek = snapshot.CanSeek;

                        var hadBufferingStart = context.GetBufferingStartTime().HasValue;
                        _logger.LogInformation(
                            $"PlaybackStateChanged: {newState}, Position: {position.TotalSeconds:F2}s, " +
                            $"BufferingProgress: {bufferingProgress:F2}, CanSeek: {canSeek}, " +
                            $"HadBufferingStart: {hadBufferingStart}");

                        var isBuffering = newState == MediaPlaybackState.Buffering;
                        context.NotifyIsBufferingChanged?.Invoke();
                        context.NotifyIsPlayingChanged?.Invoke();
                        context.NotifyIsPausedChanged?.Invoke();

                        var bufferingResult = _bufferingStateCoordinator.Handle(new BufferingStateRequest
                        {
                            IsBuffering = isBuffering,
                            IsHls = context.SessionState.IsHlsStream,
                            IsHlsTrackChange = context.SessionState.IsHlsTrackChange,
                            HasManifestOffset = context.GetHasManifestOffset?.Invoke() == true,
                            NewState = newState,
                            Position = position,
                            ExpectedHlsSeekTarget = context.SessionState.ExpectedHlsSeekTarget,
                            NaturalDuration = snapshot.NaturalDuration,
                            MetadataDuration = context.GetMetadataDuration(),
                            LastSeekTime = context.SessionState.LastSeekTime,
                            PendingSeekCount = context.SessionState.PendingSeekCount,
                            BufferingStartTime = context.GetBufferingStartTime()
                        });

                        context.SetBufferingStartTime(bufferingResult.BufferingStartTime);
                        context.SessionState.ExpectedHlsSeekTarget = bufferingResult.ExpectedHlsSeekTarget;
                        if (bufferingResult.HlsManifestOffset > TimeSpan.Zero)
                        {
                            context.ApplyManifestOffsetFromBuffering?.Invoke(bufferingResult.HlsManifestOffset);
                        }

                        if (bufferingResult.TriggerHlsBufferingFix)
                        {
                            context.HandleHlsBufferingFix?.Invoke(context.Session);
                        }

                        if (bufferingResult.StartTimeoutTimer)
                        {
                            context.BufferingTimeoutTimer?.Start();
                        }
                        else if (bufferingResult.StopTimeoutTimer)
                        {
                            context.BufferingTimeoutTimer?.Stop();
                        }

                        if (bufferingResult.ResetHlsTrackChange)
                        {
                            context.SessionState.IsHlsTrackChange = false;
                        }

                        if (newState == MediaPlaybackState.Playing)
                        {
                            _logger.LogInformation($"Transitioned to Playing state at {position:mm\\:ss}");

                            if (!context.GetHasVideoStarted())
                            {
                                context.SetHasVideoStarted(true);
                                _logger.LogInformation("Video playback started");
                            }

                            if (context.HandleResumeOnPlaybackStartAsync != null)
                            {
                                await context.HandleResumeOnPlaybackStartAsync();
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Error inside RunOnUIThreadAsync for playback state handling");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnPlaybackStateChanged event handler (outer)");
            }
        }

        private bool ShouldSkipSnapshot(PlaybackSessionSnapshot snapshot)
        {
            if (!_hasLastSnapshot)
            {
                return false;
            }

            if (snapshot.State != _lastState)
            {
                return false;
            }

            var positionDelta = Math.Abs((snapshot.Position - _lastPosition).TotalSeconds);
            if (positionDelta > PositionEpsilonSeconds)
            {
                return false;
            }

            return DateTime.UtcNow - _lastProcessedAt < _duplicateWindow;
        }

        private void RememberSnapshot(PlaybackSessionSnapshot snapshot)
        {
            _lastState = snapshot.State;
            _lastPosition = snapshot.Position;
            _lastProcessedAt = DateTime.UtcNow;
            _hasLastSnapshot = true;
        }
    }

    internal sealed class PlaybackStateChangeContext
    {
        public bool IsDisposed { get; set; }
        public MediaPlaybackSession Session { get; set; }
        public PlaybackSessionState SessionState { get; set; }
        public DispatcherTimer BufferingTimeoutTimer { get; set; }
        public Action<TimeSpan> SetRawPosition { get; set; }
        public Func<TimeSpan> GetDisplayPosition { get; set; }
        public Func<TimeSpan> GetMetadataDuration { get; set; }
        public Action<MediaPlaybackSession> HandleHlsBufferingFix { get; set; }
        public Func<Action, Task> RunOnUiThreadAsync { get; set; }
        public Action NotifyIsBufferingChanged { get; set; }
        public Action NotifyIsPlayingChanged { get; set; }
        public Action NotifyIsPausedChanged { get; set; }
        public Func<DateTime?> GetBufferingStartTime { get; set; }
        public Action<DateTime?> SetBufferingStartTime { get; set; }
        public Func<bool> GetHasManifestOffset { get; set; }
        public Action<MediaPlaybackState> SetPlaybackState { get; set; }
        public Func<bool> GetHasVideoStarted { get; set; }
        public Action<bool> SetHasVideoStarted { get; set; }
        public Func<Task> HandleResumeOnPlaybackStartAsync { get; set; }
        public Action<TimeSpan> ApplyManifestOffsetFromBuffering { get; set; }
    }
}
