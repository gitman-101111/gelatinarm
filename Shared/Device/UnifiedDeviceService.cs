using System;
using System.Collections.Generic;
using Windows.Graphics.Display;
using Windows.Graphics.Display.Core;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.System;
using Windows.System.Profile;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Preferences;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Device
{
    public interface IUnifiedDeviceService : IDisposable
    {
        // What the display reports. HDR10 is whether the console offers this app an HDR display
        // mode: only the 4K edition is offered any, and only with a display that takes HDR.
        bool SupportsHDR10 { get; }
        bool SupportsHDR10Plus { get; }
        bool DisplaySupportsDolbyVision { get; }

        // The consoles that decode HLG and Dolby Vision
        bool IsXboxSeriesConsole { get; }
        int MaxSupportedBitrate { get; }
        string GetDeviceName();
        string GetDeviceId();
    }

    public class UnifiedDeviceService : BaseService, IUnifiedDeviceService
    {
        private const int SeriesMaxBitrate = 120000000;
        private const int OneMaxBitrate = 80000000;

        private readonly DisplayInformation _displayInfo;
        private readonly object _localSettingsLock = new();
        private string _deviceId;
        private volatile ApplicationDataContainer _localSettings;
        private volatile bool _supportsHdr10;

        public UnifiedDeviceService(ILogger<UnifiedDeviceService> logger) : base(logger)
        {
            _deviceId = Guid.NewGuid().ToString();
            try
            {
                _displayInfo = DisplayInformation.GetForCurrentView();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("UnifiedDeviceService"));
            }

            IsXboxSeriesConsole = DetectXboxSeriesModel();
            DetectHdrSupport();

            RegisterForDeviceEvents();

            Logger.LogInformation(
                "UnifiedDeviceService initialized - Xbox: {IsXbox}, Series: {IsXboxSeriesConsole}, Version: {DeviceVersion}", XboxDevice.IsXbox, IsXboxSeriesConsole, GetSystemVersion());
        }

        // Which codecs play is the device profile's job (DeviceProfileService, from Microsoft's
        // tables and a runtime HEVC check). This service only reports what the console and
        // the display can do.
        public bool IsXboxSeriesConsole { get; }

        public string GetDeviceName()
        {
            try
            {
                var di = new EasClientDeviceInformation();
                return !string.IsNullOrEmpty(di.FriendlyName) ? di.FriendlyName : "Xbox Device";
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("GetDeviceName"));
                return "Xbox Device";
            }
        }

        public string GetDeviceId()
        {
            EnsureLocalSettings();
            return _deviceId;
        }

        public bool SupportsHDR10 => _supportsHdr10;
        public bool SupportsHDR10Plus { get; private set; }
        public bool DisplaySupportsDolbyVision { get; private set; }

        public int MaxSupportedBitrate => IsXboxSeriesConsole ? SeriesMaxBitrate : OneMaxBitrate;

        private void RegisterForDeviceEvents()
        {
            if (_displayInfo != null)
            {
                _displayInfo.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged;
            }
            else
            {
                Logger.LogWarning("DisplayInfo is null, cannot subscribe to AdvancedColorInfoChanged.");
            }
        }

        private void UnregisterDeviceEvents()
        {
            if (_displayInfo != null)
            {
                _displayInfo.AdvancedColorInfoChanged -= OnAdvancedColorInfoChanged;
            }
        }

        private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args)
        {
            // Raised when the display or its mode changes, the app's own switch to HDR included
            ErrorHandler.Run(CreateErrorContext("OnAdvancedColorInfoChanged"), DetectHdrSupport);
        }

        private void DetectHdrSupport()
        {
            try
            {
                if (_displayInfo == null)
                {
                    return;
                }

                var aci = _displayInfo.GetAdvancedColorInfo();
                var hasHdr = aci.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange);
                try
                {
                    _supportsHdr10 = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10);
                    try
                    {
                        SupportsHDR10Plus = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10Plus);
                    }
                    catch
                    {
                        // Hdr10Plus is missing from older SDK contracts; treat as unsupported.
                    }
                }
                catch (Exception ex)
                {
                    // Basic HDR detection stands in
                    ErrorHandler.HandleError(ex, CreateErrorContext("DetectHdrSupport", ErrorCategory.Media, ErrorSeverity.Warning));
                    _supportsHdr10 = hasHdr;
                }

                try
                {
                    ReadHdmiModes();
                }
                catch (Exception ex)
                {
                    ErrorHandler.HandleError(ex, CreateErrorContext("ReadHdmiModes", ErrorCategory.Media, ErrorSeverity.Warning));
                }

                Logger.LogInformation(
                    "Display reports: HDR={HasHdr}, HDR10={SupportsHdr10}, HDR10+={SupportsHdr10Plus}, Dolby Vision={DisplaySupportsDolbyVision}; " +
                    "colour mode now {CurrentKind}, peak {MaxNits} nits, SDR white {SdrWhiteNits} nits",
                    hasHdr, _supportsHdr10, SupportsHDR10Plus, DisplaySupportsDolbyVision, aci.CurrentAdvancedColorKind,
                    aci.MaxLuminanceInNits, aci.SdrWhiteLevelInNits);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("DetectHdrSupport"));
            }
        }

        // The HDMI modes the console offers the app say what it can show: Microsoft describes
        // them as following "device type, specifications, and settings". The standard edition
        // was offered 6 modes, none HDR, and the 4K edition 12, three of them HDR, on the same
        // console and display (2026-10-03); the colour information above reported HDR10 in
        // both. So HDR10 is read from here. Dolby Vision likewise, though not verified on a
        // display that has it.
        private void ReadHdmiModes()
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi == null)
            {
                Logger.LogInformation("HDMI display information is not available");
                return;
            }

            var st2084Modes = 0;
            var bt2020Modes = 0;
            var dolbyVisionModes = 0;
            var offered = new List<string>();
            var modes = hdmi.GetSupportedDisplayModes();
            foreach (var mode in modes)
            {
                offered.Add($"{mode.ResolutionWidthInRawPixels}x{mode.ResolutionHeightInRawPixels}@{mode.RefreshRate:0.###} " +
                            $"{mode.ColorSpace} {mode.BitsPerPixel}b{(mode.IsSmpte2084Supported ? " HDR" : string.Empty)}");
                st2084Modes += mode.IsSmpte2084Supported ? 1 : 0;
                bt2020Modes += mode.ColorSpace == HdmiDisplayColorSpace.BT2020 ? 1 : 0;
                dolbyVisionModes += mode.IsDolbyVisionLowLatencySupported ? 1 : 0;
            }

            _supportsHdr10 = st2084Modes > 0;
            DisplaySupportsDolbyVision = dolbyVisionModes > 0;

            var current = hdmi.GetCurrentDisplayMode();
            Logger.LogInformation(
                "HDMI mode now: {Width}x{Height} at {RefreshRate} Hz, {ColorSpace}, {BitsPerPixel} bits, ST 2084 {St2084}, " +
                "HDR metadata {Metadata}, Dolby Vision {DolbyVision}. Modes offered: {ModeCount}, of which ST 2084 {St2084Modes}, " +
                "BT.2020 {Bt2020Modes}, Dolby Vision {DolbyVisionModes}: {Modes}",
                current?.ResolutionWidthInRawPixels, current?.ResolutionHeightInRawPixels, current?.RefreshRate,
                current?.ColorSpace, current?.BitsPerPixel, current?.IsSmpte2084Supported, current?.Is2086MetadataSupported,
                current?.IsDolbyVisionLowLatencySupported, modes.Count, st2084Modes, bt2020Modes, dolbyVisionModes,
                string.Join("; ", offered));
        }

        private bool DetectXboxSeriesModel()
        {
            if (!XboxDevice.IsXbox)
            {
                return false;
            }

            try
            {
                var deviceInfo = new EasClientDeviceInformation();
                var systemModel = deviceInfo.SystemProductName?.ToLower() ?? "";
                var systemSku = deviceInfo.SystemSku?.ToLower() ?? "";

                Logger.LogDebug(
                    "Xbox device detection - Model: {SystemModel}, SKU: {SystemSku}, Memory: {MemoryLimit}MB", systemModel, systemSku, MemoryManager.AppMemoryUsageLimit / 1024 / 1024);

                // The model name alone: an app gets 1 GB on every Xbox (Microsoft, "System resources
                // for UWP apps and games on Xbox"), and the debugger lifts that to ~3 GB on any console,
                // so memory cannot tell the generations apart
                return systemModel.Contains("series") ||
                       systemSku.Contains("series") ||
                       systemModel.Contains("anaconda") || // Series X codename
                       systemModel.Contains("lockhart") || // Series S codename
                       systemModel.Contains("xbox2020") || // Alternative identifier
                       systemSku.Contains("anaconda") ||
                       systemSku.Contains("lockhart");
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("DetectXboxSeriesModel"));
                return false;
            }
        }

        private string GetSystemVersion()
        {
            try
            {
                var v = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
                if (ulong.TryParse(v, out var vb))
                {
                    var ma = (vb & 0xFFFF000000000000L) >> 48;
                    var mi = (vb & 0x0000FFFF00000000L) >> 32;
                    var bu = (vb & 0x00000000FFFF0000L) >> 16;
                    return $"{ma}.{mi}.{bu}";
                }

                return "Unknown";
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("GetSystemVersion"));
                return "Unknown";
            }
        }

        private void EnsureLocalSettings()
        {
            if (_localSettings == null)
            {
                lock (_localSettingsLock)
                {
                    if (_localSettings == null)
                    {
                        try
                        {
                            _localSettings = ApplicationData.Current.LocalSettings;
                            _deviceId = GetOrCreateDeviceId();
                        }
                        catch (Exception ex)
                        {
                            ErrorHandler.HandleError(ex, CreateErrorContext("EnsureLocalSettings"));
                        }
                    }
                }
            }
        }

        // Called by EnsureLocalSettings once the settings are open
        private string GetOrCreateDeviceId()
        {
            try
            {
                if (_localSettings.Values.TryGetValue(PreferenceConstants.DeviceId, out var id) && id is string dId &&
                    !string.IsNullOrEmpty(dId))
                {
                    return dId;
                }

                var nId = Guid.NewGuid().ToString();
                _localSettings.Values[PreferenceConstants.DeviceId] = nId;
                return nId;
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("GetOrCreateDeviceId"));
                return "jellyfin-xbox-" + Environment.MachineName.GetHashCode().ToString("X8");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            if (disposing)
            {
                UnregisterDeviceEvents();
            }

            base.Dispose(disposing);
        }
    }
}
