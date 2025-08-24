namespace Gelatinarm.Constants
{
    /// <summary>
    ///     Constants related to retry logic and timeouts throughout the application
    /// </summary>
    public static class RetryConstants
    {
        // Retry attempt constants
        /// <summary>
        ///     Default number of retry attempts for API operations
        /// </summary>
        public const int DEFAULT_API_RETRY_ATTEMPTS = 3;

        // Timeout constants (in seconds)

        /// <summary>
        ///     Timeout for HTTP requests to the server
        /// </summary>
        public const int HTTP_REQUEST_TIMEOUT_SECONDS = 10;

        /// <summary>
        ///     Timeout for search operations to provide quick feedback
        /// </summary>
        public const int SEARCH_TIMEOUT_SECONDS = 4;

        /// <summary>
        ///     Timeout for system monitoring operations
        /// </summary>
        public const int SYSTEM_MONITOR_TIMEOUT_SECONDS = 10;

        // Delay constants (in milliseconds)
        /// <summary>
        ///     Initial delay for exponential backoff retry logic
        /// </summary>
        public const int INITIAL_RETRY_DELAY_MS = 1000;

        /// <summary>
        ///     Delay for UI operations that need to wait for rendering
        /// </summary>
        public const int UI_RENDER_DELAY_MS = 50;

        /// <summary>
        ///     Delay for UI operations that need more time to settle
        /// </summary>
        public const int UI_SETTLE_DELAY_MS = 100;

        /// <summary>
        ///     Delay for Quick Connect polling operations
        /// </summary>
        public const int QUICK_CONNECT_POLL_DELAY_MS = 500;

        /// <summary>
        ///     Delay for Quick Connect success display
        /// </summary>
        public const int QUICK_CONNECT_SUCCESS_DELAY_MS = 1000;

        // Interval constants (in seconds)
        /// <summary>
        ///     Interval for server discovery refresh
        /// </summary>
        public const int SERVER_DISCOVERY_REFRESH_INTERVAL_SECONDS = 30;

        /// <summary>
        ///     Interval for system monitoring updates
        /// </summary>
        public const int SYSTEM_MONITOR_INTERVAL_SECONDS = 5;

        /// <summary>
        ///     Interval for device service monitoring
        /// </summary>
        public const int DEVICE_MONITOR_INTERVAL_SECONDS = 5;

        /// <summary>
        ///     Interval for playback progress reporting
        /// </summary>
        public const int PLAYBACK_PROGRESS_INTERVAL_SECONDS = 10;

        // Interval constants (in minutes)
        /// <summary>
        ///     Interval for bandwidth testing
        /// </summary>
        public const int BANDWIDTH_TEST_INTERVAL_MINUTES = 5;

        // Service initialization delays (in milliseconds)
        /// <summary>
        ///     Delay before loading cached data during startup to ensure services are initialized
        /// </summary>
        public const int CACHE_LOAD_STARTUP_DELAY_MS = 1000;

        /// <summary>
        ///     Delay before running cleanup tasks in background
        /// </summary>
        public const int CLEANUP_TASK_DELAY_MS = 10000;

        /// <summary>
        ///     Delay for batching playback position saves to reduce I/O operations
        /// </summary>
        public const int PLAYBACK_POSITION_SAVE_DELAY_MS = 5000;

        // Timeout constants (in milliseconds)
        /// <summary>
        ///     Timeout for logging service initialization
        /// </summary>
        public const int LOGGING_INIT_TIMEOUT_MS = 5000;

        /// <summary>
        ///     Timeout for session restoration during startup
        /// </summary>
        public const int SESSION_RESTORE_TIMEOUT_MS = 10000;

        /// <summary>
        ///     Delay for XAML UI stabilization after rendering
        /// </summary>
        public const int XAML_STABILIZATION_DELAY_MS = 100;

        /// <summary>
        ///     Delay to ensure UI is ready for focus operations
        /// </summary>
        public const int UI_FOCUS_READY_DELAY_MS = 100;

        /// <summary>
        ///     Maximum retry delay in seconds
        /// </summary>
        public const int MAX_RETRY_DELAY_SECONDS = 30;

        /// <summary>
        ///     Quick Connect instruction page polling interval in seconds
        /// </summary>
        public const int QUICK_CONNECT_POLL_INTERVAL_SECONDS = 5;

        /// <summary>
        ///     Quick Connect instruction page initial wait in seconds
        /// </summary>
        public const int QUICK_CONNECT_INITIAL_WAIT_SECONDS = 1;

        /// <summary>
        ///     Main view model cache expiration in minutes
        /// </summary>
        public const int MAIN_VIEW_CACHE_EXPIRATION_MINUTES = 5;

        /// <summary>
        ///     Media discovery cache expiration in minutes
        /// </summary>
        public const int MEDIA_DISCOVERY_CACHE_MINUTES = 2;


        /// <summary>
        ///     Navigation timeout in seconds
        /// </summary>
        public const int NAVIGATION_TIMEOUT_SECONDS = 5;

        // Bandwidth test response time thresholds (in milliseconds)
        /// <summary>
        ///     Response time threshold for excellent bandwidth (< 50ms)
        /// </summary>
        public const int BANDWIDTH_TEST_EXCELLENT_THRESHOLD_MS = 50;

        /// <summary>
        ///     Response time threshold for medium bandwidth (< 100ms)
        /// </summary>
        public const int BANDWIDTH_TEST_GOOD_THRESHOLD_MS = 100;

        /// <summary>
        ///     Response time threshold for lower bandwidth (< 200ms)
        /// </summary>
        public const int BANDWIDTH_TEST_FAIR_THRESHOLD_MS = 200;
    }
}
