using System;
using Windows.System.Profile;

namespace Gelatinarm.Shared.Device
{
    public static class XboxDevice
    {
        public static bool IsXbox { get; } = ReadIsXbox();

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
