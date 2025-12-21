using System;

namespace Gelatinarm.Helpers
{
    public sealed class PlaybackSessionState
    {
        public bool IsHlsStream { get; set; }
        public TimeSpan HlsManifestOffset { get; set; }
        public bool HlsManifestOffsetApplied { get; set; }
        public TimeSpan ExpectedHlsSeekTarget { get; set; }
        public DateTime LastSeekTime { get; set; } = DateTime.MinValue;
        public int PendingSeekCount { get; set; }
        public long PendingSeekPositionAfterQualitySwitch { get; set; }
        public bool HasPerformedInitialSeek { get; set; }
        public bool IsHlsTrackChange { get; set; }

        public TimeSpan GetDisplayPosition(TimeSpan rawPosition, TimeSpan serviceOffset)
        {
            if (serviceOffset > TimeSpan.Zero)
            {
                return rawPosition + serviceOffset;
            }

            if (HlsManifestOffsetApplied && HlsManifestOffset > TimeSpan.Zero)
            {
                return rawPosition + HlsManifestOffset;
            }

            return rawPosition;
        }

        public void RecordLargeSeek(TimeSpan targetPosition)
        {
            ExpectedHlsSeekTarget = targetPosition;
            LastSeekTime = DateTime.UtcNow;
            PendingSeekCount++;
        }

        public void ClearExpectedSeekTarget()
        {
            ExpectedHlsSeekTarget = TimeSpan.Zero;
        }

        public void SetManifestOffset(TimeSpan offset, bool applied)
        {
            HlsManifestOffset = offset;
            HlsManifestOffsetApplied = applied;
        }

        public void ResetManifestOffset()
        {
            HlsManifestOffset = TimeSpan.Zero;
            HlsManifestOffsetApplied = false;
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
            ResetManifestOffset();
            ClearExpectedSeekTarget();
            PendingSeekCount = 0;
            LastSeekTime = DateTime.MinValue;
            PendingSeekPositionAfterQualitySwitch = 0;
            HasPerformedInitialSeek = false;
            IsHlsTrackChange = false;
        }
    }
}
