using System;

namespace Gelatinarm.Music
{
    public sealed partial class ArtistDetailsPage : MusicDetailsPage
    {
        public ArtistDetailsPage() : base(typeof(ArtistDetailsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(ArtistDetailsViewModel);
        public new ArtistDetailsViewModel ViewModel => (ArtistDetailsViewModel)base.ViewModel;
    }
}
