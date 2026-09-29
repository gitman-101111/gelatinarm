using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Media.Playback;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Shared.Errors;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public partial class MediaPlayerViewModel
    {
        public bool IsIntroSkipAvailable
        {
            get => _isIntroSkipAvailable;
            set => SetSkipAvailability(ref _isIntroSkipAvailable, value, SkipSegmentType.Intro);
        }

        public bool IsOutroSkipAvailable
        {
            get => _isOutroSkipAvailable;
            set => SetSkipAvailability(ref _isOutroSkipAvailable, value, SkipSegmentType.Outro);
        }

        // The next-episode button announces itself as the outro skip does
        public bool NextEpisodeButtonOverlayVisible
        {
            get => _nextEpisodeButtonOverlayVisible;
            set => SetSkipAvailability(ref _nextEpisodeButtonOverlayVisible, value, SkipSegmentType.Outro);
        }

        private void SetSkipAvailability(ref bool field, bool value, SkipSegmentType type,
            [CallerMemberName] string propertyName = null)
        {
            if (SetProperty(ref field, value, propertyName) && value)
            {
                OnSkipButtonBecameAvailable(type);
            }
        }

        public event EventHandler<SkipSegmentType> SkipButtonBecameAvailable;

        [RelayCommand]
        private async Task SkipIntroAsync()
        {
            var context = CreateErrorContext("SkipIntroAsync", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip intro"))
                {
                    return;
                }

                LastActionWasSkip = true;
                if (_introEndTime.HasValue)
                {
                    Logger.LogInformation("Manual skip intro from {CurrentPosition} to {IntroEndTime}", Position, _introEndTime.Value);
                    SeekToItemPosition(_introEndTime.Value);
                    _hasAutoSkippedIntro = true;
                }

                IsIntroSkipAvailable = false;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        [RelayCommand]
        private async Task SkipOutroAsync()
        {
            var context = CreateErrorContext("SkipOutroAsync", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip outro"))
                {
                    return;
                }

                LastActionWasSkip = true;
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                if (HasNextEpisode)
                {
                    Logger.LogInformation("Skipping outro by triggering next episode");
                    await PlayNextEpisodeAsync();
                    return;
                }

                if (_outroEndTime.HasValue)
                {
                    Logger.LogInformation("Manual skip outro to {OutroEndTime}", _outroEndTime.Value);
                    SeekToItemPosition(_outroEndTime.Value);
                    _hasAutoSkippedOutro = true;
                }

                IsOutroSkipAvailable = false;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void ResetSkipSegments()
        {
            _hasAutoSkippedIntro = false;
            _hasAutoSkippedOutro = false;
            _introStartTime = null;
            _introEndTime = null;
            _outroStartTime = null;
            _outroEndTime = null;
        }

        private async Task InitializeSkipSegmentsAsync(MediaPlayer mediaPlayer, BaseItemDto item)
        {
            if (mediaPlayer == null || item == null)
            {
                return;
            }

            ResetSkipSegments();

            if (!item.Id.HasValue)
            {
                return;
            }

            try
            {
                Logger.LogDebug("Fetching media segments for item: {ItemId}", item.Id.Value);
                var segmentsResponse = await _apiClient.MediaSegments[item.Id.Value].GetAsync(config =>
                {
                    config.QueryParameters.IncludeSegmentTypes = new[]
                    {
                        MediaSegmentType.Intro, MediaSegmentType.Outro
                    };
                }).ConfigureAwait(false);

                if (segmentsResponse?.Items is not { Count: > 0 })
                {
                    Logger.LogDebug("No media segments found for this item");
                    return;
                }

                foreach (var segment in segmentsResponse.Items)
                {
                    if (segment.Type == MediaSegmentDto_Type.Intro &&
                        segment.StartTicks.HasValue && segment.EndTicks.HasValue)
                    {
                        _introStartTime = TimeSpan.FromTicks(segment.StartTicks.Value);
                        _introEndTime = TimeSpan.FromTicks(segment.EndTicks.Value);
                        Logger.LogInformation("Found intro segment: {IntroStartTime} - {IntroEndTime}", _introStartTime, _introEndTime);
                    }
                    else if (segment.Type == MediaSegmentDto_Type.Outro &&
                             segment.StartTicks.HasValue && segment.EndTicks.HasValue)
                    {
                        _outroStartTime = TimeSpan.FromTicks(segment.StartTicks.Value);
                        _outroEndTime = TimeSpan.FromTicks(segment.EndTicks.Value);
                        Logger.LogInformation("Found outro segment: {OutroStartTime} - {OutroEndTime}", _outroStartTime, _outroEndTime);
                    }
                }

                await UpdateSkipButtonVisibilityAsync();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("InitializeSkipSegments", ErrorCategory.Media), false);
            }
        }

        private void HandleAutoSkip(TimeSpan currentPosition)
        {
            switch (GetCurrentSegmentType(currentPosition))
            {
                case SkipSegmentType.Intro:
                    AutoSkip(_preferences?.AutoSkipIntroEnabled == true, _introEndTime.Value,
                        ref _hasAutoSkippedIntro, currentPosition, "intro");
                    break;
                case SkipSegmentType.Outro:
                    AutoSkip(_preferences?.AutoSkipOutroEnabled == true, _outroEndTime.Value,
                        ref _hasAutoSkippedOutro, currentPosition, "outro");
                    break;
            }
        }

        private void AutoSkip(bool enabled, TimeSpan end, ref bool alreadySkipped, TimeSpan currentPosition,
            string segment)
        {
            if (!enabled || alreadySkipped)
            {
                return;
            }

            Logger.LogInformation("Auto-skipping {Segment} from {CurrentPosition} to {SegmentEnd}", segment, currentPosition, end);
            SeekToItemPosition(end);
            alreadySkipped = true;
        }

        // Segment times are positions in the item; after a server restart the player's stream
        // starts at the manifest offset, so the seek goes to the item position less the offset
        private void SeekToItemPosition(TimeSpan itemPosition)
        {
            var session = Player?.PlaybackSession;
            if (session == null)
            {
                return;
            }

            var playerPosition = itemPosition - _playbackControlService.HlsManifestOffset;
            session.Position = playerPosition < TimeSpan.Zero ? TimeSpan.Zero : playerPosition;
        }

        private async Task UpdateSkipButtonVisibilityAsync()
        {
            if ((!_introStartTime.HasValue || !_introEndTime.HasValue) &&
                (!_outroStartTime.HasValue || !_outroEndTime.HasValue))
            {
                return;
            }

            var currentSegment = GetCurrentSegmentType(Position);

            var wasIntroAvailable = IsIntroSkipAvailable;
            var wasOutroAvailable = IsOutroSkipAvailable;

            await RunOnUIThreadAsync(() =>
            {
                IsIntroSkipAvailable = currentSegment == SkipSegmentType.Intro;
                IsOutroSkipAvailable = currentSegment == SkipSegmentType.Outro &&
                                       CurrentItem?.Type == BaseItemDto_Type.Movie;
            });

            if (IsIntroSkipAvailable != wasIntroAvailable)
            {
                Logger.LogDebug(
                    "Intro skip button visibility changed to: {IsIntroSkipAvailable} at position {Position:hh\\:mm\\:ss}", IsIntroSkipAvailable, Position);
            }

            if (IsOutroSkipAvailable != wasOutroAvailable)
            {
                Logger.LogDebug(
                    "Outro skip button visibility changed to: {IsOutroSkipAvailable} at position {Position:hh\\:mm\\:ss}", IsOutroSkipAvailable, Position);
            }
        }

        private SkipSegmentType? GetCurrentSegmentType(TimeSpan position)
        {
            if (_introStartTime.HasValue && _introEndTime.HasValue &&
                position >= _introStartTime.Value && position < _introEndTime.Value)
            {
                return SkipSegmentType.Intro;
            }

            if (_outroStartTime.HasValue && _outroEndTime.HasValue &&
                position >= _outroStartTime.Value && position < _outroEndTime.Value)
            {
                return SkipSegmentType.Outro;
            }

            return null;
        }

        // RunOnUIThreadAsync never throws: a failure in the handler is logged there
        private void OnSkipButtonBecameAvailable(SkipSegmentType segmentType)
        {
            Logger.LogDebug("{SegmentType} skip button became available", segmentType);
            FireAndForget(() => RunOnUIThreadAsync(() => SkipButtonBecameAvailable?.Invoke(this, segmentType)));
        }
    }
}
