using System;
using System.Collections.Generic;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Models
{
    public class MediaPlaybackParams
    {
        public BaseItemDto Item { get; set; }
        public string ItemId { get; set; }
        public string MediaSourceId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public long? StartPositionTicks { get; set; }
        public List<BaseItemDto> QueueItems { get; set; }
        public int StartIndex { get; set; }
        public bool IsShuffled { get; set; }
        public Type NavigationSourcePage { get; set; } // Track where we navigated from
        public object NavigationSourceParameter { get; set; } // Optional parameter for the source page
    }

    public class AudioPlaybackParams
    {
        public BaseItemDto Item { get; set; }
        public string ItemId { get; set; }
        public string MediaSourceId { get; set; }
        public long? StartPositionTicks { get; set; }
        public List<BaseItemDto> QueueItems { get; set; }
        public int StartIndex { get; set; }
        public bool IsShuffled { get; set; }
        public Type NavigationSourcePage { get; set; }
        public object NavigationSourceParameter { get; set; }
    }

    public class SubtitleTrack
    {
        public int ServerStreamIndex { get; set; }
        public string Language { get; set; }
        public string DisplayTitle { get; set; }
        public bool IsDefault { get; set; }
        public bool IsNoneOption { get; set; }
    }


    public class MovieVersion
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MediaSourceInfo SourceInfo { get; set; }
    }

    public class AudioTrack
    {
        public int ServerStreamIndex { get; set; }
        public string Language { get; set; }
        public string DisplayName { get; set; }
        public bool IsDefault { get; set; }
    }

    public class SkipSegment
    {
        public string Type { get; set; } // "Intro" or "Outro"
        public long StartTicks { get; set; }
        public long EndTicks { get; set; }
    }

    public enum SkipSegmentType
    {
        Intro,
        Outro
    }

    public class PlaybackStats
    {
        public string VideoInfo { get; set; }
        public string BufferInfo { get; set; }
        public string NetworkInfo { get; set; }
        public string PlaybackInfo { get; set; }
        public string BitrateInfo { get; set; }
        public string SubtitleInfo { get; set; }
        public DateTime LastUpdated { get; set; }

        // Additional detailed stats
        public string Player { get; set; }
        public string PlayMethod { get; set; }
        public string Protocol { get; set; }
        public string StreamType { get; set; }
        public string Container { get; set; }
        public string Size { get; set; }
        public string Bitrate { get; set; }
        public string VideoCodec { get; set; }
        public string VideoBitrate { get; set; }
        public string VideoRangeType { get; set; }
        public string AudioCodec { get; set; }
        public string AudioBitrate { get; set; }
        public string AudioChannels { get; set; }
        public string AudioSampleRate { get; set; }
        public string Resolution { get; set; }
        public string FrameRate { get; set; }
    }

    public class EpisodeNavigationParameter
    {
        public BaseItemDto Episode { get; set; }
        public bool FromEpisodesButton { get; set; }
        public Type OriginalSourcePage { get; set; }
        public object OriginalSourceParameter { get; set; }
    }

    public enum ControllerButton
    {
        A,
        B,
        X,
        Y,
        DPadUp,
        DPadDown,
        DPadLeft,
        DPadRight,
        LeftShoulder,
        RightShoulder,
        LeftTrigger,
        RightTrigger,
        View,
        LeftThumbstick,
        RightThumbstick
    }


    public enum MediaAction
    {
        PlayPause,
        Stop,
        Next,
        Previous,
        FastForward,
        Rewind,
        VolumeUp,
        VolumeDown,
        Mute,
        SkipIntro,
        SkipOutro,
        ShowInfo,
        ShowStats,
        ToggleSubtitles,
        NextSubtitleTrack,
        NextAudioTrack,
        PreviousAudioTrack,
        NavigateBack
    }

}
