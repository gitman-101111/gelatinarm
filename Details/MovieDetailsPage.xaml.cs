using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    public sealed partial class MovieDetailsPage : DetailsPage
    {
        public MovieDetailsPage() : base(typeof(MovieDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(MovieDetailsViewModel);
        public new MovieDetailsViewModel ViewModel => (MovieDetailsViewModel)base.ViewModel;

        protected override async Task InitializeViewModelAsync(object parameter)
        {
            if (!await InitializeFromParameterAsync(parameter) && ViewModel.CurrentItem != null)
            {
                Logger.LogDebug("No navigation parameter on MovieDetailsPage, refreshing current item");
                await ViewModel.RefreshAsync();
            }
        }

        // The similar-items and collection rows: a collection can hold series as well as movies
        private void OnMovieItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                NavigateToItemDetails(item);
            }
        }

        // The person page loads the person by id itself
        private void OnCastItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemPerson person && person.Id.HasValue)
            {
                Logger.LogDebug("Cast member clicked: {PersonName} (ID: {PersonId})", person.Name, person.Id);
                NavigationService.Navigate(typeof(PersonDetailsPage), person.Id.Value);
            }
        }
    }
}
