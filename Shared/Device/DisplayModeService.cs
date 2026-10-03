using System;
using System.Threading.Tasks;
using Windows.Graphics.Display.Core;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Device
{
    /// <summary>
    ///     Switches the TV between the console's default mode and a mode that suits a video: HDR,
    ///     the video's frame rate, or both. Xbox shows HDR only in a display mode the app asks
    ///     for; in the default mode the media pipeline tone-maps HDR to SDR and converts other
    ///     frame rates to the display's (Microsoft, "4K video playback for UWP apps on Xbox",
    ///     Switching Display modes).
    /// </summary>
    public interface IDisplayModeService
    {
        /// <summary>
        ///     The mode for a video: HDR when <paramref name="hdr" />, at the refresh rate that
        ///     shows <paramref name="frameRate" /> without conversion when one is given. Each
        ///     falls back to what the display is in when the console offers no such mode.
        /// </summary>
        Task MatchAsync(bool hdr, double? frameRate);

        /// <summary>
        ///     Back to the default mode, which the app's own pages are drawn for
        /// </summary>
        Task RestoreDefaultAsync();
    }

    public class DisplayModeService : BaseService, IDisplayModeService
    {
        // The mode the console was in before the first switch: its resolution is kept, and its
        // refresh rate is the one to fall back to
        private HdmiDisplayMode _defaultMode;
        private bool _isSwitched;

        public DisplayModeService(ILogger<DisplayModeService> logger) : base(logger)
        {
        }

        public Task MatchAsync(bool hdr, double? frameRate)
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

                var mode = _defaultMode == null ? null : FindMode(hdmi, hdr, frameRate);
                if (mode == null)
                {
                    Logger.LogInformation("Display mode: none offered for HDR {Hdr} at {FrameRate} fps; staying as it is", hdr, frameRate);
                    return;
                }

                if (!mode.IsSmpte2084Supported && mode.IsEqual(_defaultMode))
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
                    "requested for {FrameRate} fps - accepted: {Accepted}",
                    mode.ResolutionWidthInRawPixels, mode.ResolutionHeightInRawPixels, mode.RefreshRate, mode.ColorSpace,
                    mode.BitsPerPixel, mode.IsSmpte2084Supported, frameRate, accepted);
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

        // Among the modes at the default resolution: HDR or not as asked, then the refresh rate
        // that fits the video, else the default's; then BT.2020 before any other colour space and
        // the most bits per pixel (HDR), or the default's own colour format (SDR).
        private HdmiDisplayMode FindMode(HdmiDisplayInformation hdmi, bool hdr, double? frameRate)
        {
            HdmiDisplayMode best = null;
            var bestRank = -1;
            foreach (var mode in hdmi.GetSupportedDisplayModes())
            {
                if (mode.ResolutionWidthInRawPixels != _defaultMode.ResolutionWidthInRawPixels ||
                    mode.ResolutionHeightInRawPixels != _defaultMode.ResolutionHeightInRawPixels ||
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
        // default rate, where the console converts. Any other rate is ruled out. Rates within
        // one percent of each other count as equal, which pairs 24 fps with 23.976 Hz.
        private int RefreshRank(double refreshRate, double? frameRate)
        {
            if (frameRate > 0)
            {
                var ratio = refreshRate / frameRate.Value;
                if (Math.Abs(ratio - 1) < 0.01)
                {
                    return 3;
                }

                if (Math.Abs(ratio - 2) < 0.02)
                {
                    return 2;
                }
            }

            return Math.Abs(refreshRate - _defaultMode.RefreshRate) < 0.5 ? 1 : 0;
        }
    }
}
