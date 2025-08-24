using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Provides helper methods for async operations
    /// </summary>
    public static class AsyncHelper
    {
        /// <summary>
        ///     Executes an async action in a fire-and-forget manner with exception handling
        /// </summary>
        /// <param name="asyncAction">The async action to execute</param>
        /// <param name="logger">Optional logger for error reporting</param>
        /// <param name="callerType">The type of the calling class</param>
        /// <param name="memberName">The name of the calling member (auto-populated)</param>
        public static async void FireAndForget(
            Func<Task> asyncAction,
            ILogger logger = null,
            Type callerType = null,
            [CallerMemberName] string memberName = "")
        {
            try
            {
                await asyncAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var typeName = callerType?.Name ?? "Unknown";
                logger?.LogError(ex, $"Fire-and-forget task failed in {typeName}.{memberName}");
            }
        }

        /// <summary>
        ///     Executes an async action in a fire-and-forget manner with cancellation support
        /// </summary>
        /// <param name="asyncAction">The async action to execute</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="logger">Optional logger for error reporting</param>
        /// <param name="callerType">The type of the calling class</param>
        /// <param name="memberName">The name of the calling member (auto-populated)</param>
        public static async void FireAndForget(
            Func<CancellationToken, Task> asyncAction,
            CancellationToken cancellationToken,
            ILogger logger = null,
            Type callerType = null,
            [CallerMemberName] string memberName = "")
        {
            try
            {
                await asyncAction(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var typeName = callerType?.Name ?? "Unknown";
                logger?.LogDebug($"Fire-and-forget task cancelled in {typeName}.{memberName}");
            }
            catch (Exception ex)
            {
                var typeName = callerType?.Name ?? "Unknown";
                logger?.LogError(ex, $"Fire-and-forget task failed in {typeName}.{memberName}");
            }
        }

    }
}
