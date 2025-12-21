using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using static Gelatinarm.Constants.LibraryConstants;

namespace Gelatinarm.Views
{
    public sealed partial class LibraryPage : BasePage
    {
        private readonly JellyfinApiClient _apiClient;

        // Temporary filter collections for cancel functionality
        private readonly Dictionary<FilterItem, bool> _tempFilterStates = new();

        private readonly IUnifiedDeviceService _unifiedDeviceService;
        private readonly IMusicPlayerService _musicPlayerService;


        public LibraryPage() : base(typeof(LibraryPage))
        {
            try
            {
                InitializeComponent();
                // Enable navigation caching to preserve state when navigating back
                NavigationCacheMode = NavigationCacheMode.Enabled;
            }
#if DEBUG
            catch (Exception ex)
            {
                // If InitializeComponent fails, we can't proceed
                Debug.WriteLine($"LibraryPage: InitializeComponent failed - {ex.Message}");
                throw;
            }
#else
            catch
            {
                // If InitializeComponent fails, we can't proceed
                throw;
            }
#endif
            _unifiedDeviceService = GetService<IUnifiedDeviceService>();
            _musicPlayerService = GetService<IMusicPlayerService>();
            _apiClient = GetRequiredService<JellyfinApiClient>();

            SetupXboxNavigation();
            Logger?.LogInformation("LibraryPage: Constructor completed successfully");
        }

        protected override Type ViewModelType => typeof(LibraryViewModel);
        public LibraryViewModel ViewModel => (LibraryViewModel)base.ViewModel;

        private void SetupXboxNavigation()
        {
            try
            {
                var mediaGridView = FindName("MediaGrid") as ListView;
                if (mediaGridView != null)
                {
                    // Remove existing handler first to avoid duplicate registrations
                    mediaGridView.ItemClick -= MediaGrid_ItemClick;
                    mediaGridView.ItemClick += MediaGrid_ItemClick;
                    Logger.LogInformation("LibraryPage: GridView item click event registered");
                }

                // Don't register UnifiedDeviceService button handlers - let BasePage handle all navigation
                // This prevents conflicts with SystemNavigationManager.BackRequested

                // Subscribe to CurrentFilter changes to update item template
                if (ViewModel != null)
                {
                    // Remove existing handler first to avoid duplicate registrations
                    ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                    ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                    Logger.LogInformation("LibraryPage: ViewModel PropertyChanged event registered");

                    // Also subscribe to MediaItems collection changes
                    ViewModel.MediaItems.CollectionChanged -= MediaItems_CollectionChanged;
                    ViewModel.MediaItems.CollectionChanged += MediaItems_CollectionChanged;
                    Logger.LogInformation("LibraryPage: MediaItems CollectionChanged event registered");

                    // Subscribe to ScrollToAlphabet event
                    ViewModel.ScrollToAlphabetRequested -= OnScrollToAlphabetRequested;
                    ViewModel.ScrollToAlphabetRequested += OnScrollToAlphabetRequested;
                    Logger.LogInformation("LibraryPage: ScrollToAlphabetRequested event registered");
                }

                // Event handlers registered successfully
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error setting up Xbox navigation in LibraryPage");
            }
        }

