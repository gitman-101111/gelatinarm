namespace Gelatinarm.Player
{
    public static class PlayerConstants
    {
        public const int PositionTimerIntervalMs = 250;

        public const int NextEpisodePreloadDelayMs = 5000;

        /// <summary>
        ///     Pause between Pause() and Play() when nudging the player to recover.
        /// </summary>
        public const int PauseResumeCycleDelayMs = 100;

        /// <summary>
        ///     Wait for an HLS manifest to load. Long enough for the server to
        ///     produce it, short enough to avoid audible buffering.
        /// </summary>
        public const int HlsManifestLoadDelayMs = 500;

        /// <summary>
        ///     How long a playback error stays on screen before it is cleared.
        /// </summary>
        public const int ErrorAutoDismissDelayMs = 3000;

        public const double AutoPlayNextThresholdPercent = 99.5;

        public const int SkipBackwardSeconds = 10;
        public const int SkipForwardSeconds = 30;

        // Held-trigger skip on the controller: ten minutes
        public const int TriggerSkipSeconds = 600;

        // How the stats overlay learns what the server is doing to the stream. The server
        // only reports it while its ffmpeg job runs, and a copy job can finish the whole
        // file in a minute or two, so ask from the start of the stream and keep the answer.
        public const int StatsTranscodingInfoPollSeconds = 5;
        public const int StatsTranscodingInfoPollAttempts = 12;
    }
}
