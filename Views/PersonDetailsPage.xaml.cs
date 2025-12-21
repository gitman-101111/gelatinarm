using System.Threading.Tasks;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    public sealed partial class PersonDetailsPage : DetailsPage
    {
        public PersonDetailsPage() : base(typeof(PersonDetailsPage))
        {
            // Initialize ViewModel before InitializeComponent for x:Bind
            ViewModel = GetRequiredService<PersonDetailsViewModel>();
            DataContext = ViewModel;

            InitializeComponent();

            // Disable page caching to prevent state issues
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        public PersonDetailsViewModel ViewModel { get; }


        protected override async Task InitializeViewModelAsync(object parameter)
        {
            if (ViewModel != null)
            {
                var resolvedParameter = ResolveNavigationParameter(parameter);
                if (resolvedParameter != null)
                {
                    await ViewModel.InitializeAsync(resolvedParameter);
                }
                else if (ViewModel.CurrentItem != null)
                {
                    Logger?.LogInformation("No navigation parameter on PersonDetailsPage, using existing person");
                }
            }
        }

        /// <summary>
        ///     Handle movie item click
        /// </summary>
        private void OnMovieItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto movie && ViewModel != null)
            {
                ViewModel.NavigateToMovieCommand.Execute(movie);
            }
        }

        /// <summary>
        ///     Handle TV show item click
        /// </summary>
        private void OnTVShowItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto show && ViewModel != null)
            {
                ViewModel.NavigateToTVShowCommand.Execute(show);
            }
        }
    }
}
