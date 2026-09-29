using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    public sealed partial class SeasonDetailsPage : DetailsPage
    {
        public SeasonDetailsPage() : base(typeof(SeasonDetailsPage))
        {
            InitializeComponent();

            Loaded += OnPageLoaded;
        }

        public new SeasonDetailsViewModel ViewModel => (SeasonDetailsViewModel)base.ViewModel;

        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            if (!ViewModel.ShouldNavigateToOriginalSource || ViewModel.NavigationSourcePageForBack == null)
            {
                return false;
            }

            // Back from a season page reached from a season page is the ordinary back
            if (ViewModel.NavigationSourcePageForBack == typeof(SeasonDetailsPage))
            {
                ViewModel.ClearNavigationContext();
                return false;
            }

            Logger.LogDebug(
                "Smart back navigation: Going to {NavigationSourcePageForBackName} instead of MediaPlayerPage", ViewModel.NavigationSourcePageForBack.Name);
            e.Handled = true;

            var targetPage = ViewModel.NavigationSourcePageForBack;
            var targetParameter = ViewModel.NavigationSourceParameterForBack;
            ViewModel.ClearNavigationContext();

            NavigationService.Navigate(targetPage, targetParameter);
            return true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            FocusLandingButton.Focus(FocusState.Programmatic);

            var needNewViewModel = false;

            // Null on the first visit: this page resolves its own view model, below
            if (ViewModel != null && e.Parameter is BaseItemDto newItem)
            {
                var currentSeriesId = ViewModel.Series?.Id ?? ViewModel.CurrentSeason?.SeriesId;
                var newSeriesId = newItem.Type switch
                {
                    BaseItemDto_Type.Episode or BaseItemDto_Type.Season => newItem.SeriesId,
                    BaseItemDto_Type.Series => newItem.Id,
                    _ => null
                };

                if (currentSeriesId != newSeriesId && newSeriesId != null)
                {
                    Logger.LogDebug(
                        "Navigating to different series - current: {CurrentSeriesId}, new: {NewSeriesId}", currentSeriesId, newSeriesId);
                    needNewViewModel = true;
                }
            }

            if (ViewModel == null || needNewViewModel)
            {
                if (ViewModel != null)
                {
                    ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                    ViewModel.ClearState();
                }

                base.ViewModel = GetRequiredService<SeasonDetailsViewModel>();
                DataContext = ViewModel;
                Bindings.Update();
            }

            // The page is cached and OnNavigatedFrom drops this: a return to the same series keeps
            // its view model, and must listen to it again
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            base.OnNavigatedTo(e);
        }

        protected override async Task InitializeViewModelAsync(object parameter)
        {
            await InitializeFromParameterAsync(parameter);

            FireAndForget(() => ScrollEpisodeIntoViewAsync());
            await ScrollSeasonIntoViewAsync();

            // DetailsPage.MoveToContentArea, next, only reaches OnMoveToContentArea (which retires
            // the landing button) when Play/Resume are hidden, so retire it here too. Left as a tab stop,
            // the invisible button is an XY-navigation target that traps controller focus.
            FocusLandingButton.IsTabStop = false;
        }

        private async void OnEpisodeClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is BaseItemDto episode)
                {
                    await ViewModel.SelectEpisodeAsync(episode);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnEpisodeClick", ErrorCategory.User), false);
            }
        }

        /// <summary>
        ///     The season tabs are the top row of the page, so Up has nowhere to go. Swallow it
        ///     rather than let XY navigation search upward and land on something off-screen.
        /// </summary>
        private void OnSeasonTabsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Up || e.Key == VirtualKey.GamepadDPadUp ||
                e.Key == VirtualKey.GamepadLeftThumbstickUp)
            {
                e.Handled = true;
            }
        }

        private async void OnSeasonClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is BaseItemDto season)
                {
                    await ViewModel.SelectSeasonCommand.ExecuteAsync(season);
                    await ScrollSeasonIntoViewAsync();
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnSeasonClick", ErrorCategory.User), false);
            }
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await UiHelper.WhenIdleAsync(Dispatcher);

                FireAndForget(() => ScrollEpisodeIntoViewAsync());
                await ScrollSeasonIntoViewAsync();

                UpdateEpisodeListFocusNavigation();

                // The series overview has one target for every direction, and takes focus at once
                if (ViewModel.SelectedSeasonIndex == -1)
                {
                    FocusLandingButton.XYFocusDown = SeriesPlayButton;
                    FocusLandingButton.XYFocusRight = SeriesPlayButton;
                    FocusLandingButton.XYFocusUp = SeriesPlayButton;
                    FocusLandingButton.XYFocusLeft = SeriesPlayButton;

                    await UiHelper.WhenIdleAsync(Dispatcher);
                    SeriesPlayButton.Focus(FocusState.Programmatic);
                    FocusLandingButton.IsTabStop = false;
                    Logger.LogDebug("OnPageLoaded: Moved focus to Series Play button for Series Overview");
                }
                else if (ViewModel.CanResume)
                {
                    FocusLandingButton.XYFocusDown = ResumeButton;
                    FocusLandingButton.XYFocusRight = ResumeButton;
                }
                else
                {
                    FocusLandingButton.XYFocusDown = PlayButton;
                    FocusLandingButton.XYFocusRight = PlayButton;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnPageLoaded", ErrorCategory.User), false);
            }
        }

        /// <summary>
        ///     Scrolls the selected season into view once the season list has made its tab
        /// </summary>
        private async Task ScrollSeasonIntoViewAsync()
        {
            if (ViewModel.SelectedSeasonIndex < 0)
            {
                return;
            }

            try
            {
                await UiHelper.RunOnUIThreadAsync(async () =>
                {
                    var index = ViewModel.SelectedSeasonIndex;
                    await UiHelper.ContainerWhenReadyAsync(SeasonTabs, index);
                    if (index < SeasonTabs.Items.Count)
                    {
                        SeasonTabs.ScrollIntoView(SeasonTabs.Items[index], ScrollIntoViewAlignment.Default);
                        Logger.LogDebug("Scrolled to season index {SelectedSeasonIndex}", index);
                    }
                }, Logger, Dispatcher);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ScrollSeasonIntoViewAsync", ErrorCategory.User), false);
            }
        }

        /// <summary>
        ///     When the controller moves focus into the episode list from outside it, land on the
        ///     selected episode rather than the list's first item. The detail buttons target the
        ///     list as a whole, and the list does not bring focus into view, so without this focus
        ///     lands on episode 1, off-screen in a long season.
        /// </summary>
        private void OnEpisodesListGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (args.FocusState != FocusState.Keyboard ||
                ViewModel.SelectedEpisodeIndex < 0 || IsInEpisodesList(args.OldFocusedElement))
            {
                return;
            }

            if (EpisodesList.ContainerFromIndex(ViewModel.SelectedEpisodeIndex) is ListViewItem selected &&
                !ReferenceEquals(args.NewFocusedElement, selected))
            {
                args.TrySetNewFocusedElement(selected);
            }
        }

        private bool IsInEpisodesList(DependencyObject element)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, EpisodesList))
                {
                    return true;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        private void UpdateEpisodeListFocusNavigation()
        {
            EpisodesList.XYFocusRight = ViewModel.CanResume ? ResumeButton : PlayButton;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedEpisodeIndex))
            {
                var shouldRestoreFocus = ViewModel.IsInitialLoadComplete && ViewModel.SelectedEpisode != null;
                FireAndForget(() => ScrollEpisodeIntoViewAsync(shouldRestoreFocus));
            }
            else if (e.PropertyName == nameof(ViewModel.SelectedSeasonIndex))
            {
                FireAndForget(() => ScrollSeasonIntoViewAsync());

                if (ViewModel.SelectedSeasonIndex == -1)
                {
                    UpdateFocusNavigationForSeasonInfo();
                }
            }
            else if (e.PropertyName == nameof(ViewModel.IsSeriesOverview))
            {
                EpisodeDetailsGrid.VerticalAlignment = ViewModel.IsSeriesOverview
                    ? VerticalAlignment.Center
                    : VerticalAlignment.Stretch;
            }
            else if (e.PropertyName == nameof(ViewModel.CanResume))
            {
                UpdateEpisodeListFocusNavigation();
            }
        }

        private async Task ScrollEpisodeIntoViewAsync(bool restoreFocus = false)
        {
            if (ViewModel.SelectedEpisodeIndex < 0)
            {
                return;
            }

            try
            {
                await UiHelper.RunOnUIThreadAsync(async () =>
                {
                    // The list has its items once the page has laid out
                    await UiHelper.WhenIdleAsync(Dispatcher);
                    var index = ViewModel.SelectedEpisodeIndex;
                    Logger.LogDebug(
                        "Attempting to scroll to episode index {ViewModelSelectedEpisodeIndex} of {ItemsCount} items", index, EpisodesList.Items.Count);

                    if (index < 0 || index >= EpisodesList.Items.Count)
                    {
                        Logger.LogWarning(
                            "Selected index {ViewModelSelectedEpisodeIndex} is out of range for {ItemsCount} items", index, EpisodesList.Items.Count);
                        return;
                    }

                    EpisodesList.ScrollIntoView(EpisodesList.Items[index], ScrollIntoViewAlignment.Leading);

                    // A watched-status change re-selects the row, and focus goes back to it once the
                    // scroll has made its container
                    if (restoreFocus && await UiHelper.ContainerWhenReadyAsync(EpisodesList, index) is ListViewItem container)
                    {
                        container.Focus(FocusState.Programmatic);
                        Logger.LogDebug("Restored focus to episode at index {ViewModelSelectedEpisodeIndex}", index);
                    }
                }, Logger, Dispatcher);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ScrollEpisodeIntoView", ErrorCategory.User), false);
            }
        }

        private void UpdateFocusNavigationForSeasonInfo()
        {
            InfoButton.XYFocusLeft = SeasonTabs;
            if (ViewModel.Seasons.Count > 0)
            {
                InfoButton.XYFocusDown = SeasonTabs;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        protected override void OnMoveToContentArea()
        {
            if (ViewModel.SelectedSeasonIndex == -1 && SeriesPlayButton.Visibility == Visibility.Visible)
            {
                SeriesPlayButton.Focus(FocusState.Programmatic);
                Logger.LogDebug("Moved focus to Series Play button on Series Overview");
                return;
            }

            if (ViewModel.CanResume)
            {
                ResumeButton.Focus(FocusState.Programmatic);
            }
            else
            {
                PlayButton.Focus(FocusState.Programmatic);
            }

            FocusLandingButton.IsTabStop = false;
        }

        private async void OnInfoButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.OpenSeriesOverviewCommand.ExecuteAsync(null);
                OnMoveToContentArea();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnInfoButtonClick", ErrorCategory.User), false);
            }
        }
    }
}
