using System.Threading.Tasks;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    public sealed partial class CollectionDetailsPage : DetailsPage
    {
        public CollectionDetailsPage() : base(typeof(CollectionDetailsPage))
        {
            // Initialize ViewModel before InitializeComponent for x:Bind
            ViewModel = App.Current.Services.GetRequiredService<CollectionDetailsViewModel>();
            DataContext = ViewModel;

            InitializeComponent();
        }

        public CollectionDetailsViewModel ViewModel { get; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
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
            }
        }

        /// <summary>
        ///     Handle item click in collection
        /// </summary>
        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item && ViewModel != null)
            {
                ViewModel.NavigateToItemCommand.Execute(item);
            }
        }
    }
}
