using System;
using System.Linq;
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
        bool SupportsHDR10 { get; }
        bool SupportsHlg { get; }
        bool SupportsDolbyVision { get; }
        int MaxSupportedBitrate { get; }
        string GetDeviceName();
        string GetDeviceId();
    }

    public class UnifiedDeviceService : BaseService, IUnifiedDeviceService
    {
        private const int SeriesMaxBitrate = 120000000;
        private const int OneMaxBitrate = 80000000;

        private readonly DisplayInformation _displayInfo;
        private readonly bool _displaySupportsLowLatencyDolbyVision;
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
            _displaySupportsLowLatencyDolbyVision = DetectLowLatencyDolbyVisionDisplay();
            DetectHdrSupport();

            RegisterForDeviceEvents();

            Logger.LogInformation(
                "UnifiedDeviceService initialized - Xbox: {IsXbox}, Series: {IsXboxSeriesConsole}, Version: {DeviceVersion}", XboxDevice.IsXbox, IsXboxSeriesConsole, GetSystemVersion());
        }

        // Which codecs play is the device profile's job (DeviceProfileService, from Microsoft's
        // tables and a runtime HEVC check). This service only reports what the console and
        // the display can do.
        private bool IsXboxSeriesConsole { get; }

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

        // HLG carries no metadata to detect, so it rides on full HDR10 support on a Series console
        public bool SupportsHlg => _supportsHdr10 && IsXboxSeriesConsole;

        public bool SupportsDolbyVision => IsXboxSeriesConsole && _displaySupportsLowLatencyDolbyVision;

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
            ErrorHandler.Run(CreateErrorContext("OnAdvancedColorInfoChanged"), () =>
            {
                var newHdrInfo = sender.GetAdvancedColorInfo();
                var isHdrCurrentlyEnabled =
                    newHdrInfo.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange);
                _supportsHdr10 = isHdrCurrentlyEnabled;
                Logger.LogInformation("AdvancedColorInfoChanged: HDR Enabled: {IsHdrCurrentlyEnabled}", isHdrCurrentlyEnabled);
            });
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
                var supportsHdr10Plus = false;
                try
                {
                    _supportsHdr10 = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10);
                    try
                    {
                        supportsHdr10Plus = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10Plus);
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

                Logger.LogInformation(
                    "Display: HDR={HasHdr}, HDR10={SupportsHdr10}, HDR10+={SupportsHdr10Plus}, HLG={SupportsHlg}, DolbyVisionLowLatency={DisplaySupportsLowLatencyDolbyVision}, DolbyVision={SupportsDolbyVision}",
                    hasHdr, _supportsHdr10, supportsHdr10Plus, SupportsHlg, _displaySupportsLowLatencyDolbyVision, SupportsDolbyVision);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("DetectHdrSupport"));
            }
        }

        // The Xbox sends Dolby Vision only in its low-latency (player-led) form, for media apps as
        // well as games, so a TV that takes only TV-led Dolby Vision gets none from it; UWP has no
        // TV-led query anyway (HdrMetadataFormat stops at HDR10+). IsDolbyVisionLowLatencySupported
        // arrived in 10.0.17763, the minimum version, so it needs no ApiInformation check.
        private bool DetectLowLatencyDolbyVisionDisplay()
        {
            try
            {
                var modes = HdmiDisplayInformation.GetForCurrentView()?.GetSupportedDisplayModes();
                return modes?.Any(mode => mode.IsDolbyVisionLowLatencySupported) == true;
            }
            catch (Exception ex)
            {
                // Unknown counts as unsupported: the Dolby Vision files an HDR10 display can show are
                // accepted through SupportsHDR10 anyway, so this only sends profile 5 and the HLG- and
                // SDR-based files to a transcode instead of a direct play in the wrong colours.
                ErrorHandler.HandleError(ex, CreateErrorContext("DetectLowLatencyDolbyVisionDisplay", ErrorCategory.Media, ErrorSeverity.Warning));
                return false;
            }
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
