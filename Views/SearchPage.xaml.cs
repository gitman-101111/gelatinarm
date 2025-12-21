using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Controls;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;

namespace Gelatinarm.Views
{
    public sealed partial class
        SearchPage : BasePage, INotifyPropertyChanged // INotifyPropertyChanged for local properties
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly Guid? _userIdGuid;
        private readonly IUserProfileService _userProfileService;
        private BaseItemKind[] _currentFilter;
        private Task<SearchHintResult> _currentSearchTask;
        private string _emptyStateMessage = "Try adjusting your search term or filters";

        private string _emptyStateTitle = "No results found";
        private Dictionary<string, List<BaseItemDto>> _groupedResults;
        private readonly object _groupedResultsLock = new object();
        private string _lastSearchTerm;
        private CancellationTokenSource _searchCancellationTokenSource;
        private readonly object _cancellationTokenLock = new object();
        private ObservableCollection<BaseItemDto> _searchResults;

        public SearchPage() : base(typeof(SearchPage))
        {
            try
            {
                InitializeComponent();
            }
#if DEBUG
            catch (Exception ex)
            {
                // If InitializeComponent fails, we can't proceed
                Debug.WriteLine($"SearchPage: InitializeComponent failed - {ex.Message}");
                throw;
            }
#else
            catch
            {
                // If InitializeComponent fails, we can't proceed
                throw;
            }
#endif

            try
            {
                _apiClient = GetRequiredService<JellyfinApiClient>();
                _userProfileService = UserProfileService; // Use from BasePage
                try
                {
                    ControllerInputHelper.ConfigurePageForController(this, SearchBox, Logger);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning(ex, "Failed to configure controller input");
                    // Continue without controller configuration
                }

                // Initialize user ID
                try
                {
                    var userIdString = _userProfileService?.CurrentUserId;
                    if (!string.IsNullOrEmpty(userIdString) && Guid.TryParse(userIdString, out var parsedGuid))
                    {
                        _userIdGuid = parsedGuid;
                    }
                    else
                    {
                        Logger?.LogWarning("SearchPage: UserId is not valid.");
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "Failed to get user ID");
                }
                _searchResults = new ObservableCollection<BaseItemDto>();
                lock (_groupedResultsLock)
                {
                    _groupedResults = new Dictionary<string, List<BaseItemDto>>();
                }

                _currentFilter = null;
                UpdateFilterButtons();

                Logger?.LogInformation("SearchPage: Constructor completed successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "SearchPage Constructor Error");
#if DEBUG
                Debug.WriteLine($"SearchPage: Constructor error - {ex.Message}");
#endif
                throw;
            }
        }

        public ObservableCollection<BaseItemDto> SearchResults
        {
            get => _searchResults;
            set
            {
                _searchResults =
                    value; // Directly set, assuming UI updates through other means or this setter is not primary path
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSearchResults));
            }
        }

        public bool HasSearchResults => _searchResults?.Any() == true;

        public string EmptyStateTitle
        {
            get => _emptyStateTitle;
            set
            {
                _emptyStateTitle = value;
                OnPropertyChanged();
            }
        }

        public string EmptyStateMessage
        {
            get => _emptyStateMessage;
            set
            {
                _emptyStateMessage = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected override void CancelOngoingOperations()
        {
            // Cancel any ongoing search operations
            lock (_cancellationTokenLock)
            {
                _searchCancellationTokenSource?.Cancel();
                _searchCancellationTokenSource?.Dispose();
                _searchCancellationTokenSource = null;
            }
        }

        protected override void CleanupResources()
        {
            // Clean up event handlers when navigating away from the page
            if (GroupedResultsPanel != null)
            {
                CleanupGroupedResultsPanel();
            }
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            // Check if we have navigation parameters
            if (parameter is SearchPageParams searchParams && !string.IsNullOrEmpty(searchParams.PreselectedFilter))
            {
                Logger?.LogInformation(
                    $"SearchPage navigated to with preselected filter: {searchParams.PreselectedFilter}");

                // Set the filter based on the parameter
                switch (searchParams.PreselectedFilter.ToLower())
                {
                    case "movies":
                        _currentFilter = new[] { BaseItemKind.Movie };
                        break;
                    case "series":
                    case "tvshows":
                    case "tv shows":
                        _currentFilter = new[] { BaseItemKind.Series };
                        break;
                    case "music":
                        _currentFilter = new[]
                        {
                            BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist
                        };
                        break;
                    case "boxsets":
                    case "collections":
                        _currentFilter = new[] { BaseItemKind.BoxSet };
                        break;
                    case "all":
                    default:
                        _currentFilter = null;
                        break;
                }

                UpdateFilterButtons();
            }

            await Task.CompletedTask;
        }

        private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == VirtualKey.Enter && sender is TextBox textBox)
                {
                    // Remove focus from the TextBox to dismiss the virtual keyboard
                    if (textBox.IsEnabled)
                    {
                        textBox.IsEnabled = false;
                        textBox.IsEnabled = true;
                    }

                    if (string.IsNullOrWhiteSpace(textBox.Text))
                    {
                        _searchResults?.Clear();

                        if (NoResultsPanel != null)
                        {
                            NoResultsPanel.Visibility = Visibility.Collapsed;
                        }

                        if (ResultsCountText != null)
                        {
                            ResultsCountText.Visibility = Visibility.Collapsed;
                        }

                        return;
                    }

                    await PerformSearch(textBox.Text).ConfigureAwait(true);

                    // Focus on the search results if any exist
                    if (_searchResults?.Any() == true && GroupedResultsPanel?.Children.Any() == true)
                    {
                        // Try to focus on the first ListView in the results
                        foreach (var child in GroupedResultsPanel?.Children ?? Enumerable.Empty<UIElement>())
                        {
                            if (child is ListView listView && listView.Items.Any())
                            {
                                listView.Focus(FocusState.Programmatic);
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in SearchBox_KeyDown");
            }
        }

        private async Task PerformSearch(string searchTerm)
        {
            if (!_userIdGuid.HasValue)
            {
                Logger?.LogWarning("PerformSearch: UserId not available.");
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.IsLoading = false;
                    }
                }, Dispatcher, Logger);
                return;
            }

            if (_apiClient == null)
            {
                Logger?.LogWarning("PerformSearch: API client is null.");
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.IsLoading = false;
                    }
                }, Dispatcher, Logger);
                return;
            }

            try
            {
                // Cancel any previous search
                CancellationToken cancellationToken;
                lock (_cancellationTokenLock)
                {
                    _searchCancellationTokenSource?.Cancel();
                    _searchCancellationTokenSource?.Dispose();

                    if (_currentSearchTask != null && !_currentSearchTask.IsCompleted)
                    {
                        return;
                    }

                    // Create new cancellation token with timeout from constants
                    _searchCancellationTokenSource =
                        new CancellationTokenSource(TimeSpan.FromSeconds(RetryConstants.SEARCH_TIMEOUT_SECONDS));
                    cancellationToken = _searchCancellationTokenSource.Token;
                }

                _lastSearchTerm = searchTerm;

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.IsLoading = true;
                    }

                    if (NoResultsPanel != null)
                    {
                        NoResultsPanel.Visibility = Visibility.Collapsed;
                    }
                }, Dispatcher, Logger);

                if (_apiClient == null)
                {
                    Logger?.LogError("API client not available");
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        if (LoadingOverlay != null)
                        {
                            LoadingOverlay.IsLoading = false;
                        }
                    }, Dispatcher, Logger);
                    return;
                }

                // When filtering, get all results; otherwise get a reasonable amount for grouping
                var searchLimit = _currentFilter != null && _currentFilter.Length > 0 ? 200 : 150;

                _currentSearchTask = _apiClient.Search.Hints.GetAsync(config =>
                {
                    config.QueryParameters.SearchTerm = searchTerm;
                    config.QueryParameters.UserId = _userIdGuid.Value;
                    config.QueryParameters.Limit = searchLimit;
                    if (_currentFilter != null && _currentFilter.Length > 0)
                    {
                        config.QueryParameters.IncludeItemTypes = _currentFilter;
                    }
                    else
                    {
                        // Include all major content types when no filter is active
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Person,
                            BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist,
                            BaseItemKind.BoxSet, BaseItemKind.Season
                        };
                    }
                });

                var searchHintResult = await _currentSearchTask.ConfigureAwait(false);

                // Process items outside of UI thread
                var items = new List<BaseItemDto>();
                if (searchHintResult?.SearchHints != null && searchHintResult.TotalRecordCount > 0)
                {
                    foreach (var hint in searchHintResult.SearchHints)
                    {
                        if (!hint.Id.HasValue || !Guid.TryParse(hint.Id.Value.ToString(), out var parsedHintId))
                        {
                            continue;
                        }

                        try
                        {
                            var item = await _apiClient.Items[parsedHintId].GetAsync(config =>
                            {
                                config.QueryParameters.UserId = _userIdGuid.Value;
                            }).ConfigureAwait(false);
                            if (item != null)
                            {
                                items.Add(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogWarning(ex, $"Failed to load item {parsedHintId}");
                        }
                    }
                }

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    _searchResults?.ReplaceAll(items);
                    lock (_groupedResultsLock)
                    {
                        _groupedResults?.Clear();
                    }

                    if (GroupedResultsPanel != null)
                    {
                        CleanupGroupedResultsPanel();
                        GroupedResultsPanel?.Children.Clear();
                    }

                    if (items.Any())
                    {
                        GroupItemsByType(items);
                        CreateGroupedUI();
                    }

                    var resultCount = _searchResults?.Count ?? 0;

                    if (NoResultsPanel != null)
                    {
                        NoResultsPanel.Visibility = resultCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (GroupedResultsPanel != null)
                    {
                        GroupedResultsPanel.Visibility = resultCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (resultCount > 0)
                    {
                        if (ResultsCountText != null)
                        {
                            ResultsCountText.Text = resultCount == 1 ? "1 result" : $"{resultCount} results";
                            ResultsCountText.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        if (ResultsCountText != null)
                        {
                            ResultsCountText.Visibility = Visibility.Collapsed;
                        }

                        UpdateEmptyState(searchTerm);
                    }
                }, Dispatcher, Logger);
            }
            catch (TaskCanceledException)
            {
                Logger?.LogInformation(
                    $"Search operation timed out after {RetryConstants.SEARCH_TIMEOUT_SECONDS} seconds");

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    _emptyStateTitle = "Search timed out";
                    _emptyStateMessage = "The search took too long. Please try again with a more specific term.";
                    UpdateEmptyState(_lastSearchTerm);
                }, Dispatcher, Logger);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, $"Search failed: {ex.Message}");
            }
            finally
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.IsLoading = false;
                    }
                }, Dispatcher, Logger);
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button)
                {
                    switch (button.Name)
                    {
                        case "AllButton": _currentFilter = null; break;
                        case "MoviesButton": _currentFilter = new[] { BaseItemKind.Movie }; break;
                        case "ShowsButton": _currentFilter = new[] { BaseItemKind.Series }; break;
                        case "EpisodesButton": _currentFilter = new[] { BaseItemKind.Episode }; break;
                        case "MusicButton":
                            _currentFilter = new[]
                            {
                                BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist
                            }; break;
                    }

                    UpdateFilterButtons();
                    if (!string.IsNullOrEmpty(_lastSearchTerm))
                    {
                        FireAndForget(() => PerformSearch(_lastSearchTerm));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FilterButton_Click");
            }
        }

        private void UpdateFilterButtons()
        {
            try
            {
                if (Application.Current?.Resources == null)
                {
                    Logger?.LogWarning("Application resources not available");
                    return;
                }

                var activeStyle = Application.Current.Resources["ActiveFilterButtonStyle"] as Style;
                var normalStyle = Application.Current.Resources["FilterButtonStyle"] as Style;

                if (activeStyle == null || normalStyle == null)
                {
                    Logger?.LogWarning("Filter button styles not found in resources");
                    return;
                }

                if (AllButton != null)
                {
                    AllButton.Style = _currentFilter == null ? activeStyle : normalStyle;
                }

                if (MoviesButton != null)
                {
                    MoviesButton.Style =
                        _currentFilter?.Contains(BaseItemKind.Movie) == true ? activeStyle : normalStyle;
                }

                if (ShowsButton != null)
                {
                    ShowsButton.Style = _currentFilter?.Contains(BaseItemKind.Series) == true
                        ? activeStyle
                        : normalStyle;
                }

                if (EpisodesButton != null)
                {
                    EpisodesButton.Style = _currentFilter?.Contains(BaseItemKind.Episode) == true
                        ? activeStyle
                        : normalStyle;
                }

                if (MusicButton != null)
                {
                    MusicButton.Style = _currentFilter?.Contains(BaseItemKind.MusicAlbum) == true
                        ? activeStyle
                        : normalStyle;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in UpdateFilterButtons");
            }
        }

        private void UpdateEmptyState(string searchTerm)
        {
            var filterName = GetCurrentFilterName();

            if (!string.IsNullOrEmpty(filterName) && filterName != "All")
            {
                EmptyStateTitle = $"No {filterName.ToLower()} found";
                EmptyStateMessage = $"No {filterName.ToLower()} matching '{searchTerm}'";
            }
            else
            {
                EmptyStateTitle = "No results found";
                EmptyStateMessage = $"No items matching '{searchTerm}'";
            }
        }

        private string GetCurrentFilterName()
        {
            if (_currentFilter == null || _currentFilter.Length == 0)
            {
                return "All";
            }

            if (_currentFilter.Contains(BaseItemKind.Movie))
            {
                return "Movies";
            }

            if (_currentFilter.Contains(BaseItemKind.Series))
            {
                return "TV Shows";
            }

            if (_currentFilter.Contains(BaseItemKind.Episode))
            {
                return "Episodes";
            }

            return "All";
        }

        private void GroupItemsByType(List<BaseItemDto> items)
        {
            var newGroupedResults = new Dictionary<string, List<BaseItemDto>>();
            foreach (var item in items)
            {
                var groupName = GetGroupNameForType(item.Type);
                if (!newGroupedResults.ContainsKey(groupName))
                {
                    newGroupedResults[groupName] = new List<BaseItemDto>();
                }

                newGroupedResults[groupName].Add(item);
            }

            lock (_groupedResultsLock)
            {
                _groupedResults = newGroupedResults;
            }
        }

        private string GetGroupNameForType(BaseItemDto_Type? type)
        {
            switch (type)
            {
                case BaseItemDto_Type.Movie: return "Movies";
                case BaseItemDto_Type.Series: return "TV Shows";
                case BaseItemDto_Type.Episode: return "Episodes";
                case BaseItemDto_Type.Person: return "People";
                case BaseItemDto_Type.MusicAlbum: return "Albums";
                case BaseItemDto_Type.Audio: return "Songs";
                case BaseItemDto_Type.MusicArtist: return "Artists";
                case BaseItemDto_Type.BoxSet: return "Collections";
                case BaseItemDto_Type.Season: return "Seasons";
                default: return "Other";
            }
        }

        private void CleanupGroupedResultsPanel()
        {
            // Unsubscribe event handlers from all controls before clearing
            foreach (var child in GroupedResultsPanel.Children)
            {
                if (child is ListView listView)
                {
                    listView.ItemClick -= ListView_ItemClick;
                    // Note: We can't unsubscribe anonymous event handlers for GotFocus
                    // but they will be garbage collected when the ListView is removed
                }
                else if (child is Button button && button.Content?.ToString()?.StartsWith("Show all") == true)
                {
                    button.Click -= ShowMoreButton_Click;
                }
            }
        }

        private void CreateGroupedUI()
        {
            try
            {
                if (Resources == null)
                {
                    Logger?.LogWarning("Page resources not available");
                    return;
                }

                if (GroupedResultsPanel == null)
                {
                    Logger?.LogWarning("GroupedResultsPanel is null");
                    return;
                }

                var defaultTemplate = Resources["SearchItemTemplate"] as DataTemplate;
                var episodeTemplate = Resources["EpisodeItemTemplate"] as DataTemplate;
                var musicTemplate = Resources["MusicItemTemplate"] as DataTemplate;

                if (defaultTemplate == null)
                {
                    Logger?.LogWarning("Default search item template not found");
                    return;
                }

                Dictionary<string, List<BaseItemDto>> groupedResultsCopy;
                lock (_groupedResultsLock)
                {
                    groupedResultsCopy = _groupedResults != null ? new Dictionary<string, List<BaseItemDto>>(_groupedResults) : new Dictionary<string, List<BaseItemDto>>();
                }
                var sortedGroups = groupedResultsCopy.OrderBy(g => GetGroupPriority(g.Key));
                var isFiltered = _currentFilter != null && _currentFilter.Length > 0;

                foreach (var group in sortedGroups)
                {
                    // Create header with count
                    var headerPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 8)
                    };

                    var headerText = new TextBlock
                    {
                        Text = group.Key,
                        Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
                        FontSize = 16
                    };
                    headerPanel.Children.Add(headerText);

                    if (group.Value.Any())
                    {
                        var countText = new TextBlock
                        {
                            Text = $" ({group.Value.Count})",
                            Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
                            FontSize = 16,
                            Opacity = 0.7
                        };
                        headerPanel.Children.Add(countText);
                    }

                    GroupedResultsPanel.Children.Add(headerPanel);

                    DataTemplate template;
                    if (group.Key == "Episodes")
                    {
                        template = episodeTemplate;
                    }
                    else if (group.Key == "Albums" || group.Key == "Songs" || group.Key == "Artists")
                    {
                        template = musicTemplate;
                    }
                    else
                    {
                        template = defaultTemplate;
                    }

                    // Limit items to 15 when not filtered, show all when filtered
                    var itemsToShow = isFiltered ? group.Value : group.Value.Take(15).ToList();
                    var hasMore = !isFiltered && group.Value.Count > 15;

                    var listView = new ListView
                    {
                        ItemsSource = itemsToShow,
                        ItemTemplate = template,
                        SelectionMode = ListViewSelectionMode.None,
                        IsItemClickEnabled = true,
                        Margin = new Thickness(0, 0, hasMore ? 12 : 24, 0),
                        Padding = new Thickness(0),
                        IsTabStop = true
                    };

                    int maxColumns;
                    if (group.Key == "Episodes")
                    {
                        maxColumns = 5;
                    }
                    else if (group.Key == "Albums" || group.Key == "Songs" || group.Key == "Artists")
                    {
                        maxColumns = 8;
                    }
                    else
                    {
                        maxColumns = 8;
                    }

                    var itemsPanelXaml = $@"
                    <ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                        <ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='{maxColumns}'/>
                    </ItemsPanelTemplate>";
                    listView.ItemsPanel = XamlReader.Load(itemsPanelXaml) as ItemsPanelTemplate;

                    var itemContainerStyle = new Style(typeof(ListViewItem));
                    itemContainerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty,
                        HorizontalAlignment.Stretch));
                    itemContainerStyle.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
                    itemContainerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
                    itemContainerStyle.Setters.Add(new Setter(FocusVisualMarginProperty, new Thickness(-2)));
                    listView.ItemContainerStyle = itemContainerStyle;

                    listView.ItemClick += ListView_ItemClick;

                    // Add focus event handlers to handle cross-list navigation
                    listView.GotFocus += (s, e) =>
                    {
                        Logger?.LogInformation($"ListView for {group.Key} got focus");
                    };

                    GroupedResultsPanel.Children.Add(listView);

                    // Add "Show more" button if there are more items
                    if (hasMore)
                    {
                        var showMoreButton = new Button
                        {
                            Content = $"Show all {group.Value.Count} {group.Key.ToLower()}",
                            Style = Application.Current.Resources["FilterButtonStyle"] as Style,
                            Margin = new Thickness(0, 0, 0, 24),
                            Tag = group.Key // Store the group name for filter selection
                        };
                        showMoreButton.Click += ShowMoreButton_Click;
                        GroupedResultsPanel.Children.Add(showMoreButton);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in CreateGroupedUI");
            }
        }

        private int GetGroupPriority(string groupName)
        {
            switch (groupName)
            {
                case "Movies": return 1;
                case "TV Shows": return 2;
                case "Episodes": return 3;
                case "People": return 4;
                case "Albums": return 5;
                case "Artists": return 6;
                case "Songs": return 7;
                case "Collections": return 8;
                case "Seasons": return 9;
                default: return 10;
            }
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e?.ClickedItem is BaseItemDto item)
                {
                    NavigateToItemDetails(item);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ListView_ItemClick");
            }
        }

        private void ShowMoreButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string groupName)
                {
                    // Set the appropriate filter based on the group
                    switch (groupName)
                    {
                        case "Movies":
                            _currentFilter = new[] { BaseItemKind.Movie };
                            break;
                        case "TV Shows":
                            _currentFilter = new[] { BaseItemKind.Series };
                            break;
                        case "Episodes":
                            _currentFilter = new[] { BaseItemKind.Episode };
                            break;
                        case "Albums":
                            _currentFilter = new[] { BaseItemKind.MusicAlbum };
                            break;
                        case "Songs":
                            _currentFilter = new[] { BaseItemKind.Audio };
                            break;
                        case "Artists":
                            _currentFilter = new[] { BaseItemKind.MusicArtist };
                            break;
                        default:
                            return;
                    }

                    UpdateFilterButtons();

                    // Re-run the search with the filter applied
                    if (!string.IsNullOrEmpty(_lastSearchTerm))
                    {
                        FireAndForget(() => PerformSearch(_lastSearchTerm));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ShowMoreButton_Click");
            }
        }

        private void ResultsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e?.ClickedItem is BaseItemDto baseItemDto && baseItemDto.Id.HasValue)
                {
                    NavigateToItemDetails(baseItemDto);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ResultsGrid_ItemClick");
            }
        }
    }
}
