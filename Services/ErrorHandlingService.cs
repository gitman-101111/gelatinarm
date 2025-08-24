using System;
using System.Net.Http;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Unified error handling service implementation
    /// </summary>
    public class ErrorHandlingService : IErrorHandlingService
    {
        private readonly IDialogService _dialogService;
        private readonly bool _isDebugMode;
        private readonly ILogger<ErrorHandlingService> _logger;

        public ErrorHandlingService(ILogger<ErrorHandlingService> logger, IDialogService dialogService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
#if DEBUG
            _isDebugMode = true;
#else
            _isDebugMode = false;
#endif
        }

        public async Task HandleErrorAsync(Exception exception, ErrorContext context, bool showUserMessage = true,
            string userMessage = null)
        {
            // Log the error with appropriate level
            LogError(exception, context);

            // Show user message if appropriate
            if (showUserMessage && ShouldShowUserMessage(exception, context))
            {
                var message = userMessage ?? GetUserFriendlyMessage(exception, context);
                var title = GetErrorTitle(exception, context);

                await _dialogService.ShowErrorAsync(title, message);
            }
        }

        public async Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue,
            bool showUserMessage = false)
        {
            await HandleErrorAsync(exception, context, showUserMessage);
            return defaultValue;
        }

        public bool ShouldShowUserMessage(Exception exception, ErrorContext context)
        {
            // Don't show messages during shutdown
            if (context.IsShuttingDown)
            {
                return false;
            }

            // Don't show messages for cancelled operations
            if (exception is TaskCanceledException || exception is OperationCanceledException)
            {
                return false;
            }

            // Show messages based on category and severity
            return context.Category switch
            {
                ErrorCategory.User => true,
                ErrorCategory.Authentication => true,
                ErrorCategory.Network => context.Severity >= ErrorSeverity.Error,
                ErrorCategory.Media => context.Severity >= ErrorSeverity.Error,
                ErrorCategory.Validation => true,
                ErrorCategory.Configuration => true,
                _ => context.Severity >= ErrorSeverity.Critical
            };
        }

        public string GetUserFriendlyMessage(Exception exception, ErrorContext context)
        {
            // Special handling for specific exception types
            switch (exception)
            {
                case ApiException apiEx:
                    return GetApiErrorMessage(apiEx, context);

                case HttpRequestException httpEx:
                    return GetNetworkErrorMessage(httpEx, context);

                case TaskCanceledException _:
                case OperationCanceledException _:
                    return "The operation was cancelled.";

                case UnauthorizedAccessException _:
                    return
                        "You don't have permission to perform this action. Please check your credentials and try again.";

                case ArgumentNullException _:
                    return "Required information is missing. Please provide all required data and try again.";

                case ArgumentException _:
                    return "Invalid input provided. Please check your data and try again.";

                case ResumeStuckException _:
                case ResumeTimeoutException _:
                    return exception.Message; // These exceptions have user-friendly messages

                case InvalidOperationException invEx when invEx.Message.Contains("Quick Connect"):
                    return invEx.Message; // Already user-friendly

                case InvalidOperationException invEx when invEx.Message.Contains("resume playback", StringComparison.OrdinalIgnoreCase):
                    return invEx.Message; // Already user-friendly for resume failures

                case NotSupportedException _:
                    return "This operation is not supported on your device.";

                case TimeoutException _:
                    return "The operation timed out. Please check your connection and try again.";

                default:
                    return GetGenericErrorMessage(context);
            }
        }

        public bool ShouldRetry(Exception exception, int attemptNumber)
        {
            // Don't retry if too many attempts
            if (attemptNumber >= 3)
            {
                return false;
            }

            // Retry for transient errors
            return exception switch
            {
                HttpRequestException _ => true,
                TimeoutException _ => true,
                ApiException apiEx => IsTransientHttpError(apiEx.ResponseStatusCode),
                _ => false
            };
        }

        /// <summary>
        ///     Synchronous error handling method for use in synchronous contexts
        /// </summary>
        public void HandleError(Exception exception, ErrorContext context, bool showUserMessage = false)
        {
            // Log the error with appropriate level
            LogError(exception, context);

            // Show user message if appropriate (fire and forget the async dialog)
            if (showUserMessage && ShouldShowUserMessage(exception, context))
            {
                var message = GetUserFriendlyMessage(exception, context);
                var title = GetErrorTitle(exception, context);

                // Fire and forget the async dialog
                _ = _dialogService.ShowErrorAsync(title, message);
            }
        }

        private void LogError(Exception exception, ErrorContext context)
        {
            var logLevel = context.Severity switch
            {
                ErrorSeverity.Critical => LogLevel.Critical,
                ErrorSeverity.Error => LogLevel.Error,
                ErrorSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information
            };

            // Don't log cancellations as errors
            if (exception is TaskCanceledException || exception is OperationCanceledException)
            {
                logLevel = LogLevel.Debug;
            }

            // Log server restart/unavailable as warnings instead of errors
            if (exception is ApiException apiEx)
            {
                var statusCode = apiEx.ResponseStatusCode;
                if (statusCode == 502 || statusCode == 503 || statusCode == 504)
                {
                    logLevel = LogLevel.Warning;
                    _logger.Log(logLevel,
                        "Server temporarily unavailable (HTTP {StatusCode}) in {Source}.{Operation} - The server may be restarting",
                        statusCode, context.Source, context.Operation);
                    return;
                }
            }

            _logger.Log(logLevel, exception,
                "Error in {Source}.{Operation} - Category: {Category}, Severity: {Severity}",
                context.Source, context.Operation, context.Category, context.Severity);
        }

        private string GetErrorTitle(Exception exception, ErrorContext context)
        {
            // Check for server restart scenarios
            if (exception is ApiException apiEx)
            {
                var statusCode = apiEx.ResponseStatusCode;
                if (statusCode == 502 || statusCode == 503 || statusCode == 504)
                {
                    return "Server Temporarily Unavailable";
                }
            }

            return context.Category switch
            {
                ErrorCategory.User => "Invalid Input",
                ErrorCategory.Network => "Connection Error",
                ErrorCategory.Authentication => "Authentication Error",
                ErrorCategory.Media => "Playback Error",
                ErrorCategory.Validation => "Validation Error",
                ErrorCategory.Configuration => "Configuration Error",
                _ => "Error"
            };
        }

        private string GetApiErrorMessage(ApiException apiEx, ErrorContext context)
        {
            var statusCode = apiEx.ResponseStatusCode;

            // Handle specific status codes
            return statusCode switch
            {
                400 => context.Operation switch
                {
                    "QuickConnect" =>
                        "Quick Connect is not enabled on this server. Please use username and password to sign in.",
                    _ => "The server rejected the request. Please check your input and try again."
                },
                401 => "Authentication failed. Please check your credentials and try again.",
                403 => "Access denied. You don't have permission to perform this action.",
                404 => context.Operation switch
                {
                    "QuickConnect" => "Quick Connect endpoint not found. Your server may not support this feature.",
                    _ => "The requested resource was not found on the server."
                },
                429 => "Too many requests. Please wait a moment and try again.",
                500 => "Server error. The server encountered an error. Please try again later.",
                502 => "The Jellyfin server appears to be restarting or temporarily unavailable. Please wait a moment and try again.",
                503 => "The Jellyfin server is temporarily unavailable (possibly updating). Please wait a moment and try again.",
                504 => "The server is taking too long to respond. It may be under heavy load or restarting.",
                _ => $"Server returned error {statusCode}. Please try again."
            };
        }

        private string GetNetworkErrorMessage(HttpRequestException httpEx, ErrorContext context)
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
                ErrorCategory.User => "Invalid input. Please check your data and try again.",
                ErrorCategory.System => "An unexpected error occurred. Please try again.",
                _ => "An error occurred while performing the operation."
            };

            // Add more detail in debug mode
            if (_isDebugMode)
            {
                message += $" (Error in {context.Source}.{context.Operation})";
            }

            return message;
        }

        private bool IsTransientHttpError(int? statusCode)
        {
            if (!statusCode.HasValue)
            {
                return true;
            }

            return statusCode.Value switch
            {
                408 => true, // Request Timeout
                429 => true, // Too Many Requests
                500 => true, // Internal Server Error
                502 => true, // Bad Gateway
                503 => true, // Service Unavailable
                504 => true, // Gateway Timeout
                _ => false
            };
        }
    }
}
