using System;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;

namespace Gelatinarm.Shared.Preferences
{
    public class AppPreferences
    {
        private int _controlsHideDelay = 3;

        // UI Preferences
        // Seconds. Clamped on the way in: versions before the 1-10 s range stored up to 30 s
        public int ControlsHideDelay
        {
            get => _controlsHideDelay;
            set => _controlsHideDelay = Math.Max(UiConstants.MinControlsHideDelaySeconds,
                Math.Min(UiConstants.MaxControlsHideDelaySeconds, value));
        }

        // Uniform (black bars) or UniformToFill (no black bars)
        public string VideoStretchMode { get; set; } = "Uniform";

        // Home Screen
        // Latest Movies / Latest TV Shows: newest releases (true) or newest additions to the library
        public bool SortLatestByReleaseDate { get; set; } = true;

        // Playback Behavior
        public bool AutoPlayNextEpisode { get; set; } = true;
        public bool AutoSkipIntroEnabled { get; set; } = false;
        public bool AutoSkipOutroEnabled { get; set; } = false;
        public bool PauseOnFocusLoss { get; set; } = false;

        // Audio
        public bool AudioNormalizationEnabled { get; set; } = false;

        // Network & Streaming
        public bool EnableDirectPlay { get; set; } = true;
        public bool AllowAudioStreamCopy { get; set; } = false; // Default to false to avoid audio compatibility issues

        // 0 means the console's own maximum: no practical cap, so any file the console can
        // decode may direct play, and transcodes are not squeezed below what the network allows.
        public int MaxStreamingBitrateMbps { get; set; } = 0;

        // Connection Settings
        public int ConnectionTimeout { get; set; } = SystemConstants.DefaultTimeoutSeconds;
    }
}
