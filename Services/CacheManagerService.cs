using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.System;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Implementation of cache manager with LRU eviction and memory pressure awareness
    /// </summary>
    public class CacheManagerService : ICacheManagerService, IDisposable
    {
        // Memory pressure thresholds
        private const double HIGH_MEMORY_PRESSURE_THRESHOLD = 0.85; // Start aggressive eviction at 85% memory usage
        private const double CRITICAL_MEMORY_PRESSURE_THRESHOLD = 0.95; // Clear cache at 95% memory usage
        private readonly Dictionary<string, CacheEntry> _cache = new();
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, ICacheProvider> _cacheProviders = new();
        private readonly ILogger<CacheManagerService> _logger;
        private readonly LinkedList<string> _lruList = new();
        private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();
        private long _currentEstimatedSize;

        private bool _disposed;
        private int _evictionCount;
        private int _hitCount;

        private long _maxSizeInBytes = 100 * 1024 * 1024; // Default 100MB for Xbox 1GB limit
        private int _missCount;

        public CacheManagerService(ILogger<CacheManagerService> logger)
        {
            _logger = logger;

            // Monitor memory pressure
            MemoryManager.AppMemoryUsageIncreased += OnMemoryUsageIncreased;
            MemoryManager.AppMemoryUsageLimitChanging += OnMemoryUsageLimitChanging;
        }

        public void Set<T>(string key, T value, TimeSpan? expiration = null) where T : class
        {
            if (string.IsNullOrEmpty(key) || value == null)
            {
                return;
            }

            lock (_cacheLock)
            {
                var estimatedSize = EstimateObjectSize(value);
                var expirationTime = expiration.HasValue
                    ? DateTime.UtcNow.Add(expiration.Value)
                    : DateTime.UtcNow.AddMinutes(5); // Default 5 minute expiration                if (_cache.ContainsKey(key))
                {
                    Remove(key);
                }

                // Check if we need to evict before adding
                while (_currentEstimatedSize + estimatedSize > _maxSizeInBytes && _cache.Any())
                {
                    EvictLeastRecentlyUsed();
                }
                var entry = new CacheEntry
                {
                    Value = value,
                    Expiration = expirationTime,
                    EstimatedSize = estimatedSize,
                    AccessCount = 0,
                    LastAccessed = DateTime.UtcNow
                };

                _cache[key] = entry;
                _currentEstimatedSize += estimatedSize; var node = _lruList.AddFirst(key);
                _lruNodes[key] = node;

                _logger.LogDebug(
                    $"Cache set: {key}, Size: {estimatedSize} bytes, Total: {_currentEstimatedSize} bytes");
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
                    // Check expiration
                    if (entry.Expiration < DateTime.UtcNow)
                    {
                        Remove(key);
                        _missCount++;
                        return null;
                    }

                    // Update access tracking
                    entry.AccessCount++;
                    entry.LastAccessed = DateTime.UtcNow;

                    // Move to front of LRU list
                    if (_lruNodes.TryGetValue(key, out var node))
                    {
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);
                    }

                    _hitCount++;
                    return entry.Value as T;
                }

                _missCount++;
                return null;
            }
        }

        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _cache.Remove(key);
                    _currentEstimatedSize -= entry.EstimatedSize;

                    // Remove from LRU tracking
                    if (_lruNodes.TryGetValue(key, out var node))
                    {
                        _lruList.Remove(node);
                        _lruNodes.Remove(key);
                    }

                    return true;
                }

                return false;
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
                _logger.LogInformation("Cache cleared");
            }
        }

        public CacheStatistics GetStatistics()
        {
            lock (_cacheLock)
            {
                return new CacheStatistics
                {
                    ItemCount = _cache.Count,
                    EstimatedSizeInBytes = _currentEstimatedSize,
                    HitCount = _hitCount,
                    MissCount = _missCount,
                    EvictionCount = _evictionCount
                };
            }
        }

        public void TriggerEviction()
        {
            lock (_cacheLock)
            {
                // Remove expired items first
                var expiredKeys = _cache
                    .Where(kvp => kvp.Value.Expiration < DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    Remove(key);
                }

                // Evict until we're under 80% of max size
                while (_currentEstimatedSize > _maxSizeInBytes * 0.8 && _cache.Any())
                {
                    EvictLeastRecentlyUsed();
                }
            }
        }

        public void SetMemoryLimit(long maxSizeInBytes)
        {
            _maxSizeInBytes = maxSizeInBytes;
            _logger.LogInformation($"Cache memory limit set to {maxSizeInBytes / (1024 * 1024)}MB");

            // Trigger eviction if we're over the new limit
            if (_currentEstimatedSize > _maxSizeInBytes)
            {
                TriggerEviction();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void EvictLeastRecentlyUsed()
        {
            if (_lruList.Last != null)
            {
                var key = _lruList.Last.Value;
                Remove(key);
                _evictionCount++;
                _logger.LogDebug($"Evicted cache entry: {key}");
            }
        }

        private long EstimateObjectSize(object obj)
        {
            // Simple estimation based on object type
            // This is a rough estimate and can be improved
            if (obj == null)
            {
                return 0;
            }

            if (obj is string str)
            {
                return str.Length * 2; // Unicode characters
            }

            if (obj is byte[] bytes)
            {
                return bytes.Length;
            }

            if (obj is ICollection collection)
            {
                return collection.Count * 64; // Rough estimate per item
            }

            // For complex objects, use a base estimate plus serialization overhead
            try
            {
                var json = JsonSerializer.Serialize(obj);
                return json.Length * 2; // Unicode estimation
            }
            catch
            {
                // Fallback for non-serializable objects
                return 1024; // 1KB default
            }
        }

        private void OnMemoryUsageIncreased(object sender, object e)
        {
            try
            {
                var usage = MemoryManager.AppMemoryUsage;
                var limit = MemoryManager.AppMemoryUsageLimit;
                var usageRatio = (double)usage / limit;

                if (usageRatio > CRITICAL_MEMORY_PRESSURE_THRESHOLD)
                {
                    _logger.LogWarning($"Critical memory pressure detected ({usageRatio:P0}). Clearing cache.");
                    Clear();
                }
                else if (usageRatio > HIGH_MEMORY_PRESSURE_THRESHOLD)
                {
                    _logger.LogInformation(
                        $"High memory pressure detected ({usageRatio:P0}). Triggering cache eviction.");
                    TriggerEviction();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling memory usage increase");
            }
        }

        private void OnMemoryUsageLimitChanging(object sender, AppMemoryUsageLimitChangingEventArgs e)
        {
            try
            {
                var newLimit = e.NewLimit;
                var oldLimit = e.OldLimit;

                _logger.LogInformation(
                    $"Memory limit changing from {oldLimit / (1024 * 1024)}MB to {newLimit / (1024 * 1024)}MB");

                // Adjust cache size based on new limit
                // Reserve 80% of memory for app, 20% max for cache
                var newCacheLimit = (long)(newLimit * 0.2);
                SetMemoryLimit(newCacheLimit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling memory limit change");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Unsubscribe from memory events
                    MemoryManager.AppMemoryUsageIncreased -= OnMemoryUsageIncreased;
                    MemoryManager.AppMemoryUsageLimitChanging -= OnMemoryUsageLimitChanging;

                    // Clear the cache
                    lock (_cacheLock)
                    {
                        _cache.Clear();
                        _lruList.Clear();
                        _lruNodes.Clear();
                        _currentEstimatedSize = 0;
                    }

                    _logger?.LogInformation("CacheManagerService disposed");
                }

                _disposed = true;
            }
        }

        private class CacheEntry
        {
            public object Value { get; set; }
            public DateTime Expiration { get; set; }
            public long EstimatedSize { get; set; }
            public int AccessCount { get; set; }
            public DateTime LastAccessed { get; set; }
        }

        #region Cache Provider Support

        /// <summary>
        ///     Register a cache provider for specific functionality
        /// </summary>
        public void RegisterCacheProvider(string name, ICacheProvider provider)
        {
            if (string.IsNullOrEmpty(name) || provider == null)
            {
                return;
            }

            lock (_cacheLock)
            {
                _cacheProviders[name] = provider;
                _logger.LogInformation($"Registered cache provider: {name}");
            }
        }

        /// <summary>
        ///     Get a registered cache provider
        /// </summary>
        public ICacheProvider GetCacheProvider(string name)
        {
            lock (_cacheLock)
            {
                return _cacheProviders.TryGetValue(name, out var provider) ? provider : null;
            }
        }

        /// <summary>
        ///     Remove a cache provider
        /// </summary>
        public bool RemoveCacheProvider(string name)
        {
            lock (_cacheLock)
            {
                return _cacheProviders.Remove(name);
            }
        }

        /// <summary>
        ///     Set data using a specific cache provider
        /// </summary>
        public async Task SetWithProviderAsync(string providerName, string key, byte[] data,
            TimeSpan? expiration = null)
        {
            var provider = GetCacheProvider(providerName);
            if (provider != null)
            {
                await provider.SetAsync(key, data, expiration);
            }
            else
            {
                _logger.LogWarning($"Cache provider not found: {providerName}");
            }
        }

        /// <summary>
        ///     Get data using a specific cache provider
        /// </summary>
        public async Task<byte[]> GetWithProviderAsync(string providerName, string key)
        {
            var provider = GetCacheProvider(providerName);
            if (provider != null)
            {
                return await provider.GetAsync(key);
            }

            _logger.LogWarning($"Cache provider not found: {providerName}");
            return null;
        }

        #endregion
    }
}
