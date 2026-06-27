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
        public const int DefaultApiRetryAttempts = 3;

        // Timeout constants (in seconds)

        /// <summary>
        ///     Timeout for HTTP requests to the server
        /// </summary>
        public const int HttpRequestTimeoutSeconds = 10;

        /// <summary>
        ///     Timeout for search operations to provide quick feedback
        /// </summary>
        public const int SearchTimeoutSeconds = 4;

        /// <summary>
        ///     Timeout for system monitoring operations
        /// </summary>
        public const int SystemMonitorTimeoutSeconds = 10;

        // Delay constants (in milliseconds)
        /// <summary>
        ///     Initial delay for exponential backoff retry logic
        /// </summary>
        public const int InitialRetryDelayMs = 1000;

        /// <summary>
        ///     Delay for UI operations that need to wait for rendering
        /// </summary>
        public const int UiRenderDelayMs = 50;

        /// <summary>
        ///     Delay for UI operations that need more time to settle
        /// </summary>
        public const int UiSettleDelayMs = 100;

        /// <summary>
        ///     Delay for Quick Connect polling operations
        /// </summary>
        public const int QuickConnectPollDelayMs = 500;

        /// <summary>
        ///     Delay for Quick Connect success display
        /// </summary>
        public const int QuickConnectSuccessDelayMs = 1000;

        // Interval constants (in seconds)
        /// <summary>
        ///     Interval for server discovery refresh
        /// </summary>
        public const int ServerDiscoveryRefreshIntervalSeconds = 30;

        /// <summary>
        ///     Interval for system monitoring updates
        /// </summary>
        public const int SystemMonitorIntervalSeconds = 5;

        /// <summary>
        ///     Interval for device service monitoring
        /// </summary>
        public const int DeviceMonitorIntervalSeconds = 5;

        /// <summary>
        ///     Interval for playback progress reporting
        /// </summary>
        public const int PlaybackProgressIntervalSeconds = 10;

        // Interval constants (in minutes)
        /// <summary>
        ///     Interval for bandwidth testing
        /// </summary>
        public const int BandwidthTestIntervalMinutes = 5;

        // Service initialization delays (in milliseconds)
        /// <summary>
        ///     Delay before loading cached data during startup to ensure services are initialized
        /// </summary>
        public const int CacheLoadStartupDelayMs = 1000;

        /// <summary>
        ///     Delay before running cleanup tasks in background
        /// </summary>
        public const int CleanupTaskDelayMs = 10000;

        /// <summary>
        ///     Delay for batching playback position saves to reduce I/O operations
        /// </summary>
        public const int PlaybackPositionSaveDelayMs = 5000;

        // Timeout constants (in milliseconds)
        /// <summary>
        ///     Timeout for logging service initialization
        /// </summary>
        public const int LoggingInitTimeoutMs = 5000;

        /// <summary>
        ///     Timeout for session restoration during startup
        /// </summary>
        public const int SessionRestoreTimeoutMs = 10000;

        /// <summary>
        ///     Delay for XAML UI stabilization after rendering
        /// </summary>
        public const int XamlStabilizationDelayMs = 100;

        /// <summary>
        ///     Delay to ensure UI is ready for focus operations
        /// </summary>
        public const int UiFocusReadyDelayMs = 100;

        /// <summary>
        ///     Maximum retry delay in seconds
        /// </summary>
        public const int MaxRetryDelaySeconds = 30;

        /// <summary>
        ///     Quick Connect instruction page polling interval in seconds
        /// </summary>
        public const int QuickConnectPollIntervalSeconds = 5;

        /// <summary>
        ///     Quick Connect instruction page initial wait in seconds
        /// </summary>
        public const int QuickConnectInitialWaitSeconds = 1;

        /// <summary>
        ///     Main view model cache expiration in minutes
        /// </summary>
        public const int MainViewCacheExpirationMinutes = 5;

        /// <summary>
        ///     Media discovery cache expiration in minutes
        /// </summary>
        public const int MediaDiscoveryCacheMinutes = 2;

        /// <summary>
        ///     Navigation timeout in seconds
        /// </summary>
        public const int NavigationTimeoutSeconds = 5;

        // Bandwidth test response time thresholds (in milliseconds)
        /// <summary>
        ///     Response time threshold for excellent bandwidth (< 50ms)
        /// </summary>
        public const int BandwidthTestExcellentThresholdMs = 50;

        /// <summary>
        ///     Response time threshold for medium bandwidth (< 100ms)
        /// </summary>
        public const int BandwidthTestGoodThresholdMs = 100;

        /// <summary>
        ///     Response time threshold for lower bandwidth (< 200ms)
        /// </summary>
        public const int BandwidthTestFairThresholdMs = 200;
    }
}
