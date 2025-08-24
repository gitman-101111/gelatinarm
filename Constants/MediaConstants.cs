namespace Gelatinarm.Constants
{
    public static class MediaConstants
    {
        // Default limits for media queries
        public const int DEFAULT_QUERY_LIMIT = 20;
        public const int LARGE_QUERY_LIMIT = 50;

        // Media playback delays
        public const int MEDIA_SOURCE_CLEAR_DELAY_MS = 500;

        // Query limits
        public const int MAX_DISCOVERY_QUERY_LIMIT = 100;
        public const int EXTENDED_QUERY_LIMIT = 500;

        // Cache durations
        public const int DISCOVERY_CACHE_EXPIRATION_MINUTES = 5; // Increased from 2
        public const int CONTINUE_WATCHING_CACHE_SECONDS = 60; // Increased from 30
        public const int NEXT_UP_CACHE_MINUTES = 3; // Increased from 1
        public const int CACHE_CLEANUP_THRESHOLD_MINUTES = 15; // Increased from 10

        // Playback thresholds
        public const int WATCHED_PERCENTAGE_THRESHOLD = 90;

        // Image parameters
        public const int IMAGE_QUALITY = 80; // Reduced from 90 for better performance
        public const int BACKDROP_QUALITY = 75; // Medium quality for backgrounds

        // Buffer health thresholds
        public const int BUFFER_HEALTH_POOR_THRESHOLD = 25;
        public const int BUFFER_HEALTH_FAIR_THRESHOLD = 50;
        public const int BUFFER_HEALTH_GOOD_THRESHOLD = 75;

        // UI strings
        public const string SUBTITLE_NONE_OPTION = "None";

        // Buffer health states
        public const string BUFFER_HEALTH_POOR = "Poor";
        public const string BUFFER_HEALTH_FAIR = "Fair";
        public const string BUFFER_HEALTH_GOOD = "Good";
        public const string BUFFER_HEALTH_EXCELLENT = "Excellent";
    }
}