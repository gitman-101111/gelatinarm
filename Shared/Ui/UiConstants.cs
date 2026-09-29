namespace Gelatinarm.Shared.Ui
{
    public static class UiConstants
    {
        public const int MaxBackStackDepth = 10; // Prevent excessive memory usage

        // Media player controls auto-hide delay, in seconds (the Settings slider's range)
        public const int MinControlsHideDelaySeconds = 1;
        public const int MaxControlsHideDelaySeconds = 10;

        public const string TimeFormatHours = @"h\:mm\:ss";
        public const string TimeFormatMinutes = @"m\:ss";
        public const string TimeFormatHoursDisplay = "{0}h {1}m";
        public const string TimeFormatHoursOnly = "{0}h";
        public const string TimeFormatMinutesOnly = "{0}m";
    }
}
