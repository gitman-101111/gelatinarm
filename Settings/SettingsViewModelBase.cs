using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Preferences;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Settings
{
    /// <summary>
    ///     Shared scaffolding for the settings sections: loading, and saving each change as it
    ///     is made; subclasses supply the settings through LoadSettingsAsync.
    /// </summary>
    public abstract class SettingsViewModelBase : BaseViewModel
    {
        protected readonly IPreferencesService PreferencesService;

        protected SettingsViewModelBase(ILogger logger, IPreferencesService preferencesService) : base(logger)
        {
            PreferencesService = preferencesService;
        }

        protected abstract Task LoadSettingsAsync(CancellationToken cancellationToken);

        public virtual Task InitializeAsync()
        {
            return LoadDataAsync(true);
        }

        protected override Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            return LoadSettingsAsync(cancellationToken);
        }

        /// <summary>
        ///     Saves one changed setting: reads AppPreferences, applies the change and writes it
        ///     back. Settings save as they change, so this is the only save path; AppPreferences
        ///     keeps each value in range. One Information line per setting the user changes, from
        ///     here rather than each setter.
        /// </summary>
        protected async Task UpdateAppPreferenceAsync(Action<AppPreferences> updateAction, object newValue,
            [CallerMemberName] string setting = "")
        {
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            updateAction(appPrefs);
            await PreferencesService.UpdateAppPreferencesAsync(appPrefs).ConfigureAwait(false);
            Logger.LogInformation("Setting {Setting} changed to {Value}", setting, newValue);
        }

        /// <summary>
        ///     A setting's setter: the backing field, and on a change the save. The sections' setters
        ///     are this one line each; a setter that converts its value first calls UpdateAppPreferenceAsync itself.
        /// </summary>
        protected void SetAndSave<T>(ref T field, T value, Action<AppPreferences, T> apply, [CallerMemberName] string setting = "")
        {
            if (SetProperty(ref field, value, setting))
            {
                FireAndForget(() => UpdateAppPreferenceAsync(prefs => apply(prefs, value), value, setting), setting);
            }
        }
    }
}
