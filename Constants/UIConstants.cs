namespace Gelatinarm.Constants
{
    public static class UiConstants
    {
        // Navigation
        public const int MaxBackStackDepth = 10; // Prevent excessive memory usage

        // UI timing delays
        public const int UiRenderDelayMs = 50;
        public const int UiSettleDelayMs = 100;
        public const int SearchDebounceDelayMs = 300;
        public const int ControlsHideDelaySeconds = 2;
        public const int MiniPlayerUpdateIntervalMs = 500;
        public const int PositionUpdateIntervalMs = 100;

        // Quick Connect
        public const int QuickConnectCancelRedirectSeconds = 1;
    }
}
