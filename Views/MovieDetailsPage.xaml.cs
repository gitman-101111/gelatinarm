using System;
using System.Threading.Tasks;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.Views
{
    /// <summary>
    ///     Movie details page
    /// </summary>
    public sealed partial class MovieDetailsPage : DetailsPage
    {
        private bool _isNavigatingToPerson = false;

        public MovieDetailsPage() : base(typeof(MovieDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(MovieDetailsViewModel);
        public MovieDetailsViewModel ViewModel => (MovieDetailsViewModel)base.ViewModel;


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
                    var savedParameter = GetSavedNavigationParameter();

                    if (savedParameter != null)
                    {
                        Logger?.LogInformation("Using saved navigation parameter for back navigation");
                        await ViewModel.InitializeAsync(savedParameter);
                    }
                    else if (ViewModel.CurrentItem != null)
                    {
                        Logger?.LogInformation("No navigation parameter on MovieDetailsPage, refreshing current item");
                        await ViewModel.RefreshAsync();
                    }
                }

                // Focus on Play/Resume button after content loads
                await Task.Delay(100); // Small delay to ensure UI is ready
                MoveToContentArea();
            }
        }

        /// <summary>
        ///     Handle similar item click
        /// </summary>
        private void OnSimilarItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                var navigationService = App.Current.Services.GetRequiredService<INavigationService>();
                navigationService.Navigate(typeof(MovieDetailsPage), item);
            }
        }

        /// <summary>
        ///     Handle cast member click
        /// </summary>
        private async void OnCastItemClick(object sender, ItemClickEventArgs e)
        {
            // Prevent duplicate navigation
            if (_isNavigatingToPerson)
            {
                Logger?.LogInformation("Navigation to person already in progress, ignoring duplicate click");
                return;
            }

            if (e.ClickedItem is BaseItemPerson person && person.Id.HasValue)
            {
                try
                {
                    _isNavigatingToPerson = true;
                    Logger?.LogInformation($"Cast member clicked: {person.Name} (ID: {person.Id})");

                    // Fetch the person details as a BaseItemDto
                    var apiClient = ApiClient;
                    var personDto = await apiClient.Items[person.Id.Value].GetAsync();

                    if (personDto != null)
                    {
                        var navigationService = NavigationService;
                        navigationService.Navigate(typeof(PersonDetailsPage), personDto);
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, $"Failed to navigate to person details for {person.Name}");
                    // Show error dialog using base class method
                    await ShowMessageAsync("Error", "Failed to load person details. Please try again.");
                }
                finally
                {
                    _isNavigatingToPerson = false;
                }
            }
        }
    }
}
