using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Base class for all services providing standardized error handling and logging
    /// </summary>
    public abstract class BaseService : IDisposable
    {
        protected readonly ILogger Logger;
        private IErrorHandlingService _errorHandler;
        private bool _disposed = false;

        protected BaseService(ILogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // ErrorHandler will be retrieved lazily to avoid circular dependency during service construction
        }

        protected IErrorHandlingService ErrorHandler
        {
            get
            {
                if (_errorHandler == null && App.Current?.Services != null)
                {
                    _errorHandler =
                        App.Current.Services.GetService(typeof(IErrorHandlingService)) as IErrorHandlingService;
                }

                return _errorHandler;
            }
        }

        /// <summary>
        ///     Execute a fire-and-forget task with error handling
        /// </summary>
        protected void FireAndForget(Func<Task> asyncAction, [CallerMemberName] string memberName = "")
        {
            AsyncHelper.FireAndForget(asyncAction, Logger, GetType(), memberName);
        }

        /// <summary>
        ///     Execute a fire-and-forget task with cancellation support
        /// </summary>
        protected void FireAndForget(Func<CancellationToken, Task> asyncAction, CancellationToken cancellationToken,
            [CallerMemberName] string memberName = "")
        {
            AsyncHelper.FireAndForget(asyncAction, cancellationToken, Logger, GetType(), memberName);
        }

        /// <summary>
        ///     Retry an operation with exponential backoff (instance method)
        /// </summary>
        protected async Task<T> RetryAsync<T>(Func<Task<T>> operation,
            int maxRetries = RetryConstants.DEFAULT_API_RETRY_ATTEMPTS, TimeSpan? initialDelay = null,
            CancellationToken cancellationToken = default, [CallerMemberName] string memberName = "")
        {
            return await RetryHelper.ExecuteWithRetryAsync(
                operation,
                Logger,
                maxRetries,
                initialDelay,
                cancellationToken,
                $"{GetType().Name}.{memberName}"
            ).ConfigureAwait(false);
        }

        /// <summary>
        ///     Retry an operation with exponential backoff (instance method for void operations)
        /// </summary>
        protected async Task RetryAsync(Func<Task> operation,
            int maxRetries = RetryConstants.DEFAULT_API_RETRY_ATTEMPTS, TimeSpan? initialDelay = null,
            CancellationToken cancellationToken = default, [CallerMemberName] string memberName = "")
        {
            await RetryHelper.ExecuteWithRetryAsync(
                operation,
                Logger,
                maxRetries,
                initialDelay,
                cancellationToken,
                $"{GetType().Name}.{memberName}"
            ).ConfigureAwait(false);
        }

        /// <summary>
        ///     Retry an operation with exponential backoff
        /// </summary>
        public static async Task<T> RetryAsync<T>(Func<Task<T>> operation, ILogger logger,
            int maxRetries = RetryConstants.DEFAULT_API_RETRY_ATTEMPTS, TimeSpan? initialDelay = null,
            CancellationToken cancellationToken = default, [CallerMemberName] string memberName = "")
        {
            return await RetryHelper.ExecuteWithRetryAsync(
                operation,
                logger,
                maxRetries,
                initialDelay,
                cancellationToken,
                memberName
            ).ConfigureAwait(false);
        }


        /// <summary>
        ///     Create an error context for this service
        /// </summary>
        protected ErrorContext CreateErrorContext(
            string operation,
            ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error,
            [CallerMemberName] string memberName = "")
        {
            return new ErrorContext(GetType().Name, operation ?? memberName, category, severity);
        }

        #region Common Helper Methods

        /// <summary>
        ///     Helper method for common null-check initialization pattern
        /// </summary>
        protected T InitializeParameter<T>(T parameter, string parameterName) where T : class
        {
            return parameter ?? throw new ArgumentNullException(parameterName);
        }

        /// <summary>
        ///     Create a DispatcherTimer with the specified interval and handler
        /// </summary>
        protected DispatcherTimer CreateTimer(TimeSpan interval, EventHandler<object> handler)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += handler;
            return timer;
        }

        /// <summary>
        ///     Create a DispatcherTimer with the specified interval in milliseconds and handler
        /// </summary>
        protected DispatcherTimer CreateTimer(int intervalMs, EventHandler<object> handler)
        {
            return CreateTimer(TimeSpan.FromMilliseconds(intervalMs), handler);
        }

        /// <summary>
        ///     Handle an error using the ErrorHandler with standard patterns
        /// </summary>
        protected async Task HandleErrorAsync(
            Exception ex,
            string operation,
            ErrorCategory category = ErrorCategory.System,
            bool showUserMessage = false,
            [CallerMemberName] string memberName = "")
        {
            if (ErrorHandler != null)
            {
                var context = CreateErrorContext(operation, category, ErrorSeverity.Error, memberName);
                await ErrorHandler.HandleErrorAsync(ex, context, showUserMessage);
            }
            else
            {
                // Fallback to logging if ErrorHandler not available
                Logger.LogError(ex, "Error in {ServiceName}.{Operation}", GetType().Name, operation);
            }
        }

        /// <summary>
        ///     Handle an error and return a default value
        /// </summary>
        protected async Task<T> HandleErrorWithDefaultAsync<T>(
            Exception ex,
            string operation,
            T defaultValue = default,
            ErrorCategory category = ErrorCategory.System,
            bool showUserMessage = false,
            [CallerMemberName] string memberName = "")
        {
            await HandleErrorAsync(ex, operation, category, showUserMessage, memberName);
            return defaultValue;
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Dispose managed resources
                DisposeTimers();
                UnsubscribeEvents();
                DisposeServices();
            }

            _disposed = true;
        }

        /// <summary>
        ///     Override to dispose any timers used by the service
        /// </summary>
        protected virtual void DisposeTimers()
        {
            // Override in derived classes to dispose timers
        }

        /// <summary>
        ///     Override to unsubscribe from events
        /// </summary>
        protected virtual void UnsubscribeEvents()
        {
            // Override in derived classes to unsubscribe from events
        }

        /// <summary>
        ///     Override to dispose any child services
        /// </summary>
        protected virtual void DisposeServices()
        {
            // Override in derived classes to dispose child services
        }

        #endregion
    }
}
