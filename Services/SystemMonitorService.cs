using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Networking.Connectivity;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.Xaml;
// Stopwatch is here

namespace Gelatinarm.Services
{
    // Simple memory usage levels for Xbox
    public enum AppMemoryUsageLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        OverLimit = 3
    }

    /// <summary>
    ///     Interface for memory monitoring services
    /// </summary>
    public interface IMemoryMonitor
    {
        bool IsMemoryConstrained { get; }
        event EventHandler<MemoryUsageEventArgs> MemoryUsageChanged;
        event EventHandler<MemoryPressureEventArgs> MemoryPressureChanged;
    }

    public class MemoryUsageEventArgs : EventArgs
    {
        public bool IsMemoryConstrained { get; set; }
        public long AvailableMemory { get; set; }
        public long TotalMemory { get; set; }
    }

    /// <summary>
    ///     Interface for network monitoring services
    /// </summary>
    public interface INetworkMonitor
    {
        long CurrentBandwidth { get; }
        ConnectionType ConnectionType { get; }
        event EventHandler<NetworkConditionsEventArgs> NetworkConditionsChanged;
        event EventHandler<BandwidthEventArgs> BandwidthChanged;
        event EventHandler<ConnectionQualityEventArgs> ConnectionQualityChanged;
    }

    public class NetworkConditionsEventArgs : EventArgs
    {
        public long Bandwidth { get; set; }
        public int Latency { get; set; }
        public double PacketLoss { get; set; }
    }

    /// <summary>
    ///     Unified system monitoring service that consolidates memory monitoring, network monitoring,
    ///     and performance optimization into a single cohesive service for Xbox applications.
    /// </summary>
    public interface ISystemMonitorService : IDisposable
    {
        // Memory monitoring
        ulong AvailableMemory { get; }
        ulong TotalMemory { get; }
        double MemoryUsage { get; }
        AppMemoryUsageLevel MemoryPressure { get; }
        bool IsMemoryConstrained { get; }

        // Network monitoring
        bool IsNetworkAvailable { get; }
        double CurrentBandwidth { get; }
        NetworkMetrics NetworkMetrics { get; }

        // Performance and resource management
        bool IsResourceConstrained { get; }
        bool IsMonitoring { get; }
        SystemMetrics GetCurrentMetrics();

        // Control methods
        Task StartMonitoringAsync();
        Task StopMonitoringAsync();

        // Events
        event EventHandler<SystemMetrics> MetricsUpdated;
        event EventHandler<MemoryUsageEventArgs> MemoryUsageChanged;
        event EventHandler<AppMemoryUsageLevel> MemoryPressureChanged;
        event EventHandler<NetworkMetrics> NetworkMetricsUpdated;
        event EventHandler<bool> NetworkStatusChanged;
        event EventHandler<double> BandwidthChanged;
        event EventHandler<ResourceConstraintEventArgs> ResourceConstraintDetected;
    }

    public class SystemMetrics
    {
        public ulong AvailableMemory { get; set; }
        public ulong TotalMemory { get; set; }
        public double MemoryUsage { get; set; }
        public AppMemoryUsageLevel MemoryPressure { get; set; }
        public NetworkMetrics NetworkMetrics { get; set; }
        public bool IsMemoryConstrained { get; set; }
        public bool IsNetworkAvailable { get; set; }
        public double CurrentBandwidth { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ResourceConstraintEventArgs : EventArgs
    {
        public ResourceConstraintType Type { get; set; }
        public string Description { get; set; }
        public bool IsConstrained { get; set; }
        public object AdditionalData { get; set; }
    }

    public enum ResourceConstraintType
    {
        Memory,
        Network,
        Combined
    }

    public class SystemMonitorService : BaseService, ISystemMonitorService, IMemoryMonitor, INetworkMonitor, IDisposable
    {
        private const ulong MEMORY_THRESHOLD_LOW = 1024 * 1024 * 1024;
        private const ulong MEMORY_THRESHOLD_CRITICAL = 512 * 1024 * 1024;
        private const double BANDWIDTH_THRESHOLD_LOW = 5_000_000;

        private readonly TimeSpan _bandwidthTestInterval =
            TimeSpan.FromMinutes(RetryConstants.BANDWIDTH_TEST_INTERVAL_MINUTES);

        private readonly CoreDispatcher _dispatcher;

        private readonly IHttpClientFactory _httpClientFactory;

        private readonly bool _isXboxOne;
        private readonly bool _isXboxSeries;
        private readonly object _metricsLock = new();
        private readonly IPreferencesService _preferencesService;

        private readonly Dictionary<string, BaseItemDto> _preloadedItems = new();

        private readonly IUserProfileService
            _userProfileService; // Kept, though not directly used in provided snippets, may be used by other methods

        private SystemMetrics _currentMetrics;
        private bool _disposed;

        private double _lastBandwidth;
        private DateTime _lastBandwidthTest = DateTime.MinValue;
        private AppMemoryUsageLevel _lastMemoryPressure;
        private bool _lastNetworkStatus;
        private ConnectionQuality _lastConnectionQuality = ConnectionQuality.Good;
        private DispatcherTimer _monitoringTimer;

        public SystemMonitorService(
            CoreDispatcher dispatcher,
            ILogger<SystemMonitorService> logger,
            IPreferencesService preferencesService,
            IHttpClientFactory httpClientFactory,
            IUserProfileService userProfileService = null) : base(logger)
        {
            _dispatcher = dispatcher;
            _preferencesService = preferencesService;
            _userProfileService = userProfileService;
            _httpClientFactory = httpClientFactory;

            lock (_metricsLock)
            {
                _currentMetrics = new SystemMetrics { Timestamp = DateTime.UtcNow };
            }

            DetectXboxHardware(out _isXboxOne, out _isXboxSeries);

            if (_dispatcher != null)
            {
                _monitoringTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(RetryConstants.SYSTEM_MONITOR_INTERVAL_SECONDS)
                };
                _monitoringTimer.Tick += OnMonitoringTick;
            }
            else
            {
                Logger.LogWarning("Dispatcher not available, deferring timer initialization");
            }

            Logger.LogInformation($"System monitor initialized. Xbox One: {_isXboxOne}, Xbox Series: {_isXboxSeries}");
        }

        // IMemoryMonitor specific events - forward to internal event
        private event EventHandler<MemoryPressureEventArgs> _memoryPressureChangedForInterface;
        event EventHandler<MemoryPressureEventArgs> IMemoryMonitor.MemoryPressureChanged
        {
            add { _memoryPressureChangedForInterface += value; }
            remove { _memoryPressureChangedForInterface -= value; }
        }

        long INetworkMonitor.CurrentBandwidth => (long)CurrentBandwidth;

        public event EventHandler<NetworkConditionsEventArgs> NetworkConditionsChanged;

        public ConnectionType ConnectionType
        {
            get
            {
                try
                {
                    return ConnectionType.Ethernet;
                }
                catch
                {
                    return ConnectionType.Unknown;
                }
            }
        }

        // INetworkMonitor specific events - forward to internal events
        private event EventHandler<BandwidthEventArgs> _bandwidthChangedForInterface;
        private event EventHandler<ConnectionQualityEventArgs> _connectionQualityChangedForInterface;

        event EventHandler<BandwidthEventArgs> INetworkMonitor.BandwidthChanged
        {
            add { _bandwidthChangedForInterface += value; }
            remove { _bandwidthChangedForInterface -= value; }
        }

        event EventHandler<ConnectionQualityEventArgs> INetworkMonitor.ConnectionQualityChanged
        {
            add { _connectionQualityChangedForInterface += value; }
            remove { _connectionQualityChangedForInterface -= value; }
        }

        public bool IsMonitoring { get; private set; }

        public ulong AvailableMemory
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.AvailableMemory ?? 0; }
            }
        }

        public ulong TotalMemory { get; private set; }

        public double MemoryUsage
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.MemoryUsage ?? 0; }
            }
        }

        public AppMemoryUsageLevel MemoryPressure
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.MemoryPressure ?? AppMemoryUsageLevel.Low; }
            }
        }

        public bool IsMemoryConstrained
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.IsMemoryConstrained ?? false; }
            }
        }

        public bool IsNetworkAvailable
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.IsNetworkAvailable ?? false; }
            }
        }

        public double CurrentBandwidth
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.CurrentBandwidth ?? 0; }
            }
        }

        public NetworkMetrics NetworkMetrics
        {
            get
            {
                lock (_metricsLock) { return _currentMetrics?.NetworkMetrics; }
            }
        }

        public bool IsResourceConstrained => IsMemoryConstrained || CurrentBandwidth < BANDWIDTH_THRESHOLD_LOW;
        public event EventHandler<SystemMetrics> MetricsUpdated;
        public event EventHandler<MemoryUsageEventArgs> MemoryUsageChanged;
        public event EventHandler<AppMemoryUsageLevel> MemoryPressureChanged;
        public event EventHandler<NetworkMetrics> NetworkMetricsUpdated;
        public event EventHandler<bool> NetworkStatusChanged;
        public event EventHandler<double> BandwidthChanged;
        public event EventHandler<ResourceConstraintEventArgs> ResourceConstraintDetected;

        public async Task StartMonitoringAsync()
        {
            if (IsMonitoring)
            {
                Logger.LogWarning("Monitoring is already active");
                return;
            }

            try
            {
                Logger.LogInformation("Starting system monitoring");

                await UpdateMetricsAsync().ConfigureAwait(false);

                try
                {
                    NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex,
                        "Failed to subscribe to network status changes - continuing without network monitoring");
                }

                MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
                MemoryManager.AppMemoryUsageDecreased += OnAppMemoryUsageDecreased;
                MemoryManager.AppMemoryUsageLimitChanging += OnAppMemoryUsageLimitChanging;

                if (_monitoringTimer != null)
                {
                    _monitoringTimer.Start();
                }
                else
                {
                    if (_dispatcher != null)
                    {
                        _monitoringTimer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(RetryConstants.SYSTEM_MONITOR_INTERVAL_SECONDS)
                        };
                        _monitoringTimer.Tick += OnMonitoringTick;
                        _monitoringTimer.Start();
                    }
                    else
                    {
                        Logger.LogWarning("Cannot start monitoring timer - dispatcher not available");
                    }
                }

                IsMonitoring = true;

                Logger.LogInformation("System monitoring started successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to start system monitoring");
                throw;
            }
        }

        public async Task StopMonitoringAsync()
        {
            if (!IsMonitoring)
            {
                return;
            }

            try
            {
                Logger.LogInformation("Stopping system monitoring");

                if (_monitoringTimer != null)
                {
                    _monitoringTimer.Stop();
                }

                try
                {
                    NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to unsubscribe from network status changes");
                }

                MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
                MemoryManager.AppMemoryUsageDecreased -= OnAppMemoryUsageDecreased;
                MemoryManager.AppMemoryUsageLimitChanging -= OnAppMemoryUsageLimitChanging;

                IsMonitoring = false;

                Logger.LogInformation("System monitoring stopped");
                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error stopping system monitoring");
            }
        }

        public SystemMetrics GetCurrentMetrics()
        {
            lock (_metricsLock)
            {
                if (_currentMetrics == null)
                {
                    return new SystemMetrics { Timestamp = DateTime.UtcNow };
                }

                return new SystemMetrics
                {
                    AvailableMemory = _currentMetrics.AvailableMemory,
                    TotalMemory = _currentMetrics.TotalMemory,
                    MemoryUsage = _currentMetrics.MemoryUsage,
                    MemoryPressure = _currentMetrics.MemoryPressure,
                    NetworkMetrics = _currentMetrics.NetworkMetrics,
                    IsMemoryConstrained = _currentMetrics.IsMemoryConstrained,
                    IsNetworkAvailable = _currentMetrics.IsNetworkAvailable,
                    CurrentBandwidth = _currentMetrics.CurrentBandwidth,
                    Timestamp = DateTime.UtcNow
                };
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _disposed = true;

                // Stop monitoring timer if it exists
                if (_monitoringTimer != null)
                {
                    _monitoringTimer.Stop();
                    _monitoringTimer.Tick -= OnMonitoringTick;
                    _monitoringTimer = null;
                }

                // Only unsubscribe from events if monitoring was started
                if (IsMonitoring)
                {
                    try
                    {
                        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning(ex, "Failed to unsubscribe from NetworkStatusChanged");
                    }

                    try
                    {
                        MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
                        MemoryManager.AppMemoryUsageDecreased -= OnAppMemoryUsageDecreased;
                        MemoryManager.AppMemoryUsageLimitChanging -= OnAppMemoryUsageLimitChanging;
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning(ex, "Failed to unsubscribe from MemoryManager events");
                    }

                    IsMonitoring = false;
                    Logger?.LogInformation("System monitoring stopped during dispose.");
                }

                _monitoringTimer = null;
                // HttpClient is now managed by factory, no need to dispose
                _preloadedItems?.Clear();

                MetricsUpdated = null;
                MemoryUsageChanged = null;
                MemoryPressureChanged = null;
                NetworkMetricsUpdated = null;
                NetworkStatusChanged = null;
                BandwidthChanged = null;
                ResourceConstraintDetected = null;
                NetworkConditionsChanged = null;

                Logger.LogInformation("System monitor disposed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error disposing system monitor");
            }
        }

        private async void OnMonitoringTick(object sender, object e)
        {
            await UpdateMetricsAsync().ConfigureAwait(false);
        }

        private async Task UpdateMetricsAsync()
        {
            try
            {
                SystemMetrics previousMetrics;
                lock (_metricsLock)
                {
                    previousMetrics = _currentMetrics;
                }

                var newMetrics = new SystemMetrics { Timestamp = DateTime.UtcNow };

                await UpdateMemoryMetricsAsync(newMetrics).ConfigureAwait(false);
                await UpdateNetworkMetricsAsync(newMetrics).ConfigureAwait(false);

                CheckResourceConstraints(newMetrics, previousMetrics);

                lock (_metricsLock)
                {
                    _currentMetrics = newMetrics;
                }

                FireChangeEvents(newMetrics, previousMetrics);

                MetricsUpdated?.Invoke(this, newMetrics);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update system metrics");
            }
        }

        private async Task UpdateMemoryMetricsAsync(SystemMetrics metrics)
        {
            try
            {
                var memoryReport = MemoryManager.GetAppMemoryReport();
                var memoryUsage = MemoryManager.AppMemoryUsage;
                var memoryLimit = MemoryManager.AppMemoryUsageLimit;

                if (TotalMemory == 0)
                {
                    TotalMemory = _isXboxSeries ? 16UL * 1024 * 1024 * 1024 : 8UL * 1024 * 1024 * 1024;
                }

                metrics.TotalMemory = TotalMemory;
                metrics.AvailableMemory = memoryLimit - memoryUsage;
                metrics.MemoryUsage = (double)memoryUsage / memoryLimit * 100.0;

                if (metrics.MemoryUsage > SystemConstants.HIGH_MEMORY_USAGE_THRESHOLD)
                {
                    metrics.MemoryPressure = AppMemoryUsageLevel.OverLimit;
                }
                else if (metrics.MemoryUsage > SystemConstants.MEDIUM_HIGH_MEMORY_USAGE_THRESHOLD)
                {
                    metrics.MemoryPressure = AppMemoryUsageLevel.High;
                }
                else if (metrics.MemoryUsage > SystemConstants.MEDIUM_MEMORY_USAGE_THRESHOLD)
                {
                    metrics.MemoryPressure = AppMemoryUsageLevel.Medium;
                }
                else
                {
                    metrics.MemoryPressure = AppMemoryUsageLevel.Low;
                }

                metrics.IsMemoryConstrained = metrics.AvailableMemory < MEMORY_THRESHOLD_LOW ||
                                              metrics.MemoryPressure == AppMemoryUsageLevel.High;

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update memory metrics");
                metrics.AvailableMemory = 0;
                metrics.MemoryUsage = 0;
                metrics.IsMemoryConstrained = true;
            }
        }

        private async Task UpdateNetworkMetricsAsync(SystemMetrics metrics)
        {
            try
            {
                if (_isXboxOne || _isXboxSeries)
                {
                    metrics.IsNetworkAvailable = true;

                    var isLikelyEthernet = await IsLikelyEthernetConnectionAsync().ConfigureAwait(false);
                    var connectionType = isLikelyEthernet ? ConnectionType.Ethernet : ConnectionType.WiFi;

                    double measuredBandwidth = 0;
                    if (DateTime.UtcNow - _lastBandwidthTest > _bandwidthTestInterval)
                    {
                        measuredBandwidth = await MeasureBandwidthAsync().ConfigureAwait(false);
                        _lastBandwidthTest = DateTime.UtcNow;
                    }

                    if (measuredBandwidth > 0)
                    {
                        metrics.CurrentBandwidth = measuredBandwidth;
                    }
                    else
                    {
                        if (isLikelyEthernet)
                        {
                            metrics.CurrentBandwidth = _isXboxSeries ? 100_000_000 : 80_000_000;
                        }
                        else
                        {
                            metrics.CurrentBandwidth = _isXboxSeries ? 40_000_000 : 25_000_000;
                        }
                    }

                    metrics.NetworkMetrics = new NetworkMetrics
                    {
                        IsConnected = true,
                        ConnectionType = connectionType,
                        SignalStrength = isLikelyEthernet ? (byte)100 : (byte)75,
                        IsMetered = false,
                        LastUpdated = DateTimeOffset.UtcNow
                    };
                    return;
                }

                var profile = NetworkInformation.GetInternetConnectionProfile();

                if (profile == null)
                {
                    metrics.IsNetworkAvailable = false;
                    metrics.CurrentBandwidth = 0;
                    metrics.NetworkMetrics = new NetworkMetrics
                    {
                        IsConnected = false,
                        ConnectionType = 0,
                        LastUpdated = DateTimeOffset.UtcNow
                    };
                    return;
                }

                metrics.IsNetworkAvailable = true;

                var networkMetrics = new NetworkMetrics
                {
                    IsConnected = true,
                    ConnectionType = GetConnectionType(profile),
                    SignalStrength = GetSignalStrength(profile),
                    IsMetered = profile.IsWlanConnectionProfile &&
                                profile.GetConnectionCost()?.NetworkCostType != NetworkCostType.Unrestricted,
                    LastUpdated = DateTimeOffset.UtcNow
                };

                metrics.CurrentBandwidth = EstimateBandwidth(networkMetrics.ConnectionType);
                metrics.NetworkMetrics = networkMetrics;

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update network metrics");
                metrics.IsNetworkAvailable = false;
                metrics.CurrentBandwidth = 0;
            }
        }

        private void CheckResourceConstraints(SystemMetrics current, SystemMetrics previous)
        {
            if (current.IsMemoryConstrained != previous?.IsMemoryConstrained)
            {
                ResourceConstraintDetected?.Invoke(this, new ResourceConstraintEventArgs
                {
                    Type = ResourceConstraintType.Memory,
                    IsConstrained = current.IsMemoryConstrained,
                    Description = current.IsMemoryConstrained
                        ? $"Memory constrained - Available: {current.AvailableMemory / 1024 / 1024}MB"
                        : "Memory constraint resolved",
                    AdditionalData = current
                });

                if (current.IsMemoryConstrained)
                {
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        Logger.LogInformation("Garbage collection triggered due to memory pressure");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to trigger garbage collection");
                    }
                }
            }

            var isNetworkConstrained = current.CurrentBandwidth < BANDWIDTH_THRESHOLD_LOW;
            var wasNetworkConstrained = previous?.CurrentBandwidth < BANDWIDTH_THRESHOLD_LOW;

            if (isNetworkConstrained != wasNetworkConstrained)
            {
                ResourceConstraintDetected?.Invoke(this, new ResourceConstraintEventArgs
                {
                    Type = ResourceConstraintType.Network,
                    IsConstrained = isNetworkConstrained,
                    Description = isNetworkConstrained
                        ? $"Network bandwidth low - {current.CurrentBandwidth / 1_000_000:F1} Mbps"
                        : $"Network bandwidth sufficient - {current.CurrentBandwidth / 1_000_000:F1} Mbps",
                    AdditionalData = current
                });
            }
        }

        private void FireChangeEvents(SystemMetrics current, SystemMetrics previous)
        {
            if (previous == null || Math.Abs(current.MemoryUsage - previous.MemoryUsage) > 1.0)
            {
                MemoryUsageChanged?.Invoke(this,
                    new MemoryUsageEventArgs
                    {
                        AvailableMemory = (long)current.AvailableMemory,
                        TotalMemory = (long)current.TotalMemory,
                        IsMemoryConstrained = current.IsMemoryConstrained
                    });
            }

            if (current.MemoryPressure != _lastMemoryPressure)
            {
                MemoryPressureChanged?.Invoke(this, current.MemoryPressure);

                // Also raise IMemoryMonitor.MemoryPressureChanged for interface consumers
                _memoryPressureChangedForInterface?.Invoke(this, new MemoryPressureEventArgs
                {
                    Pressure = (MemoryPressure)(int)current.MemoryPressure
                });

                _lastMemoryPressure = current.MemoryPressure;
            }

            if (current.IsNetworkAvailable != _lastNetworkStatus)
            {
                NetworkStatusChanged?.Invoke(this, current.IsNetworkAvailable);
                _lastNetworkStatus = current.IsNetworkAvailable;
            }

            if (Math.Abs(current.CurrentBandwidth - _lastBandwidth) > 1_000_000)
            {
                BandwidthChanged?.Invoke(this, current.CurrentBandwidth);

                // Also raise INetworkMonitor.BandwidthChanged for interface consumers
                _bandwidthChangedForInterface?.Invoke(this, new BandwidthEventArgs
                {
                    BandwidthKbps = (int)(current.CurrentBandwidth / 1000) // Convert to Kbps
                });

                _lastBandwidth = current.CurrentBandwidth;
            }

            if (current.NetworkMetrics != null)
            {
                NetworkMetricsUpdated?.Invoke(this, current.NetworkMetrics);

                // Determine connection quality based on bandwidth and raise event if changed
                var quality = DetermineConnectionQuality(current.CurrentBandwidth);
                if (_lastConnectionQuality != quality)
                {
                    _connectionQualityChangedForInterface?.Invoke(this, new ConnectionQualityEventArgs
                    {
                        Quality = quality,
                        LatencyMs = 0, // Would need actual measurement
                        PacketLoss = 0.0 // Would need actual measurement
                    });
                    _lastConnectionQuality = quality;
                }
            }

            if (previous == null ||
                current.IsNetworkAvailable != previous.IsNetworkAvailable ||
                Math.Abs(current.CurrentBandwidth - previous.CurrentBandwidth) > 1_000_000)
            {
                NetworkConditionsChanged?.Invoke(this,
                    new NetworkConditionsEventArgs
                    {
                        Bandwidth = (long)current.CurrentBandwidth,
                        Latency = 0,
                        PacketLoss = 0.0
                    });
            }
        }

        private ConnectionQuality DetermineConnectionQuality(double bandwidthBps)
        {
            // Determine quality based on bandwidth (in bits per second)
            if (bandwidthBps >= 25_000_000) // 25 Mbps+ = Excellent (4K streaming)
            {
                return ConnectionQuality.Excellent;
            }
            else if (bandwidthBps >= 10_000_000) // 10 Mbps+ (HD streaming)
            {
                return ConnectionQuality.Good;
            }
            else if (bandwidthBps >= 5_000_000) // 5 Mbps+ = Fair (SD streaming)
            {
                return ConnectionQuality.Fair;
            }
            else
            {
                return ConnectionQuality.Poor;
            }
        }

        private ConnectionType GetConnectionType(ConnectionProfile profile)
        {
            if (profile.IsWlanConnectionProfile)
            {
                return ConnectionType.WiFi;
            }

            if (profile.IsWwanConnectionProfile)
            {
                return ConnectionType.Cellular;
            }

            return ConnectionType.Ethernet;
        }

        private byte GetSignalStrength(ConnectionProfile profile)
        {
            try
            {
                if (profile.IsWlanConnectionProfile)
                {
                    var signalBars = profile.GetSignalBars();
                    return signalBars.HasValue ? (byte)(signalBars.Value * 20) : (byte)80;
                }

                return 100;
            }
            catch
            {
                return 80;
            }
        }

        private double EstimateBandwidth(ConnectionType connectionType)
        {
            return connectionType switch
            {
                ConnectionType.Ethernet => _isXboxSeries ? 100_000_000 : 50_000_000,
                ConnectionType.WiFi => _isXboxSeries ? 80_000_000 : 40_000_000,
                ConnectionType.Cellular => 25_000_000,
                _ => 10_000_000
            };
        }

        private async Task<bool> IsLikelyEthernetConnectionAsync()
        {
            try
            {
                var networkAdapters = await DeviceInformation.FindAllAsync(
                        "System.Devices.InterfaceClassGuid:=\"{4d36e972-e325-11ce-bfc1-08002be10318}\"").AsTask()
                    .ConfigureAwait(false);

                foreach (var adapter in networkAdapters)
                {
                    if (adapter.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                        adapter.Name.Contains("LAN", StringComparison.OrdinalIgnoreCase) ||
                        adapter.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                        adapter.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task<double> MeasureBandwidthAsync()
        {
            try
            {
                var serverUrl = _preferencesService.GetValue<string>("ServerUrl");
                if (string.IsNullOrEmpty(serverUrl))
                {
                    Logger.LogWarning("No server URL available for bandwidth test");
                    return 0;
                }

                var testUrl = $"{serverUrl}/health";

                var httpClient = _httpClientFactory.CreateClient("JellyfinClient");
                var stopwatch = Stopwatch.StartNew();
                var response = await httpClient.GetAsync(testUrl).ConfigureAwait(false);
                stopwatch.Stop();

                if (response == null)
                {
                    Logger.LogWarning("Bandwidth test failed - no response received");
                    return 0;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning("Bandwidth test failed with status: {StatusCode}", response.StatusCode);
                    return 0;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetByteCount(content);

                var seconds = stopwatch.Elapsed.TotalSeconds;
                var bitsPerSecond = bytes * 8 / seconds;

                if (stopwatch.ElapsedMilliseconds < RetryConstants.BANDWIDTH_TEST_EXCELLENT_THRESHOLD_MS)
                {
                    return _isXboxSeries ? 100_000_000 : 80_000_000;
                }

                if (stopwatch.ElapsedMilliseconds < RetryConstants.BANDWIDTH_TEST_GOOD_THRESHOLD_MS)
                {
                    return _isXboxSeries ? 80_000_000 : 50_000_000;
                }

                if (stopwatch.ElapsedMilliseconds < RetryConstants.BANDWIDTH_TEST_FAIR_THRESHOLD_MS)
                {
                    return _isXboxSeries ? 50_000_000 : 30_000_000;
                }

                return 20_000_000;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to measure bandwidth");
                return 0;
            }
        }

        private void DetectXboxHardware(out bool isXboxOne, out bool isXboxSeries)
        {
            try
            {
                var deviceFamily = AnalyticsInfo.VersionInfo.DeviceFamily;
                if (deviceFamily.Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase))
                {
                    // Use available system resources to determine Xbox generation
                    // Xbox Series X/S have significantly more memory than Xbox One
                    var memoryLimit = MemoryManager.AppMemoryUsageLimit;

                    // Get additional device info for better detection
                    var deviceInfo = new EasClientDeviceInformation();
                    var systemModel = deviceInfo.SystemProductName?.ToLower() ?? "";
                    var systemSku = deviceInfo.SystemSku?.ToLower() ?? "";

                    Logger.LogInformation(
                        $"Xbox device info - Model: {systemModel}, SKU: {systemSku}, Memory limit: {memoryLimit / 1024 / 1024}MB");

                    // Check for Series indicators in system info
                    var hasSeriesIndicator = systemModel.Contains("series") ||
                                             systemSku.Contains("series") ||
                                             systemModel.Contains("anaconda") || // Series X codename
                                             systemModel.Contains("lockhart") || // Series S codename
                                             systemSku.Contains("anaconda") ||
                                             systemSku.Contains("lockhart");

                    // In debug/development mode, memory might be limited
                    // So we use a lower threshold (3GB) or check for Series indicators
                    // Production apps get more memory: Series X/S ~13GB, One ~5GB
                    isXboxSeries = hasSeriesIndicator || memoryLimit > 3L * 1024 * 1024 * 1024;
                    isXboxOne = !isXboxSeries;

                    Logger.LogInformation(
                        $"Xbox hardware detected - Memory limit: {memoryLimit / 1024 / 1024}MB, Detected as: {(isXboxSeries ? "Series X/S" : "One")}");
                }
                else
                {
                    isXboxOne = false;
                    isXboxSeries = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to detect Xbox hardware type, defaulting to conservative settings");
                isXboxOne = true; // Default to Xbox One for conservative resource usage
                isXboxSeries = false;
            }
        }

        #region IMemoryMonitor Implementation

        #endregion

        #region INetworkMonitor Compatibility

        #endregion

        #region Memory Manager Event Handlers

        private async void OnAppMemoryUsageIncreased(object sender, object e)
        {
            if (_dispatcher == null)
            {
                return; // Guard against null dispatcher
            }

            await UIHelper.RunOnUIThreadAsync(async () =>
            {
                await UpdateMetricsAsync().ConfigureAwait(false);
            }, _dispatcher, Logger);
        }

        private async void OnAppMemoryUsageDecreased(object sender, object e)
        {
            if (_dispatcher == null)
            {
                return;
            }

            await UIHelper.RunOnUIThreadAsync(async () =>
            {
                await UpdateMetricsAsync().ConfigureAwait(false);
            }, _dispatcher, Logger);
        }

        private async void OnAppMemoryUsageLimitChanging(object sender, AppMemoryUsageLimitChangingEventArgs e)
        {
            if (_dispatcher == null)
            {
                return;
            }

            await UIHelper.RunOnUIThreadAsync(async () =>
            {
                await UpdateMetricsAsync().ConfigureAwait(false);
            }, _dispatcher, Logger);
        }

        private async void OnNetworkStatusChanged(object sender)
        {
            if (_dispatcher == null)
            {
                return;
            }

            await UIHelper.RunOnUIThreadAsync(async () =>
            {
                await UpdateMetricsAsync().ConfigureAwait(false);
            }, _dispatcher, Logger);
        }

        #endregion
    }

    public class MemoryManagerStatus
    {
        public ulong AvailableMemory { get; set; }
        public ulong TotalMemory { get; set; }
        public AppMemoryUsageLevel UsageLevel { get; set; }
    }
}
