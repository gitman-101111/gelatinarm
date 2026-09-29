using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Home
{
    public sealed partial class MainPage : BasePage
    {
        private bool _firstTileFocused;

        public MainPage() : base(typeof(MainPage))
        {
            InitializeComponent();
            KeyDown += MainPage_KeyDown;
        }

        protected override Type ViewModelType => typeof(MainViewModel);
        public new MainViewModel ViewModel => (MainViewModel)base.ViewModel;

        // Home is the root: B must not leave it
        private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadB)
            {
                e.Handled = true;
            }
        }

        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            e.Handled = true;
            return true;
        }

        private void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                NavigateToItemDetails(item);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshData();
        }

        protected override Task InitializePageAsync(object parameter)
        {
            // Home is the root: nothing behind it to return to (sign-in pages included)
            NavigationService.ClearBackStack();

            // A controller user starts on the first tile, not the command bar. The rows are disabled
            // while the page loads and a disabled tile takes no focus, so the first load's end is the moment
            _firstTileFocused = false;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            return Task.CompletedTask;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsLoading) && !ViewModel.IsLoading && !_firstTileFocused)
            {
                FireAndForget(async () => _firstTileFocused = await FocusFirstTileAsync(), "FocusFirstTile");
            }
        }

        // The first tile of the first row that has any, in the page's order. The idle first: the rows
        // enable and the loading overlay hands focus back on the same change, and the tile must come last
        private async Task<bool> FocusFirstTileAsync()
        {
            await UiHelper.WhenIdleAsync(Dispatcher);
            foreach (var row in new[] { ContinueWatchingRow, NextUpRow, LatestMoviesRow, LatestTvShowsRow, RecentlyAddedRow, RecommendedRow })
            {
                if (row.Items.Count > 0 && await UiHelper.ContainerWhenReadyAsync(row, 0) is GridViewItem first &&
                    first.Focus(FocusState.Programmatic))
                {
                    return true;
                }
            }

            Logger.LogDebug("No home tile to focus");
            return false;
        }

        protected override void CleanupResources()
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        protected override async Task RefreshDataAsync(bool forceRefresh)
        {
            // The page shows before the rows load
            await UiHelper.WhenIdleAsync(Dispatcher);

            if (!forceRefresh)
            {
                // The cached rows show at once; Continue Watching alone is fetched again
                FireAndForget(async () =>
                {
                    await ViewModel.LoadDataAsync();
                    await ViewModel.RefreshContinueWatchingAsync();

                    // Refilling the row drops the tile that held focus, and focus with it
                    if (FocusManager.GetFocusedElement() == null)
                    {
                        await FocusFirstTileAsync();
                    }
                }, "LoadDataAndRefreshContinueWatching");
            }
            else
            {
                FireAndForget(() => ViewModel.LoadDataAsync(true), "LoadDataAsync");
            }
        }

        protected override Task OnNavigatedBackAsync()
        {
            return FocusFirstTileAsync();
        }
    }
}
