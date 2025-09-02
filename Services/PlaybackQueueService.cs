using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    public interface IPlaybackQueueService
    {
        List<BaseItemDto> Queue { get; }
        int CurrentQueueIndex { get; }
        bool IsShuffleMode { get; }
        List<int> ShuffledIndices { get; }
        int CurrentShuffleIndex { get; }

        event EventHandler<List<BaseItemDto>> QueueChanged;
        event EventHandler<int> QueueIndexChanged;

        void SetQueue(List<BaseItemDto> items, int startIndex = 0);
        void AddToQueue(BaseItemDto item);
        void AddToQueueNext(BaseItemDto item);
        void ClearQueue();
        void SetCurrentIndex(int index);

        void SetShuffle(bool enabled);
        void CreateShuffledIndices();
        int GetNextIndex(bool isRepeatAll);
        int GetPreviousIndex(bool isRepeatAll);
    }

    public class PlaybackQueueService : BaseService, IPlaybackQueueService
    {
        private int _lastQueueHash = 0;

        public PlaybackQueueService(ILogger<PlaybackQueueService> logger) : base(logger)
        {
            Queue = new List<BaseItemDto>();
            CurrentQueueIndex = -1;
            IsShuffleMode = false;
        }

        public List<BaseItemDto> Queue { get; }

        public int CurrentQueueIndex { get; private set; }

        public bool IsShuffleMode { get; private set; }

        public List<int> ShuffledIndices { get; private set; }

        public int CurrentShuffleIndex { get; private set; }

        public event EventHandler<List<BaseItemDto>> QueueChanged;
        public event EventHandler<int> QueueIndexChanged;

        public void SetQueue(List<BaseItemDto> items, int startIndex = 0)
        {
            var context = CreateErrorContext("SetQueue", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (items == null || !items.Any())
                    {
                        Logger.LogWarning("SetQueue called with null or empty items");
                        return;
                    }

                    Queue.Clear();
                    Queue.AddRange(items);
                    CurrentQueueIndex = Math.Max(0, Math.Min(startIndex, items.Count - 1));

                    Logger.LogInformation($"Queue set with {items.Count} items, starting at index {CurrentQueueIndex}");

                    // If shuffle mode is on, create shuffled indices
                    if (IsShuffleMode && Queue.Count > 1)
                    {
                        CreateShuffledIndices();
                    }

                    QueueChanged?.Invoke(this, Queue);
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void AddToQueue(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueue", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (item != null)
                    {
                        Queue.Add(item);

                        // If this is the first item, set current index
                        if (CurrentQueueIndex == -1)
                        {
                            CurrentQueueIndex = 0;
                            QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                        }

                        // Invalidate shuffle cache when queue changes
                        _lastQueueHash = 0;

                        QueueChanged?.Invoke(this, Queue);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void AddToQueueNext(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueueNext", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (item != null && CurrentQueueIndex >= 0)
                    {
                        Queue.Insert(CurrentQueueIndex + 1, item);

                        // Invalidate shuffle cache when queue changes
                        _lastQueueHash = 0;

                        QueueChanged?.Invoke(this, Queue);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void ClearQueue()
        {
            var context = CreateErrorContext("ClearQueue", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    Queue.Clear();
                    CurrentQueueIndex = -1;
                    ShuffledIndices = null;
                    _lastQueueHash = 0;
                    CurrentShuffleIndex = 0;

                    QueueChanged?.Invoke(this, Queue);
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetCurrentIndex(int index)
        {
            var context = CreateErrorContext("SetCurrentIndex", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (index >= 0 && index < Queue.Count)
                    {
                        CurrentQueueIndex = index;

                        // Update shuffle index if in shuffle mode
                        if (IsShuffleMode && ShuffledIndices != null)
                        {
                            CurrentShuffleIndex = ShuffledIndices.IndexOf(index);
                            if (CurrentShuffleIndex == -1)
                            {
                                CurrentShuffleIndex = 0;
                            }
                        }

                        QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetShuffle(bool enabled)
        {
            var context = CreateErrorContext("SetShuffle", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    IsShuffleMode = enabled;
                    Logger.LogInformation($"Shuffle mode set to: {(IsShuffleMode ? "On" : "Off")}");

                    if (IsShuffleMode && Queue.Count > 1)
                    {
                        CreateShuffledIndices();
                    }
                    else
                    {
                        ShuffledIndices = null;
                        CurrentShuffleIndex = 0;
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void CreateShuffledIndices()
        {
            // Calculate hash of current queue to detect changes
            var currentQueueHash = GetQueueHash();

            // If queue hasn't changed and we already have shuffled indices, reuse them
            if (ShuffledIndices != null &&
                _lastQueueHash == currentQueueHash &&
                ShuffledIndices.Count == Queue.Count)
            {
                CurrentShuffleIndex = ShuffledIndices.IndexOf(CurrentQueueIndex);
                if (CurrentShuffleIndex == -1)
                {
                    CurrentShuffleIndex = 0;
                }

                Logger.LogInformation("Reusing cached shuffle indices");
                return;
            }

            Logger.LogInformation("Creating new shuffle indices");
            ShuffledIndices = new List<int>();
            for (var i = 0; i < Queue.Count; i++)
            {
                if (i != CurrentQueueIndex) // Don't include current track
                {
                    ShuffledIndices.Add(i);
                }
            }

            // Shuffle the indices
            var random = new Random();
            for (var i = ShuffledIndices.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                var temp = ShuffledIndices[i];
                ShuffledIndices[i] = ShuffledIndices[j];
                ShuffledIndices[j] = temp;
            }

            // Insert current track at the beginning
            ShuffledIndices.Insert(0, CurrentQueueIndex);
            CurrentShuffleIndex = 0;

            // Update the queue hash
            _lastQueueHash = currentQueueHash;
        }

        public int GetNextIndex(bool isRepeatAll)
        {
            if (!Queue.Any())
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices?.Any() == true)
            {
                // Shuffle mode
                var nextShuffleIndex = CurrentShuffleIndex + 1;

                if (nextShuffleIndex >= ShuffledIndices.Count)
                {
                    if (isRepeatAll)
                    {
                        // Re-shuffle and start over
                        CreateShuffledIndices();
                        return ShuffledIndices.Count > 0 ? ShuffledIndices[0] : -1;
                    }

                    // No more tracks
                    return -1;
                }

                return ShuffledIndices[nextShuffleIndex];
            }

            // Normal mode
            if (CurrentQueueIndex < Queue.Count - 1)
            {
                return CurrentQueueIndex + 1;
            }

            if (isRepeatAll && Queue.Any())
            {
                // Loop back to start
                return 0;
            }

            return -1;
        }

        public int GetPreviousIndex(bool isRepeatAll)
        {
            if (!Queue.Any())
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices?.Any() == true)
            {
                // Shuffle mode
                var prevShuffleIndex = CurrentShuffleIndex - 1;

                if (prevShuffleIndex < 0)
                {
                    if (isRepeatAll)
                    {
                        // Go to last track in shuffle list
                        return ShuffledIndices[ShuffledIndices.Count - 1];
                    }

                    // No previous track
                    return 0;
                }

                return ShuffledIndices[prevShuffleIndex];
            }

            // Normal mode
            if (CurrentQueueIndex > 0)
            {
                return CurrentQueueIndex - 1;
            }

            if (isRepeatAll && Queue.Any())
            {
                // Loop back to end
                return Queue.Count - 1;
            }

            return 0;
        }

        private int GetQueueHash()
        {
            if (Queue == null || !Queue.Any())
            {
                return 0;
            }

            // Simple hash based on queue items and their order
            unchecked
            {
                var hash = 17;
                for (var i = 0; i < Queue.Count; i++)
                {
                    if (Queue[i]?.Id != null)
                    {
                        hash = (hash * 31) + Queue[i].Id.GetHashCode();
                        hash = (hash * 31) + i; // Include position in hash
                    }
                }

                return hash;
            }
        }
    }
}
