using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.Gaming.Input;
using Windows.Graphics.Display;
using Windows.Media.Devices;
using Windows.Media.Protection;
using Windows.Networking.Connectivity;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using GamepadButtons = Windows.Gaming.Input.GamepadButtons;

namespace Gelatinarm.Services
{
    public class DeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
    }

    public class GamepadEventArgs : EventArgs
    {
        public Gamepad Gamepad { get; set; }
        public int ControllerId { get; set; }
    }

    public class GamepadButtonStateChangedEventArgs : EventArgs
    {
        public GamepadButtons Button { get; set; }
        public bool IsPressed { get; set; }
        public GamepadReading Reading { get; set; }
    }

    public class SystemEventArgs : EventArgs
    {
        public string EventType { get; set; }
        public object Data { get; set; }
    }

    public interface IUnifiedDeviceService : IDisposable
    {
        bool IsXboxEnvironment { get; }
        bool IsXboxSeriesConsole { get; }
        bool IsXboxSeriesDevice { get; }
        bool IsNetworkAvailable { get; }
        bool SupportsHDR { get; }
        bool SupportsHDR10 { get; }
        bool SupportsHDR10Plus { get; }
        bool SupportsHLG { get; }
        bool SupportsDolbyVision { get; }
        bool SupportsHardwareDecoding { get; }
        int MaxSupportedBitrate { get; }
        bool IsFullScreenMode { get; }
        bool IsControllerConnected { get; }
        int ConnectedControllerCount { get; }
        GamepadButtons CurrentButtonState { get; }
        bool IsXboxNavigationEnabled { get; }
        bool IsMonitoring { get; }
        string GetDeviceName();
        string GetDeviceId();
        Task<DeviceInfo> GetDeviceInfoAsync();
        ConnectionType GetCurrentConnectionType();
        Task EnterFullScreenModeAsync();
        Task ExitFullScreenModeAsync();
        void EnableXboxNavigation();
        void DisableMouseCursor();
        Task StartMonitoringAsync();
        Task StopMonitoringAsync();
        event EventHandler<GamepadEventArgs> ControllerConnected;
        event EventHandler<GamepadEventArgs> ControllerDisconnected;
        event EventHandler<GamepadButtons> ButtonReleased;
        event EventHandler<GamepadButtonStateChangedEventArgs> ButtonStateChanged;
        event EventHandler<SystemEventArgs> SystemEvent;
    }

    public class UnifiedDeviceService : BaseService, IUnifiedDeviceService
    {
        private readonly ApplicationView _appView;
        private readonly Dictionary<string, bool> _codecSupport;
        private readonly List<Gamepad> _connectedGamepads = new();
        private readonly object _gamepadLock = new object();
        private readonly string _deviceVersion;
        private readonly CoreDispatcher _dispatcher;
        private readonly DisplayInformation _displayInfo;


        // Device Info implementation
        private DeviceInfo _cachedDeviceInfo;
        private string _deviceId;
        private volatile bool _disposed = false;
        private volatile int _inputTickCount = 0;
        private DispatcherTimer _inputTimer;
        private GamepadReading _lastReading;
        private volatile ApplicationDataContainer _localSettings;
        private readonly object _localSettingsLock = new object();
        private volatile int _monitoringTickCount = 0;
        private DispatcherTimer _monitoringTimer;
        private MediaProtectionManager _protectionManager;
        private DateTime _startTime;

        public UnifiedDeviceService(ILogger<UnifiedDeviceService> logger, CoreDispatcher dispatcher) : base(logger)
        {
            Logger.LogInformation($"[{DateTime.Now:HH:mm:ss.fff}] UnifiedDeviceService constructor starting");

            _dispatcher = dispatcher;

            // Defer ApplicationData access to avoid early initialization issues

            // Initialize codec support dictionary
            _codecSupport = new Dictionary<string, bool>();

            // Set a temporary device ID until we can access ApplicationData
            _deviceId = Guid.NewGuid().ToString();
            try
            {
                _appView = ApplicationView.GetForCurrentView();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to get ApplicationView");
                _appView = null;
            }

            try
            {
                _displayInfo = DisplayInformation.GetForCurrentView();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to get DisplayInformation");
                _displayInfo = null;
            }

            _protectionManager = new MediaProtectionManager();
            _codecSupport = new Dictionary<string, bool>();
            SupportsHardwareAcceleration = DetectXboxEnvironmentLogic();
            Supports4K = DetectXboxSeriesModel();
            _deviceVersion = GetSystemVersionLogic();
            InitializeCapabilitiesAndCodecs();

            RegisterForDeviceEvents();

            Logger.LogInformation(
                $"UnifiedDeviceService initialized - Xbox: {SupportsHardwareAcceleration}, Series: {Supports4K}, Version: {_deviceVersion}");
        }

        public bool Supports4K { get; }

        public bool SupportsHEVC => _codecSupport.GetValueOrDefault("HEVC", true);
        public bool SupportsAV1 => _codecSupport.GetValueOrDefault("AV1", Supports4K);
        public bool SupportsVP9 => _codecSupport.GetValueOrDefault("VP9", true);

        // Audio codec support
        public bool SupportsAAC => _codecSupport.GetValueOrDefault("AAC", true);
        public bool SupportsMP3 => _codecSupport.GetValueOrDefault("MP3", true);
        public bool SupportsFLAC => _codecSupport.GetValueOrDefault("FLAC", true);
        public bool SupportsALAC => _codecSupport.GetValueOrDefault("ALAC", false);
        public bool SupportsOGG => _codecSupport.GetValueOrDefault("OGG", true);
        public bool SupportsOPUS => _codecSupport.GetValueOrDefault("OPUS", true);
        public bool SupportsAC3 => _codecSupport.GetValueOrDefault("AC3", false);
        public bool SupportsEAC3 => _codecSupport.GetValueOrDefault("EAC3", false);
        public bool SupportsDTS => _codecSupport.GetValueOrDefault("DTS", false);
        public bool SupportsTrueHD => _codecSupport.GetValueOrDefault("TrueHD", false);
        public bool SupportsDTSHD => _codecSupport.GetValueOrDefault("DTS-HD", false);
        public bool SupportsDolbyAtmos => _codecSupport.GetValueOrDefault("DolbyAtmos", Supports4K);
        public bool SupportsHardwareAcceleration { get; }

        public event EventHandler<GamepadEventArgs> ControllerConnected;
        public event EventHandler<GamepadEventArgs> ControllerDisconnected;
        public event EventHandler<GamepadButtons> ButtonReleased;
        public event EventHandler<GamepadButtonStateChangedEventArgs> ButtonStateChanged;
        public event EventHandler<SystemEventArgs> SystemEvent;
        public bool IsXboxEnvironment => SupportsHardwareAcceleration;

        public bool IsFullScreenMode
        {
            get
            {
                try
                {
                    return _appView?.IsFullScreenMode ?? false;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get full screen mode");
                    return false;
                }
            }
        }

        public bool IsControllerConnected { get; private set; }
        public int ConnectedControllerCount { get; private set; }
        public GamepadButtons CurrentButtonState { get; private set; }

        public bool IsXboxNavigationEnabled { get; private set; }

        public bool IsMonitoring { get; private set; }

        public string GetDeviceName()
        {
            try
            {
                var di = new EasClientDeviceInformation();
                return !string.IsNullOrEmpty(di.FriendlyName) ? di.FriendlyName : "Xbox Device";
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("GetDeviceName");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }

                return "Xbox Device";
            }
        }

        public Task<DeviceInfo> GetDeviceInfoAsync()
        {
            if (_cachedDeviceInfo != null)
            {
                return Task.FromResult(_cachedDeviceInfo);
            }

            _cachedDeviceInfo = new DeviceInfo { Id = _deviceId, Name = GetDeviceName(), Version = GetAppVersion() };

            return Task.FromResult(_cachedDeviceInfo);
        }

        public bool IsXboxSeriesDevice => Supports4K;
        public bool IsNetworkAvailable => true;

        public string GetDeviceId()
        {
            return _deviceId;
        }

        public bool IsXboxSeriesConsole => Supports4K;

        // Video codec support
        public bool SupportsHDR => _codecSupport.GetValueOrDefault("HDR10", false);
        public bool SupportsHDR10 => _codecSupport.GetValueOrDefault("HDR10", false);
        public bool SupportsHDR10Plus => _codecSupport.GetValueOrDefault("HDR10Plus", false);
        public bool SupportsHLG => _codecSupport.GetValueOrDefault("HLG", false);
        public bool SupportsDolbyVision => _codecSupport.GetValueOrDefault("DolbyVision", Supports4K);

        // Hardware capabilities
        public bool SupportsHardwareDecoding => SupportsHardwareAcceleration;
        public int MaxSupportedBitrate => Supports4K ? 120000000 : 80000000;

        public ConnectionType GetCurrentConnectionType()
        {
            try
            {
                var p = NetworkInformation.GetInternetConnectionProfile();
                if (p == null)
                {
                    return ConnectionType.Unknown;
                }

                if (p.IsWlanConnectionProfile)
                {
                    return ConnectionType.WiFi;
                }

                if (p.IsWwanConnectionProfile)
                {
                    return ConnectionType.Cellular;
                }

                return ConnectionType.Ethernet;
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("GetCurrentConnectionType");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }

                return ConnectionType.Unknown;
            }
        }

        public async Task EnterFullScreenModeAsync()
        {
            var context = CreateErrorContext("EnterFullScreenMode");
            try
            {
                if (_appView != null && !_appView.IsFullScreenMode)
                {
                    var s = _appView.TryEnterFullScreenMode();
                    Logger.LogInformation($"EnterFullScreenMode:{s}");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public async Task ExitFullScreenModeAsync()
        {
            var context = CreateErrorContext("ExitFullScreenMode");
            try
            {
                if (_appView != null && _appView.IsFullScreenMode)
                {
                    _appView.ExitFullScreenMode();
                    Logger.LogInformation("ExitFullScreenMode");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public void EnableXboxNavigation()
        {
            try
            {
                Application.Current.RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
                IsXboxNavigationEnabled = true;
                Logger.LogInformation("Xbox nav enabled");
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("EnableXboxNavigation");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        public void DisableMouseCursor()
        {
            if (_dispatcher != null)
            {
                FireAndForget(async () =>
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        try
                        {
                            Window.Current.CoreWindow.PointerCursor = null;
                        }
                        catch (Exception ex)
                        {
                            var context = CreateErrorContext("DisableMouseCursor");
                            if (ErrorHandler is ErrorHandlingService errorService)
                            {
                                errorService.HandleError(ex, context);
                            }
                            else
                            {
                                AsyncHelper.FireAndForget(async () =>
                                    await ErrorHandler.HandleErrorAsync(ex, context, false));
                            }
                        }
                    }, _dispatcher, Logger);
                });
            }
            else
            {
                Logger.LogWarning("Dispatcher null for DisableMouseCursor");
            }

            Logger.LogInformation("Mouse cursor disable attempt.");
        }

        public async Task StartMonitoringAsync()
        {
            var context = CreateErrorContext("StartMonitoring");
            try
            {
                if (IsMonitoring)
                {
                    Logger.LogWarning("Monitoring active");
                    return;
                }

                Logger.LogInformation("Starting platform monitoring");

                // Start the monitoring timer (5 second intervals)
                if (_monitoringTimer != null)
                {
                    _monitoringTimer.Start();
                }
                else
                {
                    Logger.LogWarning("Monitoring timer null");
                }

                Logger.LogInformation("Skipping input timer on Xbox due to compatibility issues");

                IsMonitoring = true;
                _startTime = DateTime.Now;
                _inputTickCount = 0;
                _monitoringTickCount = 0;
                Logger.LogInformation($"Monitor started {_startTime:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public async Task StopMonitoringAsync()
        {
            var context = CreateErrorContext("StopMonitoring");
            try
            {
                if (!IsMonitoring)
                {
                    return;
                }

                Logger.LogInformation("Stopping platform monitoring");
                _monitoringTimer?.Stop();
                _inputTimer?.Stop();
                IsMonitoring = false;
                Logger.LogInformation("Platform monitoring stopped");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public event EventHandler<bool> HardwareDecodingStatusChanged;

        private void RegisterForDeviceEvents()
        {
            if (_dispatcher != null)
            {
                _monitoringTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(RetryConstants.DEVICE_MONITOR_INTERVAL_SECONDS)
                };
                _monitoringTimer.Tick += OnMonitoringTick;
                try
                {
                    var cv = SystemNavigationManager.GetForCurrentView();
                    if (cv != null)
                    {
                        cv.BackRequested += OnBackRequested;
                    }
                    else
                    {
                        Logger.LogWarning("SystemNavigationManager.GetForCurrentView() is null for BackRequested.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to register BackRequested handler");
                }
            }
            else { Logger.LogWarning("Dispatcher null, timer/BackRequested init deferred."); }

            // Re-enable gamepad events
            Gamepad.GamepadAdded += OnGamepadAdded;
            Gamepad.GamepadRemoved += OnGamepadRemoved;
            foreach (var gamepad in Gamepad.Gamepads) { AddGamepadLogic(gamepad); }

            if (_displayInfo != null)
            {
                try { _displayInfo.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged; }
                catch (Exception ex) { Logger.LogWarning(ex, "Failed to subscribe to AdvancedColorInfoChanged."); }
            }
            else { Logger.LogWarning("DisplayInfo is null, cannot subscribe to AdvancedColorInfoChanged."); }

            try { MediaDevice.DefaultAudioRenderDeviceChanged += OnDefaultAudioRenderDeviceChanged; }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to subscribe to DefaultAudioRenderDeviceChanged."); }
        }

        private void UnregisterDeviceEvents()
        {
            if (_monitoringTimer != null)
            {
                _monitoringTimer.Tick -= OnMonitoringTick;
            }

            if (_inputTimer != null)
            {
                _inputTimer.Tick -= OnInputTick;
            }

            if (_dispatcher != null && SystemNavigationManager.GetForCurrentView() != null)
            {
                try
                {
                    SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
                }
                catch (Exception ex)
                {
                    var context = CreateErrorContext("UnsubscribeBackRequested");
                    if (ErrorHandler is ErrorHandlingService errorService)
                    {
                        errorService.HandleError(ex, context);
                    }
                    else
                    {
                        AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                    }
                }
            }

            Gamepad.GamepadAdded -= OnGamepadAdded;
            Gamepad.GamepadRemoved -= OnGamepadRemoved;
            if (_displayInfo != null)
            {
                try
                {
                    _displayInfo.AdvancedColorInfoChanged -= OnAdvancedColorInfoChanged;
                }
                catch (Exception ex)
                {
                    var context = CreateErrorContext("UnsubscribeAdvancedColorInfo");
                    if (ErrorHandler is ErrorHandlingService errorService)
                    {
                        errorService.HandleError(ex, context);
                    }
                    else
                    {
                        AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                    }
                }
            }

            try
            {
                MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultAudioRenderDeviceChanged;
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("UnsubscribeAudioDevice");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args)
        {
            var context = CreateErrorContext("OnAdvancedColorInfoChanged");
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    var newHdrInfo = sender.GetAdvancedColorInfo();
                    var isHdrCurrentlyEnabled =
                        newHdrInfo.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange);
                    _codecSupport["HDR10"] = isHdrCurrentlyEnabled;
                    Logger.LogInformation($"AdvancedColorInfoChanged: HDR Enabled: {isHdrCurrentlyEnabled}");
                    HardwareDecodingStatusChanged?.Invoke(this, SupportsHardwareDecoding);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void OnDefaultAudioRenderDeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
        {
            var context = CreateErrorContext("OnDefaultAudioRenderDeviceChanged");
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    Logger.LogInformation($"DefaultAudioRenderDeviceChanged: ID: {args.Id}, Role: {args.Role}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void InitializeCapabilitiesAndCodecs()
        {
            Logger.LogInformation("Initializing capabilities and codecs...");
            try
            {
                // HDR support detection based on display capabilities
                if (_displayInfo != null)
                {
                    var aci = _displayInfo.GetAdvancedColorInfo();

                    // Check basic HDR support
                    var hasHDR = aci.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange);

                    // Check specific HDR format support
                    var supportsHDR10 = false;
                    var supportsHDR10Plus = false;

                    try
                    {
                        // Check if display supports HDR10 metadata
                        supportsHDR10 = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10);

                        // Check HDR10+ support (may not be available on all SDK versions)
                        try
                        {
                            supportsHDR10Plus = aci.IsHdrMetadataFormatCurrentlySupported(HdrMetadataFormat.Hdr10Plus);
                        }
                        catch
                        {
                            // HDR10+ enum value might not exist on older SDK
                            supportsHDR10Plus = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex,
                            "Failed to check HDR metadata format support, falling back to basic HDR detection");
                        supportsHDR10 = hasHDR;
                    }

                    _codecSupport["HDR10"] = supportsHDR10;
                    _codecSupport["HDR10Plus"] = supportsHDR10Plus;

                    // HLG support detection
                    // Since HLG doesn't use metadata, we can't detect it directly
                    // Only enable HLG if we have full HDR10 support AND we're on Xbox Series
                    // This prevents HLG on displays that "don't support all HDR10 modes"
                    _codecSupport["HLG"] = supportsHDR10 && Supports4K;

                    Logger.LogInformation(
                        $"HDR Detection - Basic HDR: {hasHDR}, HDR10: {supportsHDR10}, HDR10+: {supportsHDR10Plus}, HLG: {_codecSupport["HLG"]}");
                }
                else
                {
                    _codecSupport["HDR10"] = false;
                    _codecSupport["HDR10Plus"] = false;
                    _codecSupport["HLG"] = false;
                }

                // Set video codec support based on Microsoft documentation
                // https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/supported-codecs#xbox
                if (SupportsHardwareAcceleration)
                {
                    // Video codecs supported on all Xbox consoles per Microsoft docs
                    _codecSupport["H264"] = true; // H.264
                    _codecSupport["HEVC"] = true; // H.265 (HEVC)
                    _codecSupport["VP8"] = true; // VP8
                    _codecSupport["VP9"] = true; // VP9
                    _codecSupport["AV1"] = true; // AV1 (listed in MS docs)
                    _codecSupport["VC-1"] = true; // VC-1
                    _codecSupport["MPEG-1"] = true; // MPEG-1
                    _codecSupport["MPEG-2"] = true; // MPEG-2
                    _codecSupport["MPEG-4"] = true; // MPEG-4 Part 2
                    _codecSupport["H263"] = true; // H.263
                    _codecSupport["MJPEG"] = true; // Motion JPEG
                    _codecSupport["DV"] = true; // DV

                    // Dolby Vision/Atmos are capabilities, not base codecs
                    // They're implemented as metadata on top of HEVC/EAC3
                    if (Supports4K)
                    {
                        _codecSupport["DolbyVision"] = true; // Series X/S support Dolby Vision
                        _codecSupport["DolbyAtmos"] = true; // Series X/S support Dolby Atmos
                    }
                    else
                    {
                        _codecSupport["DolbyVision"] = false; // Xbox One doesn't support Dolby Vision
                        _codecSupport["DolbyAtmos"] = false; // Xbox One doesn't support Dolby Atmos
                    }
                }
                else
                {
                    // Default for non-Xbox platforms - conservative support
                    _codecSupport["H264"] = true;
                    _codecSupport["HEVC"] = false;
                    _codecSupport["AV1"] = false;
                    _codecSupport["VP8"] = true;
                    _codecSupport["VP9"] = false;
                    _codecSupport["DolbyAtmos"] = false;
                    _codecSupport["DolbyVision"] = false;
                }

                // Set audio codec support based on Microsoft documentation
                // https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/supported-codecs#xbox
                if (SupportsHardwareAcceleration)
                {
                    // Audio codecs supported on all Xbox consoles per Microsoft docs
                    _codecSupport["AAC"] = true; // AAC-LC
                    _codecSupport["HE-AAC"] = true; // HE-AAC v1/v2
                    _codecSupport["MP3"] = true; // MP3
                    _codecSupport["FLAC"] = true; // FLAC
                    _codecSupport["ALAC"] = true; // Apple Lossless (ALAC)
                    _codecSupport["WMA"] = true; // WMA 1/2/3
                    _codecSupport["WMA-Pro"] = true; // WMA Pro
                    _codecSupport["PCM"] = true; // LPCM
                    _codecSupport["AC3"] = true; // AC3 (Dolby Digital)
                    _codecSupport["AMR-NB"] = true; // AMR-NB

                    // Note: Microsoft docs don't list these, but they're commonly supported
                    _codecSupport["EAC3"] = true; // Dolby Digital Plus (common on streaming)
                    _codecSupport["OPUS"] = true; // Opus (used in WebM)
                    _codecSupport["VORBIS"] = true; // Vorbis (used in WebM/OGG)

                    // Advanced audio codecs - not listed in MS docs, so conservative
                    _codecSupport["DTS"] = false; // DTS not officially listed
                    _codecSupport["TrueHD"] = false; // TrueHD not officially listed
                    _codecSupport["DTS-HD"] = false; // DTS-HD not officially listed

                    // Series X/S may have additional support via Dolby Atmos
                    if (Supports4K)
                    {
                        _codecSupport["EAC3-Atmos"] = true; // E-AC3 with Atmos metadata
                    }
                }
                else
                {
                    // Default audio codec support for non-Xbox platforms
                    _codecSupport["AAC"] = true;
                    _codecSupport["MP3"] = true;
                    _codecSupport["FLAC"] = true;
                    _codecSupport["PCM"] = true;
                    _codecSupport["VORBIS"] = true;

                    // Advanced audio codecs typically need specific hardware
                    _codecSupport["AC3"] = false;
                    _codecSupport["EAC3"] = false;
                    _codecSupport["DTS"] = false;
                    _codecSupport["TrueHD"] = false;
                    _codecSupport["DTS-HD"] = false;
                }

                HardwareDecodingStatusChanged?.Invoke(this, SupportsHardwareDecoding);

                Logger.LogInformation("Capabilities and codecs initialized.");
                Logger.LogInformation(
                    $"Video Codecs: H264={_codecSupport.GetValueOrDefault("H264")}, HEVC={SupportsHEVC}, AV1={SupportsAV1}, VP8={_codecSupport.GetValueOrDefault("VP8")}, VP9={SupportsVP9}");
                Logger.LogInformation(
                    $"Display: HDR10={SupportsHDR}, HDR10+={SupportsHDR10Plus}, HLG={SupportsHLG}, DolbyVision={SupportsDolbyVision}");
                Logger.LogInformation(
                    $"Audio Codecs: AAC={_codecSupport.GetValueOrDefault("AAC")}, MP3={_codecSupport.GetValueOrDefault("MP3")}, FLAC={_codecSupport.GetValueOrDefault("FLAC")}, ALAC={_codecSupport.GetValueOrDefault("ALAC")}, AC3={_codecSupport.GetValueOrDefault("AC3")}");
                Logger.LogInformation(
                    $"Audio Features: DolbyAtmos={SupportsDolbyAtmos}, EAC3={_codecSupport.GetValueOrDefault("EAC3")}, OPUS={_codecSupport.GetValueOrDefault("OPUS")}, Vorbis={_codecSupport.GetValueOrDefault("VORBIS")}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to initialize capabilities and codecs");
            }
        }

        private bool DetectXboxEnvironmentLogic()
        {
            try
            {
                return AnalyticsInfo.VersionInfo.DeviceFamily.Equals("Windows.Xbox",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to detect Xbox environment");
                return false;
            }
        }

        private bool DetectXboxSeriesModel()
        {
            if (!SupportsHardwareAcceleration)
            {
                return false;
            }

            try
            {
                // Get memory limit for initial detection
                var memoryLimit = MemoryManager.AppMemoryUsageLimit;

                // Get additional device info for better detection
                var deviceInfo = new EasClientDeviceInformation();
                var systemModel = deviceInfo.SystemProductName?.ToLower() ?? "";
                var systemSku = deviceInfo.SystemSku?.ToLower() ?? "";

                Logger.LogInformation(
                    $"Xbox device detection - Model: {systemModel}, SKU: {systemSku}, Memory: {memoryLimit / 1024 / 1024}MB");

                // Check for Series indicators in system info
                var hasSeriesIndicator = systemModel.Contains("series") ||
                                         systemSku.Contains("series") ||
                                         systemModel.Contains("anaconda") || // Series X codename
                                         systemModel.Contains("lockhart") || // Series S codename  
                                         systemModel.Contains("xbox2020") || // Alternative identifier
                                         systemSku.Contains("anaconda") ||
                                         systemSku.Contains("lockhart");

                // In debug mode, memory might be limited to ~3GB
                // Production mode: Series X/S get ~13GB, Xbox One gets ~5GB
                // Use lower threshold (3GB) for debug detection
                var hasHighMemory = memoryLimit > 3L * 1024 * 1024 * 1024;

                var isSeriesConsole = hasSeriesIndicator || hasHighMemory;

                Logger.LogInformation(
                    $"Xbox Series detection result: {isSeriesConsole} (Indicator: {hasSeriesIndicator}, HighMem: {hasHighMemory})");

                return isSeriesConsole;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to detect Xbox Series model");
                return false;
            }
        }

        private string GetSystemVersionLogic()
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
                Logger.LogError(ex, "Failed to get system version");
                return "Unknown";
            }
        }

        private void OnGamepadAdded(object s, Gamepad g)
        {
            AddGamepadLogic(g);
        }

        private void OnGamepadRemoved(object s, Gamepad g)
        {
            RemoveGamepadLogic(g);
        }

        private void AddGamepadLogic(Gamepad g)
        {
            var context = CreateErrorContext("AddGamepadLogic");
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (!_connectedGamepads.Contains(g))
                    {
                        _connectedGamepads.Add(g);
                        Logger.LogInformation($"Ctrl Add. Total:{_connectedGamepads.Count}");
                        ControllerConnected?.Invoke(this,
                            new GamepadEventArgs { Gamepad = g, ControllerId = _connectedGamepads.Count - 1 });
                        UpdateControllerProperties();
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void RemoveGamepadLogic(Gamepad g)
        {
            var context = CreateErrorContext("RemoveGamepadLogic");
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    lock (_gamepadLock)
                    {
                        if (_connectedGamepads.Remove(g))
                        {
                            Logger.LogInformation($"Ctrl Remove. Total:{_connectedGamepads.Count}");
                            ControllerDisconnected?.Invoke(this, new GamepadEventArgs { Gamepad = g, ControllerId = -1 });
                            UpdateControllerProperties();
                        }
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void UpdateControllerProperties()
        {
            lock (_gamepadLock)
            {
                IsControllerConnected = _connectedGamepads.Any();
                ConnectedControllerCount = _connectedGamepads.Count;
            }
        }

        private string GetAppVersion()
        {
            try
            {
                var package = Package.Current;
                var version = package.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return "1.0.0.0";
            }
        }

        public string GetDeviceVersion()
        {
            return _deviceVersion;
        }

        public string GetOperatingSystem()
        {
            var df = AnalyticsInfo.VersionInfo.DeviceFamily;
            var v = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
            return $"{df} {v}";
        }

        public Task<IEnumerable<string>> GetSupportedCodecsAsync()
        {
            // Video codecs supported by Xbox hardware
            var c = new List<string> {
                "h264",        // H.264/AVC - all Xbox models
                "mpeg2video",  // MPEG-2 - all Xbox models
                "mpeg4",       // MPEG-4 Part 2 - all Xbox models  
                "msmpeg4v3",   // MS-MPEG4 v3 - all Xbox models
                "vc1",         // VC-1/WVC1 - all Xbox models
                "wmv3",        // Windows Media Video 9 - all Xbox models
                "wmv2",        // Windows Media Video 8 - all Xbox models
                "wmv1",        // Windows Media Video 7 - all Xbox models
                "mjpeg",       // Motion JPEG - all Xbox models
                "vp8",         // VP8 - all Xbox models
                "mpeg1video",  // MPEG-1 - all Xbox models
                "h263"         // H.263 - all Xbox models
            };

            if (SupportsHEVC)
            {
                c.Add("hevc");
                c.Add("h265"); // Alternative name for HEVC
            }

            if (SupportsAV1)
            {
                c.Add("av1");
            }

            if (SupportsVP9)
            {
                c.Add("vp9");
            }

            c.AddRange(GetSupportedAudioCodecs());
            return Task.FromResult<IEnumerable<string>>(c);
        }

        public Task<IEnumerable<string>> GetSupportedContainersAsync()
        {
            // Container formats supported by Xbox hardware
            return Task.FromResult<IEnumerable<string>>(new List<string>
            {
                // Common containers
                "mp4",         // MPEG-4 Part 14
                "m4v",         // iTunes/Apple variant of MP4
                "mkv",         // Matroska
                "webm",        // WebM (subset of Matroska)
                "avi",         // Audio Video Interleave
                "mov",         // QuickTime
                
                // Windows Media containers
                "wmv",         // Windows Media Video
                "asf",         // Advanced Systems Format
                
                // MPEG containers
                "mpg",         // MPEG Program Stream
                "mpeg",        // MPEG Program Stream
                "ts",          // MPEG Transport Stream
                "m2ts",        // Blu-ray MPEG Transport Stream
                "mts",         // AVCHD MPEG Transport Stream
                "vob",         // DVD Video Object
                
                // Mobile/streaming containers  
                "3gp",         // 3GPP
                "3g2",         // 3GPP2
                "flv",         // Flash Video
                
                // Audio-only containers (for music)
                "mp3",         // MP3 audio
                "m4a",         // MPEG-4 Audio
                "aac",         // Advanced Audio Coding
                "flac",        // Free Lossless Audio Codec
                "ogg",         // Ogg container
                "oga",         // Ogg Audio
                "opus",        // Opus audio
                "wma",         // Windows Media Audio
                "wav",         // Waveform Audio
                "alac"         // Apple Lossless
            });
        }

        public Task<long> GetMaxSupportedBitrateAsync()
        {
            return Task.FromResult((long)MaxSupportedBitrate);
        }

        public Task<int> GetMaxSupportedChannelsAsync()
        {
            return Task.FromResult(Supports4K ? 8 : 6);
        }

        public Task<Dictionary<string, bool>> GetHardwareCapabilitiesAsync()
        {
            return Task.FromResult(new Dictionary<string, bool>(_codecSupport));
        }

        public async Task<bool> CanDirectPlayAsync(string c, string co)
        {
            var sc = await GetSupportedContainersAsync().ConfigureAwait(false);
            var sco = await GetSupportedCodecsAsync().ConfigureAwait(false);
            return sc.Contains(c.ToLower()) && sco.Contains(co.ToLower());
        }

        public Task<bool> CanTranscodeAsync(string c, string co)
        {
            return Task.FromResult(false);
        }

        public IEnumerable<string> GetSupportedVideoCodecs()
        {
            var c = new List<string> { "h264" };
            if (SupportsHEVC)
            {
                c.Add("hevc");
            }

            if (SupportsAV1)
            {
                c.Add("av1");
            }

            if (SupportsVP9)
            {
                c.Add("vp9");
            }

            return c;
        }

        public IEnumerable<string> GetSupportedAudioCodecs()
        {
            var audioCodecs = new List<string> { "pcm" }; // PCM is always supported

            if (SupportsAAC)
            {
                audioCodecs.Add("aac");
            }

            if (SupportsMP3)
            {
                audioCodecs.Add("mp3");
            }

            if (SupportsFLAC)
            {
                audioCodecs.Add("flac");
            }

            if (SupportsALAC)
            {
                audioCodecs.Add("alac");
            }

            if (SupportsOGG)
            {
                audioCodecs.Add("vorbis");
            }

            if (SupportsOPUS)
            {
                audioCodecs.Add("opus");
            }

            if (SupportsAC3)
            {
                audioCodecs.Add("ac3");
            }

            if (SupportsEAC3)
            {
                audioCodecs.Add("eac3");
            }

            if (SupportsDTS)
            {
                audioCodecs.Add("dts");
            }

            if (SupportsTrueHD)
            {
                audioCodecs.Add("truehd");
            }

            if (SupportsDTSHD)
            {
                audioCodecs.Add("dts-hd");
            }

            return audioCodecs;
        }

        private void OnMonitoringTick(object s, object e)
        {
            System.Threading.Interlocked.Increment(ref _monitoringTickCount);
            try
            {
                SystemEvent?.Invoke(this,
                    new SystemEventArgs
                    {
                        EventType = "PeriodicUpdate",
                        Data = new { Controllers = ConnectedControllerCount }
                    });
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("OnMonitoringTick");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void OnInputTick(object s, object e)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _inputTickCount);
                Gamepad g;
                lock (_gamepadLock)
                {
                    if (!_connectedGamepads.Any())
                    {
                        return;
                    }

                    g = _connectedGamepads.FirstOrDefault();
                    if (g == null)
                    {
                        return;
                    }
                }

                var r = g.GetCurrentReading();
                ProcessGamepadInput(r);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error in input tick handler");
            }
        }

        private void ProcessGamepadInput(GamepadReading r)
        {
            try
            {
                var b = r.Buttons;
                var pB = _lastReading.Buttons;
                var nP = b & ~pB;
                if (nP != 0)
                {
                    ButtonStateChanged?.Invoke(this,
                        new GamepadButtonStateChangedEventArgs { Button = nP, IsPressed = true, Reading = r });
                }

                var nR = pB & ~b;
                if (nR != 0)
                {
                    ButtonReleased?.Invoke(this, nR);
                    ButtonStateChanged?.Invoke(this,
                        new GamepadButtonStateChangedEventArgs { Button = nR, IsPressed = false, Reading = r });
                }

                CurrentButtonState = b;
                _lastReading = r;
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("ProcessGamepadInput");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void OnBackRequested(object s, BackRequestedEventArgs e)
        {
            try
            {
                Logger.LogDebug("Back request");
                SystemEvent?.Invoke(this, new SystemEventArgs { EventType = "BackRequested", Data = e });
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("OnBackRequested");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void EnsureLocalSettings()
        {
            if (_localSettings == null)
            {
                lock (_localSettingsLock)
                {
                    if (_localSettings == null) // Double-check after acquiring lock
                    {
                        try
                        {
                            _localSettings = ApplicationData.Current.LocalSettings;

                            // Now that we have LocalSettings, update the device ID if needed
                            var storedId = GetOrCreateDeviceId();
                            if (!string.IsNullOrEmpty(storedId))
                            {
                                _deviceId = storedId;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError(ex, "Failed to get LocalSettings");
                        }
                    }
                }
            }
        }

        private string GetOrCreateDeviceId()
        {
            try
            {
                EnsureLocalSettings();
                if (_localSettings == null)
                {
                    // Return the temporary ID if we can't access LocalSettings
                    return _deviceId;
                }

                if (_localSettings.Values.TryGetValue("DeviceId", out var id) && id is string dId &&
                    !string.IsNullOrEmpty(dId))
                {
                    return dId;
                }

                var nId = Guid.NewGuid().ToString();
                _localSettings.Values["DeviceId"] = nId;
                return nId;
            }
            catch (Exception ex)
            {
                var context = CreateErrorContext("GetOrCreateDeviceId");
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }

                return "jellyfin-xbox-" + Environment.MachineName.GetHashCode().ToString("X8");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                Logger.LogInformation("UnifiedDeviceService disposing (managed resources)...");

                _monitoringTimer?.Stop();
                _inputTimer?.Stop();
                IsMonitoring = false;
                Logger.LogInformation("Platform monitoring stopped during dispose.");

                UnregisterDeviceEvents();

                lock (_gamepadLock)
                {
                    _connectedGamepads.Clear();
                }
                UpdateControllerProperties();

                _monitoringTimer = null;
                _inputTimer = null;

                Logger.LogInformation("UnifiedDeviceService managed resources disposed.");
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
