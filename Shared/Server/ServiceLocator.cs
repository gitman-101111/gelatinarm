using System;

namespace Gelatinarm.Shared.Server
{
    public static class ServiceLocator
    {
        public static object GetService(Type serviceType)
        {
            try
            {
                return App.Current?.Services?.GetService(serviceType);
            }
            catch (InvalidOperationException)
            {
                // Service provider not ready yet.
                return null;
            }
        }

        public static T GetService<T>() where T : class
        {
            return GetService(typeof(T)) as T;
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
