using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Async
{
    public static class AsyncHelper
    {
        /// <summary>
        ///     Cancels the operation owning <paramref name="slot" /> and puts a fresh source there
        ///     for the one replacing it, linked to <paramref name="linkedTo" /> when given. The swap
        ///     is atomic, so two overlapping starts cannot both keep running.
        /// </summary>
        public static CancellationTokenSource Supersede(ref CancellationTokenSource slot,
            CancellationToken linkedTo = default)
        {
            var next = linkedTo.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(linkedTo)
                : new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref slot, next);
            previous?.Cancel();
            previous?.Dispose();
            return next;
        }

        /// <summary>
        ///     Cancels the operation owning <paramref name="slot" /> and empties the slot, so a
        ///     second call (a Dispose after a Stop, say) finds nothing to cancel.
        /// </summary>
        public static void Cancel(ref CancellationTokenSource slot)
        {
            var previous = Interlocked.Exchange(ref slot, null);
            previous?.Cancel();
            previous?.Dispose();
        }

        public static void FireAndForget(
            Func<Task> asyncAction,
            ILogger logger,
            Type callerType,
            [CallerMemberName] string memberName = "")
        {
            _ = RunAsync(asyncAction, logger, callerType, memberName);
        }

        private static async Task RunAsync(Func<Task> asyncAction, ILogger logger, Type callerType, string memberName)
        {
            try
            {
                await asyncAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fire-and-forget task failed in {TypeName}.{MemberName}", callerType.Name, memberName);
            }
        }
    }
}
