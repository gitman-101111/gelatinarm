using System;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Playback
{
    /// <summary>
    ///     What a playback report says: the reporting player fills it from its own state
    /// </summary>
    public sealed class PlaybackReport
    {
        public Guid ItemId { get; set; }
        public string MediaSourceId { get; set; }
        public string PlaySessionId { get; set; }
        public long PositionTicks { get; set; }
        public PlaybackProgressInfo_PlayMethod PlayMethod { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public bool IsPaused { get; set; }
    }
}
