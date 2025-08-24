using System;
using System.Net.Http;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;

namespace Gelatinarm.Helpers
{
    /// <summary>
    /// Helper class for network connectivity checks
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// Checks if network is available and shows error dialog if not
        /// </summary>
        /// <returns>True if network is available, false otherwise</returns>
        public static async Task<bool> CheckNetworkAsync(IErrorHandlingService errorHandler = null, ILogger logger = null)
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();

                if (profile == null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
                {
                    logger?.LogWarning("No network connection detected");

                    if (errorHandler != null)
                    {
                        // Create a fake HttpRequestException to trigger the network error message
                        var networkException = new HttpRequestException("No network connection available");
                        var context = new ErrorContext("NetworkCheck", "NetworkHelper", ErrorCategory.Network);
                        await errorHandler.HandleErrorAsync(networkException, context, showUserMessage: true);
                    }

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error checking network connectivity");
                // Assume network is available if we can't check
                return true;
            }
        }

        /// <summary>
        /// Executes an action only if network is available
        /// </summary>
        public static async Task<T> ExecuteWithNetworkCheckAsync<T>(
            Func<Task<T>> action,
            T defaultValue,
            IErrorHandlingService errorHandler = null,
            ILogger logger = null)
        {
            if (!await CheckNetworkAsync(errorHandler, logger))
            {
                return defaultValue;
            }

            return await action();
        }
    }
}