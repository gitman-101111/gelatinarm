using System;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Player
{
    public class EpisodeNavigationParameter
    {
        public BaseItemDto Episode { get; set; }
        public bool FromEpisodesButton { get; set; }
        public Type OriginalSourcePage { get; set; }
        public object OriginalSourceParameter { get; set; }
    }
}
