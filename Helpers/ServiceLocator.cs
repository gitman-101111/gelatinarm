using System;

namespace Gelatinarm.Helpers
{
    public static class ServiceLocator
    {
        public static object GetService(Type serviceType)
        {
            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            try
            {
                return global::Gelatinarm.App.Current?.Services?.GetService(serviceType);
            }
            catch (InvalidOperationException)
            {
                // Service provider not ready yet.
                return null;
            }
        }

        public static T GetService<T>() where T : class
        {
            try
            {
                return global::Gelatinarm.App.Current?.Services?.GetService(typeof(T)) as T;
            }
            catch (InvalidOperationException)
            {
                // Service provider not ready yet.
                return null;
            }
        }

        public static object GetRequiredService(Type serviceType)
        {
            var service = GetService(serviceType);
            if (service == null)
            {
                throw new InvalidOperationException($"Service {serviceType?.Name ?? "Unknown"} not found");
            }

            return service;
        }

        public static T GetRequiredService<T>() where T : class
        {
            var service = GetService<T>();
            if (service == null)
            {
                throw new InvalidOperationException($"Service {typeof(T).Name} not found");
            }

            return service;
        }
    }
}
