namespace Gelatinarm.Shared.Preferences
{
    public static class PreferenceConstants
    {
        public const string ServerUrl = "ServerUrl";
        public const string UserId = "UserId";
        public const string UserName = "UserName";

        // A List<UserProfile> as JSON, one entry per user who has signed in on this device
        public const string SavedProfiles = "SavedProfiles";

        public const string DeviceId = "DeviceId";

        public const string IgnoreCertificateErrors = "IgnoreCertificateErrors";
        public const string CurrentLibraryId = "CurrentLibraryId";
        public const string CurrentLibraryName = "CurrentLibraryName";
        public const string CurrentLibraryType = "CurrentLibraryType";
    }
}