        private async void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(LibraryViewModel.CurrentFilter))
                {
                    Logger.LogInformation(
                        $"LibraryPage: CurrentFilter changed to {ViewModel.CurrentFilter}, updating item template");
                    // Small delay to ensure the UI has updated
                    await Task.Delay(RetryConstants.UI_SETTLE_DELAY_MS).ConfigureAwait(false);
                    await UIHelper.RunOnUIThreadAsync(() => UpdateItemTemplate(), Dispatcher, Logger);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling ViewModel property change");
            }
        }

        private async void MediaItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?.Count > 0)
                {
                    // Small delay to ensure the UI has updated with new items
                    await Task.Delay(RetryConstants.UI_RENDER_DELAY_MS).ConfigureAwait(false);
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        UpdateItemTemplate();

                        // Focus the first item if this is the initial load
                        if (ViewModel?.MediaItems?.Count > 0 && MediaGrid?.Items?.Count > 0)
                        {
                            var firstContainer = MediaGrid.ContainerFromIndex(0) as ListViewItem;
                            if (firstContainer != null)
                            {
                                firstContainer.Focus(FocusState.Keyboard);
                                Logger?.LogInformation("LibraryPage: Set focus to first media item");
                            }
                        }
                    }, Dispatcher, Logger);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling MediaItems collection change");
            }
        }

        private void MediaGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                Logger.LogInformation($"MediaGrid_ItemClick: Item clicked - {e.ClickedItem?.GetType().Name}");

                if (e.ClickedItem is BaseItemDto selectedItem)
                {
                    Logger.LogInformation(
                        $"MediaGrid_ItemClick: Selected item - Name: {selectedItem.Name}, Type: {selectedItem.Type}, ID: {selectedItem.Id}");
                    NavigateToDetailsPage(selectedItem);
                }
                else
                {
                    Logger.LogWarning(
                        $"MediaGrid_ItemClick: Clicked item is not BaseItemDto - {e.ClickedItem?.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in MediaGrid_ItemClick");
            }
        }

        private void NavigateToDetailsPage(BaseItemDto baseItem)
        {
            try
            {
                // Log back stack before navigation
                if (Frame != null)
                {
                    Logger.LogInformation(
                        $"LibraryPage - Before NavigateToDetailsPage - BackStack count: {Frame.BackStackDepth}");
                }

                NavigateToItemDetails(baseItem);

                // Log back stack after navigation
                if (Frame != null)
                {
                    Logger.LogInformation(
                        $"LibraryPage - After NavigateToDetailsPage - BackStack count: {Frame.BackStackDepth}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in NavigateToDetailsPage (or during helper execution)");
            }
        }


        protected override async Task InitializePageAsync(object parameter)
        {
            Logger?.LogInformation(
                $"LibraryPage: InitializePageAsync called with parameter: {parameter?.GetType().Name}");

            // Ensure SystemNavigationManager is properly set up for back button handling
            try
            {
                var systemNavigationManager = SystemNavigationManager.GetForCurrentView();
                systemNavigationManager.AppViewBackButtonVisibility = Frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
                Logger?.LogInformation(
                    $"LibraryPage: SystemNavigationManager configured - BackButtonVisibility: {systemNavigationManager.AppViewBackButtonVisibility}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to configure SystemNavigationManager");
            }

            if (Frame != null)
            {
                Logger?.LogInformation($"LibraryPage - BackStack count: {Frame.BackStackDepth}");
                // Log the back stack entries for debugging
                if (Frame.BackStack != null)
                {
                    for (var i = 0; i < Frame.BackStack.Count; i++)
                    {
                        var entry = Frame.BackStack[i];
                        Logger?.LogInformation($"LibraryPage - BackStack[{i}]: {entry.SourcePageType.Name}");
                    }
                }
            }
            else
            {
                Logger?.LogWarning("LibraryPage - Frame is null");
            }

            if (parameter != null)
            {
                if (parameter is BaseItemDto library)
                {
                    Logger?.LogInformation(
                        $"LibraryPage: Received library parameter: {library.Name} (ID: {library.Id}, Type: {library.CollectionType})");

                    ViewModel.SelectedLibrary = library;

                    PreferencesService?.SetValue("CurrentLibraryId", library.Id?.ToString() ?? "");
                    PreferencesService?.SetValue("CurrentLibraryName", library.Name ?? "");
                    PreferencesService?.SetValue("CurrentLibraryType", library.CollectionType?.ToString() ?? "");
                }
                else
                {
                    Logger?.LogWarning($"LibraryPage: Unexpected parameter type: {parameter.GetType().Name}");
                }
            }
            else
            {
                Logger?.LogInformation("LibraryPage: No parameter passed, trying to restore from preferences");

                // Always try to restore from preferences when no parameter is passed
                var savedLibraryId = PreferencesService?.GetValue<string>("CurrentLibraryId");
                var savedLibraryName = PreferencesService?.GetValue<string>("CurrentLibraryName");
                var savedLibraryType = PreferencesService?.GetValue<string>("CurrentLibraryType");

                if (!string.IsNullOrEmpty(savedLibraryId))
                {
                    Logger?.LogInformation(
                        $"LibraryPage: Restoring library from preferences: {savedLibraryName} (ID: {savedLibraryId}, Type: {savedLibraryType})");

                    if (!Guid.TryParse(savedLibraryId, out var libraryGuid))
                    {
                        Logger?.LogError($"Invalid library ID format: {savedLibraryId}");
                        // Invalid saved library ID, will fall through to default selection
                        return;
                    }

                    var restoredLibrary = new BaseItemDto
                    {
                        Id = libraryGuid,
                        Name = savedLibraryName,
                        Type = BaseItemDto_Type.CollectionFolder
                    };

                    if (!string.IsNullOrEmpty(savedLibraryType))
                    {
                        if (Enum.TryParse<BaseItemDto_CollectionType>(savedLibraryType, true, out var collectionType))
                        {
                            restoredLibrary.CollectionType = collectionType;
                        }
                    }

                    ViewModel.SelectedLibrary = restoredLibrary;
                }
            }

            if (ViewModel.SelectedLibrary != null)
            {
                // Setup event handlers
                SetupXboxNavigation();
                UpdateItemTemplate();

                await ViewModel.InitializeAsync().ConfigureAwait(false);
                Logger?.LogInformation(
                    $"LibraryPage: ViewModel initialized with library: {ViewModel.SelectedLibrary.Name}");

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    // Ensure the grid has focus for controller navigation
                    MediaGrid?.Focus(FocusState.Programmatic);
                }, Dispatcher, Logger);
            }
            else
            {
                // No library selected - redirect to LibrarySelectionPage
                Logger?.LogWarning("LibraryPage: No library selected, going back to LibrarySelectionPage");

                // Use GoBack if possible to preserve navigation stack
                if (NavigationService?.CanGoBack == true)
                {
                    NavigationService.GoBack();
                }
                else
                {
                    // Only navigate forward if we can't go back
                    NavigationService.Navigate(typeof(LibrarySelectionPage));
                }
            }
        }

        protected override async Task OnNavigatedBackAsync()
        {
            // Check if we're navigating back with cached data
            if (ViewModel.SelectedLibrary != null && ViewModel.MediaItems.Any())
            {
                Logger?.LogInformation(
                    "LibraryPage: Navigating back with cached library data, skipping re-initialization");

                // Re-register event handlers when navigating back
                SetupXboxNavigation();
                UpdateItemTemplate();

                // Ensure the grid has focus for controller navigation
                MediaGrid?.Focus(FocusState.Programmatic);
            }
        }

        protected override void CleanupResources()
        {
            try
            {
                // Clear focus to stop any focus animations
                if (MediaGrid != null)
                {
                    MediaGrid.Focus(FocusState.Unfocused);
                }

                // Unregister event handlers
                var mediaGridView = FindName("MediaGrid") as ListView;
                if (mediaGridView != null)
                {
                    mediaGridView.ItemClick -= MediaGrid_ItemClick;
                }

                if (ViewModel != null)
                {
                    ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                    ViewModel.MediaItems.CollectionChanged -= MediaItems_CollectionChanged;
                    ViewModel.ScrollToAlphabetRequested -= OnScrollToAlphabetRequested;
                    Logger?.LogInformation("LibraryPage: ViewModel events unregistered");
                }

                Logger?.LogInformation("LibraryPage: CleanupResources - event handlers cleaned up");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in LibraryPage CleanupResources");
            }
        }

        protected override void OnNavigatingAway()
        {
            try
            {
                // Save current library selection to preferences
                if (ViewModel?.SelectedLibrary != null)
                {
                    PreferencesService?.SetValue("CurrentLibraryId", ViewModel.SelectedLibrary.Id?.ToString() ?? "");
                    PreferencesService?.SetValue("CurrentLibraryName", ViewModel.SelectedLibrary.Name ?? "");
                    PreferencesService?.SetValue("CurrentLibraryType",
                        ViewModel.SelectedLibrary.CollectionType?.ToString() ?? "");
                    Logger?.LogInformation($"LibraryPage: Saved library selection - {ViewModel.SelectedLibrary.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in LibraryPage OnNavigatingAway");
            }
        }

        private void MediaItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger?.LogInformation("MediaItem_Click: Button click event fired");

                if (sender is Button button && button.Tag is BaseItemDto item)
                {
                    Logger?.LogInformation(
                        $"MediaItem_Click: Item found - Name: {item.Name}, Type: {item.Type}, ID: {item.Id}");
                    NavigateToDetailsPage(item);
                }
                else
                {
                    Logger?.LogWarning(
                        $"MediaItem_Click: Sender is {sender?.GetType().Name}, Tag is {(sender as Button)?.Tag?.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in MediaItem_Click");
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (NavigationService != null)
                {
                    // Pass the current library type as a parameter
                    var searchParams = new SearchPageParams
                    {
                        PreselectedFilter = ViewModel?.LibraryName ?? string.Empty
                    };

                    Logger?.LogInformation($"Navigating to SearchPage with filter: {searchParams.PreselectedFilter}");
                    NavigationService.Navigate(typeof(SearchPage), searchParams);
                }
                else
                {
                    Logger?.LogWarning("SearchButton_Click: NavigationService is null");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error navigating to SearchPage");
            }
        }

        private void SortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel != null)
                {
                    ViewModel.IsAscending = !ViewModel.IsAscending;
                }
                else
                {
                    Logger?.LogWarning("SortOrderButton_Click: ViewModel is null");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in SortOrderButton_Click");
            }
        }

        private void MusicViewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    var filter = selectedItem.Tag?.ToString();
                    if (!string.IsNullOrEmpty(filter) && ViewModel?.SetFilterCommand != null &&
                        ViewModel.SetFilterCommand.CanExecute(filter))
                    {
                        ViewModel.SetFilterCommand.Execute(filter);
                        Logger?.LogInformation($"LibraryPage: Music view changed to {filter}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in MusicViewComboBox_SelectionChanged");
            }
        }

        private void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var filterButton = FindName("FilterButton") as Button;
                var flyout = filterButton?.Flyout as Flyout;
                flyout?.Hide();

                if (ViewModel != null)
                {
                    FireAndForget(() => ViewModel.ApplyFiltersAsync(), "ApplyFilters");
                }
                else
                {
                    Logger?.LogWarning("ApplyFilterButton_Click: ViewModel is null");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ApplyFilterButton_Click");
            }
        }

        private void CancelFilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel != null)
                {
                    // Restore previous filter state
                    foreach (var kvp in _tempFilterStates)
                    {
                        kvp.Key.IsSelected = kvp.Value;
                    }

                    Logger?.LogInformation("Restored previous filter state");
                }

                var filterButton = FindName("FilterButton") as Button;
                var flyout = filterButton?.Flyout as Flyout;
                flyout?.Hide();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in CancelFilterButton_Click");
            }
        }

        private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel != null)
                {
                    ViewModel.ClearAllFilters();
                }
                else
                {
                    Logger?.LogWarning("ClearFiltersButton_Click: ViewModel is null");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ClearFiltersButton_Click");
            }
        }


        private void FilterFlyout_Opening(object sender, object e)
        {
            try
            {
                if (ViewModel != null)
                {
                    // Save current filter state for cancel functionality
                    _tempFilterStates.Clear();

                    foreach (var genre in ViewModel.Genres)
                    {
                        _tempFilterStates[genre] = genre.IsSelected;
                    }

                    foreach (var year in ViewModel.Years)
                    {
                        _tempFilterStates[year] = year.IsSelected;
                    }

                    foreach (var decade in ViewModel.Decades)
                    {
                        _tempFilterStates[decade] = decade.IsSelected;
                    }

                    foreach (var rating in ViewModel.Ratings)
                    {
                        _tempFilterStates[rating] = rating.IsSelected;
                    }

                    foreach (var resolution in ViewModel.Resolutions)
                    {
                        _tempFilterStates[resolution] = resolution.IsSelected;
                    }

                    foreach (var status in ViewModel.PlayedStatuses)
                    {
                        _tempFilterStates[status] = status.IsSelected;
                    }

                    Logger?.LogInformation("Saved current filter state for cancel functionality");
                }

                // Navigation is now handled by TabNavigation="Local" and XYFocusKeyboardNavigation="Enabled"
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FilterFlyout_Opening");
            }
        }

        private void FilterFlyout_Opened(object sender, object e)
        {
            try
            {
                Logger?.LogInformation("FilterFlyout opened");
                // Populate decade checkboxes
                PopulateDecadeCheckboxes();
                // Focus on the first interactive element when the flyout opens
                // This helps ensure proper focus navigation
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FilterFlyout_Opened");
            }
        }

        private void PopulateDecadeCheckboxes()
        {
            try
            {
                var decadesGrid = FindName("DecadesGrid") as Grid;
                if (decadesGrid == null || ViewModel?.Decades == null)
                {
                    return;
                }

                // Clear existing checkboxes
                decadesGrid.Children.Clear();

                // Add checkboxes for each decade
                int row = 0, col = 0;
                foreach (var decade in ViewModel.Decades)
                {
                    var checkBox = new CheckBox
                    {
                        Content = decade.Name,
                        IsTabStop = true,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        MinWidth = DecadeCheckboxMinWidth,
                        Margin = Application.Current.Resources["CompactControlMargin"] as Thickness? ??
                                 new Thickness(4)
                    };

                    // Set up two-way binding
                    var binding = new Binding
                    {
                        Source = decade,
                        Path = new PropertyPath("IsSelected"),
                        Mode = BindingMode.TwoWay
                    };
                    checkBox.SetBinding(CheckBox.IsCheckedProperty, binding);

                    // Set grid position
                    Grid.SetRow(checkBox, row);
                    Grid.SetColumn(checkBox, col);

                    decadesGrid.Children.Add(checkBox);

                    // Move to next position
                    col++;
                    if (col > DecadeGridColumnCount - 1)
                    {
                        col = 0;
                        row++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error populating decade checkboxes");
            }
        }

        private void FilterFlyout_Closed(object sender, object e)
        {
            try
            {
                Logger?.LogInformation("FilterFlyout closed");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FilterFlyout_Closed");
            }
        }

        private void FilterGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // With proper TabNavigation="Local" and XYFocusKeyboardNavigation="Enabled" set on the containers,
            // we shouldn't need custom key handling for basic navigation.
            // The system should handle D-pad navigation between filter sections automatically.
        }

        private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.LogInformation("Shuffle button clicked for music library");

                var hasArtists = ViewModel.MediaItems.Any(item => item.Type == BaseItemDto_Type.MusicArtist);

                if (hasArtists)
                {
                    Logger.LogInformation("Getting all songs from music library for shuffle");

                    if (UserProfileService == null)
                    {
                        Logger?.LogWarning("UserProfileService is null");
                        return;
                    }

                    var userIdStr = UserProfileService.CurrentUserId;
                    if (string.IsNullOrEmpty(userIdStr))
                    {
                        Logger?.LogWarning("No user ID available");
                        return;
                    }

                    var libraryId = ViewModel.SelectedLibrary?.Id;
                    if (!libraryId.HasValue)
                    {
                        Logger?.LogWarning("Library ID is null");
                        return;
                    }

                    if (_apiClient == null)
                    {
                        Logger?.LogWarning("API client is null");
                        return;
                    }

                    var result = await _apiClient.Items.GetAsync(config =>
                    {
                        config.QueryParameters.UserId = new Guid(userIdStr);
                        config.QueryParameters.ParentId = libraryId.Value;
                        config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Audio };
                        config.QueryParameters.SortBy = new[] { ItemSortBy.Random };
                        config.QueryParameters.Limit = ShufflePlaylistLimit;
                        config.QueryParameters.Recursive =
                            true; // Important: search recursively through all albums/artists
                        config.QueryParameters.Fields =
                            new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSources };
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (result?.Items == null || !result.Items.Any())
                    {
                        Logger.LogWarning("No songs found in music library");
                        return;
                    }

                    Logger.LogInformation($"Starting shuffle playback with {result.Items.Count} songs");

                    // Use MusicPlayerService for music playback
                    if (_musicPlayerService != null)
                    {
                        await _musicPlayerService.PlayItems(result.Items.ToList()).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogError("MusicPlayerService not available");
                    }
                }
                else
                {
                    var allMusicItems = ViewModel.MediaItems.Where(item =>
                        item.Type == BaseItemDto_Type.Audio ||
                        item.MediaType == BaseItemDto_MediaType.Audio).ToList();

                    if (!allMusicItems.Any())
                    {
                        Logger.LogWarning("No music items found to shuffle");
                        return;
                    }

                    var random = new Random();
                    var shuffledItems = allMusicItems.OrderBy(x => random.Next()).ToList();

                    // Use MusicPlayerService for music playback
                    if (_musicPlayerService != null)
                    {
                        await _musicPlayerService.PlayItems(shuffledItems).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogError("MusicPlayerService is not available");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error shuffling music library");
            }
        }

        private void MusicItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
        }

        private async void InstantMix_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is MenuFlyoutItem menuItem && menuItem.Tag is BaseItemDto seedItem)
                {
                    Logger.LogInformation($"Creating instant mix for: {seedItem.Name}");

                    if (!seedItem.Id.HasValue)
                    {
                        Logger.LogWarning("Seed item has no ID");
                        return;
                    }

                    var instantMixItems = await GetInstantMixAsync(seedItem.Id.Value).ConfigureAwait(false);

                    if (instantMixItems == null || !instantMixItems.Any())
                    {
                        Logger.LogWarning("No instant mix items returned");
                        return;
                    }

                    // Use MusicPlayerService for music playback
                    if (_musicPlayerService != null)
                    {
                        await _musicPlayerService.PlayItems(instantMixItems).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogError("MusicPlayerService is not available");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error creating instant mix");
            }
        }

        private async Task<List<BaseItemDto>> GetInstantMixAsync(Guid itemId)
        {
            try
            {
                var userIdStr = UserProfileService.CurrentUserId;
                if (!Guid.TryParse(userIdStr, out var userIdGuid))
                {
                    Logger.LogError("Invalid user ID");
                    return new List<BaseItemDto>();
                }

                var result = await _apiClient.Items[new Guid(itemId.ToString())].InstantMix.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.Limit = 50;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                }, CancellationToken.None).ConfigureAwait(false);

                return result?.Items?.ToList() ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error getting instant mix for item {itemId}");
                return new List<BaseItemDto>();
            }
        }

        private void GoToAlbum_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is MenuFlyoutItem menuItem && menuItem.Tag is BaseItemDto item)
                {
                    if (item.AlbumId.HasValue)
                    {
                        Logger.LogInformation($"Navigating to album: {item.Album}");
                        NavigationService.Navigate(typeof(AlbumDetailsPage), item.AlbumId.Value.ToString());
                    }
                    else if (item.Type == BaseItemDto_Type.MusicAlbum && item.Id.HasValue)
                    {
                        Logger.LogInformation($"Navigating to album: {item.Name}");
                        NavigationService.Navigate(typeof(AlbumDetailsPage), item.Id.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error navigating to album");
            }
        }

        private void GoToArtist_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is MenuFlyoutItem menuItem && menuItem.Tag is BaseItemDto item)
                {
                    if (item.AlbumArtists?.Any() == true)
                    {
                        var firstAlbumArtist = item.AlbumArtists.FirstOrDefault();
                        if (firstAlbumArtist?.Id.HasValue == true)
                        {
                            var artistId = firstAlbumArtist.Id.Value.ToString();
                            var artistName = firstAlbumArtist.Name;
                            Logger.LogInformation($"Navigating to artist: {artistName}");
                            NavigationService.Navigate(typeof(ArtistDetailsPage), artistId);
                        }
                    }
                    else if (item.ArtistItems?.Any() == true)
                    {
                        var firstArtistItem = item.ArtistItems.FirstOrDefault();
                        if (firstArtistItem?.Id != null)
                        {
                            var artistId = firstArtistItem.Id;
                            var artistName = firstArtistItem.Name;
                            Logger.LogInformation($"Navigating to artist: {artistName}");
                            NavigationService.Navigate(typeof(ArtistDetailsPage), artistId);
                        }
                    }
                    else if (item.Type == BaseItemDto_Type.MusicArtist && item.Id.HasValue)
                    {
                        Logger.LogInformation($"Navigating to artist: {item.Name}");
                        NavigationService.Navigate(typeof(ArtistDetailsPage), item.Id.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error navigating to artist");
            }
        }

        private void UpdateItemTemplate()
        {
            try
            {
                if (ViewModel?.SelectedLibrary == null || MediaGrid == null)
                {
                    return;
                }

                // Sync MusicViewComboBox with CurrentFilter
                if (MusicViewComboBox != null &&
                    ViewModel.SelectedLibrary.CollectionType == BaseItemDto_CollectionType.Music)
                {
                    foreach (ComboBoxItem item in MusicViewComboBox.Items)
                    {
                        if (item.Tag?.ToString() == ViewModel.CurrentFilter)
                        {
                            MusicViewComboBox.SelectedItem = item;
                            break;
                        }
                    }
                }

                var itemsPanel = MediaGrid.ItemsPanelRoot as ItemsWrapGrid;

                if (ViewModel.SelectedLibrary.CollectionType == BaseItemDto_CollectionType.Music)
                {
                    // For music libraries, use album template
                    MediaGrid.ItemTemplate = App.Current.Resources["CompactAlbumTemplate"] as DataTemplate;
                    if (itemsPanel != null)
                    {
                        itemsPanel.ItemWidth = 150;
                        // Adjust height based on current filter
                        if (ViewModel.CurrentFilter == "Albums")
                        {
                            itemsPanel.ItemHeight = 180; // 130px image + 50px for two lines of text
                        }
                        else // Artists
                        {
                            itemsPanel.ItemHeight = 155; // 130px image + 25px for one line of text + padding
                        }
                    }
                }
                else
                {
                    // For movies, TV shows, etc., use poster template
                    MediaGrid.ItemTemplate = App.Current.Resources["CompactPosterTemplate"] as DataTemplate;
                    if (itemsPanel != null)
                    {
                        itemsPanel.ItemWidth = 150;
                        itemsPanel.ItemHeight = 240;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error updating item template");
            }
        }

        private async void OnScrollToAlphabetRequested(object sender, string letter)
        {
            try
            {
                Logger?.LogInformation($"Scrolling to letter: {letter}");

                await UIHelper.RunOnUIThreadAsync(async () =>
                {
                    try
                    {
                        // Get the ScrollViewer from the ListView
                        var scrollViewer = GetScrollViewer(MediaGrid);
                        if (scrollViewer == null)
                        {
                            Logger?.LogWarning("Could not find ScrollViewer in MediaGrid");
                            return;
                        }

                        // Find the first item that starts with the selected letter
                        var targetIndex = -1;
                        for (var i = 0; i < ViewModel.MediaItems.Count; i++)
                        {
                            var item = ViewModel.MediaItems[i];
                            var itemName = item.Name ?? string.Empty;

                            // Remove "The " from the beginning for sorting purposes
                            var sortName = itemName;
                            if (sortName.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && sortName.Length > 4)
                            {
                                sortName = sortName.Substring(4).TrimStart();
                            }

                            if (letter == "#")
                            {
                                // Check if name starts with a number or special character
                                if (sortName.Length > 0 && !char.IsLetter(sortName[0]))
                                {
                                    targetIndex = i;
                                    break;
                                }
                            }
                            else if (sortName.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
                            {
                                targetIndex = i;
                                break;
                            }
                        }

                        if (targetIndex >= 0)
                        {
                            // Scroll to the item
                            MediaGrid.ScrollIntoView(ViewModel.MediaItems[targetIndex],
                                ScrollIntoViewAlignment.Leading);
                            Logger?.LogInformation(
                                $"Scrolled to item at index {targetIndex}: {ViewModel.MediaItems[targetIndex].Name}");
                        }
                        else
                        {
                            Logger?.LogInformation($"No items found starting with letter: {letter}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError(ex, "Error during scroll operation");
                    }
                }, Dispatcher, Logger);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in OnScrollToAlphabetRequested");
            }
        }

        private ScrollViewer GetScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = GetScrollViewer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }

    public class LibraryItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ArtistTemplate { get; set; }
        public DataTemplate AlbumTemplate { get; set; }
        public DataTemplate DefaultTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is BaseItemDto baseItem)
            {
                if (baseItem.Type == BaseItemDto_Type.MusicArtist)
                {
                    return ArtistTemplate;
                }

                if (baseItem.Type == BaseItemDto_Type.MusicAlbum)
                {
                    return AlbumTemplate;
                }
            }

            return DefaultTemplate;
        }
    }
}
