using System;
using Gelatinarm.Constants;

namespace Gelatinarm.Models
{
    /// <summary>
    ///     Consolidated app preferences model combining user and playback preferences
    /// </summary>
    public class AppPreferences
    {
        // === UI Preferences ===
        public int ControlsHideDelay { get; set; } = 3; // seconds

        public string VideoStretchMode { get; set; } =
            "Uniform"; // Uniform (black bars) or UniformToFill (no black bars)

        // === Playback Behavior ===
        public bool AutoPlayNextEpisode { get; set; } = true;
        public bool AutoSkipIntroEnabled { get; set; } = false;
        public bool AutoSkipOutroEnabled { get; set; } = false;
        public bool PauseOnFocusLoss { get; set; } = false; // Whether to pause when Xbox guide is opened

        // === Skip Settings ===
        // Skip values are currently hardcoded: forward=30s, backward=10s

        // === Audio & Subtitle Preferences ===
        public int DefaultSubtitleStreamIndex { get; set; } = -1;

        // === Network & Streaming ===
        public bool EnableDirectPlay { get; set; } = true; // Allow direct play when format is compatible
        public bool AllowAudioStreamCopy { get; set; } = false; // Default to false to avoid audio compatibility issues

        // === UI Settings ===
        public double TextSize { get; set; } = 14.0;

        // === Connection Settings ===
        public int ConnectionTimeout { get; set; } = SystemConstants.DEFAULT_TIMEOUT_SECONDS;
        public bool IgnoreCertificateErrors { get; set; } = true;

        // === Metadata ===
        public DateTime LastModified { get; set; } = DateTime.Now;
    }
}
