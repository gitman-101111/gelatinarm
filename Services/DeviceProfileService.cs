using System.Collections.Generic;
using System.Linq;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    public interface IDeviceProfileService
    {
        DeviceProfile GetDeviceProfile();
    }

    public class DeviceProfileService : BaseService, IDeviceProfileService
    {
        private readonly IUnifiedDeviceService _deviceService;
        private DeviceProfile _cachedProfile;

        public DeviceProfileService(IUnifiedDeviceService deviceService, ILogger<DeviceProfileService> logger) :
            base(logger)
        {
            _deviceService = deviceService;
        }

        public DeviceProfile GetDeviceProfile()
        {
            if (_cachedProfile != null)
            {
                return _cachedProfile;
            }

            var isXboxSeries = _deviceService?.IsXboxSeriesConsole ?? false;

            _cachedProfile = new DeviceProfile
            {
                Name = "Xbox UWP Media Player",
                // No bitrate limits - let the Xbox hardware and network be the limiting factors
                // Users with high-end setups can play their 4K remuxes without transcoding

                TranscodingProfiles = GetTranscodingProfiles().ToList(),
                DirectPlayProfiles = GetDirectPlayProfiles(isXboxSeries).ToList(),
                CodecProfiles = GetCodecProfiles().ToList(),
                SubtitleProfiles = GetSubtitleProfiles().ToList()
            };

            return _cachedProfile;
        }

        private DirectPlayProfile[] GetDirectPlayProfiles(bool isXboxSeries)
        {
            // Based on Microsoft's supported codecs documentation:
            // https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/supported-codecs
            return new[]
            {
                new DirectPlayProfile
                {
                    Container = "mp4,m4v,mov,3gp,3g2,fmp4",
                    VideoCodec =
                        "h264,avc1,avc3,hevc,hev1,hvc1,h265,mpeg4,mp4v,mp4s,m4s2,mp43,mpeg1video,mpeg2video,h263,mjpeg,mjpg,dv",
                    AudioCodec =
                        "aac,mp4a,mp3,ac3,ac-3,ec3,eac3,ec-3,flac,alac,opus,pcm,lpcm,wma,wmap,truehd,mlpa,dts,dtsc,dtsh,dtsl,dtse,dts-hd,dts-x,amr,amrnb,amrwb,g711,g711a,g711u,gsm,gsm610,ima_adpcm,ms_adpcm,adpcm_ima,adpcm_ms"
                },
                new DirectPlayProfile
                {
                    Container = "mkv,webm,matroska",
                    // AV1 only supported on Xbox Series consoles
                    VideoCodec = isXboxSeries
                        ? "h264,avc1,avc3,hevc,hev1,hvc1,h265,vp8,vp80,vp9,vp90,vp09,av1,av01,mpeg4,mp4v,vc1,wvc1,mpeg1video,mpeg2video,theora"
                        : "h264,avc1,avc3,hevc,hev1,hvc1,h265,vp8,vp80,vp9,vp90,vp09,mpeg4,mp4v,vc1,wvc1,mpeg1video,mpeg2video,theora",
                    AudioCodec =
                        "aac,mp4a,mp3,ac3,ac-3,ec3,eac3,ec-3,flac,alac,opus,vorbis,dts,dtsc,dtsh,dtsl,dtse,dts-hd,dts-x,dts-es,truehd,mlpa,pcm,lpcm,wma,wmap,wmal,adpcm,g711,g711a,g711u,gsm,gsm610,ima_adpcm,ms_adpcm,adpcm_ima,adpcm_ms"
                },
                new DirectPlayProfile
                {
                    Container = "avi",
                    VideoCodec =
                        "h264,avc1,avc3,mpeg4,mp4v,mp4s,m4s2,mp43,mpeg1video,mpeg2video,mpg2,mjpeg,mjpg,h263,dv",
                    AudioCodec =
                        "mp3,ac3,ac-3,aac,mp4a,pcm,lpcm,wma,adpcm,dts,g711,g711a,g711u,gsm,gsm610,ima_adpcm,ms_adpcm,adpcm_ima,adpcm_ms"
                },
                new DirectPlayProfile
                {
                    Container = "wmv,asf",
                    VideoCodec = "wmv1,wmv2,wmv3,wmv7,wmv8,wmv9,vc1,wvc1,mpeg4,mp4v",
                    AudioCodec = "wma,wmap,wmal,mp3,ac3"
                },
                new DirectPlayProfile
                {
                    Container = "mpg,mpeg,m2v,ts,m2ts,mts",
                    VideoCodec = "mpeg1video,mpeg2video,mpg2,h264,avc1,avc3,hevc,hev1,hvc1,h265,vc1,wvc1",
                    AudioCodec =
                        "mp3,mp2,aac,mp4a,ac3,ac-3,ec3,eac3,ec-3,dts,dtsc,dtsh,dtsl,dts-hd,dts-x,truehd,mlpa,pcm,lpcm"
                },
                // Add FLV container support
                new DirectPlayProfile
                {
                    Container = "flv", VideoCodec = "h264,avc1,vp6,vp6f,vp6a", AudioCodec = "mp3,aac,mp4a,pcm"
                },
                new DirectPlayProfile
                {
                    Container = "mp3,aac,m4a,flac,alac,opus,wav,wma,amr", Type = DirectPlayProfile_Type.Audio
                },
                new DirectPlayProfile { Container = "mp3", Type = DirectPlayProfile_Type.Audio, AudioCodec = "mp3" },
                new DirectPlayProfile
                {
                    Container = "aac,m4a", Type = DirectPlayProfile_Type.Audio, AudioCodec = "aac"
                },
                new DirectPlayProfile { Container = "flac", Type = DirectPlayProfile_Type.Audio, AudioCodec = "flac" },
                new DirectPlayProfile { Container = "wav", Type = DirectPlayProfile_Type.Audio, AudioCodec = "pcm" }
            };
        }

        private TranscodingProfile[] GetTranscodingProfiles()
        {
            return new[]
            {
                new TranscodingProfile
                {
                    Container = "mp4",
                    Type = TranscodingProfile_Type.Video,
                    VideoCodec = "h264,hevc",
                    AudioCodec = "aac,mp3,ac3,eac3,flac,opus", // 27 chars - server validates max 40
                    Context = TranscodingProfile_Context.Streaming,
                    Protocol = TranscodingProfile_Protocol.Hls,
                    // MaxAudioChannels removed to support Atmos/DTS:X/7.1+ audio
                    MinSegments = 2,  // Ensure at least 2 segments are ready before playback
                    SegmentLength = 3, // 3-second segments for more precise seeking and faster recovery
                    BreakOnNonKeyFrames = false, // Keep segments on keyframes for stability
                    CopyTimestamps = false,
                    EnableSubtitlesInManifest = false
                },
                new TranscodingProfile
                {
                    Container = "mp3",
                    Type = TranscodingProfile_Type.Audio,
                    AudioCodec = "mp3",
                    Context = TranscodingProfile_Context.Streaming,
                    Protocol = TranscodingProfile_Protocol.Http
                },
                new TranscodingProfile
                {
                    Container = "aac",
                    Type = TranscodingProfile_Type.Audio,
                    AudioCodec = "aac",
                    Context = TranscodingProfile_Context.Streaming,
                    Protocol = TranscodingProfile_Protocol.Http
                },
                new TranscodingProfile
                {
                    Container = "ts",
                    Type = TranscodingProfile_Type.Audio,
                    AudioCodec = "mp3,aac",
                    Context = TranscodingProfile_Context.Streaming,
                    Protocol = TranscodingProfile_Protocol.Hls
                }
            };
        }

        private CodecProfile[] GetCodecProfiles()
        {
            return new[]
            {
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "h264",
                    Conditions =
                        new[]
                        {
                            new ProfileCondition
                            {
                                Condition = ProfileCondition_Condition.LessThanEqual,
                                Property = ProfileCondition_Property.VideoBitDepth,
                                Value = "8",
                                IsRequired = false
                            },
                            new ProfileCondition
                            {
                                Condition = ProfileCondition_Condition.LessThanEqual,
                                Property = ProfileCondition_Property.VideoLevel,
                                Value = "52",
                                IsRequired = false
                            },
                            new ProfileCondition
                            {
                                Condition = ProfileCondition_Condition.EqualsAny,
                                Property = ProfileCondition_Property.VideoProfile,
                                Value = "high,main,baseline,constrained baseline",
                                IsRequired = false
                            }
                        }.ToList()
                },
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "hevc",
                    Conditions = new[]
                    {
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.LessThanEqual,
                            Property = ProfileCondition_Property.VideoBitDepth,
                            Value = "10",
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.LessThanEqual,
                            Property = ProfileCondition_Property.VideoLevel,
                            Value = "183", // Level 6.1 for 8K support
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoProfile,
                            Value = "main,main10",
                            IsRequired = false
                        }
                    }.ToList()
                },
                // HDR10 support
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "hevc",
                    Conditions = new[]
                    {
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoRangeType,
                            Value = GetSupportedVideoRangeTypes(),
                            IsRequired = false
                        }
                    }.ToList()
                },
                // Dolby Vision support (Xbox Series only)
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "hevc",
                    Conditions = new[]
                    {
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoRangeType,
                            Value = "DOVI",
                            IsRequired = false
                        }
                    }.ToList()
                },
                // VP9 profile support
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "vp9",
                    Conditions = new[]
                    {
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.LessThanEqual,
                            Property = ProfileCondition_Property.VideoBitDepth,
                            Value = "10",
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoProfile,
                            Value = "Profile0,Profile2",
                            IsRequired = false
                        }
                    }.ToList()
                },
                // AV1 profile support (Xbox Series only)
                new CodecProfile
                {
                    Type = CodecProfile_Type.Video,
                    Codec = "av1",
                    Conditions = new[]
                    {
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.LessThanEqual,
                            Property = ProfileCondition_Property.VideoBitDepth,
                            Value = "10",
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoProfile,
                            Value = "Main",
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.LessThanEqual,
                            Property = ProfileCondition_Property.VideoLevel,
                            Value = "15", // Level 5.1 for 4K@60fps
                            IsRequired = false
                        }
                    }.ToList()
                }
            };
        }

        private SubtitleProfile[] GetSubtitleProfiles()
        {
            // We use Embed method for all subtitles because:
            // 1. Jellyfin Server doesn't respect SubtitleStreamIndex for HLS subtitle tracks
            // 2. UWP MediaPlayer cannot switch subtitle tracks mid-stream
            // 3. Embed method ensures subtitles are burned into the video when SubtitleStreamIndex is provided
            return new[]
            {
                new SubtitleProfile { Format = "srt", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "subrip", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "ass", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "ssa", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "vtt", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "webvtt", Method = SubtitleProfile_Method.Embed },

                new SubtitleProfile { Format = "pgs", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "pgssub", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "dvdsub", Method = SubtitleProfile_Method.Embed },
                new SubtitleProfile { Format = "dvbsub", Method = SubtitleProfile_Method.Embed }
            };
        }

        private string GetSupportedVideoRangeTypes()
        {
            var supportedTypes = new List<string> { "SDR" };

            if (_deviceService != null)
            {
                // Only add HDR formats that are actually supported by the display
                if (_deviceService.SupportsHDR10)
                {
                    supportedTypes.Add("HDR10");
                }

                if (_deviceService.SupportsHDR10Plus)
                {
                    supportedTypes.Add("HDR10Plus");
                }

                // Only add HLG if the display fully supports it
                // This prevents HLG on displays that "don't support all HDR10 modes"
                if (_deviceService.SupportsHLG)
                {
                    supportedTypes.Add("HLG");
                }

                // Dolby Vision requires special support
                if (_deviceService.SupportsDolbyVision)
                {
                    supportedTypes.Add("DOVI");
                }
            }

            var result = string.Join(",", supportedTypes);
            Logger?.LogInformation($"Supported video range types: {result}");
            return result;
        }
    }
}
