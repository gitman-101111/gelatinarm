using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Library
{
    public sealed partial class LibrarySelectionPage : BasePage
    {
        public LibrarySelectionPage() : base(typeof(LibrarySelectionPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(LibrarySelectionViewModel);
        public new LibrarySelectionViewModel ViewModel => (LibrarySelectionViewModel)base.ViewModel;

        protected override void CleanupResources()
        {
            // Clear focus to stop any focus animations
            LibraryTypeGrid.Focus(FocusState.Unfocused);
        }

        private async void OnLibraryClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is BaseItemDto library && library.Id != null)
                {
                    Logger.LogDebug(
                        "LibrarySelectionPage: Navigating to library: {LibraryName} (ID: {LibraryId}, Type: {LibraryCollectionType})", library.Name, library.Id, library.CollectionType);

                    NavigationService.Navigate(typeof(LibraryPage), library);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnLibraryClick", ErrorCategory.User), false);
            }
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            // Reports its own failures
            await ViewModel.InitializeAsync();

            await SetInitialFocusAsync();
        }

        // The view model's count, not the grid's: on a return the page has not bound its items yet
        private async Task SetInitialFocusAsync()
        {
            if (ViewModel.Libraries.Count == 0)
            {
                Logger.LogWarning("No libraries loaded to set initial focus.");
                return;
            }

            var firstItem = await UiHelper.ContainerWhenReadyAsync(LibraryTypeGrid, 0);
            if (firstItem?.Focus(FocusState.Programmatic) == true)
            {
                Logger.LogDebug("LibrarySelectionPage: Focus set to first library item");
            }
            else
            {
                LibraryTypeGrid.Focus(FocusState.Programmatic);
                Logger.LogDebug("LibrarySelectionPage: first library item {Container}; focus set to the grid", firstItem == null ? "missing" : "refused focus");
            }
        }
    }
}
