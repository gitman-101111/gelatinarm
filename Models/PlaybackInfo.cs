using System;
using Windows.Media.Playback;

namespace Gelatinarm.Models
{
    public class PlaybackInfo
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsPaused { get; set; }
        public bool IsLive { get; set; }
        public string MediaSourceId { get; set; }
        public string AudioStreamIndex { get; set; }
        public string SubtitleStreamIndex { get; set; }
        public MediaPlaybackState PlaybackState { get; set; }
        public bool CanSeek { get; set; }
        public bool IsMuted { get; set; }
        public double Volume { get; set; }
        public string CurrentBitrate { get; set; }
        public string CurrentResolution { get; set; }
        public string CurrentVideoCodec { get; set; }
    }
}
