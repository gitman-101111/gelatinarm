using System.Collections.Generic;
using System.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Playback
{
    public static class MediaStreamHelper
    {
        /// <summary>
        ///     The audio stream that plays for <paramref name="audioStreamIndex" />: that stream,
        ///     else the file's default, else its first (what the server picks with no index)
        /// </summary>
        public static MediaStream AudioStream(IEnumerable<MediaStream> streams, int? audioStreamIndex)
        {
            var audio = streams?.Where(s => s.Type == MediaStream_Type.Audio).ToList();
            if (audio == null || audio.Count == 0)
            {
                return null;
            }

            return audio.FirstOrDefault(s => audioStreamIndex >= 0 && s.Index == audioStreamIndex)
                   ?? audio.FirstOrDefault(s => s.IsDefault == true)
                   ?? audio[0];
        }
    }
}
