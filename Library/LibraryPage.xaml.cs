using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Search;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using static Gelatinarm.Library.LibraryConstants;

namespace Gelatinarm.Library
{
    public sealed partial class LibraryPage : BasePage
    {
        // The filter options as they were when the flyout opened, for Cancel
        private readonly Dictionary<FilterItem, bool> _tempFilterStates = new();

        public LibraryPage() : base(typeof(LibraryPage))
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;

            SetupXboxNavigation();
        }

        protected override Type ViewModelType => typeof(LibraryViewModel);
        public new LibraryViewModel ViewModel => (LibraryViewModel)base.ViewModel;

        private void SetupXboxNavigation()
        {
            // The page is cached: CleanupResources drops these on leaving, a return adds them back
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            ViewModel.MediaItems.CollectionChanged -= MediaItems_CollectionChanged;
            ViewModel.MediaItems.CollectionChanged += MediaItems_CollectionChanged;

            ViewModel.JumpRequested -= OnJumpRequested;
            ViewModel.JumpRequested += OnJumpRequested;
        }

        private async void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(LibraryViewModel.CurrentFilter))
                {
                    await UiHelper.RunOnUIThreadAsync(SyncMusicViewComboBox, Logger, Dispatcher);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ViewModel_PropertyChanged", ErrorCategory.User), false);
            }
        }

        private async void MediaItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                // A load replaces the items one Add at a time: act once, on the first
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 &&
                    await UiHelper.ContainerWhenReadyAsync(MediaGrid, 0) is ListViewItem firstContainer)
                {
                    firstContainer.Focus(FocusState.Keyboard);
                    Logger.LogDebug("LibraryPage: Set focus to first media item");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("MediaItems_CollectionChanged", ErrorCategory.User), false);
            }
        }

        private void MediaGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto selectedItem)
            {
                NavigateToItemDetails(selectedItem);
            }
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            if (parameter is BaseItemDto library)
            {
                ViewModel.SelectedLibrary = library;

                // Remembered for a return with no parameter: after playback with nothing to go
                // back to, the queue service opens the library page bare
                PreferencesService.SetValue(PreferenceConstants.CurrentLibraryId, library.Id?.ToString() ?? "");
                PreferencesService.SetValue(PreferenceConstants.CurrentLibraryName, library.Name ?? "");
                PreferencesService.SetValue(PreferenceConstants.CurrentLibraryType, library.CollectionType?.ToString() ?? "");
            }
            else if (parameter != null)
            {
                Logger.LogWarning("LibraryPage: Unexpected parameter type: {ParameterGetType}", parameter.GetType().Name);
            }
            else
            {
                RestoreRememberedLibrary();
            }

            if (ViewModel.SelectedLibrary == null)
            {
                // Nothing to show: back to the picker, through the stack when it is there
                Logger.LogWarning("LibraryPage: No library selected, going back to LibrarySelectionPage");
                if (NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }
                else
                {
                    NavigationService.Navigate(typeof(LibrarySelectionPage));
                }

                return;
            }

            SetupXboxNavigation();
            SyncMusicViewComboBox();

            await ViewModel.LoadDataAsync(true).ConfigureAwait(false);
            await UiHelper.RunOnUIThreadAsync(() => MediaGrid.Focus(FocusState.Programmatic), Logger, Dispatcher);
        }

        // The library remembered by the last visit with a parameter; an unreadable id is no library
        private void RestoreRememberedLibrary()
        {
            var savedLibraryId = PreferencesService.GetValue<string>(PreferenceConstants.CurrentLibraryId);
            if (string.IsNullOrEmpty(savedLibraryId))
            {
                return;
            }

            if (!Guid.TryParse(savedLibraryId, out var libraryGuid))
            {
                Logger.LogError("Invalid library ID format: {SavedLibraryId}", savedLibraryId);
                return;
            }

            var savedLibraryName = PreferencesService.GetValue<string>(PreferenceConstants.CurrentLibraryName);
            var savedLibraryType = PreferencesService.GetValue<string>(PreferenceConstants.CurrentLibraryType);
            Logger.LogDebug(
                "LibraryPage: Restoring library from preferences: {SavedLibraryName} (ID: {SavedLibraryId}, Type: {SavedLibraryType})", savedLibraryName, savedLibraryId, savedLibraryType);

            var restoredLibrary = new BaseItemDto
            {
                Id = libraryGuid,
                Name = savedLibraryName,
                Type = BaseItemDto_Type.CollectionFolder
            };
            if (!string.IsNullOrEmpty(savedLibraryType) &&
                Enum.TryParse<BaseItemDto_CollectionType>(savedLibraryType, true, out var collectionType))
            {
                restoredLibrary.CollectionType = collectionType;
            }

            ViewModel.SelectedLibrary = restoredLibrary;
        }

        protected override Task OnNavigatedBackAsync()
        {
            if (ViewModel.SelectedLibrary != null && ViewModel.MediaItems.Count > 0)
            {
                SetupXboxNavigation();
                SyncMusicViewComboBox();

                MediaGrid.Focus(FocusState.Programmatic);
            }

            return Task.CompletedTask;
        }

        protected override void CleanupResources()
        {
            // Unfocused, so no focus animation runs on the cached page
            MediaGrid.Focus(FocusState.Unfocused);

            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.MediaItems.CollectionChanged -= MediaItems_CollectionChanged;
            ViewModel.JumpRequested -= OnJumpRequested;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            var searchParams = new SearchPageParams
            {
                LibraryType = ViewModel.SelectedLibrary?.CollectionType
            };

            NavigationService.Navigate(typeof(SearchPage), searchParams);
        }

        private void SortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsAscending = !ViewModel.IsAscending;
        }

        private void MusicViewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var filter = selectedItem.Tag?.ToString();
                if (string.IsNullOrEmpty(filter) || filter == ViewModel.CurrentFilter)
                {
                    return;
                }

                ViewModel.CurrentFilter = filter;
                Logger.LogInformation("LibraryPage: Music view changed to {Filter}", filter);

                // Artists and albums are different queries, so the picker reloads at once (the
                // flyout's filters wait for Apply). The page's first selection comes before any
                // library is chosen; that library's own load reads the view.
                if (ViewModel.IsMusicLibrary)
                {
                    FireAndForget(() => ViewModel.ApplyFiltersAsync(), "ApplyFilters");
                }
            }
        }

        private void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            (FilterButton.Flyout as Flyout)?.Hide();

            FireAndForget(() => ViewModel.ApplyFiltersAsync(), "ApplyFilters");
        }

        private void CancelFilterButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var kvp in _tempFilterStates)
            {
                kvp.Key.IsSelected = kvp.Value;
            }

            (FilterButton.Flyout as Flyout)?.Hide();
        }

        private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearAllFilters();
        }

        private void FilterFlyout_Opening(object sender, object e)
        {
            _tempFilterStates.Clear();
            foreach (var option in ViewModel.AllFilterOptions)
            {
                _tempFilterStates[option] = option.IsSelected;
            }
        }

        private void FilterFlyout_Opened(object sender, object e)
        {
            PopulateDecadeCheckboxes();

            // The flyout opens with focus on its presenter; a controller user starts on the first option
            FireAndForget(async () =>
            {
                await UiHelper.WhenIdleAsync(Dispatcher);
                var first = ControllerInputHelper.FindFirstFocusableControl(FilterFlyoutRoot);
                var focused = first?.Focus(FocusState.Programmatic) == true;
                Logger.LogDebug("Filter flyout: first option {Option}, {Outcome}", first?.GetType().Name ?? "none", focused ? "focused" : "not focused");
            }, "FocusFirstFilterOption");
        }

        private void PopulateDecadeCheckboxes()
        {
            try
            {
                DecadesGrid.Children.Clear();

                int row = 0, col = 0;
                foreach (var decade in ViewModel.Decades)
                {
                    var checkBox = new CheckBox
                    {
                        Content = decade.Name,
                        MinWidth = DecadeCheckboxMinWidth,
                        Margin = (Thickness)Application.Current.Resources["CompactControlMargin"]
                    };

                    var binding = new Binding
                    {
                        Source = decade,
                        Path = new PropertyPath("IsSelected"),
                        Mode = BindingMode.TwoWay
                    };
                    checkBox.SetBinding(CheckBox.IsCheckedProperty, binding);

                    Grid.SetRow(checkBox, row);
                    Grid.SetColumn(checkBox, col);

                    DecadesGrid.Children.Add(checkBox);

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
                ErrorHandler.HandleError(ex, CreateErrorContext("PopulateDecadeCheckboxes", ErrorCategory.User));
            }
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            FireAndForget(() => ViewModel.ShuffleAllAsync(), "ShuffleAll");
        }

        // The music view picker follows the view model's view (Artists or Albums); the grid's
        // template (ItemTemplateSelector) and cell size (bound to ItemWidth/ItemHeight) follow it
        // by themselves
        private void SyncMusicViewComboBox()
        {
            if (ViewModel.SelectedLibrary?.CollectionType != BaseItemDto_CollectionType.Music)
            {
                return;
            }

            MusicViewComboBox.SelectedItem = MusicViewComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == ViewModel.CurrentFilter);
        }

        private void OnJumpLetterClick(object sender, RoutedEventArgs e)
        {
            ViewModel.JumpTo((sender as Button)?.Content as string);
        }

        // The first letter is only known once the items load; Up from it goes to the filters
        private void OnJumpLetterLoaded(object sender, RoutedEventArgs e)
        {
            var letters = ViewModel.JumpLetters;
            if (sender is Button button && letters.Count > 0 && button.Content as string == letters[0])
            {
                button.XYFocusUp = FilterButton;
            }
        }

        private void OnJumpRequested(object sender, BaseItemDto item)
        {
            MediaGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
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
