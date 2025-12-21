using System;
using Windows.Media.Playback;

namespace Gelatinarm.Helpers
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

        public static PlaybackSessionSnapshot Capture(MediaPlaybackSession session, bool skipBufferingProgress = false)
        {
            if (session == null)
            {
                return new PlaybackSessionSnapshot(
                    hasSession: false,
                    state: MediaPlaybackState.None,
                    position: TimeSpan.Zero,
                    naturalDuration: TimeSpan.Zero,
                    bufferingProgress: 1.0,
                    canSeek: true,
                    isProtected: false);
            }

            return new PlaybackSessionSnapshot(
                hasSession: true,
                state: SafeGet(() => session.PlaybackState, MediaPlaybackState.None),
                position: SafeGet(() => session.Position, TimeSpan.Zero),
                naturalDuration: SafeGet(() => session.NaturalDuration, TimeSpan.Zero),
                bufferingProgress: skipBufferingProgress ? 1.0 : SafeGet(() => session.BufferingProgress, 1.0),
                canSeek: SafeGet(() => session.CanSeek, true),
                isProtected: SafeGet(() => session.IsProtected, false));
        }

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
