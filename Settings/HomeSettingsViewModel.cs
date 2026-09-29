using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Preferences;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Settings
{
    /// <summary>
    ///     Home screen settings: how the Latest Movies and Latest TV Shows rows are ordered
    /// </summary>
    public class HomeSettingsViewModel : SettingsViewModelBase
    {
        // Shown until the saved preferences load: the default they carry, stated once
        private bool _sortLatestByReleaseDate = new AppPreferences().SortLatestByReleaseDate;

        public HomeSettingsViewModel(
            ILogger<HomeSettingsViewModel> logger,
            IPreferencesService preferencesService) : base(logger, preferencesService)
        {
        }

        public bool SortLatestByReleaseDate
        {
            get => _sortLatestByReleaseDate;
            set => SetAndSave(ref _sortLatestByReleaseDate, value, (prefs, v) => prefs.SortLatestByReleaseDate = v);
        }

        protected override async Task LoadSettingsAsync(CancellationToken cancellationToken)
        {
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                _sortLatestByReleaseDate = appPrefs.SortLatestByReleaseDate;
                OnPropertyChanged(nameof(SortLatestByReleaseDate));
            });
        }
    }
}
