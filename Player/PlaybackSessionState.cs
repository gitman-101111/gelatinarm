using System;

namespace Gelatinarm.Player
{
    public sealed class PlaybackSessionState
    {
        public bool IsHlsStream { get; set; }
        public bool HlsManifestOffsetApplied { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public DateTime LastSeekTime { get; set; } = DateTime.MinValue;
        public int PendingSeekCount { get; set; }
        public bool HasPerformedInitialSeek { get; set; }

        public void RecordLargeSeek(TimeSpan targetPosition)
        {
            ExpectedHlsSeekTarget = targetPosition;
            LastSeekTime = DateTime.UtcNow;
            PendingSeekCount++;
        }

        public void DecrementPendingSeek()
        {
            if (PendingSeekCount > 0)
            {
                PendingSeekCount--;
            }
        }

        public void Reset()
        {
            IsHlsStream = false;
            ResetSeekTracking();
        }

        /// <summary>
        ///     Forgets the seeks made on the current stream; the stream kind stays until the next
        ///     stream opens and sets it
        /// </summary>
        public void ResetSeekTracking()
        {
            ExpectedHlsSeekTarget = TimeSpan.Zero;
            PendingSeekCount = 0;
            LastSeekTime = DateTime.MinValue;
            HasPerformedInitialSeek = false;
            HlsManifestOffsetApplied = false;
        }
    }
}
