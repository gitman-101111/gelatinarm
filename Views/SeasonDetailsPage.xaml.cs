using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    public sealed partial class SeasonDetailsPage : DetailsPage
    {
        public SeasonDetailsPage() : base(typeof(SeasonDetailsPage))
        {
            InitializeComponent();

            // Enable page caching to preserve state when navigating back
            NavigationCacheMode = NavigationCacheMode.Enabled;

            // Subscribe to layout changes to handle scrolling
            Loaded += OnPageLoaded;

            // Let the system handle focus scrolling naturally
        }

        public SeasonDetailsViewModel ViewModel { get; private set; }

        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            // Check if we should navigate to the original source page (skipping MediaPlayerPage)
            if (ViewModel?.ShouldNavigateToOriginalSource == true)
            {
                var navigationService = NavigationService;
                if (navigationService != null && ViewModel.NavigationSourcePageForBack != null)
                {
                    // Check if we're trying to navigate back to the same type of page we're currently on
                    if (ViewModel.NavigationSourcePageForBack == typeof(SeasonDetailsPage))
                    {
                        Logger?.LogInformation(
                            "Smart back navigation: Original source is SeasonDetailsPage, using standard back navigation");
                        ViewModel.ClearNavigationContext(); // Clear context since we're not using it
                        return false; // Let the standard back navigation handle it
                    }

                    Logger?.LogInformation(
                        $"Smart back navigation: Going to {ViewModel.NavigationSourcePageForBack.Name} instead of MediaPlayerPage");
                    e.Handled = true;

                    // Store the navigation info before clearing
                    var targetPage = ViewModel.NavigationSourcePageForBack;
                    var targetParameter = ViewModel.NavigationSourceParameterForBack;

                    // Clear the navigation context after using it
                    ViewModel.ClearNavigationContext();

                    navigationService.Navigate(targetPage, targetParameter);
                    return true;
                }
            }

            // Use default back navigation
            return false;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Set focus to landing button immediately on navigation
            if (FocusLandingButton != null)
            {
                FocusLandingButton.Focus(FocusState.Programmatic);
            }

            // Check if we need a fresh ViewModel (navigating to a different show)
            var needNewViewModel = false;

            if (ViewModel != null && e.Parameter is BaseItemDto newItem)
            {
                // Check if we're navigating to a different series
                var currentSeriesId = ViewModel.Series?.Id ?? ViewModel.CurrentSeason?.SeriesId;
                var newSeriesId = newItem.Type == BaseItemDto_Type.Episode ? newItem.SeriesId :
                    newItem.Type == BaseItemDto_Type.Season ? newItem.SeriesId :
                    newItem.Type == BaseItemDto_Type.Series ? newItem.Id : null;

                if (currentSeriesId != newSeriesId && newSeriesId != null)
                {
                    Logger?.LogInformation(
                        $"Navigating to different series - current: {currentSeriesId}, new: {newSeriesId}");
                    needNewViewModel = true;
                }
            }

            // Get a fresh ViewModel if needed or if we don't have one
            if (ViewModel == null || needNewViewModel)
            {
                // Clean up old ViewModel
                if (ViewModel != null)
                {
                    ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                    ViewModel.ClearState();
                }

                // Initialize new ViewModel from DI
                ViewModel = App.Current.Services.GetRequiredService<SeasonDetailsViewModel>();
                DataContext = ViewModel;

                // Subscribe to property changes
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;

                // Force UI to update bindings
                Bindings.Update();
            }

            base.OnNavigatedTo(e);
        }

        protected override async Task InitializeViewModelAsync(object parameter)
        {
            if (ViewModel != null)
            {
                // If we have a parameter, use it (normal navigation)
                if (parameter != null)
                {
                    await ViewModel.InitializeAsync(parameter);
                }
                // If no parameter, check if we have a saved parameter from navigation service (back navigation)
                else
                {
                    var navigationService = App.Current.Services.GetRequiredService<INavigationService>();
                    var savedParameter = navigationService.GetLastNavigationParameter();

                    if (savedParameter != null)
                    {
                        Logger?.LogInformation("Using saved navigation parameter for back navigation");
                        await ViewModel.InitializeAsync(savedParameter);
                    }
                }

                // Wait for data to load
                await Task.Delay(200); // Small delay to ensure UI is ready

                // Scroll to selected items after navigation
                ScrollEpisodeIntoView();
                await ScrollSeasonIntoViewOnLoad();

                // Now move focus to the primary button after everything is loaded
                MoveToContentArea();
            }
        }

        /// <summary>
        ///     Handle episode click
        /// </summary>
        private async void OnEpisodeClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto episode && ViewModel != null)
            {
                await ViewModel.SelectEpisodeCommand(episode);
            }
        }

        /// <summary>
        ///     Handle season click (explicit selection)
        /// </summary>
        private async void OnSeasonClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto season && ViewModel != null)
            {
                await ViewModel.SelectSeasonCommand.ExecuteAsync(season);

                // Scroll the selected season into view
                ScrollSeasonIntoView();
            }
        }


        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                // Wait a bit for the UI to be fully loaded
                await Task.Delay(100);

                // Initial scroll to selected items
                ScrollEpisodeIntoView();
                await ScrollSeasonIntoViewOnLoad();

                // Update XY focus navigation based on which button is visible
                UpdateEpisodeListFocusNavigation();

                // Set up focus navigation for the landing button
                if (FocusLandingButton != null)
                {
                    if (ViewModel.IsResumeButtonVisible && ResumeButton != null)
                    {
                        FocusLandingButton.XYFocusDown = ResumeButton;
                        FocusLandingButton.XYFocusRight = ResumeButton;
                    }
                    else if (ViewModel.IsPlayButtonVisible && PlayButton != null)
                    {
                        FocusLandingButton.XYFocusDown = PlayButton;
                        FocusLandingButton.XYFocusRight = PlayButton;
                    }
                }
            }
        }

        private async Task ScrollSeasonIntoViewOnLoad()
        {
            if (SeasonTabs == null || ViewModel == null || ViewModel.SelectedSeasonIndex < 0)
            {
                return;
            }

            try
            {
                // Wait for seasons to be loaded
                await Task.Delay(300);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (ViewModel.SelectedSeasonIndex < SeasonTabs.Items.Count)
                    {
                        var selectedItem = SeasonTabs.Items[ViewModel.SelectedSeasonIndex];
                        SeasonTabs.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Default);
                        Logger?.LogInformation($"Scrolled to season index {ViewModel.SelectedSeasonIndex}");
                    }
                }, Dispatcher, Logger);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error scrolling season into view on load");
            }
        }

        private void UpdateEpisodeListFocusNavigation()
        {
            if (EpisodesList == null || ViewModel == null)
            {
                return;
            }

            // Set the XYFocusRight to the visible primary button
            if (ViewModel.IsResumeButtonVisible && ResumeButton != null)
            {
                EpisodesList.XYFocusRight = ResumeButton;
            }
            else if (ViewModel.IsPlayButtonVisible && PlayButton != null)
            {
                EpisodesList.XYFocusRight = PlayButton;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedEpisodeIndex))
            {
                // Only restore focus after initial page load is complete
                bool shouldRestoreFocus = ViewModel?.IsInitialLoadComplete == true && ViewModel?.SelectedEpisode != null;
                ScrollEpisodeIntoView(shouldRestoreFocus);
            }
            else if (e.PropertyName == nameof(ViewModel.SelectedSeasonIndex))
            {
                ScrollSeasonIntoView();
            }
            else if (e.PropertyName == nameof(ViewModel.IsSeriesPosterVisible))
            {
                // Center content vertically when showing series overview
                if (EpisodeDetailsGrid != null)
                {
                    EpisodeDetailsGrid.VerticalAlignment = ViewModel.IsSeriesPosterVisible
                        ? VerticalAlignment.Center
                        : VerticalAlignment.Stretch;
                }
            }
            else if (e.PropertyName == nameof(ViewModel.IsPlayButtonVisible) ||
                     e.PropertyName == nameof(ViewModel.IsResumeButtonVisible))
            {
                UpdateEpisodeListFocusNavigation();
            }
        }

        private async void ScrollEpisodeIntoView(bool restoreFocus = false)
        {
            Logger?.LogInformation(
                $"ScrollEpisodeIntoView called - EpisodesList: {EpisodesList != null}, ViewModel: {ViewModel != null}, SelectedIndex: {ViewModel?.SelectedEpisodeIndex}, RestoreFocus: {restoreFocus}");

            if (EpisodesList == null || ViewModel == null || ViewModel.SelectedEpisodeIndex < 0)
            {
                return;
            }

            try
            {
                // Small delay to ensure the list is populated
                await Task.Delay(200);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    Logger?.LogInformation(
                        $"Attempting to scroll to episode index {ViewModel.SelectedEpisodeIndex} of {EpisodesList.Items.Count} items");

                    if (ViewModel.SelectedEpisodeIndex < EpisodesList.Items.Count)
                    {
                        var selectedItem = EpisodesList.Items[ViewModel.SelectedEpisodeIndex];
                        EpisodesList.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
                        Logger?.LogInformation("ScrollIntoView called successfully");

                        // Only restore focus when explicitly requested (e.g., after watched status change)
                        if (restoreFocus)
                        {
                            var container = EpisodesList.ContainerFromIndex(ViewModel.SelectedEpisodeIndex) as ListViewItem;
                            if (container != null)
                            {
                                container.Focus(FocusState.Programmatic);
                                Logger?.LogInformation($"Restored focus to episode at index {ViewModel.SelectedEpisodeIndex}");
                            }
                        }
                    }
                    else
                    {
                        Logger?.LogWarning(
                            $"Selected index {ViewModel.SelectedEpisodeIndex} is out of range for {EpisodesList.Items.Count} items");
                    }
                }, Dispatcher, Logger);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error scrolling episode into view");
            }
        }

        private async void ScrollSeasonIntoView()
        {
            if (SeasonTabs == null || ViewModel == null || ViewModel.SelectedSeasonIndex < 0)
            {
                return;
            }

            try
            {
                // Small delay to ensure the list is populated
                await Task.Delay(100);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (ViewModel.SelectedSeasonIndex < SeasonTabs.Items.Count)
                    {
                        // Simply use the ListView's built-in ScrollIntoView
                        var selectedItem = SeasonTabs.Items[ViewModel.SelectedSeasonIndex];
                        SeasonTabs.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Default);
                    }
                }, Dispatcher, Logger);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error scrolling season into view");
            }
        }


        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Clean up event handlers
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            Loaded -= OnPageLoaded;
        }

        /// <summary>
        ///     Override to move focus to primary button after page loads
        /// </summary>
        protected override void OnMoveToContentArea()
        {
            // Move to the actual primary button
            if (ViewModel?.IsResumeButtonVisible == true && ResumeButton != null)
            {
                ResumeButton.Focus(FocusState.Programmatic);
            }
            else if (ViewModel?.IsPlayButtonVisible == true && PlayButton != null)
            {
                PlayButton.Focus(FocusState.Programmatic);
            }
        }

        /// <summary>
        ///     Handle Info button click to navigate to series details
        /// </summary>
        private async void OnInfoButtonClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Series != null)
            {
                var navigationService = GetService<INavigationService>();
                if (navigationService != null)
                {
                    // Navigate to the series details page
                    navigationService.NavigateToItemDetails(ViewModel.Series);
                }
            }
            else
            {
                Logger?.LogWarning("Cannot navigate to series info - Series data not available");
            }
        }

    }
}
