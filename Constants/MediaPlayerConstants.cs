namespace Gelatinarm.Constants
{
    public static class MediaPlayerConstants
    {
        // Timer intervals (milliseconds)
        public const int POSITION_TIMER_INTERVAL_MS = 250;
        public const int BUFFERING_CHECK_INTERVAL_MS = 1000;
        public const int CONTROLS_HIDE_CHECK_INTERVAL_MS = 100;

        // Delays (milliseconds)
        public const int NEXT_EPISODE_PRELOAD_DELAY_MS = 5000;
        public const int SEEK_OPERATION_DELAY_MS = 100;

        // Timeouts (seconds)
        public const int API_CALL_TIMEOUT_SECONDS = 10;

        // Thresholds (percentages)
        public const double PLAYBACK_DETECTION_THRESHOLD_SECONDS = 1.0;
        public const double AUTO_PLAY_NEXT_THRESHOLD_PERCENT = 99.5;

        // Skip intervals (seconds)
        public const int SKIP_BACKWARD_SECONDS = 10;
        public const int SKIP_FORWARD_SECONDS = 30;

        // Time formatting
        public const string TIME_FORMAT_HOURS = @"h\:mm\:ss";
        public const string TIME_FORMAT_MINUTES = @"m\:ss";
        public const string TIME_FORMAT_HOURS_DISPLAY = "{0}h {1}m";
        public const string TIME_FORMAT_HOURS_ONLY = "{0}h";
        public const string TIME_FORMAT_MINUTES_ONLY = "{0}m";

        // Position reporting
        public const int POSITION_REPORT_INTERVAL_TICKS = 20; // Every 20 timer ticks (5 seconds at 250ms per tick)
    }
}