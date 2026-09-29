using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Music;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Base
{
    public abstract partial class BaseViewModel : ObservableObject, IDisposable
    {
        private bool _disposed;
        [ObservableProperty] private string _errorMessage;
        [ObservableProperty] private bool _hasData;
        [ObservableProperty] private bool _isError;
        [ObservableProperty] private bool _isLoading;
        private DateTime _lastDataLoad = DateTime.MinValue;
        private CancellationTokenSource _loadDataCts;

        protected BaseViewModel(ILogger logger)
        {
            Logger = logger;
            DisposalCts = new CancellationTokenSource();
            ErrorHandler = GetRequiredService<IErrorHandlingService>();
        }

        protected ILogger Logger { get; }

        protected IErrorHandlingService ErrorHandler { get; }

        protected CancellationTokenSource DisposalCts { get; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     After a ConfigureAwait(false), the way back to the UI thread for a property update
        /// </summary>
        protected Task RunOnUIThreadAsync(Action action)
        {
            return UiHelper.RunOnUIThreadAsync(action, logger: Logger);
        }

        protected Task RunOnUIThreadAsync(Func<Task> asyncAction)
        {
            return UiHelper.RunOnUIThreadAsync(asyncAction, logger: Logger);
        }

        protected void FireAndForget(Func<Task> asyncAction, [CallerMemberName] string memberName = "")
        {
            AsyncHelper.FireAndForget(asyncAction, Logger, GetType(), memberName);
        }

        protected static bool TryGetGuidFromParameter(object parameter, out Guid guid)
        {
            if (parameter is Guid guidValue || (parameter is string guidString && Guid.TryParse(guidString, out guidValue)))
            {
                guid = guidValue;
                return true;
            }

            guid = Guid.Empty;
            return false;
        }

        /// <summary>
        ///     Stops music before the signed-in user changes (sign-out, profile switch, adding a
        ///     user). The music player is app-wide, so otherwise it keeps playing and reporting
        ///     under whoever signs in next. Waits for the stopped report so it is sent under the
        ///     current user's token.
        /// </summary>
        protected async Task StopMusicBeforeUserChangeAsync()
        {
            var musicPlayer = GetRequiredService<IMusicPlayerService>();
            if (musicPlayer.CurrentItem == null)
            {
                return;
            }

            Logger.LogInformation("Stopping music before the signed-in user changes");
            await musicPlayer.StopAsync();
        }

        protected static T GetRequiredService<T>() where T : class
        {
            return ServiceLocator.GetRequiredService<T>();
        }

        protected virtual void DisposeManaged()
        {
            DisposalCts.Cancel();
            DisposalCts.Dispose();

            AsyncHelper.Cancel(ref _loadDataCts);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                DisposeManaged();
            }

            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        protected ErrorContext CreateErrorContext(
            string operation,
            ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }

        public async Task LoadDataAsync(bool forceRefresh, TimeSpan? cacheTimeout = null)
        {
            ThrowIfDisposed();

            if (!forceRefresh && HasData && cacheTimeout.HasValue)
            {
                var elapsed = DateTime.UtcNow - _lastDataLoad;
                if (elapsed < cacheTimeout.Value)
                {
                    Logger.LogDebug("Skipping data load - cache is still valid");
                    return;
                }
            }

            var context = CreateErrorContext("LoadData");
            var token = AsyncHelper.Supersede(ref _loadDataCts).Token;

            try
            {
                await UpdateLoadingStateAsync(true);

                await LoadDataCoreAsync(token);

                await RunOnUIThreadAsync(() =>
                {
                    HasData = true;
                    _lastDataLoad = DateTime.UtcNow;
                    IsError = false;
                    ErrorMessage = null;
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer load, or disposal
            }
            catch (Exception ex)
            {
                await RunOnUIThreadAsync(() =>
                {
                    IsError = true;
                    HasData = false;
                });

                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                await UpdateLoadingStateAsync(false);
            }
        }

        public virtual async Task RefreshAsync()
        {
            ThrowIfDisposed();

            var context = CreateErrorContext("RefreshData");

            try
            {
                if (!HasData)
                {
                    await LoadDataAsync(true);
                }
                else
                {
                    await RefreshDataCoreAsync();

                    await RunOnUIThreadAsync(() =>
                    {
                        _lastDataLoad = DateTime.UtcNow;
                        IsError = false;
                        ErrorMessage = null;
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        protected virtual Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected virtual Task RefreshDataCoreAsync()
        {
            return LoadDataCoreAsync(AsyncHelper.Supersede(ref _loadDataCts).Token);
        }

        private Task UpdateLoadingStateAsync(bool isLoading)
        {
            return RunOnUIThreadAsync(() => IsLoading = isLoading);
        }
    }
}
