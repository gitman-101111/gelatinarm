using System;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Shared.Async
{
    public static class RetryHelper
    {
        // One generator for every retry: instances created in the same tick share a seed, so the
        // home screen's parallel row retries would all draw the same jitter
        private static readonly Random Jitter = new();

        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            ILogger logger,
            CancellationToken cancellationToken,
            int maxRetries = RetryConstants.DefaultApiRetryAttempts,
            TimeSpan? initialDelay = null,
            [CallerMemberName] string memberName = "")
        {
            var retryCount = 0;
            var delay = initialDelay ?? TimeSpan.FromMilliseconds(RetryConstants.InitialRetryDelayMs);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && retryCount < maxRetries &&
                                           !cancellationToken.IsCancellationRequested)
                {
                    if (!IsTransientError(ex))
                    {
                        throw;
                    }

                    retryCount++;

                    // The exception itself carries type, message, inner exception and stack
                    if (ex is ApiException apiEx && IsServerUnavailable(apiEx))
                    {
                        logger.LogInformation(
                            "Server appears to be restarting (HTTP {ApiExResponseStatusCode}). Waiting before retry {RetryCount}/{MaxRetries}...", apiEx.ResponseStatusCode, retryCount, maxRetries);
                        delay = TimeSpan.FromSeconds(Math.Min(5 * retryCount, 15));
                    }
                    else
                    {
                        logger.LogWarning(ex, "Retry {RetryCount}/{MaxRetries} for {MemberName}", retryCount, maxRetries, memberName);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                    int jitter;
                    lock (Jitter)
                    {
                        jitter = Jitter.Next(0, 1000);
                    }

                    delay = TimeSpan.FromMilliseconds((delay.TotalMilliseconds * 2) + jitter);

                    if (delay.TotalSeconds > RetryConstants.MaxRetryDelaySeconds)
                    {
                        delay = TimeSpan.FromSeconds(RetryConstants.MaxRetryDelaySeconds);
                    }
                }
            }
        }

        /// <summary>
        ///     A gateway or availability error: the server is restarting or updating, so retries wait
        ///     longer and the user is told to wait rather than that something failed
        /// </summary>
        public static bool IsServerUnavailable(ApiException apiEx)
        {
            return apiEx.ResponseStatusCode == 502 || apiEx.ResponseStatusCode == 503 ||
                   apiEx.ResponseStatusCode == 504;
        }

        private static bool IsTransientError(Exception ex)
        {
            return ex switch
            {
                HttpRequestException => true,
                WebException => true,
                TimeoutException => true,

                // Jellyfin SDK errors that might be transient
                ApiException apiEx => apiEx.ResponseStatusCode switch
                {
                    408 => true, // Request Timeout
                    429 => true, // Too Many Requests
                    500 => true, // Internal Server Error
                    502 => true, // Bad Gateway
                    503 => true, // Service Unavailable
                    504 => true, // Gateway Timeout
                    _ => false
                },

                _ => false
            };
        }
    }
}
