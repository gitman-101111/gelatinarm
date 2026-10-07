using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Display.Core;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Device
{
    /// <summary>
    ///     Switches the TV between the console's default mode and a mode that suits a video: its
    ///     resolution, HDR, the video's frame rate. Xbox shows HDR only in a display mode the app
    ///     asks for; in the default mode the media pipeline tone-maps HDR to SDR and converts other
    ///     frame rates to the display's (Microsoft, "4K video playback for UWP apps on Xbox",
    ///     Switching Display modes).
    /// </summary>
    public interface IDisplayModeService
    {
        /// <summary>
        ///     The mode for a video: at least <paramref name="width" /> by <paramref name="height" />,
        ///     HDR when <paramref name="hdr" />, at the refresh rate that shows
        ///     <paramref name="frameRate" /> without conversion when one is given. Each falls back
        ///     to what the display is in when the console offers no such mode.
        /// </summary>
        Task MatchAsync(bool hdr, double? frameRate, int? width, int? height);

        /// <summary>
        ///     Back to the default mode, which the app's own pages are drawn for
        /// </summary>
        Task RestoreDefaultAsync();
    }

    public class DisplayModeService : BaseService, IDisplayModeService
    {
        // The mode the console was in before the first switch: its resolution is the least used,
        // and its refresh rate is the one to fall back to
        private HdmiDisplayMode _defaultMode;
        private bool _isSwitched;

        public DisplayModeService(ILogger<DisplayModeService> logger) : base(logger)
        {
        }

        public Task MatchAsync(bool hdr, double? frameRate, int? width, int? height)
        {
            if (!XboxDevice.IsXbox)
            {
                return Task.CompletedTask;
            }

            return OnUIThreadAsync("Match", async () =>
            {
                var hdmi = HdmiDisplayInformation.GetForCurrentView();
                if (hdmi == null)
                {
                    return;
                }

                if (!_isSwitched)
                {
                    _defaultMode = hdmi.GetCurrentDisplayMode();
                }

                if (_defaultMode == null)
                {
                    Logger.LogInformation("Display mode: the current mode is unknown; staying as it is");
                    return;
                }

                var modes = hdmi.GetSupportedDisplayModes();
                var mode = FindMode(modes, hdr, frameRate, width, height)
                           ?? (hdr ? FindMode(modes, false, frameRate, width, height) : null);
                if (mode == null)
                {
                    Logger.LogInformation("Display mode: none offered for HDR {Hdr}, {FrameRate} fps, {Width}x{Height}; " +
                                          "staying as it is", hdr, frameRate, width, height);
                    return;
                }

                if (mode.IsEqual(_defaultMode))
                {
                    await RestoreAsync(hdmi);
                    return;
                }

                var accepted = mode.IsSmpte2084Supported
                    ? await hdmi.RequestSetCurrentDisplayModeAsync(mode, HdmiDisplayHdrOption.Eotf2084)
                    : await hdmi.RequestSetCurrentDisplayModeAsync(mode);
                _isSwitched = _isSwitched || accepted;
                Logger.LogInformation(
                    "Display mode: {Width}x{Height} at {RefreshRate:0.###} Hz, {ColorSpace}, {BitsPerPixel} bits, HDR {Hdr} " +
                    "requested for {VideoWidth}x{VideoHeight} at {FrameRate} fps, HDR {VideoHdr} - accepted: {Accepted}",
                    mode.ResolutionWidthInRawPixels, mode.ResolutionHeightInRawPixels, mode.RefreshRate, mode.ColorSpace,
                    mode.BitsPerPixel, mode.IsSmpte2084Supported, width, height, frameRate, hdr, accepted);
            });
        }

        public Task RestoreDefaultAsync()
        {
            if (!_isSwitched)
            {
                return Task.CompletedTask;
            }

            return OnUIThreadAsync("RestoreDefault", () => RestoreAsync(HdmiDisplayInformation.GetForCurrentView()));
        }

        private async Task RestoreAsync(HdmiDisplayInformation hdmi)
        {
            if (_isSwitched && hdmi != null)
            {
                _isSwitched = false;
                await hdmi.SetDefaultDisplayModeAsync();
                Logger.LogInformation("Display mode: back to the default");
            }
        }

        // The display belongs to the view, so its mode is read and set on the UI thread. A
        // failure leaves the display as it is: playback goes on in the mode it has.
        private async Task OnUIThreadAsync(string operation, Func<Task> action)
        {
            try
            {
                await UiHelper.RunOnUIThreadAsync(action, Logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex,
                    CreateErrorContext(operation, ErrorCategory.Media, ErrorSeverity.Warning), false);
            }
        }

        // Among the modes at the video's resolution (FindResolution): HDR or not as asked, then the
        // refresh rate that fits the video, else the default's or 60 Hz; then BT.2020 before any
        // other colour space and the most bits per pixel (HDR), or the default's own colour format (SDR).
        private HdmiDisplayMode FindMode(IReadOnlyList<HdmiDisplayMode> modes, bool hdr, double? frameRate, int? width,
            int? height)
        {
            var resolution = FindResolution(modes, width, height);
            HdmiDisplayMode best = null;
            var bestRank = -1;
            foreach (var mode in modes)
            {
                if (mode.ResolutionWidthInRawPixels != resolution.Width ||
                    mode.ResolutionHeightInRawPixels != resolution.Height ||
                    mode.IsSmpte2084Supported != hdr)
                {
                    continue;
                }

                var rank = RefreshRank(mode.RefreshRate, frameRate) * 10000;
                if (rank == 0)
                {
                    continue;
                }

                rank += FormatRank(mode, hdr);
                if (rank > bestRank)
                {
                    best = mode;
                    bestRank = rank;
                }
            }

            return best;
        }

        // The smallest offered resolution that holds the video, never below the default's: a
        // video smaller than the display is scaled up by the console as before, a larger one is
        // not scaled down while the display has a mode for it. A portrait video is measured by
        // its longer side, as the display is.
        private (uint Width, uint Height) FindResolution(IReadOnlyList<HdmiDisplayMode> modes, int? width, int? height)
        {
            var videoWidth = (uint)Math.Max(Math.Max(width ?? 0, height ?? 0), 0);
            var videoHeight = (uint)Math.Max(Math.Min(width ?? 0, height ?? 0), 0);
            var neededWidth = Math.Max(videoWidth, _defaultMode.ResolutionWidthInRawPixels);
            var neededHeight = Math.Max(videoHeight, _defaultMode.ResolutionHeightInRawPixels);

            (uint Width, uint Height) smallestFit = (0, 0);
            (uint Width, uint Height) largest = (0, 0);
            foreach (var mode in modes)
            {
                var size = (Width: mode.ResolutionWidthInRawPixels, Height: mode.ResolutionHeightInRawPixels);
                if (Pixels(size) > Pixels(largest))
                {
                    largest = size;
                }

                var fits = size.Width >= neededWidth && size.Height >= neededHeight;
                if (fits && (smallestFit.Width == 0 || Pixels(size) < Pixels(smallestFit)))
                {
                    smallestFit = size;
                }
            }

            return smallestFit.Width > 0 ? smallestFit : largest;
        }

        private static ulong Pixels((uint Width, uint Height) size)
        {
            return (ulong)size.Width * size.Height;
        }

        private int FormatRank(HdmiDisplayMode mode, bool hdr)
        {
            if (hdr)
            {
                return (mode.ColorSpace == HdmiDisplayColorSpace.BT2020 ? 1000 : 0) + mode.BitsPerPixel;
            }

            var rank = mode.ColorSpace == _defaultMode.ColorSpace ? 1000 : 0;
            return mode.BitsPerPixel == _defaultMode.BitsPerPixel ? rank + 100 : rank;
        }

        // Best is a rate that shows the video's frames one for one, such as 23.976 fps at
        // 23.976 Hz. Next is one that shows each frame twice, as 25 fps at 50 Hz does. Then the
        // default rate, where the console converts, and failing that 60 Hz (a larger resolution
        // than the default's may not offer its rate). Any other rate is ruled out. Rates within
        // one percent of each other count as equal, which pairs 24 fps with 23.976 Hz.
        private int RefreshRank(double refreshRate, double? frameRate)
        {
            if (frameRate > 0)
            {
                var ratio = refreshRate / frameRate.Value;
                if (Math.Abs(ratio - 1) < 0.01)
                {
                    return 4;
                }

                if (Math.Abs(ratio - 2) < 0.02)
                {
                    return 3;
                }
            }

            if (Math.Abs(refreshRate - _defaultMode.RefreshRate) < 0.5)
            {
                return 2;
            }

            return Math.Abs(refreshRate - 60) < 0.5 || Math.Abs(refreshRate - 59.94) < 0.5 ? 1 : 0;
        }
    }
}
