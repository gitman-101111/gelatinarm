using System;
using System.Net.Http;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.Views
{
    public sealed partial class LibrarySelectionPage : BasePage
    {
        private readonly INavigationService _navigationService;
        // Logger is available from BasePage as protected Logger property

        public LibrarySelectionPage() : base(typeof(LibrarySelectionPage))
        {
            InitializeComponent();

            var serviceProvider = ((App)Application.Current)._serviceProvider;
            ViewModel = serviceProvider.GetRequiredService<LibrarySelectionViewModel>();
            _navigationService = serviceProvider.GetRequiredService<INavigationService>();
            // Logger is initialized in BasePage
        }

        public LibrarySelectionViewModel ViewModel { get; }

        protected override void CleanupResources()
        {
            // Clear focus to stop any focus animations
            if (LibraryTypeGrid != null)
            {
                LibraryTypeGrid.Focus(FocusState.Unfocused);
            }
        }

        private async void OnLibraryClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto library && library.Id != null)
            {
                Logger?.LogInformation(
                    $"LibrarySelectionPage: Navigating to library: {library.Name} (ID: {library.Id}, Type: {library.CollectionType})");

                // Small delay to ensure any animations complete
                await Task.Delay(UIConstants.UI_RENDER_DELAY_MS);

                _navigationService.Navigate(typeof(LibraryPage), library);
            }
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            // Log the back stack for debugging
            if (Frame != null)
            {
                Logger?.LogInformation($"LibrarySelectionPage - BackStack count: {Frame.BackStackDepth}");
                for (var i = 0; i < Frame.BackStack.Count; i++)
                {
                    var entry = Frame.BackStack[i];
                    Logger?.LogInformation($"LibrarySelectionPage - BackStack[{i}]: {entry.SourcePageType.Name}");
                }
            }

            try
            {
                await ViewModel.InitializeAsync();
                Logger?.LogInformation(
                    $"LibrarySelectionPage: InitializeAsync completed, Libraries count: {ViewModel.Libraries.Count}");

                // Set focus after libraries are loaded
                await SetInitialFocusAsync();
            }
            catch (UnauthorizedAccessException ex)
            {
                // Only go back for authentication errors
                Logger?.LogError(ex, "Authentication error during LibrarySelectionPage initialization");
                if (_navigationService.CanGoBack)
                {
                    _navigationService.GoBack();
                }

                throw; // Re-throw to let BasePage handle it
            }
            catch (HttpRequestException ex)
            {
                // Network errors - stay on page and show error
                Logger?.LogError(ex, "Network error during LibrarySelectionPage initialization");
                // Could show an error message to user here
                throw; // Re-throw to let BasePage handle it
            }
        }

        protected override async Task OnPageLoadedAsync()
        {
            // Focus is now set in InitializePageAsync after libraries are loaded
        }

        private async Task SetInitialFocusAsync()
        {
            // Set focus to the first library, if any items exist.
            // Using a short delay to ensure items are rendered for focus to work reliably.
            await Task.Delay(UIConstants.UI_SETTLE_DELAY_MS);
            if (LibraryTypeGrid?.Items?.Count > 0)
            {
                try
                {
                    // Focus the first item container instead of the grid itself
                    var firstItem = LibraryTypeGrid.ContainerFromIndex(0) as GridViewItem;
                    if (firstItem != null)
                    {
                        firstItem.Focus(FocusState.Programmatic);
                        Logger?.LogInformation("LibrarySelectionPage: Focus set to first library item");
                    }
                    else
                    {
                        LibraryTypeGrid.Focus(FocusState.Programmatic);
                        Logger?.LogInformation("LibrarySelectionPage: Focus set to library grid");
                    }
                }
                catch (Exception focusEx)
                {
                    Logger?.LogWarning(focusEx, "Failed to set focus to LibraryTypeGrid");
                }
            }
            else
            {
                Logger?.LogWarning("No libraries loaded to set initial focus.");
            }
        }
    }
}
