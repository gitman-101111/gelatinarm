using System.Collections.Generic;
using System.Linq;
using Gelatinarm.Details;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Playback
{
    public class AudioTrack
    {
        public int ServerStreamIndex { get; set; }
        public string DisplayName { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>
        ///     The audio choices for one version. The details page and the player both list
        ///     tracks this way, so they name them alike.
        /// </summary>
        public static List<AudioTrack> ListFrom(IEnumerable<MediaStream> streams)
        {
            return streams.Where(s => s.Type == MediaStream_Type.Audio).Select(FromStream).ToList();
        }

        private static AudioTrack FromStream(MediaStream stream)
        {
            return new AudioTrack
            {
                ServerStreamIndex = stream.Index ?? 0,
                DisplayName = GetDisplayName(stream),
                IsDefault = stream.IsDefault == true
            };
        }

        private static string GetDisplayName(MediaStream stream)
        {
            // The server's label keeps the file's own track title ("Commentary") and adds only
            // what the title does not already say; without it, same-codec tracks read alike.
            if (!string.IsNullOrEmpty(stream.DisplayTitle))
            {
                return stream.DisplayTitle;
            }

            var channels = stream.Channels ?? 2;
            var channelLayout = MediaLabels.ChannelLayout(channels) ?? $"{channels}ch";

            var codec = stream.Codec ?? "Unknown";
            var displayCodec = codec.ToLowerInvariant() switch
            {
                "ac3" => "Dolby Digital",
                "eac3" => "Dolby Digital+",
                "truehd" => "Dolby TrueHD",
                "dts" => "DTS",
                _ => codec.ToUpperInvariant()
            };

            return $"{stream.Language ?? "Unknown"} - {displayCodec} {channelLayout}";
        }
    }
}
