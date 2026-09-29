using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;

namespace Gelatinarm.SignIn
{
    /// <summary>
    ///     "Who's watching?" picker: switch between saved profiles or add a new user
    /// </summary>
    public sealed partial class ProfileSelectionPage : BasePage
    {
        public ProfileSelectionPage() : base(typeof(ProfileSelectionPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(ProfileSelectionViewModel);
        public new ProfileSelectionViewModel ViewModel => (ProfileSelectionViewModel)base.ViewModel;

        protected override Control InitialFocusControl => ProfilesGridView;

        protected override Task InitializePageAsync(object parameter)
        {
            Bindings.Update();
            return base.InitializePageAsync(parameter);
        }

        /// <summary>
        ///     Opened from Switch User, B returns to where the user came from. After a sign-out
        ///     there is nothing behind the picker, and B must not fall through to the system
        ///     (which would leave the app), so it does nothing.
        /// </summary>
        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            if (NavigationService.CanGoBack)
            {
                return false;
            }

            e.Handled = true;
            return true;
        }

        private void ProfilesGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ProfileTile tile)
            {
                ViewModel.SelectProfileCommand.Execute(tile);
            }
        }
    }
}
