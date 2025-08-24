using System;
using Windows.Media.Playback;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Helper methods for media playback operations
    /// </summary>
    public static class MediaPlaybackHelper
    {
        /// <summary>
        ///     Safely gets the buffering progress from a media playback session
        /// </summary>
        /// <param name="session">The media playback session</param>
        /// <param name="defaultValue">Default value if property is not available</param>
        /// <returns>The buffering progress or default value</returns>
        public static double GetBufferingProgressSafe(MediaPlaybackSession session, double defaultValue = 1.0)
        {
            if (session == null) return defaultValue;

            try
            {
                return session.BufferingProgress;
            }
            catch (InvalidCastException)
            {
                // BufferingProgress not available for this media type
                return defaultValue;
            }
        }

        /// <summary>
        ///     Safely gets the download progress from a media playback session
        /// </summary>
        /// <param name="session">The media playback session</param>
        /// <param name="defaultValue">Default value if property is not available</param>
        /// <returns>The download progress or default value</returns>
        public static double GetDownloadProgressSafe(MediaPlaybackSession session, double defaultValue = 1.0)
        {
            if (session == null) return defaultValue;

            try
            {
                return session.DownloadProgress;
            }
            catch (InvalidCastException)
            {
                // DownloadProgress not available for this media type
                return defaultValue;
            }
        }
    }
}