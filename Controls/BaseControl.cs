using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.Controls
{
    /// <summary>
    ///     Base class for all custom controls providing standardized error handling and service access
    /// </summary>
    public abstract class BaseControl : UserControl
    {
        private bool _servicesInitialized;

        protected BaseControl()
        {
            Loaded += OnControlLoaded;
        }

        /// <summary>
        ///     Logger instance for derived classes
        /// </summary>
        protected ILogger Logger { get; private set; }

        /// <summary>
        ///     Error handling service for standardized error processing
        /// </summary>
        protected IErrorHandlingService ErrorHandler { get; private set; }

        /// <summary>
        ///     Indicates whether the control is in design mode
        /// </summary>
        protected bool IsInDesignMode => DesignMode.DesignModeEnabled;

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_servicesInitialized && !IsInDesignMode)
            {
                InitializeServices();
                _servicesInitialized = true;
            }
        }

        /// <summary>
        ///     Initialize services from the DI container
        /// </summary>
        protected virtual void InitializeServices()
        {
            try
            {
                var app = Application.Current as App;
                if (app?.Services != null)
                {
                    var loggerType = typeof(ILogger<>).MakeGenericType(GetType());
                    Logger = app.Services.GetService(loggerType) as ILogger; ErrorHandler = app.Services.GetService<IErrorHandlingService>();

                    // Allow derived classes to get additional services
                    OnServicesInitialized(app.Services);
                }
            }
#if DEBUG
            catch (Exception ex)
            {
                // Can't use ErrorHandler here since it might not be initialized
                Debug.WriteLine($"{GetType().Name}: Failed to initialize services: {ex.Message}");
            }
#else
            catch
            {
                // Can't use ErrorHandler here since it might not be initialized
            }
#endif
        }

        /// <summary>
        ///     Override to get additional services from the DI container
        /// </summary>
        protected virtual void OnServicesInitialized(IServiceProvider services)
        {
            // Derived classes can override to get their specific services
        }


        /// <summary>
        ///     Handle an error using the error handling service
        /// </summary>
        protected void HandleError(Exception ex, string operation, bool showUserMessage = false)
        {
            if (ErrorHandler != null)
            {
                var context = new ErrorContext(GetType().Name, operation, ErrorCategory.User);
                _ = ErrorHandler.HandleErrorAsync(ex, context, showUserMessage);
            }
            else
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.{operation}");
            }
        }

        /// <summary>
        ///     Handle an error using the error handling service (async)
        /// </summary>
        protected async Task HandleErrorAsync(Exception ex, string operation, bool showUserMessage = false)
        {
            if (ErrorHandler != null)
            {
                var context = new ErrorContext(GetType().Name, operation, ErrorCategory.User);
                await ErrorHandler.HandleErrorAsync(ex, context, showUserMessage);
            }
            else
            {
                Logger?.LogError(ex, $"Error in {GetType().Name}.{operation}");
            }
        }

        /// <summary>
        ///     Create an error context for this control
        /// </summary>
        protected ErrorContext CreateErrorContext(
            string operation,
            ErrorCategory category = ErrorCategory.User,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }
    }
}
