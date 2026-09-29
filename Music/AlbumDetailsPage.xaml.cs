using System;

namespace Gelatinarm.Music
{
    public sealed partial class AlbumDetailsPage : MusicDetailsPage
    {
        public AlbumDetailsPage() : base(typeof(AlbumDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(AlbumDetailsViewModel);
        public new AlbumDetailsViewModel ViewModel => (AlbumDetailsViewModel)base.ViewModel;
    }
}
