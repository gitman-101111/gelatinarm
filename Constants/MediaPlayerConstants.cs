namespace Gelatinarm.Constants
{
    public static class MediaPlayerConstants
    {
        // Timer intervals (milliseconds)
        public const int PositionTimerIntervalMs = 250;
        public const int BufferingCheckIntervalMs = 1000;
        public const int ControlsHideCheckIntervalMs = 100;

        // Delays (milliseconds)
        public const int NextEpisodePreloadDelayMs = 5000;
        public const int SeekOperationDelayMs = 100;

        // Timeouts (seconds)
        public const int ApiCallTimeoutSeconds = 10;

        // Thresholds (percentages)
        public const double PlaybackDetectionThresholdSeconds = 1.0;
        public const double AutoPlayNextThresholdPercent = 99.5;

        // Skip intervals (seconds)
        public const int SkipBackwardSeconds = 10;
        public const int SkipForwardSeconds = 30;

        // Time formatting
        public const string TimeFormatHours = @"h\:mm\:ss";
        public const string TimeFormatMinutes = @"m\:ss";
        public const string TimeFormatHoursDisplay = "{0}h {1}m";
        public const string TimeFormatHoursOnly = "{0}h";
        public const string TimeFormatMinutesOnly = "{0}m";

        // Position reporting
        public const int PositionReportIntervalTicks = 20; // Every 20 timer ticks (5 seconds at 250ms per tick)
    }
}
