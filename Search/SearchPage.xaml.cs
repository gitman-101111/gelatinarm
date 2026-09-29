using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Search
{
    public sealed partial class SearchPage : BasePage, INotifyPropertyChanged
    {
        // The result rows, in display order: the type each holds, the filter its "Show all" button
        // applies, its tile template and how many tiles wide it runs
        private static readonly ResultGroup[] ResultGroups =
        {
            new ResultGroup("Movies", BaseItemDto_Type.Movie, BaseItemKind.Movie, "SearchItemTemplate", 8),
            new ResultGroup("TV Shows", BaseItemDto_Type.Series, BaseItemKind.Series, "SearchItemTemplate", 8),
            new ResultGroup("Episodes", BaseItemDto_Type.Episode, BaseItemKind.Episode, "EpisodeItemTemplate", 5),
            new ResultGroup("People", BaseItemDto_Type.Person, BaseItemKind.Person, "SearchItemTemplate", 8),
            new ResultGroup("Albums", BaseItemDto_Type.MusicAlbum, BaseItemKind.MusicAlbum, "MusicItemTemplate", 8),
            new ResultGroup("Artists", BaseItemDto_Type.MusicArtist, BaseItemKind.MusicArtist, "MusicItemTemplate", 8),
            new ResultGroup("Songs", BaseItemDto_Type.Audio, BaseItemKind.Audio, "MusicItemTemplate", 8),
            new ResultGroup("Collections", BaseItemDto_Type.BoxSet, BaseItemKind.BoxSet, "SearchItemTemplate", 8),
            new ResultGroup("Seasons", BaseItemDto_Type.Season, BaseItemKind.Season, "SearchItemTemplate", 8)
        };

        private readonly JellyfinApiClient _apiClient;
        private BaseItemKind[] _currentFilter;
        private string _emptyStateMessage = "Try adjusting your search term or filters";

        private string _emptyStateTitle = "No results found";
        private string _lastSearchTerm;
        private CancellationTokenSource _searchCancellationTokenSource;

        protected override Control InitialFocusControl => SearchBox;

        public SearchPage() : base(typeof(SearchPage))
        {
            InitializeComponent();

            _apiClient = GetRequiredService<JellyfinApiClient>();
            UpdateFilterButtons();
        }

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
            AsyncHelper.Cancel(ref _searchCancellationTokenSource);
        }

        protected override Task InitializePageAsync(object parameter)
        {
            if (parameter is SearchPageParams searchParams)
            {
                Logger.LogDebug("SearchPage opened from a {LibraryType} library", searchParams.LibraryType);

                _currentFilter = searchParams.LibraryType switch
                {
                    BaseItemDto_CollectionType.Movies => new[] { BaseItemKind.Movie },
                    BaseItemDto_CollectionType.Tvshows => new[] { BaseItemKind.Series },
                    BaseItemDto_CollectionType.Music => new[] { BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist },
                    BaseItemDto_CollectionType.Boxsets => new[] { BaseItemKind.BoxSet },
                    _ => null
                };

                UpdateFilterButtons();
            }

            return Task.CompletedTask;
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
                        _lastSearchTerm = null;
                        CleanupGroupedResultsPanel();
                        GroupedResultsPanel.Children.Clear();
                        NoResultsPanel.Visibility = Visibility.Collapsed;
                        ResultsCountText.Visibility = Visibility.Collapsed;
                        return;
                    }

                    await PerformSearchAsync(textBox.Text);

                    // The first row of results takes focus, away from the keyboard
                    GroupedResultsPanel.Children.OfType<ListView>().FirstOrDefault(l => l.Items.Any())
                        ?.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("SearchBox_KeyDown", ErrorCategory.User), false);
            }
        }

        private async Task PerformSearchAsync(string searchTerm)
        {
            // The page is kept for the whole session (NavigationCacheMode Required), so the
            // user is read per search: a profile switch changes it.
            var userIdGuid = UserProfileService.GetCurrentUserGuid();
            if (!userIdGuid.HasValue)
            {
                Logger.LogWarning("PerformSearchAsync: user ID not available.");
                return;
            }

            // The newest search wins: a filter pressed while a search loads re-runs it
            var searchCts = AsyncHelper.Supersede(ref _searchCancellationTokenSource);
            searchCts.CancelAfter(TimeSpan.FromSeconds(SearchConstants.SearchTimeoutSeconds));
            var token = searchCts.Token;

            try
            {
                _lastSearchTerm = searchTerm;

                await UiHelper.RunOnUIThreadAsync(() =>
                {
                    LoadingOverlay.IsLoading = true;
                    NoResultsPanel.Visibility = Visibility.Collapsed;
                }, Logger, Dispatcher);

                // A filtered search shows everything it finds; an unfiltered one is grouped and cut per row
                var isFiltered = _currentFilter != null;
                var searchLimit = isFiltered ? 200 : 150;

                var searchHintResult = await _apiClient.Search.Hints.GetAsync(config =>
                {
                    config.QueryParameters.SearchTerm = searchTerm;
                    config.QueryParameters.UserId = userIdGuid.Value;
                    config.QueryParameters.Limit = searchLimit;
                    if (isFiltered)
                    {
                        config.QueryParameters.IncludeItemTypes = _currentFilter;
                    }
                    else
                    {
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Person,
                            BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist,
                            BaseItemKind.BoxSet, BaseItemKind.Season
                        };
                    }
                }, token).ConfigureAwait(false);

                var hintIds = searchHintResult?.SearchHints?.Where(h => h.Id.HasValue).Select(h => h.Id.Value)
                                  .Distinct().ToList() ?? new List<Guid>();
                var items = await LoadHintedItemsAsync(hintIds, userIdGuid.Value, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                await UiHelper.RunOnUIThreadAsync(() =>
                {
                    CleanupGroupedResultsPanel();
                    GroupedResultsPanel.Children.Clear();

                    if (items.Count > 0)
                    {
                        CreateGroupedUi(items);
                    }

                    var resultCount = items.Count;

                    NoResultsPanel.Visibility = resultCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                    GroupedResultsPanel.Visibility = resultCount > 0 ? Visibility.Visible : Visibility.Collapsed;

                    if (resultCount > 0)
                    {
                        ResultsCountText.Text = resultCount == 1 ? "1 result" : $"{resultCount} results";
                        ResultsCountText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ResultsCountText.Visibility = Visibility.Collapsed;
                        UpdateEmptyState(searchTerm);
                    }
                }, Logger, Dispatcher);
            }
            catch (OperationCanceledException ex) when (searchCts == _searchCancellationTokenSource)
            {
                Logger.LogInformation(ex,
                    "Search operation timed out after {SearchTimeoutSeconds} seconds", SearchConstants.SearchTimeoutSeconds);

                await UiHelper.RunOnUIThreadAsync(() =>
                {
                    EmptyStateTitle = "Search timed out";
                    EmptyStateMessage = "The search took too long. Please try again with a more specific term.";
                    ResultsCountText.Visibility = Visibility.Collapsed;
                    GroupedResultsPanel.Visibility = Visibility.Collapsed;
                    NoResultsPanel.Visibility = Visibility.Visible;
                }, Logger, Dispatcher);
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer search, or the page was left
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("PerformSearchAsync", ErrorCategory.User), false);
            }
            finally
            {
                // A newer search owns the overlay now
                if (_searchCancellationTokenSource == searchCts || _searchCancellationTokenSource == null)
                {
                    await UiHelper.RunOnUIThreadAsync(() => LoadingOverlay.IsLoading = false, Logger, Dispatcher);
                }
            }
        }

        /// <summary>
        ///     The full items behind the search hints, in the hints' (relevance) order. Fetched by id
        ///     in batches: one request each would be up to 200 in a row, and all ids in one URL can
        ///     pass the server's 8 KB request-line limit.
        /// </summary>
        private async Task<List<BaseItemDto>> LoadHintedItemsAsync(List<Guid> ids, Guid userId,
            CancellationToken token)
        {
            const int BatchSize = 50;
            var batches = Enumerable.Range(0, (ids.Count + BatchSize - 1) / BatchSize)
                .Select(b => ids.Skip(b * BatchSize).Take(BatchSize).Select(id => (Guid?)id).ToArray());

            var results = await Task.WhenAll(batches.Select(batch => _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.Ids = batch;
                config.QueryParameters.UserId = userId;
                config.QueryParameters.EnableUserData = true;
                // The tiles' subtitle reads these; the rest they show is returned by default
                config.QueryParameters.Fields = new[]
                {
                    ItemFields.Overview, ItemFields.ChildCount, ItemFields.PrimaryImageAspectRatio
                };
            }, token))).ConfigureAwait(false);

            var byId = results.Where(r => r?.Items != null).SelectMany(r => r.Items)
                .Where(i => i.Id.HasValue).GroupBy(i => i.Id.Value).ToDictionary(g => g.Key, g => g.First());

            // Anything the id query left out (it is not certain every hint kind comes back that
            // way) is fetched on its own
            var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                Logger.LogDebug("Search: {Count} hinted items not returned by id, fetching singly", missing.Count);
                var singles = await Task.WhenAll(missing.Select(async id =>
                {
                    try
                    {
                        return await _apiClient.Items[id].GetAsync(c => c.QueryParameters.UserId = userId, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ErrorHandler.HandleError(ex, CreateErrorContext($"LoadItem:{id}", ErrorCategory.Network, ErrorSeverity.Warning));
                        return null;
                    }
                })).ConfigureAwait(false);
                foreach (var item in singles.Where(i => i?.Id != null))
                {
                    byId[item.Id.Value] = item;
                }
            }

            return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        // The filter buttons and the item kinds each shows (All: no filter)
        private (Button Button, BaseItemKind[] Kinds)[] FilterButtons => new (Button, BaseItemKind[])[]
        {
            (AllButton, null),
            (MoviesButton, new[] { BaseItemKind.Movie }),
            (ShowsButton, new[] { BaseItemKind.Series }),
            (EpisodesButton, new[] { BaseItemKind.Episode }),
            (MusicButton, new[] { BaseItemKind.MusicAlbum, BaseItemKind.Audio, BaseItemKind.MusicArtist })
        };

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyFilter("FilterButton_Click", FilterButtons.FirstOrDefault(f => f.Button == sender).Kinds);
        }

        /// <summary>
        ///     Shows only <paramref name="filter" /> (null: everything), re-running the last search.
        /// </summary>
        private void ApplyFilter(string operation, BaseItemKind[] filter)
        {
            ErrorHandler.Run(CreateErrorContext(operation, ErrorCategory.User), () =>
            {
                _currentFilter = filter;
                UpdateFilterButtons();
                if (!string.IsNullOrEmpty(_lastSearchTerm))
                {
                    FireAndForget(() => PerformSearchAsync(_lastSearchTerm));
                }
            });
        }

        // A button is lit when the filter shows its kinds: Music for a "Show all albums" too
        private void UpdateFilterButtons()
        {
            var resources = Application.Current.Resources;
            foreach (var (button, kinds) in FilterButtons)
            {
                var isActive = kinds == null ? _currentFilter == null : _currentFilter?.Intersect(kinds).Any() == true;
                button.Style = resources[isActive ? "ActiveFilterButtonStyle" : "FilterButtonStyle"] as Style;
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
            if (_currentFilter == null)
            {
                return "All";
            }

            // One kind: its result row's name ("People", "Albums"); Music's three: the button's
            var group = _currentFilter.Length == 1 ? ResultGroups.FirstOrDefault(g => g.Kind == _currentFilter[0]) : null;
            return group?.Name
                   ?? FilterButtons.FirstOrDefault(f => f.Kinds?.SequenceEqual(_currentFilter) == true).Button?.Content as string
                   ?? "All";
        }

        private void CleanupGroupedResultsPanel()
        {
            foreach (var child in GroupedResultsPanel.Children)
            {
                if (child is ListView listView)
                {
                    listView.ItemClick -= ListView_ItemClick;
                }
                else if (child is Button { Tag: ResultGroup } button)
                {
                    button.Click -= ShowMoreButton_Click;
                }
            }
        }

        private void CreateGroupedUi(List<BaseItemDto> items)
        {
            try
            {
                var isFiltered = _currentFilter != null;

                foreach (var group in ResultGroups)
                {
                    var groupItems = items.Where(i => i.Type == group.Type).ToList();
                    if (groupItems.Count == 0)
                    {
                        continue;
                    }

                    var headerPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 8)
                    };

                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = group.Name,
                        Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style
                    });

                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = $" ({groupItems.Count})",
                        Style = Application.Current.Resources["SectionHeaderTextStyle"] as Style,
                        Foreground = Application.Current.Resources["SystemControlForegroundBaseMediumBrush"] as Brush
                    });

                    GroupedResultsPanel.Children.Add(headerPanel);

                    var itemsToShow = isFiltered ? groupItems : groupItems.Take(15).ToList();
                    var hasMore = !isFiltered && groupItems.Count > 15;

                    var listView = new ListView
                    {
                        ItemsSource = itemsToShow,
                        ItemTemplate = Resources[group.TemplateKey] as DataTemplate,
                        SelectionMode = ListViewSelectionMode.None,
                        IsItemClickEnabled = true,
                        Margin = new Thickness(0, 0, hasMore ? 12 : 24, 0),
                        Padding = new Thickness(0),
                        IsTabStop = true
                    };

                    var itemsPanelXaml = $@"
                    <ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                        <ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='{group.Columns}'/>
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

                    GroupedResultsPanel.Children.Add(listView);

                    if (hasMore)
                    {
                        var showMoreButton = new Button
                        {
                            Content = $"Show all {groupItems.Count} {group.Name.ToLower()}",
                            Style = Application.Current.Resources["FilterButtonStyle"] as Style,
                            Margin = new Thickness(0, 0, 0, 24),
                            Tag = group
                        };
                        showMoreButton.Click += ShowMoreButton_Click;
                        GroupedResultsPanel.Children.Add(showMoreButton);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("CreateGroupedUi", ErrorCategory.User));
            }
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                NavigateToItemDetails(item);
            }
        }

        private void ShowMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: ResultGroup group })
            {
                ApplyFilter("ShowMoreButton_Click", new[] { group.Kind });
            }
        }

        private sealed class ResultGroup
        {
            public ResultGroup(string name, BaseItemDto_Type type, BaseItemKind kind, string templateKey, int columns)
            {
                Name = name;
                Type = type;
                Kind = kind;
                TemplateKey = templateKey;
                Columns = columns;
            }

            public string Name { get; }
            public BaseItemDto_Type Type { get; }
            public BaseItemKind Kind { get; }
            public string TemplateKey { get; }
            public int Columns { get; }
        }
    }
}
