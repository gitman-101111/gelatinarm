using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Base
{
    public abstract class BaseService : IDisposable
    {
        protected readonly ILogger Logger;
        private IErrorHandlingService _errorHandler;
        private bool _disposed;

        protected BaseService(ILogger logger)
        {
            Logger = logger;
        }

        // Resolved on first use: the error handler is itself a service, and resolving it at
        // construction would make the graph circular
        protected IErrorHandlingService ErrorHandler => _errorHandler ??= ServiceLocator.GetRequiredService<IErrorHandlingService>();

        protected void FireAndForget(Func<Task> asyncAction, [CallerMemberName] string memberName = "")
        {
            AsyncHelper.FireAndForget(asyncAction, Logger, GetType(), memberName);
        }

        protected Task<T> RetryAsync<T>(Func<Task<T>> operation,
            CancellationToken cancellationToken = default, [CallerMemberName] string memberName = "")
        {
            return RetryHelper.ExecuteWithRetryAsync(operation, Logger, cancellationToken,
                memberName: $"{GetType().Name}.{memberName}");
        }

        protected ErrorContext CreateErrorContext(
            string operation,
            ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }

        protected bool TryGetUserIdGuid(IUserProfileService userProfileService, out Guid userIdGuid)
        {
            userIdGuid = Guid.Empty;
            var userId = userProfileService.GetCurrentUserGuid();
            if (!userId.HasValue)
            {
                Logger.LogDebug("User ID not available");
                return false;
            }

            userIdGuid = userId.Value;
            return true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected bool IsDisposed => _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                UnsubscribeEvents();
            }

            _disposed = true;
        }

        protected virtual void UnsubscribeEvents()
        {
        }
    }
}
