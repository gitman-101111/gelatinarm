using System;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Base
{
    public abstract class BaseControl : UserControl
    {
        private bool _servicesInitialized;

        protected BaseControl()
        {
            Loaded += OnControlLoaded;
        }

        protected ILogger Logger { get; private set; }

        protected IErrorHandlingService ErrorHandler { get; private set; }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_servicesInitialized && !DesignMode.DesignModeEnabled)
            {
                InitializeServices();
                _servicesInitialized = true;
            }
        }

        private void InitializeServices()
        {
            Logger = ServiceLocator.GetService(typeof(ILogger<>).MakeGenericType(GetType())) as ILogger
                     ?? throw new InvalidOperationException($"No logger for {GetType().Name}");
            ErrorHandler = GetRequiredService<IErrorHandlingService>();
            OnServicesInitialized();
        }

        protected virtual void OnServicesInitialized()
        {
        }

        protected static T GetRequiredService<T>() where T : class
        {
            return ServiceLocator.GetRequiredService<T>();
        }

        protected ErrorContext CreateErrorContext(string operation)
        {
            return new ErrorContext(GetType().Name, operation, ErrorCategory.User);
        }
    }
}
