namespace Gelatinarm.Home
{
    public static class HomeConstants
    {
        public const int DefaultQueryLimit = 20;

        public const int DiscoveryCacheExpirationMinutes = 5;
        public const int NextUpCacheMinutes = 3;

        /// <summary>
        ///     Home rows retry sooner and fewer times than other requests: a row that still fails
        ///     keeps what it showed, and the rest of the screen loads without it
        /// </summary>
        public const int HomeRowRetryAttempts = 2;
        public const int HomeRowRetryDelayMs = 500;
    }
}
