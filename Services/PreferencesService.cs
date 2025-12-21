using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.System.Profile;

namespace Gelatinarm.Services
{
    public interface IPreferencesService
    {
        // Xbox Features
        bool IsXboxEnvironment { get; }

        // Storage Operations
        Task SaveAsync<T>(string key, T data);
        Task<T> LoadAsync<T>(string key);
        T GetValue<T>(string key, T defaultValue = default);
        void SetValue<T>(string key, T value);
        void RemoveValue(string key);
        Task RemoveSettingAsync(string key);
        Task ClearCacheAsync();


        Task<Dictionary<string, object>> GetAllPreferences();

        // Consolidated App Preferences
        Task<AppPreferences> GetAppPreferencesAsync();
        Task UpdateAppPreferencesAsync(AppPreferences preferences);
        Task SaveAsDefaultAppPreferencesAsync(AppPreferences preferences);

        // Playback Position
        Task<long> GetPlaybackPositionAsync(string itemId);
        Task SetPlaybackPositionAsync(string itemId, long positionTicks);
    }

    public class PreferencesService : BaseService, IPreferencesService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly object _appPreferencesLock = new();

        private readonly Dictionary<string, long> _playbackPositions = new();
        private readonly object _positionsLock = new();
        private readonly SemaphoreSlim _savePositionSemaphore = new(1, 1);
        private StorageFolder _cacheFolder;

        // Consolidated App Preferences Implementation
        private AppPreferences _cachedAppPreferences;

        private StorageFolder _localFolder;
        private ApplicationDataContainer _localSettings;

        public PreferencesService(ILogger<PreferencesService> logger) : base(logger)
        {
            // Detect Xbox environment
            IsXboxEnvironment = DetectXboxEnvironment();
            Logger.LogInformation($"Preferences service initialized. Xbox environment: {IsXboxEnvironment}");

            // Delay loading cached data to avoid potential recursion during startup
            // Schedule background cache load
            FireAndForget(async () =>
            {
                await Task.Delay(RetryConstants.CACHE_LOAD_STARTUP_DELAY_MS).ConfigureAwait(false);
                await LoadCachedDataAsync().ConfigureAwait(false);
#if DEBUG
                Logger?.LogDebug("PreferencesService: Cache load completed");
#endif
            });

            // Cleanup can run later
#if DEBUG
            Logger?.LogDebug("PreferencesService: Scheduling background cleanup");
#endif
            FireAndForget(async () =>
            {
                await Task.Delay(RetryConstants.CLEANUP_TASK_DELAY_MS).ConfigureAwait(false);
                await CleanupStoredPreferencesAsync().ConfigureAwait(false);
            });
        }

        public bool IsXboxEnvironment { get; }

        public async Task<Dictionary<string, object>> GetAllPreferences()
        {
            var result = new Dictionary<string, object>();

            var appPrefs = await GetAppPreferencesAsync().ConfigureAwait(false);
            result["AppPreferences"] = appPrefs;


            var settingsSnapshot = new Dictionary<string, object>();
            EnsureApplicationDataLoaded();
            lock (_localSettings)
            {
                foreach (var container in _localSettings.Values)
                {
                    settingsSnapshot[container.Key] = container.Value;
                }
            }

            foreach (var kvp in settingsSnapshot)
            {
                result[kvp.Key] = kvp.Value;
            }

            return result;
        }

        public async Task<AppPreferences> GetAppPreferencesAsync()
        {
            lock (_appPreferencesLock)
            {
                if (_cachedAppPreferences != null)
                {
                    return _cachedAppPreferences;
                }
            }

            try
            {
                _cachedAppPreferences = await LoadAsync<AppPreferences>("AppPreferences").ConfigureAwait(false);
                if (_cachedAppPreferences == null)
                {
                    // Create new default preferences
                    _cachedAppPreferences = new AppPreferences();
                    await SaveAsync("AppPreferences", _cachedAppPreferences).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load app preferences");
                _cachedAppPreferences = new AppPreferences();
            }

            lock (_appPreferencesLock)
            {
                return _cachedAppPreferences;
            }
        }

        public async Task UpdateAppPreferencesAsync(AppPreferences preferences)
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }

