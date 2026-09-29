using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Preferences;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     Applies the Connection Timeout setting to each request. HttpClient.Timeout is fixed
    ///     once a client has sent anything, and the API client is built at startup, before a user
    ///     (whose preferences hold the setting) is known -- so the setting is read at send time.
    /// </summary>
    public sealed class ConnectionTimeoutHandler : DelegatingHandler
    {
        private readonly IPreferencesService _preferencesService;

        public ConnectionTimeoutHandler(IPreferencesService preferencesService)
        {
            _preferencesService = preferencesService;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var preferences = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(preferences.ConnectionTimeout));
            return await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
    }
}
