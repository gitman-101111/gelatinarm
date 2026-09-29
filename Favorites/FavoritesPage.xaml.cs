using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Favorites
{
    public sealed partial class FavoritesPage : BasePage
    {
        public FavoritesPage() : base(typeof(FavoritesPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(FavoritesViewModel);
        public new FavoritesViewModel ViewModel => (FavoritesViewModel)base.ViewModel;

        protected override async Task RefreshDataAsync(bool forceRefresh)
        {
            await ViewModel.LoadDataAsync(true);

            // A controller user starts on the first tile, not the filter buttons
            if (await UiHelper.ContainerWhenReadyAsync(FavoritesList, 0) is ListViewItem first)
            {
                first.Focus(FocusState.Programmatic);
            }
        }

        private async void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                var resources = Application.Current.Resources;
                foreach (var filterButton in new[] { AllButton, MoviesButton, ShowsButton, MusicButton })
                {
                    filterButton.Style = resources[filterButton == button ? "ActiveFilterButtonStyle" : "FilterButtonStyle"] as Style;
                }

                await ViewModel.ApplyFilterAsync(button.Tag?.ToString() ?? "All");
            }
        }

        private void FavoritesGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto selectedItem)
            {
                NavigateToItemDetails(selectedItem);
            }
        }
    }
}
