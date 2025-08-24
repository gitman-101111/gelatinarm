using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.Controls
{
    public sealed partial class UnwatchedIndicator : UserControl
    {
        public static readonly DependencyProperty UnwatchedCountProperty =
            DependencyProperty.Register(nameof(UnwatchedCount), typeof(int?), typeof(UnwatchedIndicator),
                new PropertyMetadata(null, OnUnwatchedCountChanged));


        public static readonly DependencyProperty ItemTypeProperty =
            DependencyProperty.Register(nameof(ItemType), typeof(BaseItemDto_Type?), typeof(UnwatchedIndicator),
                new PropertyMetadata(null, OnItemTypeChanged));

        public UnwatchedIndicator()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
        }

        public int? UnwatchedCount
        {
            get => (int?)GetValue(UnwatchedCountProperty);
            set => SetValue(UnwatchedCountProperty, value);
        }


        public BaseItemDto_Type? ItemType
        {
            get => (BaseItemDto_Type?)GetValue(ItemTypeProperty);
            set => SetValue(ItemTypeProperty, value);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DataContextChanged -= OnDataContextChanged;
            Unloaded -= OnUnloaded;
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (DataContext is BaseItemDto item)
            {
                ItemType = item.Type;

                // Check if this is a music-related item type
                var isMusicItem = item.Type == BaseItemDto_Type.MusicAlbum ||
                                  item.Type == BaseItemDto_Type.Audio ||
                                  item.Type == BaseItemDto_Type.MusicArtist;

                // Don't show unwatched indicators for music items
                if (isMusicItem)
                {
                    UnwatchedCount = null;
                }
                else if (item.Type == BaseItemDto_Type.Series && item.UserData?.UnplayedItemCount.HasValue == true)
                {
                    UnwatchedCount = item.UserData.UnplayedItemCount.Value;
                }
                else if (item.UserData?.Played == false)
                {
                    UnwatchedCount = 1; // For movies, just show the indicator
                }
                else
                {
                    UnwatchedCount = null;
                }
            }
        }

        private static void OnUnwatchedCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as UnwatchedIndicator;
            if (control != null)
            {
                control.UpdateVisuals();
            }
        }

        private static void OnItemTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as UnwatchedIndicator;
            control?.UpdateVisuals();
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            var showIndicator = UnwatchedCount.HasValue && UnwatchedCount.Value > 0;
            if (!showIndicator)
            {
                if (TriangleIndicator != null)
                {
                    TriangleIndicator.Visibility = Visibility.Collapsed;
                }

                if (SquareIndicator != null)
                {
                    SquareIndicator.Visibility = Visibility.Collapsed;
                }

                return;
            }

            // Check if this is a TV show (Series) or movie
            var isTvShow = ItemType == BaseItemDto_Type.Series;

            if (TriangleIndicator != null)
            {
                // Show triangle for movies
                TriangleIndicator.Visibility = !isTvShow ? Visibility.Visible : Visibility.Collapsed;
                // No margin - sits flush against corner
                TriangleIndicator.Margin = new Thickness(0);
            }

            if (SquareIndicator != null)
            {
                // Show square with count for TV shows
                SquareIndicator.Visibility = isTvShow ? Visibility.Visible : Visibility.Collapsed;
                // No margin - sits flush against corner
                SquareIndicator.Margin = new Thickness(0);
            }

            if (CountText != null && UnwatchedCount.HasValue && isTvShow)
            {
                // Format the count text for TV shows
                CountText.Text = UnwatchedCount.Value > 99 ? "99+" : UnwatchedCount.Value.ToString();
            }
        }
    }
}
