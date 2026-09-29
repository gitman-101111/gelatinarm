using System;
using System.Net.Http;
using System.Threading.Tasks;
using Gelatinarm.Player;
using Gelatinarm.Shared.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Shared.Errors
{
    public interface IErrorHandlingService
    {
        Task HandleErrorAsync(Exception exception, ErrorContext context, bool showUserMessage = true);

        /// <summary>
        ///     Handles the error without a dialog and hands back <paramref name="defaultValue" />
        /// </summary>
        Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue);

        /// <summary>
        ///     For contexts that cannot await: the dialog, if any, is fired and forgotten
        /// </summary>
        void HandleError(Exception exception, ErrorContext context, bool showUserMessage = false);

        /// <summary>
        ///     The message the user is shown for this error, for callers that present it themselves
        /// </summary>
        string GetUserMessage(Exception exception, ErrorContext context);

        /// <summary>
        ///     Runs synchronous work and handles any exception it throws, as HandleError does
        /// </summary>
        void Run(ErrorContext context, Action action);
    }

    public class ErrorHandlingService : IErrorHandlingService
    {
        private readonly IDialogService _dialogService;
        private readonly bool _isDebugMode;
        private readonly ILogger<ErrorHandlingService> _logger;

        public ErrorHandlingService(ILogger<ErrorHandlingService> logger, IDialogService dialogService)
        {
            _logger = logger;
            _dialogService = dialogService;
#if DEBUG
            _isDebugMode = true;
#else
            _isDebugMode = false;
#endif
        }

        public async Task HandleErrorAsync(Exception exception, ErrorContext context, bool showUserMessage = true)
        {
            LogError(exception, context);

            if (showUserMessage && ShouldShowUserMessage(exception, context))
            {
                var message = GetUserMessage(exception, context);
                var title = GetErrorTitle(exception, context);

                await _dialogService.ShowMessageAsync(title, message);
            }
        }

        public async Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue)
        {
            await HandleErrorAsync(exception, context, false);
            return defaultValue;
        }

        private static bool ShouldShowUserMessage(Exception exception, ErrorContext context)
        {
            if (exception is OperationCanceledException)
            {
                return false;
            }

            return context.Category switch
            {
                ErrorCategory.User => true,
                ErrorCategory.Authentication => true,
                ErrorCategory.Network => context.Severity >= ErrorSeverity.Error,
                ErrorCategory.Media => context.Severity >= ErrorSeverity.Error,
                ErrorCategory.Configuration => true,
                _ => false
            };
        }

        public string GetUserMessage(Exception exception, ErrorContext context)
        {
            switch (exception)
            {
                case ApiException apiEx:
                    return GetApiErrorMessage(apiEx);

                case HttpRequestException httpEx:
                    return GetNetworkErrorMessage(httpEx);

                case OperationCanceledException:
                    return "The operation was cancelled.";

                case UnauthorizedAccessException:
                    return
                        "You don't have permission to perform this action. Please check your credentials and try again.";

                case ArgumentNullException:
                    return "Required information is missing. Please provide all required data and try again.";

                case ArgumentException:
                    return "Invalid input provided. Please check your data and try again.";

                case ResumeStuckException:
                    return exception.Message; // Written for the user

                case InvalidOperationException invEx when invEx.Message.Contains("Quick Connect"):
                    return invEx.Message; // Already user-friendly

                case NotSupportedException:
                    return "This operation is not supported on your device.";

                case TimeoutException:
                    return "The operation timed out. Please check your connection and try again.";

                default:
                    return GetGenericErrorMessage(context);
            }
        }

        public void Run(ErrorContext context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HandleError(ex, context);
            }
        }

        public void HandleError(Exception exception, ErrorContext context, bool showUserMessage = false)
        {
            LogError(exception, context);

            // A synchronous caller cannot wait for the dialog
            if (showUserMessage && ShouldShowUserMessage(exception, context))
            {
                var message = GetUserMessage(exception, context);
                var title = GetErrorTitle(exception, context);

                AsyncHelper.FireAndForget(() => _dialogService.ShowMessageAsync(title, message), _logger, GetType());
            }
        }

        private void LogError(Exception exception, ErrorContext context)
        {
            var logLevel = context.Severity == ErrorSeverity.Error ? LogLevel.Error : LogLevel.Warning;

            // A cancellation is not an error
            if (exception is OperationCanceledException)
            {
                logLevel = LogLevel.Debug;
            }

            // A restarting server is a warning, not an error
            if (exception is ApiException apiEx && RetryHelper.IsServerUnavailable(apiEx))
            {
                _logger.LogWarning(
                    "Server temporarily unavailable (HTTP {StatusCode}) in {Source}.{Operation} - The server may be restarting",
                    apiEx.ResponseStatusCode, context.Source, context.Operation);
                return;
            }

            _logger.Log(logLevel, exception,
                "Error in {Source}.{Operation} - Category: {Category}, Severity: {Severity}",
                context.Source, context.Operation, context.Category, context.Severity);
        }

        private static string GetErrorTitle(Exception exception, ErrorContext context)
        {
            if (exception is ApiException apiEx && RetryHelper.IsServerUnavailable(apiEx))
            {
                return "Server Temporarily Unavailable";
            }

            return context.Category switch
            {
                // UI actions (toggles, page events, filters): their failures are rarely bad input
                ErrorCategory.User => "Something Went Wrong",
                ErrorCategory.Network => "Connection Error",
                ErrorCategory.Authentication => "Authentication Error",
                ErrorCategory.Media => "Playback Error",
                ErrorCategory.Configuration => "Configuration Error",
                _ => "Error"
            };
        }

        private static string GetApiErrorMessage(ApiException apiEx)
        {
            return apiEx.ResponseStatusCode switch
            {
                400 => "The server rejected the request. Please check your input and try again.",
                401 => "Authentication failed. Please check your credentials and try again.",
                403 => "Access denied. You don't have permission to perform this action.",
                404 => "The requested resource was not found on the server.",
                429 => "Too many requests. Please wait a moment and try again.",
                500 => "Server error. The server encountered an error. Please try again later.",
                502 =>
                    "The Jellyfin server appears to be restarting or temporarily unavailable. Please wait a moment and try again.",
                503 =>
                    "The Jellyfin server is temporarily unavailable (possibly updating). Please wait a moment and try again.",
                504 => "The server is taking too long to respond. It may be under heavy load or restarting.",
                var status => $"Server returned error {status}. Please try again."
            };
        }

        private static string GetNetworkErrorMessage(HttpRequestException httpEx)
        {
            if (httpEx.Message.Contains("host") || httpEx.Message.Contains("DNS"))
            {
                return "Could not connect to server. Please check the server address and your network connection.";
            }

            if (httpEx.Message.Contains("SSL") || httpEx.Message.Contains("certificate"))
            {
                return "Secure connection failed. There may be an issue with the server's security certificate.";
            }

            return "Network error. Please check your internet connection and try again.";
        }

        private string GetGenericErrorMessage(ErrorContext context)
        {
            var message = context.Category switch
            {
                ErrorCategory.Media => "Unable to play media. Please try again or select different quality settings.",
                ErrorCategory.User => "That didn't work. Please try again.",
                ErrorCategory.System => "An unexpected error occurred. Please try again.",
                _ => "An error occurred while performing the operation."
            };

            if (_isDebugMode)
            {
                message += $" (Error in {context.Source}.{context.Operation})";
            }

            return message;
        }
    }
}
