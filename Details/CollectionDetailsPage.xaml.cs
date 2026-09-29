using System;
using Windows.UI.Xaml.Controls;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Details
{
    public sealed partial class CollectionDetailsPage : DetailsPage
    {
        public CollectionDetailsPage() : base(typeof(CollectionDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(CollectionDetailsViewModel);
        public new CollectionDetailsViewModel ViewModel => (CollectionDetailsViewModel)base.ViewModel;

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                ViewModel.NavigateToItemCommand.Execute(item);
            }
        }
    }
}
