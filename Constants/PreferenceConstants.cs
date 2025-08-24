namespace Gelatinarm.Constants
{
    public static class PreferenceConstants
    {
        // Authentication & User
        public const string ServerUrl = "ServerUrl";
        public const string AccessToken = "AccessToken";
        public const string UserId = "UserId";
        public const string UserName = "UserName";
        public const string ServerId = "ServerId";

        public const string
            UserDto = "UserDto"; // Complex object (UserDto) stored as JSON string, used by UserProfileService

        public const string DeviceId = "DeviceId";
        public const string ConnectionTimeout = "ConnectionTimeout";

        // Playback & Media
        public const string EnableDirectPlay = "EnableDirectPlay";
        public const string MaxBitrate = "MaxBitrate";
        public const string AudioChannels = "AudioChannels";
        public const string EnableSubtitles = "EnableSubtitles";
        public const string AutoPlayNextEpisode = "AutoPlayNextEpisode";
        public const string IsPlaybackOptimizationEnabled = "IsPlaybackOptimizationEnabled";
        public const string UseAutomaticQualitySwitching = "UseAutomaticQualitySwitching";
        public const string AllowAudioStreamCopy = "AllowAudioStreamCopy";
        public const string PauseOnFocusLoss = "PauseOnFocusLoss";

        // MediaEnhancementService specific keys
        public const string EnableMediaEnhancements = "EnableMediaEnhancements";
        public const string NightModeEnabled = "NightModeEnabled";
        public const string SpatialAudioEnabled = "SpatialAudioEnabled";

        // PreferencesService internal file/storage keys (for SaveAsync/LoadAsync)
        public const string PlaybackPositionsFileKey = "PlaybackPositions.json";

        // UI/ViewModel specific
        public const string TextSize = "TextSize";
        public const string RecentSearches = "RecentSearches";
        public const string IgnoreCertificateErrors = "IgnoreCertificateErrors";
        public const string AppState = "AppState";
        public const string CurrentLibraryId = "CurrentLibraryId";
        public const string CurrentLibraryName = "CurrentLibraryName";
        public const string CurrentLibraryType = "CurrentLibraryType";


        // Cached codec support information
        public const string CachedCodecSupport = "CachedCodecSupport";
    }
}
