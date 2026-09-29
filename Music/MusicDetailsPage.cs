using System;
using System.Diagnostics.CodeAnalysis;
using Windows.UI.Xaml.Input;
using Gelatinarm.Details;
using Gelatinarm.Shared.Ui;

namespace Gelatinarm.Music
{
    // The album and artist pages: the controller's Menu button and A on a track row open the
    // row's context menu (the track lists' PreviewKeyDown); the row's own button opens it on click
    public abstract class MusicDetailsPage : DetailsPage
    {
        protected MusicDetailsPage(Type loggerType) : base(loggerType)
        {
        }

        [SuppressMessage("Style", "IDE0060", Justification = "A XAML event handler's signature")]
        protected void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            ControllerInputHelper.HandleContextMenuKey(e, Logger);
        }
    }
}
