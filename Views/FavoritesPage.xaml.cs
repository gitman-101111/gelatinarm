using System;
using System.Threading.Tasks;
using Gelatinarm.Controls;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
// Added for Application.Current.Resources and Style

namespace Gelatinarm.Views
{
    public sealed partial class FavoritesPage : BasePage
    {
        public FavoritesPage() : base(typeof(FavoritesPage))
        {
            InitializeComponent();
            ControllerInputHelper.ConfigurePageForController(this, null, Logger);
        }

        protected override Type ViewModelType => typeof(FavoritesViewModel);
        public FavoritesViewModel ViewModel => (FavoritesViewModel)base.ViewModel;

        protected override async Task RefreshDataAsync(bool forceRefresh)
        {
            await ViewModel.LoadFavoritesAsync();
        }


        private async void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AllButton.Style = Application.Current.Resources["FilterButtonStyle"] as Style;
                MoviesButton.Style = Application.Current.Resources["FilterButtonStyle"] as Style;
                ShowsButton.Style = Application.Current.Resources["FilterButtonStyle"] as Style;
                MusicButton.Style = Application.Current.Resources["FilterButtonStyle"] as Style;

                button.Style = Application.Current.Resources["ActiveFilterButtonStyle"] as Style;

                var filter = button.Tag?.ToString() ?? "All";
                await ViewModel.ApplyFilterAsync(filter);
            }
        }

        private void FavoritesGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto selectedItem)
            {
                Logger?.LogInformation(
                    $"Navigating to details for favorite item: {selectedItem.Name} (Type: {selectedItem.Type})");
                // Navigate to item details
                NavigateToItemDetails(selectedItem);
            }
        }
    }
}
