using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Details;
using Gelatinarm.Playback;
using Gelatinarm.Shared.Errors;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public sealed partial class MediaPlayerPage
    {
        private async Task ExecuteTrackChangeAsync<TTrack>(
            string label,
            TTrack selectedTrack,
            Func<TTrack, string> displaySelector,
            IAsyncRelayCommand<TTrack> command,
            Flyout flyout) where TTrack : class
        {
            if (selectedTrack == null)
            {
                return;
            }

            Logger.LogDebug("{Label} track chosen: {Track}", label, displaySelector(selectedTrack));

            if (!command.CanExecute(selectedTrack))
            {
                return;
            }

            await command.ExecuteAsync(selectedTrack);
            flyout?.Hide();
        }

        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            SubtitlesFlyout.ShowAt(sender as FrameworkElement);
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            AudioFlyout.ShowAt(sender as FrameworkElement);
        }

        private async void AudioList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is AudioTrack selectedAudio)
                {
                    await ExecuteTrackChangeAsync(
                        "Audio",
                        selectedAudio,
                        audio => audio.DisplayName,
                        ViewModel.ChangeAudioTrackCommand,
                        AudioFlyout);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("AudioList_ItemClick", ErrorCategory.Media), false);
            }
        }

        private async void SubtitlesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is SubtitleTrack selectedSubtitle)
                {
                    await ExecuteTrackChangeAsync(
                        "Subtitle",
                        selectedSubtitle,
                        subtitle => subtitle.DisplayTitle,
                        ViewModel.ChangeSubtitleCommand,
                        SubtitlesFlyout);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("SubtitlesList_ItemClick", ErrorCategory.Media), false);
            }
        }

        private void EpisodesButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentItem?.SeriesId != null && ViewModel.CurrentItem.Type == BaseItemDto_Type.Episode)
            {
                var navigationParam = new EpisodeNavigationParameter
                {
                    Episode = ViewModel.CurrentItem,
                    FromEpisodesButton = true,
                    OriginalSourcePage = ViewModel.NavigationSourcePage,
                    OriginalSourceParameter = ViewModel.NavigationSourceParameter
                };

                Logger.LogDebug(
                    "EpisodesButton_Click - CurrentItem: {CurrentItemName} (Type: {CurrentItemType})", ViewModel.CurrentItem?.Name, ViewModel.CurrentItem?.Type);

                // NavigationService drops this page from the back stack: Back from the season
                // page goes to the page before the player
                NavigationService.Navigate(typeof(SeasonDetailsPage), navigationParam);
            }
            else
            {
                Logger.LogWarning(
                    "EpisodesButton_Click - Cannot navigate: CurrentItem is not an episode or has no SeriesId");
            }
        }

        private async void MediaPlayerPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                Logger.LogDebug(
                    "MediaPlayerPage_KeyDown: Key={EKey}, OriginalSource={OriginalSourceGetType}", e.Key, e.OriginalSource?.GetType().Name);

                if (_isDisposing == 1)
                {
                    Logger.LogWarning("Ignoring key {EKey} - the page is being disposed", e.Key);
                    return;
                }

                // The controls stay while the user is navigating them
                if (CheckControlVisibility())
                {
                    ResetControlVisibilityTimer();
                }

                // The same instance the view model is subscribed to
                var handled = ViewModel.GetControllerInputService().HandleKeyDown(e.Key);
                e.Handled = handled;
                Logger.LogDebug("ControllerInputService handled key {EKey}: {Handled}", e.Key, handled);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("MediaPlayerPage_KeyDown", ErrorCategory.Media), false);
            }
        }
    }
}