            preferences.LastModified = DateTime.Now;

            try
            {
                await SaveAsync("AppPreferences", preferences).ConfigureAwait(false);
                lock (_appPreferencesLock)
                {
                    _cachedAppPreferences = preferences;
                }

                Logger.LogInformation("App preferences updated successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to update app preferences");
                throw;
            }
        }

        public async Task SaveAsDefaultAppPreferencesAsync(AppPreferences preferences)
        {
            try
            {
                if (preferences == null)
                {
                    throw new ArgumentNullException(nameof(preferences));
                }

                lock (_appPreferencesLock)
                {
                    _cachedAppPreferences = preferences;
                }

                await SaveAsync("AppPreferences", preferences).ConfigureAwait(false);
                Logger.LogInformation("Saved app preferences as defaults");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to save default app preferences");
                throw;
            }
        }


        #region Storage Operations

        public async Task SaveAsync<T>(string key, T data)
        {
            var context = CreateErrorContext("SaveAsync");
            try
            {
                var json = JsonSerializer.Serialize(data, JsonOptions);

                if (IsXboxEnvironment)
                {
                    SetValue(key, json);
                }
                else
                {
                    // Ensure ApplicationData is loaded
                    EnsureApplicationDataLoaded();

                    var file = await _localFolder
                        .CreateFileAsync($"{key}.json", CreationCollisionOption.ReplaceExisting).AsTask()
                        .ConfigureAwait(false);
                    await FileIO.WriteTextAsync(file, json).AsTask().ConfigureAwait(false);
                }

                Logger.LogDebug($"Saved data for key: {key}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public async Task<T> LoadAsync<T>(string key)
        {
            var context = CreateErrorContext("LoadAsync");
            try
            {
                string json = null;

                if (IsXboxEnvironment)
                {
                    json = GetValue<string>(key);
                }

                if (string.IsNullOrEmpty(json))
                {
                    try
                    {
                        // Ensure ApplicationData is loaded
                        EnsureApplicationDataLoaded();

                        var file = await _localFolder.GetFileAsync($"{key}.json").AsTask().ConfigureAwait(false);
                        json = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                    }
                    catch (FileNotFoundException)
                    {
                        Logger.LogDebug($"File not found for key {key}, returning default.");
                        return default;
                    }
                }

                if (!string.IsNullOrEmpty(json))
                {
                    return JsonSerializer.Deserialize<T>(json, JsonOptions);
                }

                return default;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    return await ErrorHandler.HandleErrorAsync<T>(ex, context, default).ConfigureAwait(false);
                }

                Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                return default;
            }
        }

        public T GetValue<T>(string key, T defaultValue = default)
        {
            var context = CreateErrorContext("GetValue");
            try
            {
                // Ensure ApplicationData is loaded
                EnsureApplicationDataLoaded();

                if (_localSettings.Values.ContainsKey(key))
                {
                    var value = _localSettings.Values[key];

                    // Only log detailed values for non-complex types and non-AppPreferences
                    if (key == "AppPreferences")
                    {
                        Logger.LogDebug($"[PREFERENCES] Retrieved {key} from local settings");
                    }
                    else if (value is string strVal && strVal.Length > SystemConstants.MAX_PREFERENCE_STRING_LENGTH)
                    {
                        Logger.LogDebug($"[PREFERENCES] Retrieved {key} (large value) from local settings");
                    }
                    else
                    {
                        Logger.LogDebug($"[PREFERENCES] Retrieved {key} = '{value}' from local settings");
                    }

                    if (value is T typedValue)
                    {
                        return typedValue;
                    }

                    if (value is string jsonValue && typeof(T).IsClass && typeof(T) != typeof(string))
                    {
                        var deserialized = JsonSerializer.Deserialize<T>(jsonValue, JsonOptions);
                        return deserialized;
                    }

                    var converted = (T)Convert.ChangeType(value, typeof(T));
                    return converted;
                }

                Logger.LogDebug($"[PREFERENCES] Key '{key}' not found in local settings, returning default");
                return defaultValue;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    // Use the synchronous HandleError method if available, otherwise fire and forget the async version
                    if (ErrorHandler is ErrorHandlingService errorService)
                    {
                        errorService.HandleError(ex, context);
                    }
                    else
                    {
                        FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                    }

                    return defaultValue;
                }

                Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                return defaultValue;
            }
        }

        public void SetValue<T>(string key, T value)
        {
            var context = CreateErrorContext("SetValue");
            try
            {
                // Ensure ApplicationData is loaded
                EnsureApplicationDataLoaded();

                if (value == null)
                {
                    _localSettings.Values.Remove(key);
                    Logger.LogDebug($"[PREFERENCES] Removed key '{key}' from local settings.");
                }
                else if (value is string || value.GetType().IsPrimitive)
                {
                    if (key == "UserId")
                    {
                        Logger.LogWarning(
                            $"[PREFERENCES-DEBUG] About to store UserId = '{value}' (Type: {value?.GetType()?.Name})");
                    }

                    _localSettings.Values[key] = value;
                    // Only log detailed values for simple types
                    if (key == "AppPreferences" || (value is string strVal &&
                                                    strVal.Length > SystemConstants.MAX_PREFERENCE_STRING_LENGTH))
                    {
                        Logger.LogDebug($"[PREFERENCES] Stored {key} to local settings");
                    }
                    else
                    {
                        Logger.LogDebug($"[PREFERENCES] Stored {key} = '{value}' to local settings");
                    }

                    var verifyValue = _localSettings.Values[key];
                    if (key == "UserId" && verifyValue?.ToString() != value?.ToString())
                    {
                        Logger.LogError(
                            $"[PREFERENCES-ERROR] UserId storage verification failed! Expected: '{value}', Got: '{verifyValue}'");
                    }
                }
                else
                {
                    var json = JsonSerializer.Serialize(value, JsonOptions);
                    _localSettings.Values[key] = json;
                    Logger.LogDebug($"[PREFERENCES] Stored serialized object for key '{key}' to local settings.");
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    // Use the synchronous HandleError method if available, otherwise fire and forget the async version
                    if (ErrorHandler is ErrorHandlingService errorService)
                    {
                        errorService.HandleError(ex, context);
                    }
                    else
                    {
                        FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                    }
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                }
            }
        }

        public void RemoveValue(string key)
        {
            var context = CreateErrorContext("RemoveValue");
            try
            {
                // Ensure ApplicationData is loaded
                EnsureApplicationDataLoaded();

                if (_localSettings.Values.ContainsKey(key))
                {
                    _localSettings.Values.Remove(key);
                    Logger.LogDebug($"[PREFERENCES] Removed key '{key}' from local settings.");
                }
                else
                {
                    Logger.LogDebug($"[PREFERENCES] Key '{key}' not found in local settings, nothing to remove.");
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        public async Task RemoveSettingAsync(string key)
        {
            var context = CreateErrorContext("RemoveSettingAsync");
            try
            {
                // Ensure ApplicationData is loaded
                EnsureApplicationDataLoaded();

                _localSettings.Values.Remove(key);

                try
                {
                    var file = await _localFolder.GetFileAsync($"{key}.json").AsTask().ConfigureAwait(false);
                    await file.DeleteAsync().AsTask().ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    Logger.LogDebug($"File not found for key {key} during remove, that's okay.");
                }

                Logger.LogDebug($"Removed setting: {key}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task ClearCacheAsync()
        {
            var context = CreateErrorContext("ClearCache");
            try
            {
                // Ensure ApplicationData is loaded
                EnsureApplicationDataLoaded();

                var files = await _cacheFolder.GetFilesAsync().AsTask().ConfigureAwait(false);
                foreach (var file in files)
                {
                    await file.DeleteAsync().AsTask().ConfigureAwait(false);
                }

                Logger.LogInformation("Cache cleared successfully");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        #endregion

        #region Display and Authentication Preferences


        #endregion

        #region Playback Position

        public async Task<long> GetPlaybackPositionAsync(string itemId)
        {
            lock (_positionsLock)
            {
                if (_playbackPositions.TryGetValue(itemId, out var position))
                {
                    return position;
                }
            }

            var positions =
                await LoadAsync<Dictionary<string, long>>(PreferenceConstants.PlaybackPositionsFileKey)
                    .ConfigureAwait(false) ?? new Dictionary<string, long>();
            positions.TryGetValue(itemId, out var loadedPosition);

            lock (_positionsLock)
            {
                _playbackPositions[itemId] = loadedPosition;
            }

            return loadedPosition;
        }

        public async Task SetPlaybackPositionAsync(string itemId, long positionTicks)
        {
            lock (_positionsLock)
            {
                _playbackPositions[itemId] = positionTicks;
            }

            FireAndForget(async () =>
            {
                await _savePositionSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await Task.Delay(RetryConstants.PLAYBACK_POSITION_SAVE_DELAY_MS).ConfigureAwait(false);

                    Dictionary<string, long> positionsToSave;
                    lock (_positionsLock)
                    {
                        positionsToSave = new Dictionary<string, long>(_playbackPositions);
                    }

                    await SaveAsync(PreferenceConstants.PlaybackPositionsFileKey, positionsToSave)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _savePositionSemaphore.Release();
                }
            });
        }

        #endregion

        #region Helper Methods

        private void EnsureApplicationDataLoaded()
        {
            if (_localSettings == null)
            {
                var appData = ApplicationData.Current;
                _localSettings = appData.LocalSettings;
                _localFolder = appData.LocalFolder;
                _cacheFolder = appData.LocalCacheFolder;
            }
        }

        private bool DetectXboxEnvironment()
        {
            // Detecting Xbox environment
            try
            {
                // Getting device family
                var deviceFamily = AnalyticsInfo.VersionInfo.DeviceFamily;
                // Device family detected
                var isXbox = deviceFamily.Equals("Windows.Xbox", StringComparison.OrdinalIgnoreCase);
                // Xbox detection result
                return isXbox;
            }
            catch (Exception ex)
            {
                // Xbox detection failed
                Logger?.LogError(ex, "Failed to detect Xbox environment");
                return false;
            }
        }

        private async Task LoadCachedDataAsync()
        {
            try
            {
                // Load display preferences
                try
                {
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load display preferences");
                }

                // Load app preferences
                try
                {
                    _cachedAppPreferences = await LoadAsync<AppPreferences>("AppPreferences").ConfigureAwait(false);
                    if (_cachedAppPreferences == null)
                    {
                        _cachedAppPreferences = new AppPreferences();
                        await SaveAsync("AppPreferences", _cachedAppPreferences).ConfigureAwait(false);
                    }
                }
                catch (Exception playbackEx)
                {
                    Logger.LogWarning(playbackEx, "Failed to load cached playback preferences during initial load");
                }

                // Load playback positions
                try
                {
                    var positions =
                        await LoadAsync<Dictionary<string, long>>(PreferenceConstants.PlaybackPositionsFileKey)
                            .ConfigureAwait(false);
                    if (positions != null)
                    {
                        lock (_positionsLock)
                        {
                            foreach (var kvp in positions)
                            {
                                _playbackPositions[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load playback positions");
                }

                Logger.LogInformation("Finished loading cached data.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load cached data");
            }
        }

        private async Task CleanupStoredPreferencesAsync()
        {
            var context = CreateErrorContext("CleanupStoredPreferences");
            try
            {
                Logger.LogDebug("=== CleanupStoredPreferencesAsync START ===");

                EnsureApplicationDataLoaded();
                if (_localSettings.Values.ContainsKey("PlaybackPreferences"))
                {
                    Logger.LogDebug("CleanupStoredPreferencesAsync: Found PlaybackPreferences key, removing it");
                    Logger.LogInformation("Removing stored PlaybackPreferences to prevent deserialization issues");
                    _localSettings.Values.Remove("PlaybackPreferences");
                    Logger.LogDebug("CleanupStoredPreferencesAsync: PlaybackPreferences key removed");
                }
                else
                {
                    Logger.LogDebug("CleanupStoredPreferencesAsync: No PlaybackPreferences key found");
                }


                Logger.LogDebug("=== CleanupStoredPreferencesAsync END ===");
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        #endregion
    }
}
