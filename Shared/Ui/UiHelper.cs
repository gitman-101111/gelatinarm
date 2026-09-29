using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Ui
{
    public static class UiHelper
    {
        /// <summary>
        ///     An exception from the action is logged and not passed on (the other overload passes
        ///     it to the caller)
        /// </summary>
        public static async Task RunOnUIThreadAsync(Action action, ILogger logger, CoreDispatcher dispatcher = null)
        {
            try
            {
                await RunAsync(() =>
                {
                    action();
                    return Task.CompletedTask;
                }, dispatcher, logger).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already logged by RunAsync; this overload has never thrown to its callers.
            }
        }

        // Two overloads only, of the same shape: a third (Func<T>) once won overload resolution for a
        // Task-returning method group and left its task unawaited
        public static Task RunOnUIThreadAsync(Func<Task> asyncAction, ILogger logger, CoreDispatcher dispatcher = null)
        {
            return RunAsync(asyncAction, dispatcher, logger);
        }

        /// <summary>
        ///     Completes once the UI thread has finished the layout and render work already queued: a
        ///     callback at Low priority runs behind it. What a fixed delay used to guess at before a
        ///     focus move or a scroll into a freshly laid-out page.
        /// </summary>
        public static Task WhenIdleAsync(CoreDispatcher dispatcher = null)
        {
            dispatcher ??= CoreApplication.MainView?.CoreWindow?.Dispatcher ?? Window.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return Task.CompletedTask;
            }

            var idle = new TaskCompletionSource<bool>();
            _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, () => idle.SetResult(true));
            return idle.Task;
        }

        /// <summary>
        ///     The container of <paramref name="index" /> once the list has made and arranged it, or null
        ///     when it has none (an item scrolled out of a virtualizing panel, an emptied list). On the UI
        ///     thread. UpdateLayout runs the layout pass the items' arrival left pending, which makes and
        ///     arranges the containers now: a Low-priority wait can come round before the next layout tick,
        ///     and did on the library picker. Where the panel still prepares the container later,
        ///     ContainerContentChanging says when, and the idle after that says it is arranged.
        /// </summary>
        public static async Task<SelectorItem> ContainerWhenReadyAsync(ListViewBase list, int index)
        {
            list.UpdateLayout();
            if (list.ContainerFromIndex(index) is SelectorItem ready)
            {
                return ready;
            }

            var prepared = new TaskCompletionSource<bool>();
            TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs> onPrepared = null;
            onPrepared = (sender, args) =>
            {
                if (args.ItemIndex == index && !args.InRecycleQueue)
                {
                    sender.ContainerContentChanging -= onPrepared;
                    prepared.TrySetResult(true);
                }
            };
            list.ContainerContentChanging += onPrepared;
            await Task.WhenAny(prepared.Task, WhenIdleAsync(list.Dispatcher));
            list.ContainerContentChanging -= onPrepared;
            await WhenIdleAsync(list.Dispatcher);
            return list.ContainerFromIndex(index) as SelectorItem;
        }

        private static async Task RunAsync(Func<Task> work, CoreDispatcher dispatcher, ILogger logger)
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
                    await work();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error executing work on UI thread");
                    tcs.SetException(ex);
                }
            }).AsTask().ConfigureAwait(false);

            await tcs.Task.ConfigureAwait(false);
        }
    }
}
