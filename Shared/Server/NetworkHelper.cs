using System;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Gelatinarm.Shared.Errors;

namespace Gelatinarm.Shared.Server
{
    public static class NetworkHelper
    {
        public static async Task<bool> CheckNetworkAsync(IErrorHandlingService errorHandler)
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();

                if (profile == null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
                {
                    // An HttpRequestException is what the handler logs and shows as a network error
                    var networkException = new HttpRequestException("No network connection available");
                    var context = new ErrorContext("NetworkHelper", "CheckNetwork", ErrorCategory.Network);
                    await errorHandler.HandleErrorAsync(networkException, context);

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Assumed available when the check itself fails: the request then tells
                await errorHandler.HandleErrorAsync(ex,
                    new ErrorContext("NetworkHelper", "CheckNetwork", ErrorCategory.Network, ErrorSeverity.Warning), false);
                return true;
            }
        }
    }
}
