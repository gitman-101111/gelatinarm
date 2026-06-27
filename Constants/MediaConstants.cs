namespace Gelatinarm.Constants
{
    public static class MediaConstants
    {
        // Default limits for media queries
        public const int DefaultQueryLimit = 20;
        public const int LargeQueryLimit = 50;

        // Media playback delays
        public const int MediaSourceClearDelayMs = 500;

        // Query limits
        public const int MaxDiscoveryQueryLimit = 100;
        public const int ExtendedQueryLimit = 500;

        // Cache durations
        public const int DiscoveryCacheExpirationMinutes = 5; // Increased from 2
        public const int ContinueWatchingCacheSeconds = 60; // Increased from 30
        public const int NextUpCacheMinutes = 3; // Increased from 1
        public const int CacheCleanupThresholdMinutes = 15; // Increased from 10

        // Playback thresholds
        public const int WatchedPercentageThreshold = 90;

        // Image parameters
        public const int ImageQuality = 80; // Reduced from 90 for better performance
        public const int BackdropQuality = 75; // Medium quality for backgrounds

        // Buffer health thresholds
        public const int BufferHealthPoorThreshold = 25;
        public const int BufferHealthFairThreshold = 50;
        public const int BufferHealthGoodThreshold = 75;

        // UI strings
        public const string SubtitleNoneOption = "None";

        // Buffer health states
        public const string BufferHealthPoor = "Poor";
        public const string BufferHealthFair = "Fair";
        public const string BufferHealthGood = "Good";
        public const string BufferHealthExcellent = "Excellent";
    }
}
