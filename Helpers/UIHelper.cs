using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Helper class for common UI operations and patterns
    /// </summary>
    public static class UIHelper
    {
        /// <summary>
        ///     Execute an action on the UI thread
        /// </summary>
        public static async Task RunOnUIThreadAsync(Action action, CoreDispatcher dispatcher = null,
            ILogger logger = null)
        {
            dispatcher ??= CoreApplication.MainView?.CoreWindow?.Dispatcher ?? Window.Current?.Dispatcher;

            if (dispatcher == null)
            {
                logger?.LogWarning("Dispatcher is null in RunOnUIThreadAsync");
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error executing action on UI thread");
                }
            }).AsTask().ConfigureAwait(false);
        }

        /// <summary>
        ///     Execute a function on the UI thread and return result
        /// </summary>
        public static async Task<T> RunOnUIThreadAsync<T>(Func<T> func, CoreDispatcher dispatcher = null,
            ILogger logger = null)
        {
            dispatcher ??= CoreApplication.MainView?.CoreWindow?.Dispatcher ?? Window.Current?.Dispatcher;

            if (dispatcher == null)
            {
                logger?.LogWarning("Dispatcher is null in RunOnUIThreadAsync");
                return default;
            }

            var tcs = new TaskCompletionSource<T>();

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    var result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error executing function on UI thread");
                    tcs.SetException(ex);
                }
            }).AsTask().ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        ///     Execute an async action on the UI thread
        /// </summary>
        public static async Task RunOnUIThreadAsync(Func<Task> asyncAction, CoreDispatcher dispatcher = null,
            ILogger logger = null)
        {
            dispatcher ??= CoreApplication.MainView?.CoreWindow?.Dispatcher ?? Window.Current?.Dispatcher;

            if (dispatcher == null)
            {
                logger?.LogWarning("Dispatcher is null in RunOnUIThreadAsync");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await asyncAction();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error executing async action on UI thread");
                    tcs.SetException(ex);
                }
            }).AsTask().ConfigureAwait(false);

            await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        ///     Execute an async function on the UI thread and return result
        /// </summary>
        public static async Task<T> RunOnUIThreadAsync<T>(Func<Task<T>> asyncFunc, CoreDispatcher dispatcher = null,
            ILogger logger = null)
        {
            dispatcher ??= CoreApplication.MainView?.CoreWindow?.Dispatcher ?? Window.Current?.Dispatcher;

            if (dispatcher == null)
            {
                logger?.LogWarning("Dispatcher is null in RunOnUIThreadAsync");
                return default;
            }

            var tcs = new TaskCompletionSource<T>();

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    var result = await asyncFunc();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error executing async function on UI thread");
                    tcs.SetException(ex);
                }
            }).AsTask().ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }

    }
}
