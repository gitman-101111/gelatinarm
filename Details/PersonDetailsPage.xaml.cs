using System;
using Windows.UI.Xaml.Controls;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Details
{
    public sealed partial class PersonDetailsPage : DetailsPage
    {
        public PersonDetailsPage() : base(typeof(PersonDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(PersonDetailsViewModel);
        public new PersonDetailsViewModel ViewModel => (PersonDetailsViewModel)base.ViewModel;

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                ViewModel.NavigateToItemCommand.Execute(item);
            }
        }
    }
}
