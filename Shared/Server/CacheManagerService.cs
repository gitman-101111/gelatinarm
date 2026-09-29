using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Windows.System;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Server
{
    public interface ICacheManagerService
    {
        void Set<T>(string key, T value, TimeSpan? expiration) where T : class;

        T Get<T>(string key) where T : class;

        void Clear();
    }

    public class CacheManagerService : BaseService, ICacheManagerService
    {
        private const double HighMemoryPressureThreshold = 0.85;
        private const double CriticalMemoryPressureThreshold = 0.95;
        private const double CacheShareOfAppMemory = 0.1;
        private readonly Dictionary<string, CacheEntry> _cache = new();
        private readonly object _cacheLock = new();
        private readonly LinkedList<string> _lruList = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
        private long _currentEstimatedSize;

        // 100 MB until the platform states its limit: CacheShareOfAppMemory of an Xbox app's 1 GB
        private long _maxSizeInBytes = 100 * 1024 * 1024;

        public CacheManagerService(ILogger<CacheManagerService> logger) : base(logger)
        {
            MemoryManager.AppMemoryUsageIncreased += OnMemoryUsageIncreased;
            MemoryManager.AppMemoryUsageLimitChanging += OnMemoryUsageLimitChanging;
        }

        public void Set<T>(string key, T value, TimeSpan? expiration) where T : class
        {
            if (string.IsNullOrEmpty(key) || value == null)
            {
                return;
            }

            lock (_cacheLock)
            {
                var estimatedSize = EstimateObjectSize(value);
                var expirationTime = DateTime.UtcNow.Add(expiration ?? TimeSpan.FromMinutes(5));

                Remove(key);

                while (_currentEstimatedSize + estimatedSize > _maxSizeInBytes && _cache.Count > 0)
                {
                    EvictLeastRecentlyUsed();
                }

                _cache[key] = new CacheEntry
                {
                    Value = value,
                    Expiration = expirationTime,
                    EstimatedSize = estimatedSize
                };
                _currentEstimatedSize += estimatedSize;
                _lruNodes[key] = _lruList.AddFirst(key);

                Logger.LogDebug(
                    "Cache set: {Key}, Size: {EstimatedSize} bytes, Total: {CurrentEstimatedSize} bytes", key, estimatedSize, _currentEstimatedSize);
            }
        }

        public T Get<T>(string key) where T : class
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.Expiration < DateTime.UtcNow)
                    {
                        Remove(key);
                        return null;
                    }

                    var node = _lruNodes[key];
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    return entry.Value as T;
                }

                return null;
            }
        }

        private void Remove(string key)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _cache.Remove(key);
                    _currentEstimatedSize -= entry.EstimatedSize;
                    _lruList.Remove(_lruNodes[key]);
                    _lruNodes.Remove(key);
                }
            }
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _lruList.Clear();
                _lruNodes.Clear();
                _currentEstimatedSize = 0;
                Logger.LogDebug("Cache cleared");
            }
        }

        private void TriggerEviction()
        {
            lock (_cacheLock)
            {
                var expiredKeys = _cache
                    .Where(kvp => kvp.Value.Expiration < DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    Remove(key);
                }

                while (_currentEstimatedSize > _maxSizeInBytes * 0.8 && _cache.Count > 0)
                {
                    EvictLeastRecentlyUsed();
                }
            }
        }

        private void SetMemoryLimit(long maxSizeInBytes)
        {
            _maxSizeInBytes = maxSizeInBytes;
            Logger.LogInformation("Cache memory limit set to {MaxSizeInBytes}MB", maxSizeInBytes / (1024 * 1024));

            if (_currentEstimatedSize > _maxSizeInBytes)
            {
                TriggerEviction();
            }
        }

        private void EvictLeastRecentlyUsed()
        {
            if (_lruList.Last != null)
            {
                var key = _lruList.Last.Value;
                Remove(key);
                Logger.LogDebug("Evicted cache entry: {Key}", key);
            }
        }

        private static long EstimateObjectSize(object obj)
        {
            if (obj is string str)
            {
                return str.Length * 2;
            }

            if (obj is byte[] bytes)
            {
                return bytes.Length;
            }

            if (obj is ICollection collection)
            {
                return collection.Count * 64;
            }

            try
            {
                return JsonSerializer.Serialize(obj).Length * 2;
            }
            catch
            {
                // A value the serializer cannot walk (a cycle, a platform type) is charged a token size
                return 1024;
            }
        }

        private void OnMemoryUsageIncreased(object sender, object e)
        {
            try
            {
                var usage = MemoryManager.AppMemoryUsage;
                var limit = MemoryManager.AppMemoryUsageLimit;
                var usageRatio = (double)usage / limit;

                if (usageRatio > CriticalMemoryPressureThreshold)
                {
                    Logger.LogWarning("Critical memory pressure detected ({UsageRatio:P0}). Clearing cache.", usageRatio);
                    Clear();
                }
                else if (usageRatio > HighMemoryPressureThreshold)
                {
                    Logger.LogInformation(
                        "High memory pressure detected ({UsageRatio:P0}). Triggering cache eviction.", usageRatio);
                    TriggerEviction();
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnMemoryUsageIncreased"));
            }
        }

        private void OnMemoryUsageLimitChanging(object sender, AppMemoryUsageLimitChangingEventArgs e)
        {
            try
            {
                var newLimit = e.NewLimit;
                var oldLimit = e.OldLimit;

                Logger.LogInformation(
                    "Memory limit changing from {OldLimit}MB to {NewLimit}MB", oldLimit / (1024 * 1024), newLimit / (1024 * 1024));

                var newCacheLimit = (long)(newLimit * CacheShareOfAppMemory);
                SetMemoryLimit(newCacheLimit);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnMemoryUsageLimitChanging"));
            }
        }

        protected override void UnsubscribeEvents()
        {
            MemoryManager.AppMemoryUsageIncreased -= OnMemoryUsageIncreased;
            MemoryManager.AppMemoryUsageLimitChanging -= OnMemoryUsageLimitChanging;
        }

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed && disposing)
            {
                Clear();
            }

            base.Dispose(disposing);
        }

        private sealed class CacheEntry
        {
            public object Value { get; set; }
            public DateTime Expiration { get; set; }
            public long EstimatedSize { get; set; }
        }
    }
}
