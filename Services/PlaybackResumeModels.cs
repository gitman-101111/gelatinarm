using System;
using System.Threading;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    internal enum ResumeState
    {
        NotStarted,      // Resume not yet attempted
        InProgress,      // Initial resume seek applied
        Verifying,       // Checking if position is advancing at target
        RecoveryNeeded,  // Stuck, applying recovery techniques
        Succeeded,       // Resume completed successfully
        Failed           // Resume failed after all attempts
    }

    internal struct RetryConfig
    {
        public int MaxAttempts;
        public int DelayMs;
        public double ToleranceSeconds;

        public static RetryConfig ForHls => new RetryConfig
        {
            MaxAttempts = 8,  // HLS needs more time for server to transcode
            DelayMs = 5000,   // Give server time to restart transcode and generate segments
            ToleranceSeconds = 10.0  // Accounts for segment boundaries + keyframe alignment
        };

        public static RetryConfig ForDirectPlay => new RetryConfig
        {
            MaxAttempts = 5,
            DelayMs = 1000,
            ToleranceSeconds = 5.0  // Accounts for keyframe alignment and buffering
        };
    }

    internal interface IStreamResumePolicy
    {
        bool CanTrustStartTimeTicks { get; }
        bool NeedsClientSeek { get; }
        double AcceptableToleranceSeconds { get; }
        bool ShouldUseManifestOffset { get; }
    }

    internal sealed class DirectPlayResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => true;
        public bool NeedsClientSeek => false;
        public double AcceptableToleranceSeconds => 3.0;
        public bool ShouldUseManifestOffset => false;
    }

    internal sealed class HlsResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => false;
        public bool NeedsClientSeek => true;
        public double AcceptableToleranceSeconds => RetryConfig.ForHls.ToleranceSeconds;
        public bool ShouldUseManifestOffset => true;
    }

    internal sealed class HlsInitialResumePolicy : IStreamResumePolicy
    {
        public bool CanTrustStartTimeTicks => false;
        public bool NeedsClientSeek => true;
        public double AcceptableToleranceSeconds => RetryConfig.ForHls.ToleranceSeconds;
        public bool ShouldUseManifestOffset => false;
    }

    internal sealed class ResumeVerificationHelper
    {
        private TimeSpan _lastVerifiedPosition = TimeSpan.Zero;
        private DateTime _lastPositionCheckTime = DateTime.MinValue;
        private int _stuckPositionCount = 0;

        public TimeSpan LastVerifiedPosition => _lastVerifiedPosition;
        public DateTime LastPositionCheckTime => _lastPositionCheckTime;
        public int StuckPositionCount => Volatile.Read(ref _stuckPositionCount);

        public void Reset()
        {
            _lastVerifiedPosition = TimeSpan.Zero;
            _lastPositionCheckTime = DateTime.MinValue;
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public void StartVerification(TimeSpan currentPosition)
        {
            _lastVerifiedPosition = currentPosition;
            _lastPositionCheckTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public bool TryGetPositionChange(TimeSpan currentPosition, out double positionChange)
        {
            positionChange = 0;
            var timeSinceLastCheck = DateTime.UtcNow - _lastPositionCheckTime;
            if (timeSinceLastCheck.TotalSeconds < 1)
            {
                return false;
            }

            positionChange = Math.Abs((currentPosition - _lastVerifiedPosition).TotalSeconds);
            return true;
        }

        public int IncrementStuckCount()
        {
            return Interlocked.Increment(ref _stuckPositionCount);
        }

        public void ClearStuckCount()
        {
            Interlocked.Exchange(ref _stuckPositionCount, 0);
        }

        public void UpdateLastCheck(TimeSpan currentPosition)
        {
            _lastPositionCheckTime = DateTime.UtcNow;
            _lastVerifiedPosition = currentPosition;
        }

        public static bool IsRecoverySeekOnly(int recoveryAttemptLevel, double positionChange)
        {
            return recoveryAttemptLevel == 2 && positionChange <= 1.5;
        }
    }
}
