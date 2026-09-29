using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Shared.Ui
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
            // Its own event: nothing to unsubscribe. A cached page's tiles unload and load again,
            // and must keep following their item.
            DataContextChanged += OnDataContextChanged;
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

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (DataContext is BaseItemDto item)
            {
                ItemType = item.Type;

                var isMusicItem = item.Type == BaseItemDto_Type.MusicAlbum ||
                                  item.Type == BaseItemDto_Type.Audio ||
                                  item.Type == BaseItemDto_Type.MusicArtist;

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
                    UnwatchedCount = 1;
                }
                else
                {
                    UnwatchedCount = null;
                }
            }
        }

        private static void OnUnwatchedCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((UnwatchedIndicator)d).UpdateVisuals();
        }

        private static void OnItemTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((UnwatchedIndicator)d).UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            var showIndicator = UnwatchedCount > 0;
            if (!showIndicator)
            {
                TriangleIndicator.Visibility = Visibility.Collapsed;
                SquareIndicator.Visibility = Visibility.Collapsed;
                return;
            }

            var isTvShow = ItemType == BaseItemDto_Type.Series;

            TriangleIndicator.Visibility = isTvShow ? Visibility.Collapsed : Visibility.Visible;
            SquareIndicator.Visibility = isTvShow ? Visibility.Visible : Visibility.Collapsed;

            if (isTvShow)
            {
                CountText.Text = UnwatchedCount > 99 ? "99+" : UnwatchedCount.Value.ToString();
            }
        }
    }
}
