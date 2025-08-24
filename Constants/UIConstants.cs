namespace Gelatinarm.Constants
{
    public static class UIConstants
    {
        // Navigation
        public const int MAX_BACK_STACK_DEPTH = 10; // Prevent excessive memory usage

        // UI timing delays
        public const int UI_RENDER_DELAY_MS = 50;
        public const int UI_SETTLE_DELAY_MS = 100;
        public const int SEARCH_DEBOUNCE_DELAY_MS = 300;
        public const int CONTROLS_HIDE_DELAY_SECONDS = 2;
        public const int MINI_PLAYER_UPDATE_INTERVAL_MS = 500;
        public const int POSITION_UPDATE_INTERVAL_MS = 100;

        // Quick Connect
        public const int QUICK_CONNECT_CANCEL_REDIRECT_SECONDS = 1;
    }
}
