using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Base class for all ViewModels providing common functionality and proper disposal
    /// </summary>
    public abstract class BaseViewModel : ObservableObject, IDisposable
    {
        private bool _disposed = false;
        private string _errorMessage;
        private bool _hasData = false;
        private bool _isError = false;
        private bool _isLoading = false;
        private bool _isRefreshing = false;
        private DateTime _lastDataLoad = DateTime.MinValue;
        private CancellationTokenSource _loadDataCts;

        protected BaseViewModel(ILogger logger = null)
        {
            Logger = logger;
            DisposalCts = new CancellationTokenSource();
            ErrorHandler = App.Current?.Services?.GetService(typeof(IErrorHandlingService)) as IErrorHandlingService;
        }

        /// <summary>
        ///     Logger instance for derived classes
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        ///     Error handling service for standardized error processing
        /// </summary>
        protected IErrorHandlingService ErrorHandler { get; }

        /// <summary>
        ///     CancellationTokenSource for cancelling operations when disposing
        /// </summary>
        protected CancellationTokenSource DisposalCts { get; }

        /// <summary>
        ///     Indicates if the ViewModel is currently loading data
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        /// <summary>
        ///     Error message to display to the user
        /// </summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        /// <summary>
        ///     Indicates if an error has occurred
        /// </summary>
        public bool IsError
        {
            get => _isError;
            set => SetProperty(ref _isError, value);
        }

        /// <summary>
        ///     Indicates if the ViewModel is currently refreshing data
        /// </summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        /// <summary>
        ///     Indicates if the ViewModel has data loaded
        /// </summary>
        public bool HasData
        {
            get => _hasData;
            set => SetProperty(ref _hasData, value);
        }

        /// <summary>
        ///     The timestamp of the last successful data load
        /// </summary>
        public DateTime LastDataLoad
        {
            get => _lastDataLoad;
            protected set => SetProperty(ref _lastDataLoad, value);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Execute an action on the UI thread. Use this after ConfigureAwait(false) to update UI properties.
        /// </summary>
        protected async Task RunOnUIThreadAsync(Action action)
        {
            await UIHelper.RunOnUIThreadAsync(action, logger: Logger);
        }

        /// <summary>
        ///     Override to dispose managed resources
        /// </summary>
        protected virtual void DisposeManaged()
        {
            // Base implementation cancels any pending operations
            DisposalCts?.Cancel();
            DisposalCts?.Dispose();

            // Cancel and dispose data loading token
            DisposeCancellationTokenSource(ref _loadDataCts);
        }

        /// <summary>
        ///     Override to dispose unmanaged resources
        /// </summary>
        protected virtual void DisposeUnmanaged()
        {
            // Base implementation does nothing
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

            DisposeUnmanaged();
            _disposed = true;
        }

        ~BaseViewModel()
        {
            Dispose(false);
        }

        /// <summary>
        ///     Throws if the object has been disposed
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        /// <summary>
        ///     Safe pattern for CancellationTokenSource disposal
        /// </summary>
        protected static void DisposeCancellationTokenSource(ref CancellationTokenSource cts)
        {
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed
                }
                finally
                {
                    cts.Dispose();
                    cts = null;
                }
            }
        }


        /// <summary>
        ///     Create an error context for this view model
        /// </summary>
        protected ErrorContext CreateErrorContext(
            string operation,
            ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }

        #region Data Loading Pattern

        /// <summary>
        ///     Standard pattern for loading data with proper state management and error handling
        /// </summary>
        /// <param name="forceRefresh">Force reload even if data was recently loaded</param>
        /// <param name="cacheTimeout">How long to consider cached data valid (null = always reload)</param>
        public virtual async Task LoadDataAsync(bool forceRefresh = false, TimeSpan? cacheTimeout = null)
        {
            ThrowIfDisposed();

            // Check if we should skip loading
            if (!forceRefresh && HasData && cacheTimeout.HasValue)
            {
                var elapsed = DateTime.UtcNow - LastDataLoad;
                if (elapsed < cacheTimeout.Value)
                {
                    Logger?.LogInformation("Skipping data load - cache is still valid");
                    return;
                }
            }

            var context = CreateErrorContext("LoadData");

            // Cancel any existing load operation
            _loadDataCts?.Cancel();
            _loadDataCts = new CancellationTokenSource();

            try
            {
                await UpdateLoadingStateAsync(true);

                // Call the derived class implementation
                await LoadDataCoreAsync(_loadDataCts.Token);

                // Update state on success
                await RunOnUIThreadAsync(() =>
                {
                    HasData = true;
                    LastDataLoad = DateTime.UtcNow;
                    IsError = false;
                    ErrorMessage = null;
                });
            }
            catch (OperationCanceledException)
            {
                Logger?.LogInformation("Data loading was cancelled");
            }
            catch (Exception ex)
            {
                await RunOnUIThreadAsync(() =>
                {
                    IsError = true;
                    HasData = false;
                });

                if (ErrorHandler != null)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
                else
                {
                    Logger?.LogError(ex, "Error loading data");
                    ErrorMessage = "Failed to load data";
                }
            }
            finally
            {
                await UpdateLoadingStateAsync(false);
            }
        }

        /// <summary>
        ///     Standard pattern for refreshing data
        /// </summary>
        public virtual async Task RefreshAsync()
        {
            ThrowIfDisposed();

            var context = CreateErrorContext("RefreshData");

            try
            {
                IsRefreshing = true;

                // If we don't have data yet, do a full load
                if (!HasData)
                {
                    await LoadDataAsync(true);
                }
                else
                {
                    // Otherwise do a refresh
                    await RefreshDataCoreAsync();

                    await RunOnUIThreadAsync(() =>
                    {
                        LastDataLoad = DateTime.UtcNow;
                        IsError = false;
                        ErrorMessage = null;
                    });
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
                else
                {
                    Logger?.LogError(ex, "Error refreshing data");
                    ErrorMessage = "Failed to refresh data";
                }
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        /// <summary>
        ///     Override this method to implement the actual data loading logic
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        protected virtual Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            // Default implementation does nothing
            // Derived classes should override this to load their data
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Override this method to implement custom refresh logic
        ///     By default, this calls LoadDataCoreAsync
        /// </summary>
        protected virtual async Task RefreshDataCoreAsync()
        {
            // Default implementation just reloads all data
            _loadDataCts?.Cancel();
            _loadDataCts = new CancellationTokenSource();
            await LoadDataCoreAsync(_loadDataCts.Token);
        }

        /// <summary>
        ///     Helper method to update loading state on the UI thread
        /// </summary>
        private async Task UpdateLoadingStateAsync(bool isLoading)
        {
            await RunOnUIThreadAsync(() => IsLoading = isLoading);
        }

        /// <summary>
        ///     Clear all data and reset state
        /// </summary>
        public virtual async Task ClearDataAsync()
        {
            _loadDataCts?.Cancel();

            await RunOnUIThreadAsync(() =>
            {
                HasData = false;
                IsError = false;
                ErrorMessage = null;
                LastDataLoad = default;
            });

            await ClearDataCoreAsync();
        }

        /// <summary>
        ///     Override to clear view model specific data
        /// </summary>
        protected virtual Task ClearDataCoreAsync()
        {
            return Task.CompletedTask;
        }

        #endregion
    }
}
