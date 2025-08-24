using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Unified retry helper for consistent retry logic across the application
    /// </summary>
    public static class RetryHelper
    {
        /// <summary>
        ///     Retry an operation with exponential backoff
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            ILogger logger = null,
            int maxRetries = RetryConstants.DEFAULT_API_RETRY_ATTEMPTS,
            TimeSpan? initialDelay = null,
            CancellationToken cancellationToken = default,
            [CallerMemberName] string memberName = "",
            Func<Exception, bool> shouldRetry = null)
        {
            var retryCount = 0;
            var delay = initialDelay ?? TimeSpan.FromMilliseconds(RetryConstants.INITIAL_RETRY_DELAY_MS);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // Don't retry cancellations
                }
                catch (Exception ex) when (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
                {
                    // Check if we should retry this exception
                    if (shouldRetry != null && !shouldRetry(ex))
                    {
                        throw;
                    }

                    // Default retry logic for common transient errors
                    if (!IsTransientError(ex))
                    {
                        throw;
                    }

                    retryCount++;

                    // Logging for HttpRequestException
                    if (ex is HttpRequestException httpEx)
                    {
                        logger?.LogWarning($"Retry {retryCount}/{maxRetries} for {memberName}: HttpRequestException");
                        logger?.LogWarning($"  Message: {httpEx.Message}");
                        if (httpEx.InnerException != null)
                        {
                            logger?.LogWarning(
                                $"  Inner Exception: {httpEx.InnerException.GetType().Name}: {httpEx.InnerException.Message}");
                        }

                        logger?.LogWarning($"  Stack Trace: {httpEx.StackTrace}");
                    }
                    else if (ex is ApiException apiEx)
                    {
                        // For server restart scenarios (502, 503, 504), use longer delays
                        if (apiEx.ResponseStatusCode == 502 || apiEx.ResponseStatusCode == 503 || apiEx.ResponseStatusCode == 504)
                        {
                            logger?.LogInformation($"Server appears to be restarting (HTTP {apiEx.ResponseStatusCode}). Waiting before retry {retryCount}/{maxRetries}...");
                            // Use longer delay for server restart scenarios
                            delay = TimeSpan.FromSeconds(Math.Min(5 * retryCount, 15)); // 5s, 10s, 15s
                        }
                        else
                        {
                            logger?.LogWarning($"Retry {retryCount}/{maxRetries} for {memberName}: ApiException");
                            logger?.LogWarning($"  Status Code: {apiEx.ResponseStatusCode}");
                            logger?.LogWarning($"  Message: {apiEx.Message}");
                        }

                        if (apiEx.ResponseHeaders != null && apiEx.ResponseHeaders.Count > 0)
                        {
                            logger?.LogDebug(
                                $"  Response Headers: {string.Join(", ", apiEx.ResponseHeaders.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");
                        }
                    }
                    else
                    {
                        logger?.LogWarning(
                            $"Retry {retryCount}/{maxRetries} for {memberName}: {ex.GetType().Name} - {ex.Message}");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                    // Exponential backoff with jitter
                    var random = new Random();
                    var jitter = random.Next(0, 1000);
                    delay = TimeSpan.FromMilliseconds((delay.TotalMilliseconds * 2) + jitter);

                    // Cap the delay at maximum retry delay
                    if (delay.TotalSeconds > RetryConstants.MAX_RETRY_DELAY_SECONDS)
                    {
                        delay = TimeSpan.FromSeconds(RetryConstants.MAX_RETRY_DELAY_SECONDS);
                    }
                }
            }
        }

        /// <summary>
        ///     Retry an operation that doesn't return a value
        /// </summary>
        public static async Task ExecuteWithRetryAsync(
            Func<Task> operation,
            ILogger logger = null,
            int maxRetries = RetryConstants.DEFAULT_API_RETRY_ATTEMPTS,
            TimeSpan? initialDelay = null,
            CancellationToken cancellationToken = default,
            [CallerMemberName] string memberName = "",
            Func<Exception, bool> shouldRetry = null)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }, logger, maxRetries, initialDelay, cancellationToken, memberName, shouldRetry).ConfigureAwait(false);
        }

        /// <summary>
        ///     Determines if an exception is a transient error that should be retried
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            return ex switch
            {
                // Network errors
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

                // Other transient errors
                InvalidOperationException ioe when ioe.Message.Contains("SDK client not available") => true,

                _ => false
            };
        }
    }
}
