using System;
using Windows.Media.Playback;

namespace Gelatinarm.Player
{
    internal readonly struct PlaybackSessionSnapshot
    {
        public bool HasSession { get; }
        public MediaPlaybackState State { get; }
        public TimeSpan Position { get; }
        public TimeSpan NaturalDuration { get; }
        public double BufferingProgress { get; }
        public bool CanSeek { get; }
        public bool IsProtected { get; }

        private PlaybackSessionSnapshot(
            bool hasSession,
            MediaPlaybackState state,
            TimeSpan position,
            TimeSpan naturalDuration,
            double bufferingProgress,
            bool canSeek,
            bool isProtected)
        {
            HasSession = hasSession;
            State = state;
            Position = position;
            NaturalDuration = naturalDuration;
            BufferingProgress = bufferingProgress;
            CanSeek = canSeek;
            IsProtected = isProtected;
        }

        public static PlaybackSessionSnapshot Capture(MediaPlaybackSession session, bool skipBufferingProgress)
        {
            if (session == null)
            {
                return new PlaybackSessionSnapshot(
                    false,
                    MediaPlaybackState.None,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    1.0,
                    true,
                    false);
            }

            return new PlaybackSessionSnapshot(
                true,
                SafeGet(() => session.PlaybackState, MediaPlaybackState.None),
                SafeGet(() => session.Position, TimeSpan.Zero),
                SafeGet(() => session.NaturalDuration, TimeSpan.Zero),
                skipBufferingProgress ? 1.0 : SafeGet(() => session.BufferingProgress, 1.0),
                SafeGet(() => session.CanSeek, true),
                SafeGet(() => session.IsProtected, false));
        }

        // A session whose player has been closed throws from every property; the snapshot then says what an empty player would
        private static T SafeGet<T>(Func<T> getter, T fallback)
        {
            try
            {
                return getter();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
