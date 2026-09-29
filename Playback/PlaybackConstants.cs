namespace Gelatinarm.Playback
{
    public static class PlaybackConstants
    {

        // How often a playing item's progress goes to the server, video and music alike (the
        // pace Jellyfin's own clients report at)
        public const int ProgressReportIntervalSeconds = 10;

        public const string SubtitleNoneOption = "None";

        public const int ApiCallTimeoutSeconds = 10;
    }
}
