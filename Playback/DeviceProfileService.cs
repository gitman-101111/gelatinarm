using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Media.Protection;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Errors;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Playback
{
    public interface IDeviceProfileService
    {
        /// <summary>
        ///     The console's profile for one playback. With <paramref name="hevcVideoCopyExpected" />
        ///     the transcoding entries let the server copy HEVC under HEVC's own limits (see
        ///     <see cref="ExpectsVideoCopy" />); otherwise they name both codecs in one entry.
        /// </summary>
        DeviceProfile GetDeviceProfile(bool hevcVideoCopyExpected);

        /// <summary>
        ///     Whether the server will copy this source's video rather than re-encode it: HEVC the
        ///     console decodes, within the profile's picture, depth, level, profile, range and bitrate
        ///     limits (<paramref name="maxStreamingBitrateMbps" /> 0 meaning the console's own), and no
        ///     subtitle chosen, since every subtitle is burned in.
        /// </summary>
        bool ExpectsVideoCopy(MediaSourceInfo source, int? subtitleStreamIndex, int maxStreamingBitrateMbps);
    }

    public class DeviceProfileService : BaseService, IDeviceProfileService
    {
        private static readonly string[] SubtitleFormats =
        {
            "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "pgs", "pgssub", "dvdsub", "dvbsub"
        };

        private static readonly string[] HevcCodecs = { "hevc", "h265", "hev1", "hvc1" };
        private static readonly string[] HevcProfiles = { "main", "main 10", "main10", "dvhe.08" };

        private readonly IUnifiedDeviceService _deviceService;
        private readonly Dictionary<bool, DeviceProfile> _profilesByExpectation = new Dictionary<bool, DeviceProfile>();

        public DeviceProfileService(IUnifiedDeviceService deviceService, ILogger<DeviceProfileService> logger) :
            base(logger)
        {
            _deviceService = deviceService;
        }

        public DeviceProfile GetDeviceProfile(bool hevcVideoCopyExpected)
        {
            if (_profilesByExpectation.TryGetValue(hevcVideoCopyExpected, out var cached))
            {
                return cached;
            }

            var hasHevc = HasHardwareDecode("hvc1");
            var hasVp9 = HasHardwareDecode("vp09");

            var profile = new DeviceProfile
            {
                Name = "Xbox UWP Media Player",
                // Left unset, Jellyfin fills in its DeviceProfile defaults of 8 Mbps for both,
                // which rejected direct play of anything larger (ContainerBitrateExceedsLimit)
                // and transcoded it down to ~7.5 Mbps. These are the hardware ceiling; the user's
                // Maximum Bitrate setting is sent with each PlaybackInfo request.
                MaxStreamingBitrate = _deviceService.MaxSupportedBitrate,
                MaxStaticBitrate = _deviceService.MaxSupportedBitrate,
                TranscodingProfiles = GetTranscodingProfiles(hasHevc, hevcVideoCopyExpected),
                DirectPlayProfiles = GetDirectPlayProfiles(hasHevc, hasVp9),
                CodecProfiles = GetCodecProfiles(),
                SubtitleProfiles = GetSubtitleProfiles()
            };

            _profilesByExpectation[hevcVideoCopyExpected] = profile;
            return profile;
        }

        // The server's own copy test, as far as the client can see it: the HEVC codec profile's
        // conditions below and the bitrate ceiling. A value the stream does not report passes, as
        // it does on the server (IsRequired is false throughout).
        public bool ExpectsVideoCopy(MediaSourceInfo source, int? subtitleStreamIndex, int maxStreamingBitrateMbps)
        {
            var video = source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            if (video?.Codec == null || subtitleStreamIndex >= 0 ||
                !HevcCodecs.Contains(video.Codec, StringComparer.OrdinalIgnoreCase) || !HasHardwareDecode("hvc1"))
            {
                return false; // a chosen subtitle is burned in by the server, which means a re-encode
            }

            var fourK = SupportsFourK();
            var frameRate = video.AverageFrameRate ?? video.RealFrameRate;
            var maxBitrate = maxStreamingBitrateMbps > 0 ? maxStreamingBitrateMbps * 1000000L : _deviceService.MaxSupportedBitrate;
            return (video.Width ?? 0) <= (fourK ? 3840 : 1920)
                   && (video.Height ?? 0) <= (fourK ? 2160 : 1080)
                   && (frameRate ?? 0) <= 60
                   && (video.BitDepth ?? 0) <= 10
                   && (video.Level ?? 0) <= 183
                   && (video.Profile == null || HevcProfiles.Contains(video.Profile, StringComparer.OrdinalIgnoreCase))
                   && (video.VideoRangeType == null ||
                       SupportedVideoRangeTypes().Contains(video.VideoRangeType.ToString(), StringComparer.OrdinalIgnoreCase))
                   && (source.Bitrate ?? 0) <= maxBitrate;
        }

        /// <summary>
        ///     Microsoft's documented check (4K video playback on Xbox), which it recommends over
        ///     going by console type. Hardware HEVC is NotSupported on the original Xbox One;
        ///     VP9 is listed only for the Xbox One X and the Series consoles (Supported
        ///     technologies on Xbox).
        /// </summary>
        private bool HasHardwareDecode(string codec)
        {
            try
            {
                var result = new ProtectionCapabilities().IsTypeSupported(
                    $"video/mp4;codecs=\"{codec},mp4a\";features=\"decode-res-x=3840,decode-res-y=2160," +
                    "decode-bitrate=20000,decode-fps=30,decode-bpc=10\"",
                    "com.microsoft.playready.hardware");
                Logger.LogInformation("Hardware {Codec} decode check: {Result}", codec, result);
                return result != ProtectionCapabilityResult.NotSupported;
            }
            catch (Exception ex)
            {
                // Unknown means the server converts it: a stream that plays beats a guess
                ErrorHandler.HandleError(ex, CreateErrorContext($"HasHardwareDecode:{codec}", ErrorCategory.Media, ErrorSeverity.Warning));
                return false;
            }
        }

        private static List<DirectPlayProfile> GetDirectPlayProfiles(bool hasHevc, bool hasVp9)
        {
            // Microsoft's tables: the video codecs Xbox supports (Supported technologies on Xbox),
            // in the containers the video table gives them, with the audio the Xbox audio table
            // gives each container.
            // https://learn.microsoft.com/en-us/windows/uwp/apps-for-xbox/supported-technologies
            // https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/supported-codecs
            // Kept outside the tables because they played on the console: AAC, AC3 and MP3 in MKV
            // (the audio table has no MKV column), FLAC in .m4a, and WMV1 in ASF (2560 wide).
            // Beyond those, a codec the console accepts but cannot decode plays silent, with no
            // failure to retry on (FLAC in MKV did). DTS in MKV is listed without a console test
            // yet: neither table has DTS, and owners report the console's own Media Player app
            // does not play it (AVForums, 2015). If a DTS track in MKV plays silent, remove it.
            //
            // Every profile states its Type. The server only considers Video-type profiles for
            // video and Audio-type for music, and an omitted Type deserializes as the enum's
            // first value, Audio -- so untyped video profiles are silently ignored and every
            // video comes back as DirectPlayError.
            const string H264 = "h264,avc1,avc3,";
            var hevc = hasHevc ? string.Join(",", HevcCodecs) + "," : string.Empty;
            var vp9 = hasVp9 ? "vp9,vp90,vp09," : string.Empty;
            const string Vc1 = "vc1,wvc1,wmv3,";
            const string Aac = "aac,mp4a,";
            const string Ac3 = "ac3,ac-3,";
            const string Dts = "dts,dca,";
            const string Amr = "amrnb,amr_nb,";
            const string Wma = "wma,wmap,wmav1,wmav2,wmapro,wmavoice,";
            const string Lpcm = "pcm,lpcm,pcm_s16le,pcm_s24le,pcm_s32le,pcm_u8,pcm_f32le,";

            return new List<DirectPlayProfile>
            {
                Video("mp4,m4v,mov,fmp4", H264 + hevc + vp9 + Vc1 + "mpeg4,mp4v", Aac + Ac3 + Amr + "alac,mp3"),
                Video("3gp", H264 + "mpeg4,mp4v", Amr),
                Video("mkv,webm,matroska", H264 + hevc + vp9 + Vc1 + "mpeg2video,mpeg4,mp4v", Aac + Ac3 + Dts + "mp3"),
                Video("avi", H264 + Vc1 + "mpeg4,mp4v", Ac3 + "mp3"),
                Video("asf,wmv", Vc1 + "wmv1", Wma + Ac3),
                Video("mpg,mpeg,ts,m2ts,mts", H264 + "mpeg2video", Ac3 + "mp2"),
                Audio("mp3", "mp3"),
                Audio("aac", Aac),
                Audio("m4a,m4b", Aac + Ac3 + Amr + "alac,mp3,flac"),
                Audio("asf,wma", Wma + Ac3),
                Audio("ac3", Ac3),
                Audio("amr,3gp", Amr),
                Audio("flac", "flac"),
                Audio("wav", Lpcm + "pcm_alaw,pcm_mulaw,adpcm_ima_wav,adpcm_ms,gsm_ms")
            };
        }

        private static DirectPlayProfile Video(string containers, string videoCodecs, string audioCodecs)
        {
            return new DirectPlayProfile
            {
                Type = DirectPlayProfile_Type.Video,
                Container = containers,
                VideoCodec = videoCodecs.TrimEnd(','),
                AudioCodec = audioCodecs.TrimEnd(',')
            };
        }

        private static DirectPlayProfile Audio(string containers, string audioCodecs)
        {
            return new DirectPlayProfile
            {
                Type = DirectPlayProfile_Type.Audio,
                Container = containers,
                AudioCodec = audioCodecs.TrimEnd(',')
            };
        }

        private static List<TranscodingProfile> GetTranscodingProfiles(bool hasHevc, bool hevcVideoCopyExpected)
        {
            // The server applies the codec conditions of every codec a video transcoding profile
            // names, so one profile naming "hevc,h264" put H.264's limits (1080p, 8-bit) on every
            // HEVC transcode and remux: a 4K HDR10 file with TrueHD audio came back 1080p SDR even
            // in the 4K edition (server log, 2026-09-28). An HEVC-only profile first keeps HEVC's
            // own limits, so the video is copied and only the audio re-encoded; H.264 stays as the
            // fallback for other sources and for a server that cannot encode HEVC. Without
            // hardware HEVC the server must not copy HEVC into the stream either (owner decision 16).
            // Only when the client expects that copy (ExpectsVideoCopy): a copy needs no encoder,
            // whereas an entry naming HEVC alone would make a server that may not encode HEVC
            // (Jellyfin's default) encode it in software, since the server reorders a disallowed
            // codec behind the others only within one entry (ShiftVideoCodecsIfNeeded). For a
            // re-encode the two codecs therefore share one entry.
            var profiles = new List<TranscodingProfile>();
            if (hasHevc && hevcVideoCopyExpected)
            {
                profiles.Add(VideoTranscodingProfile("hevc"));
                profiles.Add(VideoTranscodingProfile("h264"));
            }
            else
            {
                profiles.Add(VideoTranscodingProfile(hasHevc ? "hevc,h264" : "h264"));
            }

            profiles.AddRange(new[]
            {
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
            });
            return profiles;
        }

        private static TranscodingProfile VideoTranscodingProfile(string videoCodec)
        {
            return new TranscodingProfile
            {
                Container = "mp4",
                Type = TranscodingProfile_Type.Video,
                VideoCodec = videoCodec,
                // Only AAC and AC3 are safe to stream-copy into HLS/MPEG-TS.
                // MP3 and FLAC are not valid HLS audio codecs -- Xbox's AdaptiveMediaSource
                // fails to open manifests that contain them. Any other codec will be
                // transcoded to AAC (first in the list) by the server.
                AudioCodec = "aac,ac3",
                Context = TranscodingProfile_Context.Streaming,
                Protocol = TranscodingProfile_Protocol.Hls,
                MinSegments = 5,
                SegmentLength = 5, // 5-second segments for better seeking precision
                BreakOnNonKeyFrames = false, // Keep segments on keyframes for stability
                CopyTimestamps = false,
                EnableSubtitlesInManifest = false,
                EnableMpegtsM2TsMode = false
            };
        }

        private List<CodecProfile> GetCodecProfiles()
        {
            var profiles = new List<CodecProfile>
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
                                // EqualsAny is pipe-delimited (Jellyfin ConditionProcessor splits on '|' only).
                                Value = "high|main|baseline|constrained baseline",
                                IsRequired = false
                            }
                        }.Concat(PictureLimits(upTo4K: false)).ToList()
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
                            Value = "183", // Level 6.1
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoProfile,
                            // Pipe-delimited. ffprobe/Jellyfin report HEVC Main 10 as "Main 10"
                            // (space). "main10" is kept as a defensive alias. Case is ignored.
                            Value = string.Join("|", HevcProfiles),
                            IsRequired = false
                        },
                        new ProfileCondition
                        {
                            Condition = ProfileCondition_Condition.EqualsAny,
                            Property = ProfileCondition_Property.VideoRangeType,
                            Value = GetSupportedVideoRangeTypes(),
                            IsRequired = false
                        }
                    }.Concat(PictureLimits(upTo4K: true)).ToList()
                },
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
                            Value = "Profile0|Profile2",
                            IsRequired = false
                        }
                    }.Concat(PictureLimits(upTo4K: true)).ToList()
                }
            };

            profiles.Add(new CodecProfile
            {
                Type = CodecProfile_Type.Video,
                Codec = "vc1,wvc1,wmv3,mpeg2video,mpeg4,mp4v",
                Conditions = PictureLimits(upTo4K: false).ToList()
            });

            // The console's AAC decoder stops at 5.1: a 7.1 AAC track direct plays with no
            // sound and no error (device: Iron Man 3, AAC LC 8 ch in MKV). Above 6 channels
            // the server re-encodes the audio and the video can still be copied.
            profiles.Add(new CodecProfile
            {
                Type = CodecProfile_Type.VideoAudio,
                Codec = "aac",
                Conditions = new[]
                {
                    new ProfileCondition
                    {
                        Condition = ProfileCondition_Condition.LessThanEqual,
                        Property = ProfileCondition_Property.AudioChannels,
                        Value = "6",
                        IsRequired = false
                    }
                }.ToList()
            });

            // ffprobe reports DTS-HD MA/HRA, DTS:X and DTS Express as codec "dts" too, told
            // apart only by profile. Only the core profiles direct play: Media Foundation gives
            // DTS-HD and LBR their own subtypes (MFAudioFormat_DTS_HD, _DTS_XLL, _DTS_LBR), so a
            // core decoder is no evidence for them, and Express has no core at all. The rest
            // keep the video copied and only the audio re-encoded.
            profiles.Add(new CodecProfile
            {
                Type = CodecProfile_Type.VideoAudio,
                Codec = "dts,dca",
                Conditions = new[]
                {
                    new ProfileCondition
                    {
                        Condition = ProfileCondition_Condition.EqualsAny,
                        Property = ProfileCondition_Property.AudioProfile,
                        Value = "DTS|DTS-ES|DTS 96/24",
                        IsRequired = false
                    }
                }.ToList()
            });

            return profiles;
        }

        // Xbox plays H.264, VC-1, MPEG-2 and MPEG-4 up to 1080p60 and HEVC and VP9 up to
        // 2160p60 (Supported technologies on Xbox). 4K also needs the restricted hevcPlayback
        // capability, which closes the app when a game starts and so rules out background
        // music. The standard edition keeps background music; without the capability the
        // video driver runs out of graphics memory on 4K frames (device: MakeResident
        // E_OUTOFMEMORY, then a native crash), so there the server scales everything to 1080p.
        // Re-tested 2026-09-28 on a Series X with the cap lifted: a 4K HLG HEVC file direct
        // played for 15 s, then the second seek removed the D3D device (decoder error
        // 0xC00D36B4, 798 MB in use against 198 MB after open) and the app ended.
        private static IEnumerable<ProfileCondition> PictureLimits(bool upTo4K)
        {
            var fourK = upTo4K && SupportsFourK();
            yield return LessThanEqual(ProfileCondition_Property.Width, fourK ? "3840" : "1920");
            yield return LessThanEqual(ProfileCondition_Property.Height, fourK ? "2160" : "1080");
            yield return LessThanEqual(ProfileCondition_Property.VideoFramerate, "60");
        }

        private static bool SupportsFourK()
        {
#if EDITION_4K
            return true;
#else
            return false;
#endif
        }

        private static ProfileCondition LessThanEqual(ProfileCondition_Property property, string value)
        {
            return new ProfileCondition
            {
                Condition = ProfileCondition_Condition.LessThanEqual,
                Property = property,
                Value = value,
                IsRequired = false
            };
        }

        private static List<SubtitleProfile> GetSubtitleProfiles()
        {
            // The player renders no subtitles itself: nothing enables the MediaPlaybackItem's
            // timed-text tracks. So every format is Encode (burned in by the server). A
            // selected subtitle therefore means a transcode; with no subtitle selected the
            // file can still direct play. Declaring Embed here would let the server direct
            // play with a subtitle selected, and the subtitle would never appear.
            return SubtitleFormats
                .Select(format => new SubtitleProfile { Format = format, Method = SubtitleProfile_Method.Encode })
                .ToList();
        }

        private string GetSupportedVideoRangeTypes()
        {
            // EqualsAny is pipe-delimited; commas are treated as a single unmatched literal.
            var result = string.Join("|", SupportedVideoRangeTypes());
            Logger.LogInformation("Supported video range types: {Result}", result);
            return result;
        }

        private List<string> SupportedVideoRangeTypes()
        {
            var supportedTypes = new List<string> { "SDR" };

            if (_deviceService.SupportsHDR10)
            {
                supportedTypes.Add("HDR10");
            }

            if (_deviceService.SupportsHDR10Plus)
            {
                supportedTypes.Add("HDR10Plus");
            }

            // Not on a display that "doesn't support all HDR10 modes": SupportsHlg is the full check
            if (_deviceService.SupportsHlg)
            {
                supportedTypes.Add("HLG");
            }

            // Dolby Vision (Series consoles on a DV display). The range types go in
            // this one list rather than a separate HEVC codec profile: Jellyfin applies
            // every codec profile for a codec and all of their conditions must pass,
            // so a DV-only HEVC profile disqualified every non-DV HEVC file.
            if (_deviceService.SupportsDolbyVision)
            {
                supportedTypes.Add("DOVI");
                supportedTypes.Add("DOVIWithHDR10");
                supportedTypes.Add("DOVIWithHLG");
                supportedTypes.Add("DOVIWithSDR");
            }

            return supportedTypes;
        }
    }
}
