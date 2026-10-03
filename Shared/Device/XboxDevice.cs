using System;
using Windows.System.Profile;

namespace Gelatinarm.Shared.Device
{
    public static class XboxDevice
    {
        public static bool IsXbox { get; } = ReadIsXbox();

        /// <summary>
        ///     Whether this build is the 4K edition (Docs/DEV_SETUP.md). Xbox gives 4K video memory
        ///     and HDR display modes only to an app with the hevcPlayback capability, which the 4K
        ///     edition has in place of background music.
        /// </summary>
        public static bool IsFourKEdition
        {
            get
            {
#if EDITION_4K
                return true;
#else
                return false;
#endif
            }
        }

        private static bool ReadIsXbox()
        {
            try
            {
                return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Xbox",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Unknown family: the desktop code paths are the safer guess
                return false;
            }
        }
    }
}
