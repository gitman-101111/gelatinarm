using System.Threading.Tasks;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
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
            ViewModel = GetRequiredService<CollectionDetailsViewModel>();
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
                var resolvedParameter = ResolveNavigationParameter(parameter);
                if (resolvedParameter != null)
                {
                    await ViewModel.InitializeAsync(resolvedParameter);
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
