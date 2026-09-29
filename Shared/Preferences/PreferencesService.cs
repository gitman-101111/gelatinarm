using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Storage;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Preferences
{
    public interface IPreferencesService
    {
        T GetValue<T>(string key, T defaultValue = default);
        void SetValue<T>(string key, T value);
        void RemoveValue(string key);

        Task<AppPreferences> GetAppPreferencesAsync();
        Task UpdateAppPreferencesAsync(AppPreferences preferences);

        /// <summary>
        ///     Raised after UpdateAppPreferencesAsync has saved, with the preferences as saved
        /// </summary>
        event EventHandler<AppPreferences> AppPreferencesChanged;

        /// <summary>
        ///     Scopes app preferences to the given user (null = no active user).
        ///     Clears the cached preferences so the next read loads the user's own settings.
        /// </summary>
        void SetActiveUserScope(string userId);

        void RemoveUserScopedValues(string userId);
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

        private const string AppPreferencesBaseKey = "AppPreferences";

        private readonly object _appPreferencesLock = new();

        private AppPreferences _cachedAppPreferences;

        private StorageFolder _localFolder;
        private ApplicationDataContainer _localSettings;

        // Active user scope for per-user settings; pushed by AuthenticationService
        private string _userScope;

        public event EventHandler<AppPreferences> AppPreferencesChanged;

        // The cache fills on the first read (the first request's timeout handler makes it); a
        // warm-up here would run while the services it needs are still being constructed
        public PreferencesService(ILogger<PreferencesService> logger) : base(logger)
        {
            FireAndForget(async () =>
            {
                await Task.Delay(RetryConstants.CleanupTaskDelayMs).ConfigureAwait(false);
                await CleanupStoredPreferencesAsync().ConfigureAwait(false);
            });
        }

        /// <summary>
        ///     Unscoped (pre-login) reads use the base key
        /// </summary>
        private string AppPreferencesKey =>
            string.IsNullOrEmpty(_userScope) ? AppPreferencesBaseKey : $"{AppPreferencesBaseKey}_{_userScope}";

        public void SetActiveUserScope(string userId)
        {
            lock (_appPreferencesLock)
            {
                if (_userScope == userId)
                {
                    return;
                }

                _userScope = userId;
                // Drop the previous user's cached preferences; next read loads the new user's
                _cachedAppPreferences = null;
            }

            Logger.LogDebug("App preferences scoped to user: {UserId}", userId ?? "none");
        }

        public void RemoveUserScopedValues(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            var key = $"{AppPreferencesBaseKey}_{userId}";
            lock (_appPreferencesLock)
            {
                if (_userScope == userId)
                {
                    _cachedAppPreferences = null;
                }
            }

            // Removes both the local-settings entry and the backing file
            FireAndForget(() => RemoveSettingAsync(key));
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
                _cachedAppPreferences = await LoadOrCreateAppPreferencesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("GetAppPreferences", ErrorCategory.Configuration), false);
                // Defaults stand in for this read only: cached, a later save would write them over
                // the user's settings; the next read tries storage again
                return new AppPreferences();
            }

            lock (_appPreferencesLock)
            {
                return _cachedAppPreferences;
            }
        }

        /// <summary>
        ///     The stored preferences, or new defaults (saved) when there are none. Throws on a
        ///     read failure: the caller decides whether defaults may stand in.
        /// </summary>
        private async Task<AppPreferences> LoadOrCreateAppPreferencesAsync()
        {
            var preferences = await LoadOrMigrateAppPreferencesAsync().ConfigureAwait(false);
            if (preferences == null)
            {
                preferences = new AppPreferences();
                await SaveAsync(AppPreferencesKey, preferences).ConfigureAwait(false);
            }

            return preferences;
        }

        public async Task UpdateAppPreferencesAsync(AppPreferences preferences)
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }

            await SaveAsync(AppPreferencesKey, preferences).ConfigureAwait(false);
            lock (_appPreferencesLock)
            {
                _cachedAppPreferences = preferences;
            }

            AppPreferencesChanged?.Invoke(this, preferences);
        }

        /// <summary>
        ///     Loads the active user's preferences. One-time upgrade path: installs from
        ///     before per-user settings have a single unscoped blob - the first user to
        ///     sign in adopts it, then it is deleted so later users start from defaults.
        /// </summary>
        private async Task<AppPreferences> LoadOrMigrateAppPreferencesAsync()
        {
            var prefs = await LoadAsync<AppPreferences>(AppPreferencesKey).ConfigureAwait(false);
            if (prefs != null || string.IsNullOrEmpty(_userScope))
            {
                return prefs;
            }

            prefs = await LoadAsync<AppPreferences>(AppPreferencesBaseKey).ConfigureAwait(false);
            if (prefs != null)
            {
                await SaveAsync(AppPreferencesKey, prefs).ConfigureAwait(false);
                await RemoveSettingAsync(AppPreferencesBaseKey).ConfigureAwait(false);
                Logger.LogInformation("Migrated legacy app preferences to user scope: {UserScope}", _userScope);
            }

            return prefs;
        }

        private async Task SaveAsync<T>(string key, T data)
        {
            var context = CreateErrorContext("SaveAsync");
            try
            {
                var json = JsonSerializer.Serialize(data, JsonOptions);

                if (XboxDevice.IsXbox)
                {
                    SetValue(key, json);
                }
                else
                {
                    EnsureApplicationDataLoaded();

                    var file = await _localFolder
                        .CreateFileAsync($"{key}.json", CreationCollisionOption.ReplaceExisting).AsTask()
                        .ConfigureAwait(false);
                    await FileIO.WriteTextAsync(file, json).AsTask().ConfigureAwait(false);
                }

                Logger.LogDebug("Saved data for key: {Key}", key);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        // Missing is "none"; any other failure is thrown -- taken for "none", a failed read had
        // its caller save defaults over the user's settings
        private async Task<T> LoadAsync<T>(string key)
        {
            EnsureApplicationDataLoaded();

            string json = null;
            if (XboxDevice.IsXbox && _localSettings.Values.TryGetValue(key, out var stored))
            {
                json = stored as string;
            }

            if (string.IsNullOrEmpty(json))
            {
                try
                {
                    var file = await _localFolder.GetFileAsync($"{key}.json").AsTask().ConfigureAwait(false);
                    json = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                }
                catch (FileNotFoundException ex)
                {
                    Logger.LogDebug(ex, "No stored value for key {Key}", key);
                    return default;
                }
            }

            return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        public T GetValue<T>(string key, T defaultValue = default)
        {
            var context = CreateErrorContext("GetValue");
            try
            {
                EnsureApplicationDataLoaded();

                if (_localSettings.Values.TryGetValue(key, out var value))
                {
                    if (key.StartsWith(AppPreferencesBaseKey, StringComparison.Ordinal))
                    {
                        Logger.LogDebug("[PREFERENCES] Retrieved {Key} from local settings", key);
                    }
                    else if (value is string strVal && strVal.Length > SystemConstants.MaxPreferenceStringLength)
                    {
                        Logger.LogDebug("[PREFERENCES] Retrieved {Key} (large value) from local settings", key);
                    }
                    else
                    {
                        Logger.LogDebug("[PREFERENCES] Retrieved {Key} = '{Value}' from local settings", key, value);
                    }

                    if (value is T typedValue)
                    {
                        return typedValue;
                    }

                    if (value is string jsonValue && typeof(T).IsClass && typeof(T) != typeof(string))
                    {
                        return JsonSerializer.Deserialize<T>(jsonValue, JsonOptions);
                    }

                    return (T)Convert.ChangeType(value, typeof(T));
                }

                Logger.LogDebug("[PREFERENCES] Key '{Key}' not found in local settings, returning default", key);
                return defaultValue;
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, context);

                return defaultValue;
            }
        }

        public void SetValue<T>(string key, T value)
        {
            var context = CreateErrorContext("SetValue");
            try
            {
                EnsureApplicationDataLoaded();

                if (value == null)
                {
                    _localSettings.Values.Remove(key);
                    Logger.LogDebug("[PREFERENCES] Removed key '{Key}' from local settings.", key);
                }
                else if (value is string || value.GetType().IsPrimitive)
                {
                    _localSettings.Values[key] = value;
                    if (key.StartsWith(AppPreferencesBaseKey, StringComparison.Ordinal) || (value is string strVal &&
                                                    strVal.Length > SystemConstants.MaxPreferenceStringLength))
                    {
                        Logger.LogDebug("[PREFERENCES] Stored {Key} to local settings", key);
                    }
                    else
                    {
                        Logger.LogDebug("[PREFERENCES] Stored {Key} = '{Value}' to local settings", key, value);
                    }
                }
                else
                {
                    _localSettings.Values[key] = JsonSerializer.Serialize(value, JsonOptions);
                    Logger.LogDebug("[PREFERENCES] Stored serialized object for key '{Key}' to local settings.", key);
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, context);
            }
        }

        public void RemoveValue(string key)
        {
            var context = CreateErrorContext("RemoveValue");
            try
            {
                EnsureApplicationDataLoaded();

                if (_localSettings.Values.Remove(key))
                {
                    Logger.LogDebug("[PREFERENCES] Removed key '{Key}' from local settings.", key);
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, context);
            }
        }

        private async Task RemoveSettingAsync(string key)
        {
            var context = CreateErrorContext("RemoveSettingAsync");
            try
            {
                EnsureApplicationDataLoaded();

                _localSettings.Values.Remove(key);

                try
                {
                    var file = await _localFolder.GetFileAsync($"{key}.json").AsTask().ConfigureAwait(false);
                    await file.DeleteAsync().AsTask().ConfigureAwait(false);
                }
                catch (FileNotFoundException ex)
                {
                    Logger.LogDebug(ex, "No stored file for key {Key} to remove", key);
                }

                Logger.LogDebug("Removed setting: {Key}", key);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void EnsureApplicationDataLoaded()
        {
            if (_localSettings == null)
            {
                var appData = ApplicationData.Current;
                _localSettings = appData.LocalSettings;
                _localFolder = appData.LocalFolder;
            }
        }

        private async Task CleanupStoredPreferencesAsync()
        {
            var context = CreateErrorContext("CleanupStoredPreferences");
            try
            {
                // Older versions stored a PlaybackPreferences object this version cannot read
                EnsureApplicationDataLoaded();
                if (_localSettings.Values.Remove("PlaybackPreferences"))
                {
                    Logger.LogInformation("Removed the stored PlaybackPreferences left by an older version");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }
    }
}
