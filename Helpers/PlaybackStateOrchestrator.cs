using System;
using System.Threading.Tasks;
using Gelatinarm.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;
using Windows.UI.Xaml;

namespace Gelatinarm.Helpers
{
    internal sealed class PlaybackStateOrchestrator
    {
        private readonly ILogger _logger;
        private readonly BufferingStateCoordinator _bufferingStateCoordinator;

        public PlaybackStateOrchestrator(
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
                _logger.LogInformation("[VM-PLAYBACK-STATE] Handler entered");

                var snapshot = PlaybackSessionSnapshot.Capture(
                    context.Session,
                    skipBufferingProgress: context.SessionState?.IsHlsStream == true);
                if (!snapshot.HasSession)
                {
                    _logger.LogWarning("[VM-PLAYBACK-STATE] Playback session missing, ignoring event");
                    return;
                }

                var newState = snapshot.State;
                _logger.LogInformation($"[VM-PLAYBACK-STATE] State changed to: {newState}, " +
                                       $"Position: {snapshot.Position.TotalSeconds:F2}s, " +
                                       $"BufferingProgress: {snapshot.BufferingProgress:P}");

                await context.RunOnUiThreadAsync(async () =>
                {
                    try
                    {
                        var wasBuffering = context.GetIsBuffering();
                        var uiSnapshot = PlaybackSessionSnapshot.Capture(
                            context.Session,
                            skipBufferingProgress: context.SessionState?.IsHlsStream == true);
                        var rawPosition = uiSnapshot.Position;

                        context.SetRawPosition(rawPosition);

                        var position = context.GetDisplayPosition();
                        var bufferingProgress = uiSnapshot.BufferingProgress;
                        var canSeek = uiSnapshot.CanSeek;

                        _logger.LogInformation($"PlaybackStateChanged: {newState}, Position: {position.TotalSeconds:F2}s, " +
                                               $"BufferingProgress: {bufferingProgress:F2}, CanSeek: {canSeek}, WasBuffering: {wasBuffering}");

                        context.SetIsBuffering(newState == MediaPlaybackState.Buffering);

                        var bufferingResult = _bufferingStateCoordinator.Handle(new BufferingStateRequest
                        {
                            IsBuffering = context.GetIsBuffering(),
                            WasBuffering = wasBuffering,
                            IsHls = context.SessionState.IsHlsStream,
                            NewState = newState,
                            Position = position,
                            ExpectedHlsSeekTarget = context.SessionState.ExpectedHlsSeekTarget,
                            NaturalDuration = uiSnapshot.NaturalDuration,
                            MetadataDuration = context.GetMetadataDuration(),
                            BufferingStartTime = context.GetBufferingStartTime(),
                            HlsManifestOffset = context.SessionState.HlsManifestOffset,
                            HlsManifestOffsetApplied = context.SessionState.HlsManifestOffsetApplied
                        });

                        context.SetBufferingStartTime(bufferingResult.BufferingStartTime);
                        context.SessionState.ExpectedHlsSeekTarget = bufferingResult.ExpectedHlsSeekTarget;
                        context.SessionState.HlsManifestOffset = bufferingResult.HlsManifestOffset;
                        context.SessionState.HlsManifestOffsetApplied = bufferingResult.HlsManifestOffsetApplied;

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
                        _logger.LogError(innerEx, $"Error inside RunOnUIThreadAsync for state {newState}");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnPlaybackStateChanged event handler (outer)");
            }
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
        public Func<bool> GetIsBuffering { get; set; }
        public Action<bool> SetIsBuffering { get; set; }
        public Func<DateTime?> GetBufferingStartTime { get; set; }
        public Action<DateTime?> SetBufferingStartTime { get; set; }
        public Func<bool> GetHasVideoStarted { get; set; }
        public Action<bool> SetHasVideoStarted { get; set; }
        public Func<Task> HandleResumeOnPlaybackStartAsync { get; set; }
    }
}
