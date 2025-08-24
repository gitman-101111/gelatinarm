using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     File-based cache provider implementation for storing cache data on disk
    /// </summary>
    public class FileCacheProvider : ICacheProvider
    {
        private readonly string _cacheSubfolder;
        private readonly ILogger<FileCacheProvider> _logger;
        private readonly Dictionary<string, CacheMetadata> _metadata = new();
        private readonly object _metadataLock = new();
        private StorageFolder _cacheFolder;

        public FileCacheProvider(ILogger<FileCacheProvider> logger, string cacheSubfolder = "FileCache")
        {
            _logger = logger;
            _cacheSubfolder = cacheSubfolder;
        }

        public async Task SetAsync(string key, byte[] data, TimeSpan? expiration = null)
        {
            if (string.IsNullOrEmpty(key) || data == null || _cacheFolder == null)
            {
                return;
            }

            try
            {
                var fileName = GetSafeFileName(key);
                var file = await _cacheFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, data);

                lock (_metadataLock)
                {
                    _metadata[key] = new CacheMetadata
                    {
                        Expiration = expiration.HasValue
                            ? DateTime.UtcNow.Add(expiration.Value)
                            : DateTime.UtcNow.AddDays(7), // Default 7 days for file cache
                        Size = data.Length,
                        LastAccessed = DateTime.UtcNow
                    };
                }

                await SaveMetadataAsync();
                _logger.LogDebug($"File cache set: {key}, Size: {data.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set cache item: {key}");
            }
        }

        public async Task<byte[]> GetAsync(string key)
        {
            if (string.IsNullOrEmpty(key) || _cacheFolder == null)
            {
                return null;
            }

            try
            {
                // Check metadata first
                lock (_metadataLock)
                {
                    if (_metadata.TryGetValue(key, out var metadata))
                    {
                        if (metadata.Expiration < DateTime.UtcNow)
                        {
                            // Expired - remove it
                            _ = RemoveAsync(key);
                            return null;
                        }

                        metadata.LastAccessed = DateTime.UtcNow;
                    }
                    else
                    {
                        return null;
                    }
                }

                var fileName = GetSafeFileName(key);
                var file = await _cacheFolder.TryGetItemAsync(fileName) as StorageFile;

                if (file != null)
                {
                    var buffer = await FileIO.ReadBufferAsync(file);
                    var bytes = new byte[buffer.Length];
                    using (var reader = DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }

                    return bytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get cache item: {key}");
            }

            return null;
        }

        public async Task<bool> RemoveAsync(string key)
        {
            if (string.IsNullOrEmpty(key) || _cacheFolder == null)
            {
                return false;
            }

            try
            {
                var fileName = GetSafeFileName(key);
                var file = await _cacheFolder.TryGetItemAsync(fileName) as StorageFile;

                if (file != null)
                {
                    await file.DeleteAsync();

                    lock (_metadataLock)
                    {
                        _metadata.Remove(key);
                    }

                    await SaveMetadataAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove cache item: {key}");
            }

            return false;
        }

        public async Task ClearAsync()
        {
            if (_cacheFolder == null)
            {
                return;
            }

            try
            {
                IReadOnlyList<StorageFile> files;
                try
                {
                    files = await _cacheFolder.GetFilesAsync();
                }
                catch (FileNotFoundException)
                {
                    // Folder doesn't exist, nothing to clear
                    _logger?.LogDebug("Cache folder does not exist, nothing to clear");
                    return;
                }
                foreach (var file in files)
                {
                    if (!file.Name.Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
                    {
                        await file.DeleteAsync();
                    }
                }

                lock (_metadataLock)
                {
                    _metadata.Clear();
                }

                await SaveMetadataAsync();
                _logger.LogInformation("File cache cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear file cache");
            }
        }

        public async Task<bool> ExistsAsync(string key)
        {
            if (string.IsNullOrEmpty(key) || _cacheFolder == null)
            {
                return false;
            }

            lock (_metadataLock)
            {
                if (_metadata.TryGetValue(key, out var metadata))
                {
                    if (metadata.Expiration < DateTime.UtcNow)
                    {
                        // Expired
                        return false;
                    }

                    return true;
                }
            }

            return false;
        }

        public async Task<long> GetSizeAsync()
        {
            if (_cacheFolder == null)
            {
                return 0;
            }

            try
            {
                // Clean up expired items first
                await CleanupExpiredAsync();

                lock (_metadataLock)
                {
                    return _metadata.Values.Sum(m => m.Size);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get cache size");
                return 0;
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                _cacheFolder =
                    await localFolder.CreateFolderAsync(_cacheSubfolder, CreationCollisionOption.OpenIfExists);
                await LoadMetadataAsync();
                _logger.LogInformation($"File cache provider initialized in folder: {_cacheSubfolder}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize file cache provider");
            }
        }

        private async Task CleanupExpiredAsync()
        {
            var expiredKeys = new List<string>();

            lock (_metadataLock)
            {
                expiredKeys = _metadata
                    .Where(kvp => kvp.Value.Expiration < DateTime.UtcNow)
                    .Select(kvp => kvp.Key)
                    .ToList();
            }

            foreach (var key in expiredKeys)
            {
                await RemoveAsync(key);
            }
        }

        private string GetSafeFileName(string key)
        {
            // Convert key to a safe filename
            var hash = key.GetHashCode();
            var safeKey = key.Length > 50 ? key.Substring(0, 50) : key;
            safeKey = string.Join("_", safeKey.Split(Path.GetInvalidFileNameChars()));
            return $"{safeKey}_{hash}.cache";
        }

        private async Task LoadMetadataAsync()
        {
            if (_cacheFolder == null)
            {
                return;
            }

            try
            {
                var metadataFile = await _cacheFolder.TryGetItemAsync("metadata.json") as StorageFile;
                if (metadataFile != null)
                {
                    var json = await FileIO.ReadTextAsync(metadataFile);
                    var metadata = JsonSerializer.Deserialize<Dictionary<string, CacheMetadata>>(json);

                    lock (_metadataLock)
                    {
                        _metadata.Clear();
                        foreach (var kvp in metadata)
                        {
                            _metadata[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache metadata");
            }
        }

        private async Task SaveMetadataAsync()
        {
            if (_cacheFolder == null)
            {
                return;
            }

            try
            {
                Dictionary<string, CacheMetadata> metadataCopy;
                lock (_metadataLock)
                {
                    metadataCopy = new Dictionary<string, CacheMetadata>(_metadata);
                }

                var json = JsonSerializer.Serialize(metadataCopy);
                var metadataFile =
                    await _cacheFolder.CreateFileAsync("metadata.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(metadataFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save cache metadata");
            }
        }

        private class CacheMetadata
        {
            public DateTime Expiration { get; set; }
            public long Size { get; set; }
            public DateTime LastAccessed { get; set; }
        }
    }
}
