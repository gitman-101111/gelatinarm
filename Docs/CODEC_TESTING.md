# Codec testing

Shows what the console decodes: each clip is sent to it unconverted and a person
checks picture and sound. The device profile (`Playback/DeviceProfileService.cs`)
and the README format tables follow the results.

## Clips

`Docs/codec-test-media/generate.sh` builds 267 clips, most of 30 seconds (about 5.5 GB),
into `videos/` and `music/`, with `manifest.tsv` and a `RESULTS.md` sheet to fill in.

```sh
FFMPEG=/path/to/ffmpeg Docs/codec-test-media/generate.sh ~/gelatinarm-test-media
ONLY='^(Z|M5)' Docs/codec-test-media/generate.sh ~/gelatinarm-test-media   # part of the set
```

Needs a full ffmpeg build (`nix build nixpkgs#ffmpeg-full`). Optional, and the clips
that need them are skipped without them: `dovi_tool` and `mkvmerge` (`D`), `curl`,
`fdkaac` and `hdr10plus_tool` (`X`, HE-AAC in `M`).

| ID | Varies | Covers |
|---|---|---|
| `V` | Video codec | H.264 (Baseline, Main, High; 10-bit, 4:2:2, 4:4:4; interlaced, anamorphic, portrait; level 5.1; 60 Mbps; 23.976 to 60 fps), HEVC (Main, Main 10, 12-bit, 4:2:2, 4:4:4; HDR10, HLG), VP9 (Profile 0, 1, 2; HDR10), VP8, AV1, VVC, MPEG-1, MPEG-2, MPEG-4 Part 2 (Simple and Advanced Simple, with and without B-frames), H.263, MS-MPEG4 v2 and v3, WMV1, WMV2, Theora, Sorenson, DV, ProRes, Motion JPEG |
| `C` | Container | MP4, M4V, MOV, fragmented MP4, MP4 with its index at the end, 3GP, MKV, WebM, TS, M2TS, MTS, MPG, VOB, AVI, ASF |
| `A` | Audio in a video file | AAC, AC3, E-AC3, DTS, TrueHD, FLAC, ALAC, Opus, Vorbis, MP3, MP2, PCM, Blu-ray and DVD LPCM, WMA; mono to 7.1; in MKV, MP4, MOV, TS, M2TS, AVI; several tracks in one file; QuickTime against MP4 structure |
| `Z` | Picture size | 1920x1200 to 3840x2160 in H.264, HEVC and VP9; 1440p portrait and at 60 fps; 4K HDR10 at 30 and 60 fps |
| `D` | Dolby Vision | Profile 8.1 and profile 5, in MKV and MP4 |
| `M` | Music | MP3, MP2, AAC, HE-AAC, ALAC, FLAC, WAV (PCM, float, ADPCM, A-law, mu-law, GSM), WMA, WMA Pro, WMA Lossless, AC3, E-AC3, DTS, Opus, Vorbis, AMR, AIFF, WavPack, True Audio, APE; mono to 7.1; up to 24-bit 192 kHz; embedded covers of 1000 to 6000 px and up to 9 MB |
| `X` | Formats ffmpeg cannot encode, from public sample collections | VC-1 (Advanced in WMV, MKV, MP4, TS; Main in WMV), E-AC3 7.1, E-AC3 with Atmos, DTS-HD MA and HRA, TrueHD, HE-AAC v1 and v2, WMA Pro, Dolby Vision profile 5 and 8.4, HDR10+ |

- **Picture**: a moving test pattern with the clip's ID, video format and audio
  format burned in, and a running clock.
- **Sound**: one beep per channel per second, in channel order, each at its own
  pitch. Channel 4 of a 5.1 or 7.1 clip is the LFE, a 60 Hz hum.
- The profile 5 clips are an ordinary HDR picture flagged as profile 5.
- `X` clips built around a downloaded video have no burned-in label, and some are
  shorter than 30 seconds.

## Running a pass

1. **Libraries.** `videos/` in a Movies library, `music/` in a Music library. Add
   the videos to one collection: Play on a collection queues it, starting at the
   first unwatched clip.
2. **Profile.** In `DeviceProfileService`, send one `Video` and one `Audio`
   direct-play entry with no container or codec named, and no codec profiles, when
   the request allows direct play. Keep the picture-size conditions unless the `Z`
   clips are under test. Not for commit: with this profile, files the console
   cannot decode play silent or black.
3. **Build.** Debug, debugger attached: the app logs to the debug output only, and
   below Warning only in Debug builds. Save the output after each sitting.
4. **Play.** About ten seconds a clip; a 7.1 clip needs eight for every channel.
   Seek back in each `Z` clip. Note silence, a missing channel, a black or green
   picture, stutter, a freeze or a crash.
5. **After a crash.** Mark the clip watched and press Play on the collection again.

A queue passes the chosen audio track index to the next item: play multi-track
clips (`A23`, `A48`, `A49`) by themselves.

## Reading the logs

Client (debug output):

| Line | Meaning |
|---|---|
| `[PLAYBACK-INFO] ...` then `Direct Play is available` | The server sends the file as it is |
| `Opening HLS stream ... TranscodeReasons=` | A server stream, with the server's reasons |
| `[MEDIA-FAILED] Error: ..., HResult: ...` | The console refused the file |
| `Direct play failed for X; retrying as a server stream` | The app's fallback started |
| `Player audio tracks at open: 1, selected 0: FullySupported` | The player's view of each audio track. It does not predict silence |
| `=== Media playback failed: SourceNotSupported` | Music: the console refused the file |
| `Unhandled exception ... (hevcdecoder.dll)`, `Video UMD error` | The console's decoder or video driver failed |
| `CVideoDecoderContext::ModifyDPB ... newSize=` | Bytes the console reserves for the decoder's frames |

Server log:

| Line | Meaning |
|---|---|
| `User "x" started playback of "X"` / `stopped playback ... at "N"ms` | Start and stop of each clip, with the stop position |
| `TranscodeManager: ... ffmpeg ... -i file:"..."` | The server made a stream. `-codec:v:0 copy` is a remux, an encoder name a transcode. No such line between a start and a stop: direct play |
| `FFmpeg exited with code N` right after a job start | The server's conversion failed |
