using Windows.System;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Device
{
    /// <summary>
    ///     The app's memory use for the log. Microsoft gives a UWP app 1 GB in the foreground on every
    ///     Xbox (128 MB in the background); under the Visual Studio debugger that limit is lifted, so
    ///     a debug run reports a larger one than users have.
    /// </summary>
    public static class AppMemory
    {
        private const double Megabyte = 1024.0 * 1024.0;

        public static void Log(ILogger logger, string moment, LogLevel level = LogLevel.Information)
        {
            logger.Log(level, "[MEMORY] {Moment}: {Usage:F2} MB / {Limit:F2} MB", moment,
                MemoryManager.AppMemoryUsage / Megabyte, MemoryManager.AppMemoryUsageLimit / Megabyte);
        }
    }
}
