namespace Gelatinarm.Music
{
    public static class MusicConstants
    {
        public const int MediaSourceClearDelayMs = 500;

        // Xbox's MediaPlayer fails (SourceNotSupported) on audio files whose embedded cover
        // art is larger than about this, in either dimension. Such files skip the direct
        // attempt and are streamed by the server with the picture stripped.
        public const int MaxDirectPlayEmbeddedArtworkPixels = 1500;

        public const int MaxDiscoveryQueryLimit = 100;

        /// <summary>
        ///     How long sign-out or a profile switch waits for the music "playback stopped"
        ///     report before carrying on, so an unreachable server cannot stall them
        /// </summary>
        public const int StopReportBeforeUserChangeTimeoutMs = 3000;

        public const int MiniPlayerUpdateIntervalMs = 500;
    }
}
