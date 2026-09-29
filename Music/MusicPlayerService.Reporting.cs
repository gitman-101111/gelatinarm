using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Playback;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Errors;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Music
{
    public partial class MusicPlayerService
    {
        private async Task StartPlaybackReportingAsync()
        {
            var context = CreateErrorContext("StartPlaybackReportingAsync", ErrorCategory.Media);
            try
            {
                var report = CreateReport(_mediaControlService.Position.Ticks);
                if (report == null)
                {
                    return;
                }

                await _mediaSessionService.ReportPlaybackStartAsync(report).ConfigureAwait(false);

                StopProgressReporting();
                var cancellationToken = AsyncHelper.Supersede(ref _progressReportCancellationTokenSource).Token;

                _progressReportTimer = new Timer(_ =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            ReportProgress();
                        }
                    }, null, TimeSpan.FromSeconds(PlaybackConstants.ProgressReportIntervalSeconds),
                    TimeSpan.FromSeconds(PlaybackConstants.ProgressReportIntervalSeconds));
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        // A timer callback: an exception escaping it would end the app
        private void ReportProgress()
        {
            ErrorHandler.Run(CreateErrorContext("ReportProgress", ErrorCategory.Media), () =>
                _mediaSessionService.ReportPlaybackProgress(
                    CreateReport(_mediaControlService.Position.Ticks, !_mediaControlService.IsPlaying)));
        }

        private void StopProgressReporting()
        {
            AsyncHelper.Cancel(ref _progressReportCancellationTokenSource);

            _progressReportTimer?.Dispose();
            _progressReportTimer = null;
        }

        private async Task StopPlaybackReportingAsync()
        {
            var context = CreateErrorContext("StopPlaybackReportingAsync", ErrorCategory.Media);
            try
            {
                StopProgressReporting();

                await _mediaSessionService.ReportPlaybackStoppedAsync(CreateReport(_mediaControlService.Position.Ticks))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        // Music does not track its stream choice (its transcoding fallback included), so every
        // report says DirectPlay
        private PlaybackReport CreateReport(long positionTicks, bool isPaused = false)
        {
            var itemId = _mediaControlService.CurrentItem?.Id;
            if (itemId == null || _currentMediaSource == null || _currentPlaySessionId == null)
            {
                return null;
            }

            return new PlaybackReport
            {
                ItemId = itemId.Value,
                MediaSourceId = _currentMediaSource.Id,
                PlaySessionId = _currentPlaySessionId,
                PositionTicks = positionTicks,
                PlayMethod = PlaybackProgressInfo_PlayMethod.DirectPlay,
                IsPaused = isPaused
            };
        }
    }
}
