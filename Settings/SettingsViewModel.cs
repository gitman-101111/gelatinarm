using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Settings
{
    // The settings page's three sections, each its own view model
    public class SettingsViewModel : BaseViewModel
    {
        public SettingsViewModel(
            ServerSettingsViewModel serverSettings,
            PlaybackSettingsViewModel playbackSettings,
            HomeSettingsViewModel homeSettings,
            ILogger<SettingsViewModel> logger) : base(logger)
        {
            ServerSettings = serverSettings;
            PlaybackSettings = playbackSettings;
            HomeSettings = homeSettings;
        }

        public ServerSettingsViewModel ServerSettings { get; }
        public PlaybackSettingsViewModel PlaybackSettings { get; }
        public HomeSettingsViewModel HomeSettings { get; }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            var tasks = new[]
            {
                ServerSettings.InitializeAsync(), PlaybackSettings.InitializeAsync(), HomeSettings.InitializeAsync()
            };

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
