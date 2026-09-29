using System;
using System.Collections.Generic;
using System.Linq;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public interface IPlaybackQueueService
    {
        List<BaseItemDto> Queue { get; }
        int CurrentQueueIndex { get; }
        bool IsShuffleMode { get; }

        event EventHandler<List<BaseItemDto>> QueueChanged;
        event EventHandler<int> QueueIndexChanged;

        void SetQueue(List<BaseItemDto> items, int startIndex);
        void AddToQueue(BaseItemDto item);
        void AddToQueueNext(BaseItemDto item);
        void ClearQueue();
        void SetCurrentIndex(int index);

        void SetShuffle(bool enabled);
        int GetNextIndex(bool isRepeatAll);
        bool HasNext(bool isRepeatAll);
        int GetPreviousIndex(bool isRepeatAll);
    }

    /// <summary>
    ///     The music player's queue: order, position, shuffle and the indices repeat walks
    /// </summary>
    public class MusicQueueService : BaseService, IPlaybackQueueService
    {
        private int _lastQueueHash;

        public MusicQueueService(ILogger<MusicQueueService> logger) : base(logger)
        {
            Queue = new List<BaseItemDto>();
            CurrentQueueIndex = -1;
        }

        public List<BaseItemDto> Queue { get; }

        public int CurrentQueueIndex { get; private set; }

        public bool IsShuffleMode { get; private set; }

        private List<int> ShuffledIndices { get; set; }

        private int CurrentShuffleIndex { get; set; }

        public event EventHandler<List<BaseItemDto>> QueueChanged;
        public event EventHandler<int> QueueIndexChanged;

        private void RaiseQueueChanged()
        {
            QueueChanged?.Invoke(this, Queue);
        }

        private void RaiseQueueIndexChanged()
        {
            QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
        }

        public void SetQueue(List<BaseItemDto> items, int startIndex)
        {
            var context = CreateErrorContext("SetQueue", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (items is not { Count: > 0 })
                {
                    Logger.LogWarning("SetQueue called with null or empty items");
                    return;
                }

                Queue.Clear();
                Queue.AddRange(items);
                CurrentQueueIndex = Math.Max(0, Math.Min(startIndex, items.Count - 1));

                Logger.LogDebug("Queue set with {ItemsCount} items, starting at index {CurrentQueueIndex}", items.Count, CurrentQueueIndex);

                if (IsShuffleMode && Queue.Count > 1)
                {
                    CreateShuffledIndices();
                }

                RaiseQueueChanged();
                RaiseQueueIndexChanged();
            });
        }

        public void AddToQueue(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueue", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (item != null)
                {
                    Queue.Add(item);
                    ShuffledIndices?.Add(Queue.Count - 1);
                    KeepShuffleInStep();

                    if (CurrentQueueIndex == -1)
                    {
                        CurrentQueueIndex = 0;
                        RaiseQueueIndexChanged();
                    }

                    RaiseQueueChanged();
                }
            });
        }

        public void AddToQueueNext(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueueNext", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (item != null && CurrentQueueIndex >= 0)
                {
                    var position = CurrentQueueIndex + 1;
                    Queue.Insert(position, item);

                    if (ShuffledIndices != null)
                    {
                        // Everything after the insert moved up one; in shuffle too, it plays next
                        for (var i = 0; i < ShuffledIndices.Count; i++)
                        {
                            if (ShuffledIndices[i] >= position)
                            {
                                ShuffledIndices[i]++;
                            }
                        }

                        ShuffledIndices.Insert(CurrentShuffleIndex + 1, position);
                    }

                    KeepShuffleInStep();
                    RaiseQueueChanged();
                }
            });
        }

        public void ClearQueue()
        {
            var context = CreateErrorContext("ClearQueue", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                Queue.Clear();
                CurrentQueueIndex = -1;
                ShuffledIndices = null;
                _lastQueueHash = 0;
                CurrentShuffleIndex = 0;

                RaiseQueueChanged();
                RaiseQueueIndexChanged();
            });
        }

        public void SetCurrentIndex(int index)
        {
            var context = CreateErrorContext("SetCurrentIndex", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (index >= 0 && index < Queue.Count)
                {
                    CurrentQueueIndex = index;

                    if (IsShuffleMode && ShuffledIndices != null)
                    {
                        CurrentShuffleIndex = Math.Max(0, ShuffledIndices.IndexOf(index));
                    }

                    RaiseQueueIndexChanged();
                }
            });
        }

        public void SetShuffle(bool enabled)
        {
            var context = CreateErrorContext("SetShuffle", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                IsShuffleMode = enabled;
                Logger.LogInformation("Shuffle mode set to: {IsShuffleMode}", IsShuffleMode ? "On" : "Off");

                if (IsShuffleMode && Queue.Count > 1)
                {
                    CreateShuffledIndices();
                }
                else
                {
                    ShuffledIndices = null;
                    CurrentShuffleIndex = 0;
                }
            });
        }

        // The shuffled order was edited with the queue: it stays valid for this queue
        private void KeepShuffleInStep()
        {
            _lastQueueHash = ShuffledIndices != null ? GetQueueHash() : 0;
        }

        private void CreateShuffledIndices()
        {
            var currentQueueHash = GetQueueHash();

            if (ShuffledIndices != null &&
                _lastQueueHash == currentQueueHash &&
                ShuffledIndices.Count == Queue.Count)
            {
                CurrentShuffleIndex = Math.Max(0, ShuffledIndices.IndexOf(CurrentQueueIndex));
                Logger.LogDebug("Reusing cached shuffle indices");
                return;
            }

            Logger.LogDebug("Creating new shuffle indices");
            // The current track plays first; the rest follow in random order
            ShuffledIndices = ShuffleHelper.Shuffled(Enumerable.Range(0, Queue.Count).Where(i => i != CurrentQueueIndex));
            ShuffledIndices.Insert(0, CurrentQueueIndex);
            CurrentShuffleIndex = 0;

            _lastQueueHash = currentQueueHash;
        }

        public int GetNextIndex(bool isRepeatAll)
        {
            if (Queue.Count == 0)
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices is { Count: > 0 })
            {
                var nextShuffleIndex = CurrentShuffleIndex + 1;

                if (nextShuffleIndex >= ShuffledIndices.Count)
                {
                    if (isRepeatAll)
                    {
                        CreateShuffledIndices();
                        return ShuffledIndices.Count > 0 ? ShuffledIndices[0] : -1;
                    }

                    return -1;
                }

                return ShuffledIndices[nextShuffleIndex];
            }

            if (CurrentQueueIndex < Queue.Count - 1)
            {
                return CurrentQueueIndex + 1;
            }

            if (isRepeatAll)
            {
                return 0;
            }

            return -1;
        }

        // Whether Next has a track to go to, in play order; unlike GetNextIndex it changes nothing
        public bool HasNext(bool isRepeatAll)
        {
            if (Queue.Count == 0)
            {
                return false;
            }

            if (isRepeatAll)
            {
                return true;
            }

            return IsShuffleMode && ShuffledIndices is { Count: > 0 }
                ? CurrentShuffleIndex < ShuffledIndices.Count - 1
                : CurrentQueueIndex < Queue.Count - 1;
        }

        public int GetPreviousIndex(bool isRepeatAll)
        {
            if (Queue.Count == 0)
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices is { Count: > 0 })
            {
                var prevShuffleIndex = CurrentShuffleIndex - 1;

                if (prevShuffleIndex < 0)
                {
                    if (isRepeatAll)
                    {
                        return ShuffledIndices[ShuffledIndices.Count - 1];
                    }

                    // The first track of the play order restarts, as the unshuffled branch's 0 does
                    return ShuffledIndices[0];
                }

                return ShuffledIndices[prevShuffleIndex];
            }

            if (CurrentQueueIndex > 0)
            {
                return CurrentQueueIndex - 1;
            }

            if (isRepeatAll)
            {
                return Queue.Count - 1;
            }

            return 0;
        }

        private int GetQueueHash()
        {
            if (Queue.Count == 0)
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < Queue.Count; i++)
                {
                    if (Queue[i]?.Id != null)
                    {
                        hash = (hash * 31) + Queue[i].Id.GetHashCode();
                        hash = (hash * 31) + i;
                    }
                }

                return hash;
            }
        }
    }
}
