using System;
using System.Diagnostics.CodeAnalysis;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Music
{
    // One track in an album or artist list, and the flyout its button opens (so do the
    // controller's Menu button and A on the row, through the list's PreviewKeyDown). The commands
    // are the hosting page's view model's.
    public sealed partial class TrackRow : UserControl
    {
        public TrackRow()
        {
            InitializeComponent();
        }

        private BaseItemDto Track => (BaseItemDto)DataContext;

        // The row's DataContext is its track; the list's, inherited from the page, is the view model
        private MusicDetailsViewModel ViewModel
        {
            get
            {
                for (var element = VisualTreeHelper.GetParent(this); element != null; element = VisualTreeHelper.GetParent(element))
                {
                    if (element is FrameworkElement { DataContext: MusicDetailsViewModel viewModel })
                    {
                        return viewModel;
                    }
                }

                throw new InvalidOperationException("TrackRow is not inside a music details page");
            }
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        private void OnClick(object sender, RoutedEventArgs e)
        {
            ControllerInputHelper.ShowContextFlyout(sender);
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        private void OnPlay(object sender, RoutedEventArgs e)
        {
            ViewModel.PlayTrackCommand.Execute(Track);
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        private void OnPlayNext(object sender, RoutedEventArgs e)
        {
            ViewModel.PlayNextCommand.Execute(Track);
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        private void OnAddToQueue(object sender, RoutedEventArgs e)
        {
            ViewModel.AddToQueueCommand.Execute(Track);
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        private void OnStartInstantMix(object sender, RoutedEventArgs e)
        {
            ViewModel.StartInstantMixCommand.Execute(Track);
        }
    }
}
