namespace Gelatinarm.Shared.Server
{
    public static class SystemConstants
    {
        public const int MaxPreferenceStringLength = 100;

        public const int DefaultTimeoutSeconds = 30;

        // Named HttpClient registered in App.ConfigureServices. A typo at a
        // CreateClient call site silently yields an unconfigured client, so both
        // the registration and every lookup go through this.
        public const string JellyfinHttpClientName = "JellyfinClient";

        public const int ExtendedQueryLimit = 500;
    }
}
