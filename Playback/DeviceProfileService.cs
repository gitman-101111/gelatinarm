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
        ///     <paramref name="source" />, when the file is known, turns the picture limits for a
        ///     portrait video and admits what only a QuickTime file plays.
        ///     <paramref name="hdrOnAnyDisplay" /> is the setting of that name;
        ///     <paramref name="dolbyVisionAloneFailed" /> leaves out Dolby Vision without an HDR10,
        ///     HLG or SDR layer, for the retry after such a file failed to decode.
        /// </summary>
        DeviceProfile GetDeviceProfile(bool hevcVideoCopyExpected, MediaSourceInfo source, bool hdrOnAnyDisplay,
            bool dolbyVisionAloneFailed);

        /// <summary>
        ///     Whether the server will copy this source's video rather than re-encode it: HEVC the
        ///     console decodes, within the profile's picture, depth, level, profile, range and bitrate
        ///     limits (<paramref name="maxStreamingBitrateMbps" /> 0 meaning the console's own), and no
        ///     subtitle chosen, since every subtitle is burned in.
        /// </summary>
        bool ExpectsVideoCopy(MediaSourceInfo source, int? subtitleStreamIndex, int maxStreamingBitrateMbps,
            bool hdrOnAnyDisplay);
    }

    public class DeviceProfileService : BaseService, IDeviceProfileService
    {
        private static readonly string[] SubtitleFormats =
        {
            "srt", "subrip", "ass", "ssa", "vtt", "webvtt", "pgs", "pgssub", "dvdsub", "dvbsub"
        };

        private static readonly string[] HevcCodecs = { "hevc", "h265", "hev1", "hvc1" };
        private static readonly string[] HevcProfiles = { "main", "main 10", "main10", "dvhe.08" };

        // Dolby Vision with no HDR10, HLG or SDR layer under it (profile 5)
        private const string DolbyVisionAlone = "DOVI";

        // The largest picture in the standard edition, as a landscape frame (see PictureLimits)
        private static readonly (int Width, int Height) Hd = (1920, 1080);

        private readonly IUnifiedDeviceService _deviceService;

        // The range types are part of the key because the display's report can change while the
        // app runs (HDR switched on or off in the console's settings)
        private readonly Dictionary<(bool HevcCopy, bool Portrait, bool QuickTime, string RangeTypes), DeviceProfile> _profiles =
            new Dictionary<(bool HevcCopy, bool Portrait, bool QuickTime, string RangeTypes), DeviceProfile>();

        public DeviceProfileService(IUnifiedDeviceService deviceService, ILogger<DeviceProfileService> logger) :
            base(logger)
        {
            _deviceService = deviceService;
        }

        public DeviceProfile GetDeviceProfile(bool hevcVideoCopyExpected, MediaSourceInfo source, bool hdrOnAnyDisplay,
            bool dolbyVisionAloneFailed)
        {
            var portrait = IsPortrait(source);
            var quickTime = IsQuickTime(source);

            // EqualsAny is pipe-delimited; commas are treated as a single unmatched literal.
            var rangeTypes = string.Join("|", SupportedVideoRangeTypes(hdrOnAnyDisplay)
                .Where(type => !dolbyVisionAloneFailed || type != DolbyVisionAlone));
            var key = (hevcVideoCopyExpected, portrait, quickTime, rangeTypes);
            if (_profiles.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Logger.LogInformation("Supported video range types: {RangeTypes}", rangeTypes);

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
                DirectPlayProfiles = GetDirectPlayProfiles(hasHevc, hasVp9, quickTime),
                CodecProfiles = GetCodecProfiles(portrait, rangeTypes),
                SubtitleProfiles = GetSubtitleProfiles()
            };

            _profiles[key] = profile;
            return profile;
        }

        // Taller than wide as stored. A phone video stored landscape with a rotation flag is
        // a landscape frame to the decoder and needs no turning.
        private static bool IsPortrait(MediaSourceInfo source)
        {
            var video = source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            return video?.Height > video?.Width;
        }

        // ALAC and PCM play in a QuickTime-structured file and are silent in an MP4-structured
        // one, whatever either is named; AC3 and E-AC3 play in MP4 and make the console refuse
        // a QuickTime file (Docs/CODEC_TESTING.md). The server reports both as one container
        // and does not say which structure a file has, so its name stands in: .mov files are
        // QuickTime files in practice.
        private static bool IsQuickTime(MediaSourceInfo source)
        {
            return source?.Path != null &&
                   (source.Path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                    source.Path.EndsWith(".qt", StringComparison.OrdinalIgnoreCase));
        }

        // The server's own copy test, as far as the client can see it: the HEVC codec profile's
        // conditions below and the bitrate ceiling. A value the stream does not report passes, as
        // it does on the server (IsRequired is false throughout).
        public bool ExpectsVideoCopy(MediaSourceInfo source, int? subtitleStreamIndex, int maxStreamingBitrateMbps,
            bool hdrOnAnyDisplay)
        {
            var video = source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            if (video?.Codec == null || subtitleStreamIndex >= 0 ||
                !HevcCodecs.Contains(video.Codec, StringComparer.OrdinalIgnoreCase) || !HasHardwareDecode("hvc1"))
            {
                return false; // a chosen subtitle is burned in by the server, which means a re-encode
            }

            var portrait = IsPortrait(source);
            var sizeFits = XboxDevice.IsFourKEdition ||
                           ((video.Width ?? 0) <= (portrait ? Hd.Height : Hd.Width) &&
                            (video.Height ?? 0) <= (portrait ? Hd.Width : Hd.Height));
            var frameRate = video.AverageFrameRate ?? video.RealFrameRate;
            var maxBitrate = maxStreamingBitrateMbps > 0 ? maxStreamingBitrateMbps * 1000000L : _deviceService.MaxSupportedBitrate;
            return sizeFits
                   && (frameRate ?? 0) <= 60
                   && (video.BitDepth ?? 0) <= 10
                   && (video.Level ?? 0) <= 183
                   && (video.Profile == null || HevcProfiles.Contains(video.Profile, StringComparer.OrdinalIgnoreCase))
                   && (video.VideoRangeType == null ||
                       SupportedVideoRangeTypes(hdrOnAnyDisplay).Contains(video.VideoRangeType.ToString(), StringComparer.OrdinalIgnoreCase))
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

        private static List<DirectPlayProfile> GetDirectPlayProfiles(bool hasHevc, bool hasVp9, bool quickTime)
        {
            // What played on an Xbox Series X when every test clip was sent to it as it is
            // (2026-10-03, Docs/CODEC_TESTING.md). Microsoft's tables, for comparison:
            // https://learn.microsoft.com/en-us/windows/uwp/apps-for-xbox/supported-technologies
            // https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/supported-codecs
            // A codec belongs here only in a container it was heard and seen in. The console
            // opens most things it cannot decode and plays them silent or black with no
            // failure to retry on: DTS (DTS-HD too), TrueHD and Vorbis, PCM in MKV, Opus in
            // MP4, VC-1 in MKV and TS (it plays in MP4 and WMV), H.264 and HEVC in an MPEG
            // program stream, AV1, VVC, ProRes, VP9 4:4:4. AC3 in WMV stutters.
            // Two are worse and must stay out: MPEG-4 Part 2 with B-frames crashes the console's
            // video driver two seconds in (in AVI it stutters), and 12-bit HEVC crashes its HEVC
            // decoder.
            //
            // Every profile states its Type. The server only considers Video-type profiles for
            // video and Audio-type for music, and an omitted Type deserializes as the enum's
            // first value, Audio -- so untyped video profiles are silently ignored and every
            // video comes back as DirectPlayError.
            const string H264 = "h264,avc1,avc3,";
            var hevc = hasHevc ? string.Join(",", HevcCodecs) + "," : string.Empty;
            var vp9 = hasVp9 ? "vp9,vp90,vp09," : string.Empty;
            const string Vc1 = "vc1,wvc1,wmv3,";
            const string Mpeg4 = "mpeg4,mp4v,";
            const string Aac = "aac,mp4a,";
            const string Ac3 = "ac3,ac-3,";
            const string Eac3 = "eac3,ec-3,";
            const string Amr = "amrnb,amr_nb,";
            const string Wma = "wma,wmap,wmav1,wmav2,wmapro,wmavoice,";
            const string Lpcm = "pcm,lpcm,pcm_s16le,pcm_s24le,pcm_s32le,pcm_u8,pcm_f32le,";
            // What differs between the two structures of the MP4 family (IsQuickTime)
            var byStructure = quickTime ? Lpcm + "pcm_s16be,alac," : Ac3 + Eac3;

            return new List<DirectPlayProfile>
            {
                // Opus only in stereo, MPEG-4 Part 2 only as Simple Profile (GetCodecProfiles)
                Video("mp4,m4v,mov,fmp4,3gp", H264 + hevc + vp9 + Vc1 + Mpeg4 + "h263",
                    Aac + byStructure + Amr + "mp3,flac"),
                Video("mkv,webm,matroska", H264 + hevc + vp9 + Mpeg4 + "vp8,mpeg2video",
                    Aac + Ac3 + Eac3 + "mp3,mp2,flac,alac,opus"),
                Video("avi", H264 + Mpeg4 + "msmpeg4v2,msmpeg4v3", Aac + Ac3 + Lpcm + "mp3"),
                Video("asf,wmv", Vc1 + "wmv1,wmv2", Wma),
                Video("ts,m2ts,mts", H264 + hevc + "mpeg2video,mpeg1video",
                    Aac + Ac3 + Eac3 + "mp3,mp2,pcm_bluray"),
                Video("mpg,mpeg", "mpeg2video,mpeg1video", Ac3 + "mp2"),
                Audio("mp3,mp2", "mp3,mp2"),
                Audio("aac", Aac),
                Audio("m4a,m4b", Aac + Ac3 + Amr + "alac,mp3,flac"),
                Audio("asf,wma", Wma + "wmalossless"),
                Audio("ac3", Ac3),
                Audio("eac3", Eac3),
                Audio("amr,3gp", Amr),
                Audio("flac", "flac"),
                Audio("mka,mkv,matroska", "flac,opus"),
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
                AudioCodec = "aac,ac3,eac3",
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

        private static List<CodecProfile> GetCodecProfiles(bool portrait, string rangeTypes)
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
                        }.Concat(PictureLimits(portrait)).ToList()
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
                            Value = rangeTypes,
                            IsRequired = false
                        }
                    }.Concat(PictureLimits(portrait)).ToList()
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
                            // The server reports "Profile 0"; without the space this condition
                            // failed and every VP9 file was converted (server log, 2026-10-03)
                            Value = "Profile 0|Profile 2|Profile0|Profile2",
                            IsRequired = false
                        }
                    }.Concat(PictureLimits(portrait)).ToList()
                }
            };

            profiles.Add(new CodecProfile
            {
                Type = CodecProfile_Type.Video,
                Codec = "vc1,wvc1,wmv3,mpeg2video,mpeg1video,vp8,msmpeg4v2,msmpeg4v3,wmv1,wmv2,h263",
                Conditions = PictureLimits(portrait).ToList()
            });

            // MPEG-4 Part 2 plays as Simple Profile, which has no B-frames. With B-frames
            // (Advanced Simple, as most DivX and Xvid files are) it crashed the console's video
            // driver in MP4 and MKV and stuttered in AVI. Required: a file whose profile the
            // server does not know is converted too.
            profiles.Add(new CodecProfile
            {
                Type = CodecProfile_Type.Video,
                Codec = "mpeg4,mp4v",
                Conditions = new[]
                {
                    new ProfileCondition
                    {
                        Condition = ProfileCondition_Condition.EqualsAny,
                        Property = ProfileCondition_Property.VideoProfile,
                        Value = "simple profile",
                        IsRequired = true
                    }
                }.Concat(PictureLimits(portrait)).ToList()
            });

            // The console's AAC decoder stops at 5.1: a 7.1 AAC track direct plays with no
            // sound and no error (device: Iron Man 3, AAC LC 8 ch in MKV). Above 6 channels
            // the server re-encodes the audio and the video can still be copied.
            profiles.Add(AudioChannelLimit(CodecProfile_Type.VideoAudio, "aac", "6"));

            // As music a 7.1 AAC file is refused outright; asking the server for it spares the
            // failed first attempt.
            profiles.Add(AudioChannelLimit(CodecProfile_Type.Audio, "aac", "6"));

            // Opus above stereo is silent in the same way (5.1 and 7.1 tried).
            profiles.Add(AudioChannelLimit(CodecProfile_Type.VideoAudio, "opus", "2"));

            // 32-bit FLAC is silent in the same way (device: a 32-bit 192 kHz stereo track in
            // MKV, whose 16-bit track played). FLAC up to 24-bit played in video and as music.
            // 32-bit was never heard as music either, so it is asked of the server there too.
            profiles.Add(AudioBitDepthLimit(CodecProfile_Type.VideoAudio, "flac", "24"));
            profiles.Add(AudioBitDepthLimit(CodecProfile_Type.Audio, "flac", "24"));

            // E-AC3 plays at 7.1 in a video file. As music a 7.1 file is refused, like AAC.
            profiles.Add(AudioChannelLimit(CodecProfile_Type.Audio, "eac3", "6"));

            return profiles;
        }

        private static CodecProfile AudioBitDepthLimit(CodecProfile_Type type, string codec, string maxBits)
        {
            return new CodecProfile
            {
                Type = type,
                Codec = codec,
                Conditions = new List<ProfileCondition>
                {
                    LessThanEqual(ProfileCondition_Property.AudioBitDepth, maxBits)
                }
            };
        }

        private static CodecProfile AudioChannelLimit(CodecProfile_Type type, string codec, string maxChannels)
        {
            return new CodecProfile
            {
                Type = type,
                Codec = codec,
                Conditions = new List<ProfileCondition>
                {
                    LessThanEqual(ProfileCondition_Property.AudioChannels, maxChannels)
                }
            };
        }

        // The standard edition takes 1920x1080, which is where Microsoft puts an app without
        // the hevcPlayback capability; everything larger is the 4K edition's (owner decision,
        // 2026-10-03). The console itself went further with the limit lifted (Series X,
        // Docs/CODEC_TESTING.md): H.264, HEVC and VP9 played up to 2560x1440 at 60 fps with
        // seeks, and at 3840x2160 the standard edition ran out of graphics memory (VP9 crashed
        // on opening, HEVC at 60 fps on its first seek). A portrait video gets the same frame
        // on its side.
        //
        // The 4K edition has no size limit. Its hevcPlayback capability brings the memory
        // for 4K, and closes the app when a game starts, which rules out background music.
        private static IEnumerable<ProfileCondition> PictureLimits(bool portrait)
        {
            if (!XboxDevice.IsFourKEdition)
            {
                yield return LessThanEqual(ProfileCondition_Property.Width, (portrait ? Hd.Height : Hd.Width).ToString());
                yield return LessThanEqual(ProfileCondition_Property.Height, (portrait ? Hd.Width : Hd.Height).ToString());
            }

            yield return LessThanEqual(ProfileCondition_Property.VideoFramerate, "60");
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

        // The standard edition is offered no HDR display mode (UnifiedDeviceService): the
        // console shows every HDR file converted to standard range, whatever the display, so
        // there is nothing to gate and everything the console decodes is claimed. HDR10,
        // HDR10+, HLG and Dolby Vision over an HDR10 or HLG layer (profiles 8.1 and 8.4) all
        // played that way on a Series X (2026-10-03, Docs/CODEC_TESTING.md). HLG and Dolby
        // Vision need a Series console. Profile 8.1 that also carries HDR10+ is its own range
        // type to the server, which otherwise streams the file itself with the HDR10+ removed.
        //
        // The 4K edition can put the display in HDR. With HDR on Any Display (the default) it
        // claims the same; with the setting off, a format only when the console offers an
        // HDR mode for it. HLG has no mode of its own and rides on HDR10.
        //
        // Dolby Vision alone (profile 5) is claimed too, though in that test it failed to
        // decode: the display had no Dolby Vision. PlaybackControlService retries it with
        // dolbyVisionAloneFailed.
        //
        // The range types go in one list rather than a separate HEVC codec profile: Jellyfin
        // applies every codec profile for a codec and all of their conditions must pass, so a
        // Dolby Vision-only HEVC profile disqualified every other HEVC file.
        private List<string> SupportedVideoRangeTypes(bool hdrOnAnyDisplay)
        {
            hdrOnAnyDisplay = hdrOnAnyDisplay || !XboxDevice.IsFourKEdition;
            var series = _deviceService.IsXboxSeriesConsole;
            var hdr10 = hdrOnAnyDisplay || _deviceService.SupportsHDR10;
            var dolbyVision = series && (hdrOnAnyDisplay || _deviceService.DisplaySupportsDolbyVision);
            var supportedTypes = new List<string> { "SDR" };

            if (hdr10)
            {
                supportedTypes.Add("HDR10");
            }

            if (hdrOnAnyDisplay || _deviceService.SupportsHDR10Plus)
            {
                supportedTypes.Add("HDR10Plus");
            }

            if (series && hdr10)
            {
                supportedTypes.Add("HLG");
            }

            if (dolbyVision)
            {
                supportedTypes.Add(DolbyVisionAlone);
            }

            if (series && (hdr10 || dolbyVision))
            {
                supportedTypes.Add("DOVIWithHDR10");
                supportedTypes.Add("DOVIWithHDR10Plus");
                supportedTypes.Add("DOVIWithHLG");
            }

            if (series)
            {
                supportedTypes.Add("DOVIWithSDR");
            }

            return supportedTypes;
        }
    }
}
