using System.Collections.Generic;
using System.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Playback
{
    public class SubtitleTrack
    {
        public int ServerStreamIndex { get; set; }
        public string DisplayTitle { get; set; }
        public bool IsDefault { get; set; }
        public bool IsNoneOption { get; set; }

        private static SubtitleTrack None()
        {
            return new SubtitleTrack
            {
                ServerStreamIndex = -1,
                DisplayTitle = PlaybackConstants.SubtitleNoneOption,
                IsNoneOption = true
            };
        }

        /// <summary>
        ///     The subtitle choices for one version: "None" first, then its subtitle streams. The
        ///     details page and the player both list tracks this way, so they name them alike.
        /// </summary>
        public static List<SubtitleTrack> ListFrom(IEnumerable<MediaStream> streams)
        {
            var tracks = new List<SubtitleTrack> { None() };
            tracks.AddRange(streams.Where(s => s.Type == MediaStream_Type.Subtitle).Select(FromStream));
            return tracks;
        }

        private static SubtitleTrack FromStream(MediaStream stream)
        {
            return new SubtitleTrack
            {
                ServerStreamIndex = stream.Index ?? 0,
                DisplayTitle = GetDisplayTitle(stream),
                IsDefault = stream.IsDefault == true
            };
        }

        private static string GetDisplayTitle(MediaStream stream)
        {
            // As for audio: the server's label carries the file's track title.
            if (!string.IsNullOrEmpty(stream.DisplayTitle))
            {
                return stream.DisplayTitle;
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stream.Language))
            {
                parts.Add(stream.Language);
            }

            if (!string.IsNullOrEmpty(stream.Title) && stream.Title != stream.Language)
            {
                parts.Add(stream.Title);
            }

            if (!string.IsNullOrEmpty(stream.Codec))
            {
                parts.Add($"[{stream.Codec.ToUpperInvariant()}]");
            }

            var flags = new List<string>();
            if (stream.IsDefault == true)
            {
                flags.Add("Default");
            }

            if (stream.IsForced == true)
            {
                flags.Add("Forced");
            }

            if (stream.IsExternal == true)
            {
                flags.Add("External");
            }

            if (flags.Count > 0)
            {
                parts.Add($"({string.Join(", ", flags)})");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : $"Subtitle Track {stream.Index}";
        }
    }
}
