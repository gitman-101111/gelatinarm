namespace Gelatinarm.Shared.Async
{
    public static class RetryConstants
    {
        public const int DefaultApiRetryAttempts = 3;

        public const int InitialRetryDelayMs = 1000;

        public const int CleanupTaskDelayMs = 10000;

        public const int SessionRestoreTimeoutMs = 10000;

        public const int MaxRetryDelaySeconds = 30;

        public const int NavigationTimeoutSeconds = 5;
    }
}
