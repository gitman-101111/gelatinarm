using System;
using System.Collections.Generic;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Player
{
    public class MediaPlaybackParams
    {
        public BaseItemDto Item { get; set; }
        public string MediaSourceId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public long? StartPositionTicks { get; set; }
        public List<BaseItemDto> QueueItems { get; set; }
        public int StartIndex { get; set; }
        public Type NavigationSourcePage { get; set; }
        public object NavigationSourceParameter { get; set; }
    }
}
