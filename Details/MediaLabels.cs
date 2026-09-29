using System.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Details
{
    public static class MediaLabels
    {
        public static string Resolution(int height)
        {
            return height switch
            {
                >= 2160 => "4K",
                >= 1080 => "1080p",
                >= 720 => "720p",
                >= 480 => "480p",
                _ => $"{height}p"
            };
        }

        // A channel count's usual name; null for a count without one
        public static string ChannelLayout(int channels)
        {
            return channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                6 => "5.1",
                8 => "7.1",
                _ => null
            };
        }

        // The mini player and the system media controls name the playing track alike
        public static string TrackTitle(BaseItemDto track)
        {
            return track.Name ?? "Unknown Track";
        }

        public static string TrackArtist(BaseItemDto track)
        {
            return track.AlbumArtist ?? track.Artists?.FirstOrDefault() ?? "Unknown Artist";
        }
    }
}
