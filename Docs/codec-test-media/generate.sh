#!/usr/bin/env bash
# Generates the codec test clips for Gelatinarm (Docs/CODEC_TESTING.md), plus RESULTS.md to
# record what the console did with each one.
#
#   FFMPEG=/path/to/ffmpeg ./generate.sh [output-dir]
#   ONLY='^(V12|M1[5-7])$' ./generate.sh      regenerate just those IDs
#   ONLY='^(Z|M5)' ./generate.sh               every ID starting with Z or M5
#
# Dolby Vision clips (D01-D04) also need dovi_tool and mkvmerge on PATH, or DOVI_TOOL and
# MKVMERGE set; without them those four are skipped.
#
# Each video carries its ID and stream description burned into the picture and a running
# timestamp (seek check). Audio beeps one channel at a time, in channel order, one per
# second; LFE is a 60 Hz hum. A silent or missing channel is therefore audible.
set -uo pipefail

FFMPEG=${FFMPEG:-ffmpeg}
OUT=$(realpath "${1:-$(dirname "$0")}")
DUR=${DUR:-30}
JOBS=${JOBS:-6}
FONT=${FONT:-$(fc-match -f '%{file}' 'DejaVu Sans:bold')}

VID="$OUT/videos"
MUS="$OUT/music/Gelatinarm Test/Codec Tests"
LOGS="$OUT/logs"
mkdir -p "$VID" "$MUS" "$LOGS"
: > "$OUT/manifest.tsv"
: > "$LOGS/failed"

DP="Direct play"
RX="Remux (video copied, audio converted)"
TV="Transcode (video re-encoded)"

channels() {
    case $1 in
        mono) echo 1 ;; stereo) echo 2 ;; "5.1" | "5.1(side)") echo 6 ;; "7.1" | "7.1(wide)") echo 8 ;;
        *) echo "unknown layout $1" >&2; exit 1 ;;
    esac
}

# One beep per channel in turn; channel 4 of 6 or 8 is the LFE.
asrc() {
    local layout=$1 rate=${2:-48000} n i exprs=""
    n=$(channels "$layout")
    local freqs=(440 554 659 60 784 880 988 1047)
    ((n <= 3)) && freqs=(440 554 659)
    for ((i = 0; i < n; i++)); do
        exprs+="${exprs:+|}0.4*sin(2*PI*${freqs[i]}*t)*lt(mod(t+$n-$i,$n),0.6)"
    done
    echo "aevalsrc=exprs='$exprs':channel_layout=$layout:sample_rate=$rate:duration=$DUR"
}

vsrc() {
    local size=$1 fps=$2 line1=$3 line2=$4
    local dt="drawtext=fontfile=$FONT:fontcolor=white:box=1:boxcolor=black@0.75:boxborderw=h/90:fontsize=h/20:x=(w-tw)/2"
    echo "testsrc2=size=$size:rate=$fps:duration=$DUR,$dt:text='$line1':y=h*0.70,$dt:text='$line2':y=h*0.80${VFX:+,$VFX}"
}

clean() { tr -d "':,%\\\\" <<< "$1"; }

throttle() { while (($(jobs -rp | wc -l) >= JOBS)); do wait -n; done; }

run() {
    local id=$1 path=$2; shift 2
    [[ -n ${ONLY:-} && ! $id =~ $ONLY ]] && return 0
    if "$FFMPEG" -hide_banner -loglevel error -nostdin -y "$@" "$path" > "$LOGS/$id.log" 2>&1; then
        echo "ok    $id  $(basename "$path")"
    else
        echo "FAIL  $id  $(basename "$path")  (see logs/$id.log)"
        echo "$id" >> "$LOGS/failed"
    fi
}

# mk ID FILE SIZE FPS LAYOUT VIDEO-DESC AUDIO-DESC EXPECTED NOTE [ffmpeg output args...]
# VFX=<filter> appends a filter to the video source (pixel conversion, interlacing).
mk() {
    local id=$1 file=$2 size=$3 fps=$4 layout=$5 vdesc=$6 adesc=$7 expected=$8 note=$9; shift 9
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$file" "$vdesc" "$adesc" "$expected" "$note" >> "$OUT/manifest.tsv"
    throttle
    run "$id" "$VID/$file" \
        -f lavfi -i "$(vsrc "$size" "$fps" "$id  $(clean "$vdesc")" "$(clean "$adesc")  in  ${file##*.}")" \
        -f lavfi -i "$(asrc "$layout")" \
        -metadata title="${file%.*}" "$@" &
}

# mka ID FILE LAYOUT RATE AUDIO-DESC EXPECTED NOTE [ffmpeg args...]
mka() {
    local id=$1 file=$2 layout=$3 rate=$4 adesc=$5 expected=$6 note=$7; shift 7
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$file" "-" "$adesc" "$expected" "$note" >> "$OUT/manifest.tsv"
    throttle
    run "$id" "$MUS/$file" -f lavfi -i "$(asrc "$layout" "$rate")" "$@" \
        -metadata title="$id $adesc" -metadata artist="Gelatinarm Test" -metadata album_artist="Gelatinarm Test" \
        -metadata album="Codec Tests" -metadata track="$((10#${id#M}))" &
}

# ---- encoder settings -------------------------------------------------------------------
X264="-c:v libx264 -preset veryfast -crf 23 -g 60"
X265="-c:v libx265 -preset veryfast -crf 24 -g 60"
X265P="log-level=error:repeat-headers=1"
VP9="-c:v libvpx-vp9 -b:v 0 -crf 34 -deadline good -cpu-used 5 -row-mt 1 -g 60"
MPEG2="-c:v mpeg2video -g 15 -bf 2"
H264HI="$X264 -profile:v high -pix_fmt yuv420p"
AAC2="-c:a aac -b:a 128k"
AC3_2="-c:a ac3 -b:a 192k"
AC3_6="-c:a ac3 -b:a 448k"

HD=1920x1080 UHD=3840x2160 HD720=1280x720
BT2020="zscale=tin=bt709:pin=bt709:min=bt709:rin=limited:p=bt2020:m=bt2020nc:r=limited"
PQ="$BT2020:t=smpte2084:npl=203,format=yuv420p10le"
HLG="$BT2020:t=arib-std-b67,format=yuv420p10le"
PQ_FLAGS="-pix_fmt yuv420p10le -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc"
HLG_FLAGS="-pix_fmt yuv420p10le -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc"
HDR10P="$X265P:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16355)L(10000000,50):max-cll=1000,400"
HLGP="$X265P:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc"

NOTE_HEVC="One S and later; original Xbox One transcodes to H.264"
NOTE_4K="4K edition only; the standard edition transcodes to 1080p"
NOTE_VP9="One X and Series (hardware check); otherwise transcode"
NOTE_HDR="Only if the display reports this range; otherwise tone-mapped transcode"
NOISE="noise=alls=30:allf=t+u"
AMR="-c:a libopencore_amrnb -ar 8000 -ac 1 -b:a 12.2k"
MP3="-c:a libmp3lame -b:a 160k"
ASP="-c:v mpeg4 -q:v 4 -g 60"
NOTE_SIZE="Above 1080p: transcoded in the standard edition"
NOTE_ASP="MPEG-4 Part 2 crashed the console in MP4 and MKV with B-frames"

# ---- V: video codecs (MKV + AAC stereo unless the row says otherwise) --------------------
mk V01 V01_h264-baseline_1080p30.mkv $HD 30 stereo "H.264 Baseline 8-bit 1080p30" "AAC 2.0" "$DP" "" \
    $X264 -profile:v baseline -pix_fmt yuv420p $AAC2
mk V02 V02_h264-main_1080p30.mkv $HD 30 stereo "H.264 Main 8-bit 1080p30" "AAC 2.0" "$DP" "" \
    $X264 -profile:v main -pix_fmt yuv420p $AAC2
mk V03 V03_h264-high_1080p30.mkv $HD 30 stereo "H.264 High 8-bit 1080p30" "AAC 2.0" "$DP" "Baseline case" \
    $H264HI $AAC2
mk V04 V04_h264-high_1080p60.mkv $HD 60 stereo "H.264 High 8-bit 1080p60" "AAC 2.0" "$DP" "" \
    $X264 -g 120 -profile:v high -pix_fmt yuv420p $AAC2
mk V05 V05_h264-high10_1080p30.mkv $HD 30 stereo "H.264 High 10 10-bit 1080p30" "AAC 2.0" "$TV" "Bit depth above 8" \
    $X264 -profile:v high10 -pix_fmt yuv420p10le $AAC2
mk V06 V06_h264-high444_1080p30.mkv $HD 30 stereo "H.264 High 4:4:4 8-bit 1080p30" "AAC 2.0" "$TV" "Profile not in the allowed list" \
    $X264 -profile:v high444 -pix_fmt yuv444p $AAC2
mk V07 V07_h264-high_2160p30.mkv $UHD 30 stereo "H.264 High 8-bit 2160p30" "AAC 2.0" "$TV" "H.264 is capped at 1080p in both editions" \
    $H264HI $AAC2
VFX=interlace mk V08 V08_h264-high_1080i30.ts $HD 60 stereo "H.264 High 8-bit 1080i30 (interlaced)" "AC3 2.0" "$DP" "Profile has no interlace condition; watch for combing" \
    $H264HI -flags +ilme+ildct -x264-params tff=1 $AC3_2

mk V10 V10_hevc-main_1080p30.mkv $HD 30 stereo "HEVC Main 8-bit 1080p30" "AAC 2.0" "$DP" "$NOTE_HEVC" \
    $X265 -pix_fmt yuv420p -x265-params "$X265P" $AAC2
mk V11 V11_hevc-main10_1080p30_sdr.mkv $HD 30 stereo "HEVC Main 10 SDR 1080p30" "AAC 2.0" "$DP" "$NOTE_HEVC" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" $AAC2
VFX=$PQ mk V12 V12_hevc-main10_1080p30_hdr10.mkv $HD 30 stereo "HEVC Main 10 HDR10 1080p30" "AAC 2.0" "$DP" "$NOTE_HDR" \
    $X265 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
VFX=$HLG mk V13 V13_hevc-main10_1080p30_hlg.mkv $HD 30 stereo "HEVC Main 10 HLG 1080p30" "AAC 2.0" "$DP" "$NOTE_HDR" \
    $X265 $HLG_FLAGS -x265-params "$HLGP" $AAC2
VFX=$PQ mk V15 V15_hevc-main10_2160p30_hdr10.mkv $UHD 30 stereo "HEVC Main 10 HDR10 2160p30" "AAC 2.0" "$DP" "$NOTE_4K. $NOTE_HDR" \
    $X265 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk V16 V16_hevc-main_2160p30_sdr.mkv $UHD 30 stereo "HEVC Main 8-bit SDR 2160p30" "AAC 2.0" "$DP" "$NOTE_4K" \
    $X265 -pix_fmt yuv420p -x265-params "$X265P" $AAC2
mk V17 V17_hevc-422-10bit_1080p30.mkv $HD 30 stereo "HEVC Rext 4:2:2 10-bit 1080p30" "AAC 2.0" "$TV" "Profile not Main or Main 10" \
    $X265 -pix_fmt yuv422p10le -x265-params "$X265P" $AAC2

mk V20 V20_vp9-profile0_1080p30.mkv $HD 30 stereo "VP9 Profile 0 8-bit 1080p30" "AAC 2.0" "$DP" "$NOTE_VP9" \
    $VP9 -pix_fmt yuv420p $AAC2
mk V21 V21_vp9-profile2_1080p30.mkv $HD 30 stereo "VP9 Profile 2 10-bit 1080p30" "AAC 2.0" "$DP" "$NOTE_VP9" \
    $VP9 -pix_fmt yuv420p10le -profile:v 2 $AAC2
mk V22 V22_vp9-profile0_2160p30.mkv $UHD 30 stereo "VP9 Profile 0 8-bit 2160p30" "AAC 2.0" "$DP" "$NOTE_4K. $NOTE_VP9" \
    $VP9 -deadline realtime -cpu-used 8 -pix_fmt yuv420p $AAC2
mk V23 V23_vp8_1080p30.webm $HD 30 stereo "VP8 1080p30" "Vorbis 2.0" "$TV" "VP8 is not in the profile" \
    -c:v libvpx -b:v 3M -deadline good -cpu-used 4 -g 60 -pix_fmt yuv420p -c:a libvorbis -q:a 4
mk V24 V24_av1_1080p30.mkv $HD 30 stereo "AV1 Main 8-bit 1080p30" "AAC 2.0" "$TV" "AV1 is not in the profile" \
    -c:v libsvtav1 -preset 10 -crf 35 -g 60 -pix_fmt yuv420p $AAC2

mk V30 V30_mpeg2_1080p30.mkv $HD 30 stereo "MPEG-2 Main 1080p30" "AC3 2.0" "$DP" "" \
    $MPEG2 -b:v 8M -pix_fmt yuv420p $AC3_2
VFX=interlace mk V31 V31_mpeg2_576i25_dvd.mpg 720x576 50 stereo "MPEG-2 576i25 (DVD-like)" "AC3 2.0" "$DP" "Watch for combing" \
    $MPEG2 -b:v 5M -flags +ilme+ildct -aspect 16:9 -pix_fmt yuv420p $AC3_2 -f vob
mk V32 V32_mpeg1_480p30.mpg 720x480 30 stereo "MPEG-1 480p30" "MP2 2.0" "$TV" "README says MPEG-1 direct plays, but no direct-play profile lists mpeg1video" \
    -c:v mpeg1video -b:v 3M -g 15 -pix_fmt yuv420p -c:a mp2 -b:a 192k
mk V33 V33_mpeg4-asp_720p30.mkv $HD720 30 stereo "MPEG-4 Part 2 ASP 720p30" "MP3 2.0" "$DP" "" \
    -c:v mpeg4 -bf 2 -q:v 4 -g 60 -c:a libmp3lame -b:a 160k
mk V34 V34_mpeg4-xvid_720p30.avi $HD720 30 stereo "MPEG-4 Part 2 (XVID tag) 720p30" "MP3 2.0" "$DP" "" \
    -c:v mpeg4 -vtag XVID -bf 2 -q:v 4 -g 60 -c:a libmp3lame -b:a 160k
mk V35 V35_msmpeg4v3_720p30.avi $HD720 30 stereo "MS-MPEG4 v3 (DivX 3) 720p30" "MP3 2.0" "$TV" "msmpeg4v3 is not in the profile" \
    -c:v msmpeg4 -q:v 4 -g 60 -c:a libmp3lame -b:a 160k
mk V36 V36_wmv1_720p30.wmv $HD720 30 stereo "WMV1 (WMV 7) 720p30" "WMA v2 2.0" "$DP" "" \
    -c:v wmv1 -q:v 4 -g 60 -c:a wmav2 -b:a 128k
mk V37 V37_wmv2_720p30.wmv $HD720 30 stereo "WMV2 (WMV 8) 720p30" "WMA v2 2.0" "$TV" "wmv2 is not in the profile" \
    -c:v wmv2 -q:v 4 -g 60 -c:a wmav2 -b:a 128k
mk V38 V38_mjpeg_360p30.mkv 640x360 30 stereo "Motion JPEG 360p30" "AAC 2.0" "$TV" "mjpeg is not in the profile" \
    -c:v mjpeg -q:v 6 -pix_fmt yuvj420p $AAC2

# ---- V: frame rates and picture shapes (H.264 High, MKV, AAC stereo) ----
mk V40 V40_h264_1080p23.976.mkv $HD 24000/1001 stereo "H.264 High 1080p23.976" "AAC 2.0" "$DP" "" $H264HI $AAC2
mk V41 V41_h264_1080p25.mkv $HD 25 stereo "H.264 High 1080p25" "AAC 2.0" "$DP" "" $H264HI $AAC2
mk V42 V42_h264_1080p50.mkv $HD 50 stereo "H.264 High 1080p50" "AAC 2.0" "$DP" "" $H264HI -g 100 $AAC2
mk V43 V43_h264_1080p59.94.mkv $HD 60000/1001 stereo "H.264 High 1080p59.94" "AAC 2.0" "$DP" "" $H264HI -g 120 $AAC2
mk V44 V44_h264_1440p30.mkv 2560x1440 30 stereo "H.264 High 1440p30" "AAC 2.0" "$TV" "$NOTE_SIZE" $H264HI $AAC2
mk V45 V45_h264_vertical-1080x1920.mkv 1080x1920 30 stereo "H.264 High 1080x1920 (vertical)" "AAC 2.0" "$TV" "Taller than 1080: transcoded in the standard edition" \
    $H264HI $AAC2
mk V46 V46_h264_anamorphic-576p.mkv 720x576 25 stereo "H.264 High 720x576 anamorphic 16:9" "AAC 2.0" "$DP" "The picture must fill a 16:9 frame, not 5:4" \
    $H264HI -aspect 16:9 $AAC2
mk V47 V47_h264_level51-16refs.mkv $HD 30 stereo "H.264 High level 5.1, 16 reference frames" "AAC 2.0" "$DP" "" \
    $H264HI -refs 16 -level 5.1 $AAC2
VFX=$NOISE mk V48 V48_h264_1080p30_60mbps.mkv $HD 30 stereo "H.264 High 1080p30 at 60 Mbps" "AAC 2.0" "$DP" "Bitrate test; the picture is noisy on purpose" \
    -c:v libx264 -preset veryfast -b:v 60M -maxrate 60M -bufsize 120M -g 60 -profile:v high -pix_fmt yuv420p $AAC2
mk V49 V49_h264-high422_1080p30.mkv $HD 30 stereo "H.264 High 4:2:2 8-bit 1080p30" "AAC 2.0" "$TV" "Profile not in the allowed list" \
    $X264 -profile:v high422 -pix_fmt yuv422p $AAC2

# ---- V: HEVC, VP9 and AV1 variants ----
mk V50 V50_hevc-main_1080p60.mkv $HD 60 stereo "HEVC Main 8-bit 1080p60" "AAC 2.0" "$DP" "$NOTE_HEVC" \
    $X265 -g 120 -pix_fmt yuv420p -x265-params "$X265P" $AAC2
mk V51 V51_hevc-main12_1080p30.mkv $HD 30 stereo "HEVC Main 12 (12-bit) 1080p30" "AAC 2.0" "$TV" "Bit depth above 10" \
    $X265 -pix_fmt yuv420p12le -x265-params "$X265P" $AAC2
mk V52 V52_hevc-444_1080p30.mkv $HD 30 stereo "HEVC Rext 4:4:4 8-bit 1080p30" "AAC 2.0" "$TV" "Profile not Main or Main 10" \
    $X265 -pix_fmt yuv444p -x265-params "$X265P" $AAC2
VFX=$NOISE mk V53 V53_hevc-main10_1080p30_50mbps.mkv $HD 30 stereo "HEVC Main 10 1080p30 at 50 Mbps" "AAC 2.0" "$DP" "Bitrate test; the picture is noisy on purpose" \
    -c:v libx265 -preset veryfast -b:v 50M -g 60 -pix_fmt yuv420p10le -x265-params "$X265P:vbv-maxrate=50000:vbv-bufsize=100000" $AAC2
VFX=$PQ mk V54 V54_vp9-profile2_1080p30_hdr10.mkv $HD 30 stereo "VP9 Profile 2 HDR10 1080p30" "AAC 2.0" "$DP" "The profile has no range rule for VP9" \
    $VP9 -profile:v 2 $PQ_FLAGS $AAC2
mk V55 V55_vp9-profile1-444_1080p30.mkv $HD 30 stereo "VP9 Profile 1 4:4:4 8-bit 1080p30" "AAC 2.0" "$TV" "Profile not 0 or 2" \
    $VP9 -pix_fmt yuv444p -profile:v 1 $AAC2
mk V56 V56_vp9-profile0_1080p60.mkv $HD 60 stereo "VP9 Profile 0 8-bit 1080p60" "AAC 2.0" "$DP" "$NOTE_VP9" \
    $VP9 -g 120 -pix_fmt yuv420p $AAC2
mk V57 V57_av1_1080p30.mp4 $HD 30 stereo "AV1 Main 8-bit 1080p30" "AAC 2.0" "$TV" "AV1 showed a black picture in MKV" \
    -c:v libsvtav1 -preset 10 -crf 35 -g 60 -pix_fmt yuv420p $AAC2 -movflags +faststart
mk V58 V58_av1-10bit_1080p30.mkv $HD 30 stereo "AV1 Main 10-bit 1080p30" "AAC 2.0" "$TV" "" \
    -c:v libsvtav1 -preset 10 -crf 35 -g 60 -pix_fmt yuv420p10le $AAC2

# ---- V: MPEG-4 Part 2 again, to find what the crash depends on ----
mk V59 V59_mpeg4-sp_480p30.mp4 640x480 30 stereo "MPEG-4 Part 2 Simple Profile 480p30, no B-frames" "AAC 2.0" "$DP" "Simple Profile played; with B-frames the console crashed" \
    $ASP -bf 0 $AAC2 -movflags +faststart
mk V60 V60_xvid-nobframes_720p30.avi $HD720 30 stereo "MPEG-4 Part 2 (Xvid) 720p30, no B-frames" "MP3 2.0" "$DP" "With B-frames in AVI it stuttered" \
    -c:v libxvid -q:v 4 -g 60 -bf 0 $MP3
mk V61 V61_mpeg4-nobframes_720p30.mkv $HD720 30 stereo "MPEG-4 Part 2 720p30, no B-frames" "MP3 2.0" "$DP" "Simple Profile played; with B-frames the console crashed" \
    $ASP -bf 0 $MP3

# ---- V: other codecs ----
mk V62 V62_h263_cif.3gp 352x288 30 stereo "H.263 352x288" "AMR-NB mono" "$TV" "" -c:v h263 -q:v 5 -g 60 $AMR
mk V63 V63_h264_amr.3gp 640x360 30 stereo "H.264 Baseline 360p30" "AMR-NB mono" "$DP" "In the profile from Microsoft's tables, never played" \
    $X264 -profile:v baseline -pix_fmt yuv420p $AMR
mk V64 V64_msmpeg4v2_720p30.avi $HD720 30 stereo "MS-MPEG4 v2 720p30" "MP3 2.0" "$TV" "" -c:v msmpeg4v2 -q:v 4 -g 60 $MP3
mk V65 V65_theora_720p30.ogv $HD720 30 stereo "Theora 720p30" "Vorbis 2.0" "$TV" "" -c:v libtheora -q:v 6 -g 60 -c:a libvorbis -q:a 4
mk V66 V66_flv1_360p30.flv 640x360 30 stereo "Sorenson H.263 (FLV1) 360p30" "MP3 2.0" "$TV" "" -c:v flv -q:v 5 -g 60 $MP3 -ar 44100
mk V67 V67_h264_aac.flv $HD720 30 stereo "H.264 High 720p30" "AAC 2.0" "$RX" "FLV is not a direct-play container" $H264HI $AAC2
mk V68 V68_dv_576i25.avi 720x576 25 stereo "DV (PAL) 576i25" "PCM 16-bit 2.0" "$TV" "" -c:v dvvideo -pix_fmt yuv420p -c:a pcm_s16le
mk V69 V69_prores_720p30.mov $HD720 30 stereo "ProRes 422 Proxy 720p30" "PCM 16-bit 2.0" "$TV" "" \
    -c:v prores_ks -profile:v 0 -pix_fmt yuv422p10le -c:a pcm_s16le
mk V70 V70_vvc_720p30.mp4 $HD720 30 stereo "VVC (H.266) 10-bit 720p30" "AAC 2.0" "$TV" "" \
    -c:v libvvenc -preset faster -qp 34 -pix_fmt yuv420p10le $AAC2 -movflags +faststart
VFX=interlace mk V71 V71_mpeg2_1080i30.ts $HD 60 stereo "MPEG-2 1080i30 (broadcast-like)" "AC3 2.0" "$DP" "Watch for combing" \
    $MPEG2 -b:v 12M -flags +ilme+ildct -pix_fmt yuv420p $AC3_2
mk V72 V72_vp8_720p30.mkv $HD720 30 stereo "VP8 720p30" "AAC 2.0" "$DP" "VP8 played in WebM" \
    -c:v libvpx -b:v 2M -deadline good -cpu-used 4 -g 60 -pix_fmt yuv420p $AAC2
VFX=$PQ mk V73 V73_hevc-main10_2160p60_hdr10.mkv $UHD 60 stereo "HEVC Main 10 HDR10 2160p60" "AAC 2.0" "$DP" "$NOTE_4K" \
    $X265 -g 120 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk V80 V80_mpeg4-sp_1080p30.mp4 $HD 30 stereo "MPEG-4 Part 2 Simple Profile 1080p30, no B-frames" "AAC 2.0" "$DP" "" \
    $ASP -bf 0 $AAC2 -movflags +faststart
mk V81 V81_mpeg4-asp-qpel-nobframes_720p30.mkv $HD720 30 stereo "MPEG-4 Part 2 Advanced Simple (quarter-pel) 720p30, no B-frames" "MP3 2.0" "$TV" "Is it the B-frames or the profile that crashes?" \
    $ASP -bf 0 -flags +qpel $MP3
mk V82 V82_mpeg4-1bframe_480p30.mp4 640x480 30 stereo "MPEG-4 Part 2 480p30, one B-frame" "AAC 2.0" "$TV" "$NOTE_ASP" \
    $ASP -bf 1 $AAC2 -movflags +faststart
mk V83 V83_mpeg4-sp_amr.3gp 352x288 30 stereo "MPEG-4 Part 2 Simple Profile 352x288" "AMR-NB mono" "$DP" "The old phone format" \
    $ASP -bf 0 $AMR
mk V84 V84_mpeg1_480p30.ts 720x480 30 stereo "MPEG-1 480p30" "MP2 2.0" "$DP" "" \
    -c:v mpeg1video -b:v 3M -g 15 -pix_fmt yuv420p -c:a mp2 -b:a 192k
VFX=$PQ mk V87 V87_hevc-hdr10_1080p23.976.mkv $HD 24000/1001 stereo "HEVC Main 10 HDR10 1080p23.976" "AAC 2.0" "$DP" "HDR and a film frame rate together" \
    $X265 -g 48 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk V85 V85_h264_ac3.mpg $HD720 30 stereo "H.264 High 720p30 in an MPEG program stream" "AC3 2.0" "$DP" "" $H264HI $AC3_2 -f vob
mk V86 V86_hevc_ac3.mpg $HD720 30 stereo "HEVC Main 10 720p30 in an MPEG program stream" "AC3 2.0" "$DP" "" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" $AC3_2 -f vob

# ---- C: containers ----------------------------------------------------------------------
mk C01 C01_h264_aac.mp4 $HD 30 stereo "H.264 High 1080p30" "AAC 2.0" "$DP" "" $H264HI $AAC2 -movflags +faststart
mk C02 C02_h264_aac.m4v $HD 30 stereo "H.264 High 1080p30" "AAC 2.0" "$DP" "" $H264HI $AAC2 -f mp4 -movflags +faststart
mk C03 C03_h264_aac.mov $HD 30 stereo "H.264 High 1080p30" "AAC 2.0" "$DP" "" $H264HI $AAC2 -movflags +faststart
mk C04 C04_h264_ac3.ts $HD 30 stereo "H.264 High 1080p30" "AC3 2.0" "$DP" "" $H264HI $AC3_2
mk C05 C05_h264_aac.ts $HD 30 stereo "H.264 High 1080p30" "AAC 2.0" "$RX" "TS profile allows only AC3 and MP2 audio; AAC may be passed through with Audio Direct Stream on" \
    $H264HI $AAC2
mk C06 C06_h264_ac3.m2ts $HD 30 stereo "H.264 High 1080p30" "AC3 2.0" "$DP" "" $H264HI $AC3_2 -mpegts_m2ts_mode 1
mk C07 C07_mpeg2_ac3.ts $HD 30 "5.1" "MPEG-2 Main 1080p30" "AC3 5.1" "$DP" "" $MPEG2 -b:v 8M -pix_fmt yuv420p $AC3_6
mk C08 C08_mpeg2_mp2.mpeg 720x480 30 stereo "MPEG-2 Main 480p30" "MP2 2.0" "$DP" "" \
    $MPEG2 -b:v 4M -aspect 16:9 -pix_fmt yuv420p -c:a mp2 -b:a 192k
mk C09 C09_h264_mp3.avi $HD 30 stereo "H.264 High 1080p30" "MP3 2.0" "$DP" "" $H264HI -bf 0 -c:a libmp3lame -b:a 160k
mk C10 C10_h264_aac.avi $HD 30 stereo "H.264 High 1080p30" "AAC 2.0" "$RX" "AVI profile allows only AC3 and MP3 audio" \
    $H264HI -bf 0 $AAC2
mk C11 C11_hevc-hvc1_aac.mp4 $HD 30 stereo "HEVC Main 10 SDR 1080p30 (hvc1 tag)" "AAC 2.0" "$DP" "$NOTE_HEVC" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" -tag:v hvc1 $AAC2 -movflags +faststart
mk C12 C12_hevc-hev1_aac.mp4 $HD 30 stereo "HEVC Main 10 SDR 1080p30 (hev1 tag)" "AAC 2.0" "$DP" "$NOTE_HEVC. Media Foundation is known to be pickier about hev1" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" -tag:v hev1 $AAC2 -movflags +faststart
mk C13 C13_hevc_ac3.ts $HD 30 stereo "HEVC Main 10 SDR 1080p30" "AC3 2.0" "$RX" "TS profile does not list HEVC; AC3 may be passed through" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" $AC3_2
mk C14 C14_vp9_aac.mp4 $HD 30 stereo "VP9 Profile 0 1080p30" "AAC 2.0" "$DP" "$NOTE_VP9" $VP9 -pix_fmt yuv420p $AAC2 -movflags +faststart
mk C15 C15_vp9_opus.webm $HD 30 stereo "VP9 Profile 0 1080p30" "Opus 2.0" "$TV" "Opus is not allowed, and VP9 cannot be copied into the HLS fallback" \
    $VP9 -pix_fmt yuv420p -c:a libopus -b:a 128k
mk C16 C16_mpeg4-asp_aac.mp4 $HD720 30 stereo "MPEG-4 Part 2 ASP 720p30" "AAC 2.0" "$DP" "" -c:v mpeg4 -bf 2 -q:v 4 -g 60 $AAC2 -movflags +faststart

# ---- C: more containers ----
mk C20 C20_h264_aac_fragmented.mp4 $HD 30 stereo "H.264 High 1080p30, fragmented MP4" "AAC 2.0" "$DP" "" \
    $H264HI $AAC2 -movflags +frag_keyframe+empty_moov+default_base_moof
mk C21 C21_h264_aac_moov-at-end.mp4 $HD 30 stereo "H.264 High 1080p30, index at the end of the file" "AAC 2.0" "$DP" "Watch how long it takes to start" \
    $H264HI $AAC2
mk C22 C22_h264_ac3.mts $HD 30 stereo "H.264 High 1080p30 (AVCHD)" "AC3 2.0" "$DP" "" $H264HI $AC3_2 -f mpegts -mpegts_m2ts_mode 1
mk C23 C23_h264_aac.3gp $HD720 30 stereo "H.264 Baseline 720p30" "AAC 2.0" "$RX" "The 3GP entry allows AMR audio only" \
    $X264 -profile:v baseline -pix_fmt yuv420p $AAC2
VFX=$PQ mk C24 C24_hevc-hdr10_aac.mp4 $HD 30 stereo "HEVC Main 10 HDR10 1080p30 (hvc1)" "AAC 2.0" "$DP" "$NOTE_HDR" \
    $X265 $PQ_FLAGS -x265-params "$HDR10P" -tag:v hvc1 $AAC2 -movflags +faststart
VFX=$PQ mk C25 C25_hevc-hdr10_ac3.ts $HD 30 stereo "HEVC Main 10 HDR10 1080p30" "AC3 2.0" "$DP" "$NOTE_HDR" \
    $X265 $PQ_FLAGS -x265-params "$HDR10P" $AC3_2
mk C26 C26_wmv2_wma.asf $HD720 30 stereo "WMV2 720p30" "WMA v2 2.0" "$DP" "" -c:v wmv2 -q:v 4 -g 60 -c:a wmav2 -b:a 128k
mk C27 C27_mpeg2_ac3.vob 720x480 30 stereo "MPEG-2 480p30 (DVD VOB)" "AC3 2.0" "$DP" "" \
    $MPEG2 -b:v 5M -aspect 16:9 -pix_fmt yuv420p $AC3_2 -f vob
mk C28 C28_hevc_eac3.mkv $HD 30 "5.1" "HEVC Main 10 SDR 1080p30" "E-AC3 5.1" "$DP" "The usual streaming-service pairing" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" -c:a eac3 -b:a 384k

# ---- A: audio in video (H.264 High 720p30 video) -----------------------------------------
A264="$X264 -profile:v high -pix_fmt yuv420p"
AV="H.264 High 720p30"
mk A01 A01_aac-5.1.mkv $HD720 30 "5.1" "$AV" "AAC LC 5.1" "$DP" "" $A264 -c:a aac -b:a 384k
mk A02 A02_aac-7.1.mkv $HD720 30 "7.1" "$AV" "AAC LC 7.1" "$RX" "AAC above 6 channels plays silent on the console" $A264 -c:a aac -b:a 512k
mk A03 A03_ac3-2.0.mkv $HD720 30 stereo "$AV" "AC3 2.0" "$DP" "" $A264 $AC3_2
mk A04 A04_ac3-5.1.mkv $HD720 30 "5.1" "$AV" "AC3 5.1" "$DP" "" $A264 $AC3_6
mk A05 A05_eac3-5.1.mkv $HD720 30 "5.1" "$AV" "E-AC3 5.1" "$RX" "" $A264 -c:a eac3 -b:a 384k
mk A06 A06_dts-5.1.mkv $HD720 30 "5.1(side)" "$AV" "DTS core 5.1" "$RX" "" $A264 -c:a dca -strict -2 -b:a 1509k
mk A07 A07_truehd-5.1.mkv $HD720 30 "5.1(side)" "$AV" "TrueHD 5.1" "$RX" "" $A264 -c:a truehd -strict -2
mk A08 A08_flac-2.0.mkv $HD720 30 stereo "$AV" "FLAC 2.0 16-bit" "$RX" "FLAC in MKV played silent on the console" $A264 -c:a flac -sample_fmt s16
mk A09 A09_flac-7.1.mkv $HD720 30 "7.1" "$AV" "FLAC 7.1 24-bit" "$RX" "Check how many channels survive the conversion" $A264 -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
mk A10 A10_opus-2.0.mkv $HD720 30 stereo "$AV" "Opus 2.0" "$RX" "" $A264 -c:a libopus -b:a 128k
mk A11 A11_opus-5.1.mkv $HD720 30 "5.1" "$AV" "Opus 5.1" "$RX" "" $A264 -c:a libopus -b:a 256k
mk A12 A12_vorbis-2.0.mkv $HD720 30 stereo "$AV" "Vorbis 2.0" "$RX" "" $A264 -c:a libvorbis -q:a 4
mk A13 A13_mp3-2.0.mkv $HD720 30 stereo "$AV" "MP3 2.0" "$DP" "" $A264 -c:a libmp3lame -b:a 160k
mk A14 A14_mp2-2.0.mkv $HD720 30 stereo "$AV" "MP2 2.0" "$RX" "MP2 is only allowed in TS/MPG" $A264 -c:a mp2 -b:a 192k
mk A15 A15_pcm-s16-2.0.mkv $HD720 30 stereo "$AV" "PCM 16-bit 2.0" "$RX" "" $A264 -c:a pcm_s16le
mk A16 A16_pcm-s24-5.1.mkv $HD720 30 "5.1" "$AV" "PCM 24-bit 5.1" "$RX" "" $A264 -c:a pcm_s24le
mk A17 A17_ac3-5.1.mp4 $HD720 30 "5.1" "$AV" "AC3 5.1" "$DP" "" $A264 $AC3_6 -movflags +faststart
mk A18 A18_alac-2.0.mp4 $HD720 30 stereo "$AV" "ALAC 2.0" "$DP" "" $A264 -c:a alac -movflags +faststart
mk A19 A19_mp3-2.0.mp4 $HD720 30 stereo "$AV" "MP3 2.0" "$DP" "" $A264 -c:a libmp3lame -b:a 160k -movflags +faststart
mk A20 A20_eac3-5.1.mp4 $HD720 30 "5.1" "$AV" "E-AC3 5.1" "$RX" "" $A264 -c:a eac3 -b:a 384k -movflags +faststart
mk A21 A21_flac-2.0.mp4 $HD720 30 stereo "$AV" "FLAC 2.0" "$RX" "" $A264 -c:a flac -sample_fmt s16 -movflags +faststart
mk A22 A22_opus-2.0.mp4 $HD720 30 stereo "$AV" "Opus 2.0" "$RX" "" $A264 -c:a libopus -b:a 128k -movflags +faststart

# ---- A: audio codecs in the containers they were not tried in (H.264 High 720p30) ----
mk A30 A30_aac-5.1.mp4 $HD720 30 "5.1" "$AV" "AAC LC 5.1" "$DP" "" $A264 -c:a aac -b:a 384k -movflags +faststart
mk A31 A31_aac-5.1.ts $HD720 30 "5.1" "$AV" "AAC LC 5.1" "$DP" "" $A264 -c:a aac -b:a 384k
mk A32 A32_aac-96khz.mkv $HD720 30 stereo "$AV" "AAC LC 2.0 96 kHz" "$DP" "" $A264 -c:a aac -b:a 256k -ar 96000
mk A33 A33_aac-mono.mkv $HD720 30 stereo "$AV" "AAC LC mono" "$DP" "" $A264 -c:a aac -b:a 96k -ac 1
mk A34 A34_eac3-5.1.ts $HD720 30 "5.1" "$AV" "E-AC3 5.1" "$RX" "E-AC3 is allowed only in MKV and MP4" $A264 -c:a eac3 -b:a 384k
mk A35 A35_ac3-5.1.avi $HD720 30 "5.1" "$AV" "AC3 5.1" "$DP" "In the profile, never played" $A264 -bf 0 $AC3_6
mk A36 A36_mp2-2.0.ts $HD720 30 stereo "$AV" "MP2 2.0" "$DP" "In the profile, never played" $A264 -c:a mp2 -b:a 192k
mk A37 A37_mp3-2.0.ts $HD720 30 stereo "$AV" "MP3 2.0" "$RX" "" $A264 $MP3
mk A38 A38_dts-5.1.m2ts $HD720 30 "5.1(side)" "$AV" "DTS core 5.1" "$RX" "Silent in MKV" \
    $A264 -c:a dca -strict -2 -b:a 1509k -mpegts_m2ts_mode 1
mk A39 A39_pcm-s16-2.0.mov $HD720 30 stereo "$AV" "PCM 16-bit 2.0" "$RX" "Silent in MKV" $A264 -c:a pcm_s16le
mk A40 A40_lpcm-2.0.m2ts $HD720 30 stereo "$AV" "Blu-ray LPCM 16-bit 2.0" "$RX" "" $A264 -c:a pcm_bluray -sample_fmt s16 -mpegts_m2ts_mode 1
mk A41 A41_pcm-s16-2.0.avi $HD720 30 stereo "$AV" "PCM 16-bit 2.0" "$RX" "" $A264 -bf 0 -c:a pcm_s16le
mk A42 A42_alac-2.0.mkv $HD720 30 stereo "$AV" "ALAC 2.0" "$RX" "Silent in MP4" $A264 -c:a alac
mk A43 A43_alac-2.0.mov $HD720 30 stereo "$AV" "ALAC 2.0" "$RX" "" $A264 -c:a alac
mk A44 A44_flac-5.1.mkv $HD720 30 "5.1" "$AV" "FLAC 5.1 16-bit" "$DP" "" $A264 -c:a flac -sample_fmt s16
mk A45 A45_flac-5.1.mp4 $HD720 30 "5.1" "$AV" "FLAC 5.1 16-bit" "$DP" "" $A264 -c:a flac -sample_fmt s16 -movflags +faststart
mk A46 A46_flac-24-96.mkv $HD720 30 stereo "$AV" "FLAC 2.0 24-bit 96 kHz" "$DP" "" \
    $A264 -c:a flac -ar 96000 -sample_fmt s32 -bits_per_raw_sample 24
mk A47 A47_wmav1-2.0.wmv $HD720 30 stereo "WMV2 720p30" "WMA v1 2.0" "$DP" "" -c:v wmv2 -q:v 4 -g 60 -c:a wmav1 -b:a 128k

# Above 5.1. Eight beeps in turn (the fourth is the LFE hum) mean every channel arrived.
# E-AC3, TrueHD and DTS cannot be encoded above 5.1 here; those need real files.
mk A50 A50_aac-7.1.mp4 $HD720 30 "7.1" "$AV" "AAC LC 7.1" "$RX" "Silent in MKV" $A264 -c:a aac -b:a 512k -movflags +faststart
mk A51 A51_flac-7.1.mp4 $HD720 30 "7.1" "$AV" "FLAC 7.1 16-bit" "$DP" "FLAC 7.1 played in MKV" $A264 -c:a flac -sample_fmt s16 -movflags +faststart
mk A52 A52_lpcm-7.1.m2ts $HD720 30 "7.1" "$AV" "Blu-ray LPCM 16-bit 7.1" "$RX" "" $A264 -c:a pcm_bluray -sample_fmt s16 -mpegts_m2ts_mode 1
mk A53 A53_opus-7.1.mkv $HD720 30 "7.1" "$AV" "Opus 7.1" "$RX" "Opus 5.1 was silent" $A264 -c:a libopus -b:a 384k
mk A54 A54_pcm-s24-7.1.mov $HD720 30 "7.1" "$AV" "PCM 24-bit 7.1" "$RX" "" $A264 -c:a pcm_s24le

# Two files with several audio tracks, encoded in one go
multi() {
    local id=$1 file=$2 adesc=$3 expected=$4 note=$5; shift 5
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$file" "$AV" "$adesc" "$expected" "$note" >> "$OUT/manifest.tsv"
    throttle
    run "$id" "$VID/$file" \
        -f lavfi -i "$(vsrc $HD720 30 "$id  $AV" "$(clean "$adesc")")" \
        -f lavfi -i "$(asrc "5.1(side)")" \
        -metadata title="${file%.*}" $A264 "$@" &
}
multi A48 A48_multi-audio.mp4 "Tracks 1 AAC 2.0 (default), 2 AC3 5.1, 3 E-AC3 5.1" "$DP" "Switch through all three tracks" \
    -map 0:v -map 1:a -map 1:a -map 1:a -c:a:0 aac -ac:a:0 2 -b:a:0 128k -c:a:1 ac3 -b:a:1 448k -c:a:2 eac3 -b:a:2 384k \
    -metadata:s:a:0 title="AAC 2.0" -metadata:s:a:1 title="AC3 5.1" -metadata:s:a:2 title="E-AC3 5.1" \
    -disposition:a:0 default -disposition:a:1 0 -disposition:a:2 0 -movflags +faststart
multi A49 A49_default-is-track-2.mkv "Tracks 1 DTS 5.1, 2 AAC 2.0 (default)" "$DP" "Sound on opening means the default track was chosen, not the first" \
    -map 0:v -map 1:a -map 1:a -c:a:0 dca -strict -2 -b:a:0 1509k -c:a:1 aac -ac:a:1 2 -b:a:1 128k \
    -metadata:s:a:0 title="DTS 5.1" -metadata:s:a:1 title="AAC 2.0" -disposition:a:0 0 -disposition:a:1 default
# Under the real profile the server grants direct play on track 2 and names it; the player's own choice is track 1
multi A77 A77_no-default-track.mkv "Tracks 1 DTS 5.1, 2 AC3 5.1, neither flagged default" "$DP" "Sound on opening means the server's track was chosen" \
    -map 0:v -map 1:a -map 1:a -c:a:0 dca -strict -2 -b:a:0 1509k -c:a:1 ac3 -b:a:1 448k \
    -metadata:s:a:0 title="DTS 5.1" -metadata:s:a:1 title="AC3 5.1" -disposition:a:0 0 -disposition:a:1 0

# ALAC and PCM played in MOV and not in MP4 or MKV. The server reports MOV and MP4 as one
# container, so these find out whether the file's structure or its name decides.
mk A60 A60_alac_mov-structure.mp4 $HD720 30 stereo "$AV" "ALAC 2.0, QuickTime structure named .mp4" "$RX" "ALAC played in MOV and was silent in MP4" \
    $A264 -c:a alac -f mov
mk A61 A61_alac_mp4-structure.mov $HD720 30 stereo "$AV" "ALAC 2.0, MP4 structure named .mov" "$RX" "" \
    $A264 -c:a alac -f mp4 -movflags +faststart
mk A63 A63_pcm-s16-2.0.mp4 $HD720 30 stereo "$AV" "PCM 16-bit 2.0, MP4 structure" "$DP" "PCM played in MOV; MP4 was never tried" \
    $A264 -c:a pcm_s16le -movflags +faststart
mk A64 A64_pcm_mov-structure.mp4 $HD720 30 stereo "$AV" "PCM 16-bit 2.0, QuickTime structure named .mp4" "$DP" "" \
    $A264 -c:a pcm_s16le -f mov
mk A65 A65_pcm-s16be-2.0.mov $HD720 30 stereo "$AV" "PCM 16-bit big-endian 2.0" "$RX" "Big-endian PCM is not in the profile" $A264 -c:a pcm_s16be
mk A66 A66_pcm-s24-5.1.mp4 $HD720 30 "5.1" "$AV" "PCM 24-bit 5.1, MP4 structure" "$DP" "" $A264 -c:a pcm_s24le -movflags +faststart
mk A67 A67_lpcm-dvd.vob 720x480 30 stereo "MPEG-2 480p30 (DVD VOB)" "DVD LPCM 16-bit 2.0" "$RX" "DVD LPCM is not in the profile" \
    $MPEG2 -b:v 5M -aspect 16:9 -pix_fmt yuv420p -c:a pcm_dvd -f vob

# The codecs the profile allows in MOV and WMV without a clip of their own: the server
# reports MOV as MP4. ffmpeg cannot put FLAC in a QuickTime file.
mk A70 A70_ac3-5.1.mov $HD720 30 "5.1" "$AV" "AC3 5.1" "$DP" "" $A264 $AC3_6
mk A71 A71_eac3-5.1.mov $HD720 30 "5.1" "$AV" "E-AC3 5.1" "$DP" "" $A264 -c:a eac3 -b:a 384k
mk A72 A72_mp3-2.0.mov $HD720 30 stereo "$AV" "MP3 2.0" "$DP" "" $A264 -c:a libmp3lame -b:a 192k
mk A74 A74_amr.mov $HD720 30 stereo "$AV" "AMR-NB mono" "$DP" "" $A264 $AMR
mk A75 A75_ac3-2.0.wmv $HD720 30 stereo "WMV2 720p30" "AC3 2.0" "$DP" "" -c:v wmv2 -q:v 4 -g 60 $AC3_2

# ---- M: music library --------------------------------------------------------------------
# cover SIZE [EXT] [noisy]: a square cover image in logs/. "noisy" makes it weigh like a photo.
cover() {
    local size=$1 ext=${2:-jpg} noise=${3:+,noise=alls=60:allf=u}
    "$FFMPEG" -hide_banner -loglevel error -nostdin -y -f lavfi \
        -i "testsrc2=size=${size}x${size}:rate=1,drawtext=fontfile=$FONT:text='$size x $size':fontsize=h/8:fontcolor=white:box=1:boxcolor=black@0.75:x=(w-tw)/2:y=(h-th)/2$noise" \
        -frames:v 1 -q:v 3 "$LOGS/cover-$size${3:+-noisy}.$ext"
}
cover 1000
cover 2000
ART=(-map 0:a -map 1:v -c:v copy -disposition:v attached_pic -metadata:s:v title=Cover -metadata:s:v "comment=Cover (front)")
SRV="Server stream"

mka M01 M01_mp3.mp3 stereo 44100 "MP3 2.0 44.1 kHz" "$DP" "" -c:a libmp3lame -b:a 192k
mka M02 M02_aac-adts.aac stereo 44100 "AAC LC 2.0 (raw ADTS)" "$DP" "" -c:a aac -b:a 160k
mka M03 M03_aac.m4a stereo 44100 "AAC LC 2.0" "$DP" "" -c:a aac -b:a 160k
mka M04 M04_alac.m4a stereo 44100 "ALAC 2.0 16-bit 44.1 kHz" "$DP" "" -c:a alac -sample_fmt s16p
mka M05 M05_flac-in-m4a.m4a stereo 44100 "FLAC 2.0 in MP4 container" "$DP" "" -c:a flac -sample_fmt s16 -f mp4
mka M06 M06_flac-16-44.flac stereo 44100 "FLAC 2.0 16-bit 44.1 kHz" "$DP" "" -c:a flac -sample_fmt s16
mka M07 M07_flac-24-96.flac stereo 96000 "FLAC 2.0 24-bit 96 kHz" "$DP" "" -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
mka M08 M08_flac-5.1.flac "5.1" 48000 "FLAC 5.1 16-bit 48 kHz" "$DP" "Check every channel beeps" -c:a flac -sample_fmt s16
mka M09 M09_wav-pcm16.wav stereo 44100 "PCM 16-bit 2.0 44.1 kHz" "$DP" "" -c:a pcm_s16le
mka M10 M10_wav-pcm24-96.wav stereo 96000 "PCM 24-bit 2.0 96 kHz" "$DP" "" -c:a pcm_s24le
mka M11 M11_wma.wma stereo 44100 "WMA v2 2.0" "$DP" "" -c:a wmav2 -b:a 160k
mka M12 M12_ac3-5.1.ac3 "5.1" 48000 "AC3 5.1 (raw)" "$DP" "Raw AC3 carries no tags; shows by file name" -c:a ac3 -b:a 448k
mka M13 M13_vorbis.ogg stereo 44100 "Vorbis 2.0" "$SRV" "Not in the profile" -c:a libvorbis -q:a 5
mka M14 M14_opus.opus stereo 48000 "Opus 2.0" "$SRV" "Not in the profile" -c:a libopus -b:a 128k
mka M15 M15_mp3-art-1000.mp3 stereo 44100 "MP3 2.0 + 1000 px cover" "$DP" "Artwork under the 1500 px limit" \
    -i "$LOGS/cover-1000.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M16 M16_mp3-art-2000.mp3 stereo 44100 "MP3 2.0 + 2000 px cover" "$SRV (as MP3)" "Artwork over the 1500 px limit" \
    -i "$LOGS/cover-2000.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M17 M17_flac-art-2000.flac stereo 44100 "FLAC 2.0 + 2000 px cover" "$SRV (as FLAC)" "Artwork over the 1500 px limit" \
    -i "$LOGS/cover-2000.jpg" "${ART[@]}" -c:a flac -sample_fmt s16

# ---- M: more music formats ----
cover 1500
cover 1600
mka M20 M20_aiff.aiff stereo 44100 "AIFF 16-bit 2.0" "$SRV" "Not in the profile" -c:a pcm_s16be
mka M21 M21_wavpack.wv stereo 44100 "WavPack 2.0" "$SRV" "Not in the profile" -c:a wavpack
mka M22 M22_tta.tta stereo 44100 "True Audio 2.0" "$SRV" "Not in the profile" -c:a tta
mka M23 M23_flac-24-192.flac stereo 192000 "FLAC 2.0 24-bit 192 kHz" "$DP" "" -c:a flac -sample_fmt s32 -bits_per_raw_sample 24
mka M24 M24_wav-float32.wav stereo 48000 "PCM 32-bit float 2.0" "$DP" "" -c:a pcm_f32le
mka M25 M25_wav-5.1.wav "5.1" 48000 "PCM 16-bit 5.1" "$DP" "Check every channel beeps" -c:a pcm_s16le
mka M26 M26_flac-7.1.flac "7.1" 48000 "FLAC 7.1 16-bit" "$DP" "Check every channel beeps" -c:a flac -sample_fmt s16
mka M27 M27_flac.mka stereo 44100 "FLAC 2.0 in Matroska audio" "$SRV" "Not in the profile" -c:a flac -sample_fmt s16
mka M28 M28_opus.mka stereo 48000 "Opus 2.0 in Matroska audio" "$SRV" "Not in the profile" -c:a libopus -b:a 128k
mka M29 M29_mp3-vbr.mp3 stereo 44100 "MP3 2.0 VBR" "$DP" "The seek bar and length must be right" -c:a libmp3lame -q:a 2
mka M30 M30_mp3-art-1500.mp3 stereo 44100 "MP3 2.0 + 1500 px cover" "$DP" "Artwork exactly at the limit" \
    -i "$LOGS/cover-1500.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M31 M31_mp3-art-1600.mp3 stereo 44100 "MP3 2.0 + 1600 px cover" "$SRV (as MP3)" "Artwork just over the limit" \
    -i "$LOGS/cover-1600.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M32 M32_flac-art-1000.flac stereo 44100 "FLAC 2.0 + 1000 px cover" "$DP" "Artwork under the limit" \
    -i "$LOGS/cover-1000.jpg" "${ART[@]}" -c:a flac -sample_fmt s16
mka M33 M33_alac-24-96.m4a stereo 96000 "ALAC 2.0 24-bit 96 kHz" "$DP" "" -c:a alac -sample_fmt s32p
mka M34 M34_aac-5.1.m4a "5.1" 48000 "AAC LC 5.1" "$DP" "Check every channel beeps" -c:a aac -b:a 384k
mka M35 M35_ac3.m4a stereo 48000 "AC3 2.0 in M4A" "$DP" "In the profile, never played" -c:a ac3 -b:a 192k -f mp4
mka M36 M36_eac3-5.1.eac3 "5.1" 48000 "E-AC3 5.1 (raw)" "$SRV" "Not in the profile; shows by file name" -c:a eac3 -b:a 384k
mka M37 M37_dts-5.1.dts "5.1(side)" 48000 "DTS 5.1 (raw)" "$SRV" "Not in the profile; shows by file name" -c:a dca -strict -2 -b:a 1509k
mka M38 M38_mp2.mp2 stereo 44100 "MP2 2.0" "$SRV" "Not in the profile; shows by file name" -c:a mp2 -b:a 192k
mka M39 M39_amr.amr mono 8000 "AMR-NB mono" "$DP" "In the profile, never played; shows by file name" -c:a libopencore_amrnb -b:a 12.2k
mka M40 M40_wmav1.wma stereo 44100 "WMA v1 2.0" "$DP" "" -c:a wmav1 -b:a 160k
mka M41 M41_aac.m4b stereo 44100 "AAC LC 2.0 (M4B audiobook)" "$DP" "" -c:a aac -b:a 128k
mka M42 M42_wav-adpcm.wav stereo 44100 "MS ADPCM 2.0 in WAV" "$DP" "In the profile, never played" -c:a adpcm_ms
mka M43 M43_wav-alaw.wav mono 8000 "A-law mono 8 kHz in WAV" "$DP" "In the profile, never played" -c:a pcm_alaw
mka M44 M44_wav-7.1.wav "7.1" 48000 "PCM 16-bit 7.1" "$DP" "Check every channel beeps" -c:a pcm_s16le
mka M45 M45_aac-7.1.m4a "7.1" 48000 "AAC LC 7.1" "$DP" "AAC 7.1 was silent in a video file" -c:a aac -b:a 512k
mka M46 M46_alac-7.1.m4a "7.1(wide)" 48000 "ALAC 7.1 (wide layout) 16-bit" "$DP" "Check every channel beeps" -c:a alac -sample_fmt s16p


# Cover art above the app's 1500 px limit: 2000 px covers played, so where does it stop?
cover 3000
cover 4000
cover 6000
cover 2000 png
cover 3000 png
cover 3000 jpg noisy
mka M50 M50_mp3-art-3000.mp3 stereo 44100 "MP3 2.0 + 3000 px cover" "$SRV (as MP3)" "Artwork over the 1500 px limit" \
    -i "$LOGS/cover-3000.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M51 M51_mp3-art-4000.mp3 stereo 44100 "MP3 2.0 + 4000 px cover" "$SRV (as MP3)" "" \
    -i "$LOGS/cover-4000.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M52 M52_mp3-art-2000-png.mp3 stereo 44100 "MP3 2.0 + 2000 px PNG cover" "$SRV (as MP3)" "" \
    -i "$LOGS/cover-2000.png" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M53 M53_flac-art-4000.flac stereo 44100 "FLAC 2.0 + 4000 px cover" "$SRV (as FLAC)" "" \
    -i "$LOGS/cover-4000.jpg" "${ART[@]}" -c:a flac -sample_fmt s16
mka M54 M54_flac-art-3000-png.flac stereo 44100 "FLAC 2.0 + 3000 px PNG cover" "$SRV (as FLAC)" "" \
    -i "$LOGS/cover-3000.png" "${ART[@]}" -c:a flac -sample_fmt s16
mka M55 M55_aac-art-3000.m4a stereo 44100 "AAC 2.0 in M4A + 3000 px cover" "$SRV (as MP3)" "" \
    -i "$LOGS/cover-3000.jpg" "${ART[@]}" -c:a aac -b:a 160k
mka M56 M56_mp3-art-3000-heavy.mp3 stereo 44100 "MP3 2.0 + 3000 px cover, several MB" "$SRV (as MP3)" "A photo-like cover: is it the pixels or the bytes?" \
    -i "$LOGS/cover-3000-noisy.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M57 M57_mp3-art-6000.mp3 stereo 44100 "MP3 2.0 + 6000 px cover" "$SRV (as MP3)" "" \
    -i "$LOGS/cover-6000.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3

# Cover size in bytes, apart from its pixels. Noise makes a cover as heavy as a photograph.
cover 4000 jpg noisy
cover 2400 jpg noisy
cover 1400 png noisy
mka M60 M60_flac-art-4000-heavy.flac stereo 44100 "FLAC 2.0 + 4000 px cover of about 9 MB" "$DP" "" \
    -i "$LOGS/cover-4000-noisy.jpg" "${ART[@]}" -c:a flac -sample_fmt s16
mka M61 M61_flac-art-1400-png-heavy.flac stereo 44100 "FLAC 2.0 + 1400 px PNG cover of about 4.5 MB" "$DP" "" \
    -i "$LOGS/cover-1400-noisy.png" "${ART[@]}" -c:a flac -sample_fmt s16
mka M62 M62_flac-art-3000-heavy.flac stereo 44100 "FLAC 2.0 + 3000 px cover of about 5 MB" "$DP" "" \
    -i "$LOGS/cover-3000-noisy.jpg" "${ART[@]}" -c:a flac -sample_fmt s16
mka M63 M63_flac-art-2400-heavy.flac stereo 44100 "FLAC 2.0 + 2400 px cover of about 3 MB" "$DP" "" \
    -i "$LOGS/cover-2400-noisy.jpg" "${ART[@]}" -c:a flac -sample_fmt s16
mka M64 M64_mp3-art-4000-heavy.mp3 stereo 44100 "MP3 2.0 + 4000 px cover of about 9 MB" "$DP" "" \
    -i "$LOGS/cover-4000-noisy.jpg" "${ART[@]}" -c:a libmp3lame -b:a 192k -id3v2_version 3
mka M65 M65_aac-art-3000-heavy.m4a stereo 44100 "AAC 2.0 in M4A + 3000 px cover of about 5 MB" "$DP" "" \
    -i "$LOGS/cover-3000-noisy.jpg" "${ART[@]}" -c:a aac -b:a 160k

# ---- Z: picture sizes above 1080p (the standard edition; named Z to sort last) ------------
# The profile limits the standard edition to 1920x1080 because 4K frames ran it out of
# graphics memory. These find where that starts. Expect the app to go down on some.
ZNOTE="Sent as it is only with the size limit lifted"
mk Z01 Z01_h264_1920x1200.mkv 1920x1200 30 stereo "H.264 High 1920x1200 (16:10)" "AAC 2.0" "$TV" "$ZNOTE" $H264HI $AAC2
mk Z02 Z02_h264_1920x1440.mkv 1920x1440 30 stereo "H.264 High 1920x1440 (4:3)" "AAC 2.0" "$TV" "$ZNOTE" $H264HI $AAC2
mk Z03 Z03_h264_2560x1080.mkv 2560x1080 30 stereo "H.264 High 2560x1080 (ultrawide)" "AAC 2.0" "$TV" "$ZNOTE" $H264HI $AAC2
mk Z04 Z04_h264_2560x1440.mkv 2560x1440 30 stereo "H.264 High 2560x1440" "AAC 2.0" "$TV" "$ZNOTE" $H264HI $AAC2
mk Z05 Z05_hevc_2560x1440.mkv 2560x1440 30 stereo "HEVC Main 10 2560x1440" "AAC 2.0" "$TV" "$ZNOTE" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" $AAC2
mk Z06 Z06_h264_3840x2160.mkv $UHD 30 stereo "H.264 High 3840x2160" "AAC 2.0" "$TV" "$ZNOTE" $H264HI $AAC2
mk Z07 Z07_hevc_3840x2160.mkv $UHD 30 stereo "HEVC Main 3840x2160" "AAC 2.0" "$TV" "$ZNOTE" \
    $X265 -pix_fmt yuv420p -x265-params "$X265P" $AAC2
VFX=$PQ mk Z08 Z08_hevc-hdr10_3840x2160.mkv $UHD 30 stereo "HEVC Main 10 HDR10 3840x2160" "AAC 2.0" "$TV" "$ZNOTE" \
    $X265 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk Z09 Z09_vp9_3840x2160.mkv $UHD 30 stereo "VP9 Profile 0 3840x2160" "AAC 2.0" "$TV" "$ZNOTE" \
    $VP9 -deadline realtime -cpu-used 8 -pix_fmt yuv420p $AAC2
VFX=$PQ mk Z10 Z10_hevc-hdr10_3840x2160p60.mkv $UHD 60 stereo "HEVC Main 10 HDR10 3840x2160 at 60 fps" "AAC 2.0" "$TV" "$ZNOTE" \
    $X265 -g 120 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk Z11 Z11_vp9_2560x1440.mkv 2560x1440 30 stereo "VP9 Profile 0 2560x1440" "AAC 2.0" "$TV" "$ZNOTE" \
    $VP9 -pix_fmt yuv420p $AAC2
# The standard edition's limit itself: 1440p at 60 fps, and 1440p on its side
mk Z12 Z12_h264_1440x2560.mkv 1440x2560 30 stereo "H.264 High 1440x2560 (portrait)" "AAC 2.0" "$DP" "" $H264HI $AAC2
mk Z13 Z13_hevc_1440x2560.mkv 1440x2560 30 stereo "HEVC Main 10 1440x2560 (portrait)" "AAC 2.0" "$DP" "" \
    $X265 -pix_fmt yuv420p10le -x265-params "$X265P" $AAC2
mk Z14 Z14_h264_2560x1440p60.mkv 2560x1440 60 stereo "H.264 High 2560x1440 at 60 fps" "AAC 2.0" "$DP" "" $H264HI -g 120 $AAC2
VFX=$PQ mk Z15 Z15_hevc-hdr10_2560x1440p60.mkv 2560x1440 60 stereo "HEVC Main 10 HDR10 2560x1440 at 60 fps" "AAC 2.0" "$DP" "" \
    $X265 -g 120 $PQ_FLAGS -x265-params "$HDR10P" $AAC2
mk Z16 Z16_vp9_2560x1440p60.mkv 2560x1440 60 stereo "VP9 Profile 0 2560x1440 at 60 fps" "AAC 2.0" "$DP" "" \
    $VP9 -g 120 -pix_fmt yuv420p $AAC2

wait

# ---- A23: one file, four audio tracks, assembled from the clips above ---------------------
printf '%s\t%s\t%s\t%s\t%s\t%s\n' A23 A23_multi-audio.mkv "H.264 High 1080p30" \
    "Tracks: 1 AAC 2.0 (default), 2 AC3 5.1, 3 E-AC3 5.1, 4 DTS 5.1" \
    "$DP on tracks 1-2; $RX on tracks 3-4" "Tests switching audio track; record each track in Notes" >> "$OUT/manifest.tsv"
run A23 "$VID/A23_multi-audio.mkv" -i "$VID/V03_h264-high_1080p30.mkv" -i "$VID/A04_ac3-5.1.mkv" \
    -i "$VID/A05_eac3-5.1.mkv" -i "$VID/A06_dts-5.1.mkv" \
    -map 0:v -map 0:a -map 1:a -map 2:a -map 3:a -c copy \
    -metadata title="A23_multi-audio" \
    -metadata:s:a:0 title="AAC 2.0" -metadata:s:a:1 title="AC3 5.1" -metadata:s:a:2 title="E-AC3 5.1" -metadata:s:a:3 title="DTS 5.1" \
    -metadata:s:a:0 language=eng -metadata:s:a:1 language=eng -metadata:s:a:2 language=eng -metadata:s:a:3 language=eng \
    -disposition:a:0 default -disposition:a:1 0 -disposition:a:2 0 -disposition:a:3 0

# ---- D: Dolby Vision (needs dovi_tool and mkvmerge; skipped without them) ------------------
# An HEVC stream with generated Dolby Vision metadata. Profile 8.1 sits on an HDR10 picture.
# The profile 5 clips carry an ordinary HDR picture flagged as profile 5: they show whether
# the console opens such a file, not how a real one looks.
DOVI_TOOL=${DOVI_TOOL:-dovi_tool}
MKVMERGE=${MKVMERGE:-mkvmerge}
dv() {
    local id=$1 profile=$2 name=$3 vdesc=$4 note=$5 work="$LOGS/dv-$1" x265p=$X265P VFX=$PQ
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$name.mkv" "$vdesc" "AAC 2.0" "$DP" "$note" >> "$OUT/manifest.tsv"
    [[ -n ${ONLY:-} && ! $id =~ $ONLY ]] && return 0
    if ! command -v "$DOVI_TOOL" > /dev/null || ! command -v "$MKVMERGE" > /dev/null; then
        echo "skip  $id  $name.mkv  (needs dovi_tool and mkvmerge)"
        return 0
    fi
    mkdir -p "$work"
    [[ $profile == 8.1 ]] && x265p=$HDR10P
    printf '{"cm_version":"V40","profile":"%s","length":%d,"level6":{"max_display_mastering_luminance":1000,"min_display_mastering_luminance":1,"max_content_light_level":1000,"max_frame_average_light_level":400}}' \
        "$profile" $((DUR * 30)) > "$work/rpu.json"
    if "$FFMPEG" -hide_banner -loglevel error -nostdin -y \
            -f lavfi -i "$(vsrc $HD 30 "$id  $(clean "$vdesc")" "AAC 2.0  in  mkv")" \
            -c:v libx265 -preset veryfast -crf 24 -g 60 $PQ_FLAGS -x265-params "$x265p" -f hevc "$work/base.hevc" &&
        "$FFMPEG" -hide_banner -loglevel error -nostdin -y -f lavfi -i "$(asrc stereo)" $AAC2 "$work/audio.m4a" &&
        "$DOVI_TOOL" generate -j "$work/rpu.json" -o "$work/rpu.bin" &&
        "$DOVI_TOOL" inject-rpu -i "$work/base.hevc" --rpu-in "$work/rpu.bin" -o "$work/dv.hevc" &&
        "$MKVMERGE" -q -o "$VID/$name.mkv" --title "$name" --default-duration 0:30fps "$work/dv.hevc" "$work/audio.m4a"
    then
        echo "ok    $id  $name.mkv"
        rm -rf "$work"
    else
        echo "FAIL  $id  $name.mkv"
        echo "$id" >> "$LOGS/failed"
    fi > >(tee -a "$LOGS/$id.log") 2>> "$LOGS/$id.log"
}
# The same stream in MP4. The burned-in label still says mkv.
dvmp4() {
    local id=$1 src=$2 name=$3 vdesc=$4 note=$5; shift 5
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$name.mp4" "$vdesc" "AAC 2.0" "$DP" "$note" >> "$OUT/manifest.tsv"
    [[ -e "$VID/$src.mkv" ]] || return 0
    run "$id" "$VID/$name.mp4" -i "$VID/$src.mkv" -c copy -strict unofficial -metadata title="$name" -movflags +faststart "$@"
}
dv D01 8.1 D01_dolby-vision-p8.1_1080p30 "Dolby Vision profile 8.1 (HDR10 base) 1080p30" "Plays as HDR10 where Dolby Vision is not available"
dv D02 5 D02_dolby-vision-p5_1080p30 "Dolby Vision profile 5 (synthetic) 1080p30" "Shows whether it opens; a real profile 5 file has other colours"
dvmp4 D03 D01_dolby-vision-p8.1_1080p30 D03_dolby-vision-p8.1_1080p30 "Dolby Vision profile 8.1 (HDR10 base) 1080p30" "" -tag:v hvc1
dvmp4 D04 D02_dolby-vision-p5_1080p30 D04_dolby-vision-p5_1080p30 "Dolby Vision profile 5 (synthetic) 1080p30" ""

# ---- X: formats ffmpeg cannot encode ------------------------------------------------------
# Streams from public sample collections (samples.ffmpeg.org, Dolby's developer media,
# repo.jellyfin.org), fetched once into downloads/ with curl, and clips made with fdkaac,
# hdr10plus_tool and mkvmerge. A clip whose source or tool is missing is skipped. Clips
# built around a downloaded video carry no burned-in label.
DL="$OUT/downloads"
FDKAAC=${FDKAAC:-fdkaac}
HDR10PLUS_TOOL=${HDR10PLUS_TOOL:-hdr10plus_tool}
FF_SAMPLES=https://samples.ffmpeg.org
DOLBY=https://media.developer.dolby.com
JF_SAMPLES=https://repo.jellyfin.org/test-videos

wanted() { [[ -z ${ONLY:-} || $1 =~ $ONLY ]]; }
ent() { printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "" "" >> "$OUT/manifest.tsv"; }
fetch() {
    [[ -s "$DL/$1" ]] && return 0
    command -v curl > /dev/null || return 1
    mkdir -p "$DL"
    curl -sS -L --fail -o "$DL/$1" "$2" 2>> "$LOGS/fetch.log" || { rm -f "$DL/$1"; return 1; }
}
skip() { echo "skip  $1  $2  ($3)"; }
# xsrc ID PATH VIDEO-DESC AUDIO-DESC SOURCE URL [ffmpeg args]: -i SOURCE comes first
xsrc() {
    local id=$1 path=$2 vdesc=$3 adesc=$4 src=$5 url=$6; shift 6
    ent "$id" "$(basename "$path")" "$vdesc" "$adesc"
    wanted "$id" || return 0
    fetch "$src" "$url" || { skip "$id" "$(basename "$path")" "could not fetch $src"; return 0; }
    run "$id" "$path" -i "$DL/$src" "$@"
}
# xkeep ID PATH VIDEO-DESC AUDIO-DESC SOURCE URL: the downloaded file as it is
xkeep() {
    local id=$1 path=$2 vdesc=$3 adesc=$4 src=$5 url=$6
    ent "$id" "$(basename "$path")" "$vdesc" "$adesc"
    wanted "$id" || return 0
    fetch "$src" "$url" || { skip "$id" "$(basename "$path")" "could not fetch $src"; return 0; }
    cp "$DL/$src" "$path" && echo "ok    $id  $(basename "$path")"
}
XTAGS=(-metadata artist="Gelatinarm Test" -metadata album_artist="Gelatinarm Test" -metadata album="Codec Tests")
pattern() { vsrc $HD720 30 "$1  H.264 High 720p30" "$(clean "$2")"; }

# VC-1
VC1TS=vc1-interlaced-bframes.m2ts
VC1URL=$FF_SAMPLES/V-codecs/WVC1/$VC1TS
xsrc X01 "$VID/X01_vc1-advanced_576p25.wmv" "VC-1 Advanced 1440x576 at 25 fps" "WMA v2 2.0" \
    Test_1440x576_WVC1_6Mbps.wmv "$FF_SAMPLES/V-codecs/WVC1/Test_1440x576_WVC1_6Mbps.wmv" \
    -f lavfi -i "$(asrc stereo)" -map 0:v:0 -map 1:a -c:v copy -c:a wmav2 -b:a 160k -t "$DUR"
xsrc X02 "$VID/X02_vc1-advanced_1080i.mkv" "VC-1 Advanced 1080i (Blu-ray stream)" "AAC 2.0" "$VC1TS" "$VC1URL" \
    -f lavfi -i "$(asrc stereo)" -map 0:v:0 -map 1:a -c:v copy $AAC2 -shortest
xsrc X03 "$VID/X03_vc1-advanced_1080i.mp4" "VC-1 Advanced 1080i (Blu-ray stream)" "AAC 2.0" "$VC1TS" "$VC1URL" \
    -f lavfi -i "$(asrc stereo)" -map 0:v:0 -map 1:a -c:v copy $AAC2 -shortest -movflags +faststart
xsrc X04 "$VID/X04_vc1-advanced_1080i.ts" "VC-1 Advanced 1080i (Blu-ray stream)" "AC3 2.0" "$VC1TS" "$VC1URL" \
    -f lavfi -i "$(asrc stereo)" -map 0:v:0 -map 1:a -c:v copy -c:a ac3 -b:a 192k -shortest
xkeep X05 "$VID/X05_wmv3_720p_wmapro-5.1.wmv" "VC-1 Main (WMV3) 720p" "WMA Pro 5.1" \
    WMVHDsplash.wmv "$FF_SAMPLES/A-codecs/WMA9/wmapro/WMVHDsplash.wmv"

# Audio in video files. The E-AC3 7.1 and Atmos files are Dolby's own.
EAC371=7_pt_1.eac3
xsrc X10 "$VID/X10_eac3-7.1.mkv" "H.264 High 720p30" "E-AC3 7.1" "$EAC371" "$FF_SAMPLES/A-codecs/AC3/eac3/$EAC371" \
    -f lavfi -i "$(pattern X10 "E-AC3 7.1  in  mkv")" -map 1:v -map 0:a:0 $H264HI -c:a copy -t "$DUR"
xkeep X11 "$VID/X11_eac3-7.1_channel-id.mp4" "H.264 High 1080p30" "E-AC3 7.1, each channel announced" \
    MP4_HPL40_30fps_channel_id_71.mp4 "$DOLBY/DDP/MP4_HPL40_30fps_channel_id_71.mp4"
xkeep X12 "$VID/X12_eac3-atmos.mp4" "H.264 Main 1080p24" "E-AC3 5.1 with Dolby Atmos (JOC)" \
    shattered-3Mb.mp4 "$DOLBY/Atmos/MP4/shattered-3Mb.mp4"
xsrc X13 "$VID/X13_eac3-atmos.mkv" "H.264 Main 1080p24" "E-AC3 5.1 with Dolby Atmos (JOC)" \
    shattered-3Mb.mp4 "$DOLBY/Atmos/MP4/shattered-3Mb.mp4" -map 0:v:0 -map 0:a:0 -c copy
xsrc X14 "$VID/X14_dts-hd-hra-5.1.mkv" "H.264 High 720p30" "DTS-HD High Resolution 5.1" "$VC1TS" "$VC1URL" \
    -f lavfi -i "$(pattern X14 "DTS-HD HRA 5.1  in  mkv")" -map 1:v -map 0:a:0 $H264HI -c:a copy -shortest
xsrc X15 "$VID/X15_dts-hd-ma-5.1.mkv" "H.264 High 1080p (4 s)" "DTS-HD Master Audio 5.1" \
    bond_sample_dtshdma.m2ts "$FF_SAMPLES/A-codecs/DTS/bond_sample_dtshdma.m2ts" -map 0:v:0 -map 0:a:0 -c copy
xsrc X16 "$VID/X16_truehd-5.1_vc1.mkv" "VC-1 Advanced 1080p (6 s)" "TrueHD 5.1" \
    vc1-with-truehd.m2ts "$FF_SAMPLES/A-codecs/TrueHD/vc1-with-truehd.m2ts" -map 0:v:0 -map 0:a:0 -c copy
xsrc X17 "$VID/X17_eac3-7.1.ts" "H.264 High 720p30" "E-AC3 7.1" "$EAC371" "$FF_SAMPLES/A-codecs/AC3/eac3/$EAC371" \
    -f lavfi -i "$(pattern X17 "E-AC3 7.1  in  ts")" -map 1:v -map 0:a:0 $H264HI -c:a copy -t "$DUR"

# HE-AAC: fdkaac encodes it, ffmpeg's own AAC encoder does not
heaac() {
    local id=$1 profile=$2 bitrate=$3 work="$LOGS/$1"
    wanted "$id" || return 0
    command -v "$FDKAAC" > /dev/null || { skip "$id" "$4" "needs fdkaac"; return 1; }
    "$FFMPEG" -hide_banner -loglevel error -nostdin -y -f lavfi -i "$(asrc stereo 44100)" -c:a pcm_s16le "$work.wav" &&
        "$FDKAAC" -S -p "$profile" -b "$bitrate" -o "$work.m4a" "$work.wav" 2> "$LOGS/$id.fdk.log"
}
ent X20 X20_he-aac-v1.mkv "H.264 High 720p30" "HE-AAC v1 2.0"
heaac X20 5 64 X20_he-aac-v1.mkv && run X20 "$VID/X20_he-aac-v1.mkv" -f lavfi -i "$(pattern X20 "HE-AAC v1 2.0  in  mkv")" \
    -i "$LOGS/X20.m4a" -map 0:v -map 1:a $H264HI -c:a copy -shortest
ent X21 X21_he-aac-v2.mp4 "H.264 High 720p30" "HE-AAC v2 2.0"
heaac X21 29 32 X21_he-aac-v2.mp4 && run X21 "$VID/X21_he-aac-v2.mp4" -f lavfi -i "$(pattern X21 "HE-AAC v2 2.0  in  mp4")" \
    -i "$LOGS/X21.m4a" -map 0:v -map 1:a $H264HI -c:a copy -shortest -movflags +faststart
ent M70 M70_he-aac-v1.m4a - "HE-AAC v1 2.0"
heaac M70 5 64 M70_he-aac-v1.m4a && run M70 "$MUS/M70_he-aac-v1.m4a" -i "$LOGS/M70.m4a" -c copy "${XTAGS[@]}" \
    -metadata title="M70 HE-AAC v1 2.0" -metadata track=70
ent M71 M71_he-aac-v2.m4a - "HE-AAC v2 2.0"
heaac M71 29 32 M71_he-aac-v2.m4a && run M71 "$MUS/M71_he-aac-v2.m4a" -i "$LOGS/M71.m4a" -c copy "${XTAGS[@]}" \
    -metadata title="M71 HE-AAC v2 2.0" -metadata track=71

# Dolby Vision as encoded for Jellyfin's test set, and HDR10+
xkeep X30 "$VID/X30_dolby-vision-p5_1080p60.mp4" "Dolby Vision profile 5 1080p60" "-" \
    jellyfin-1080p-dv-p5.mp4 "$JF_SAMPLES/HDR/Dolby%20Vision/Test%20Jellyfin%201080p%20DV%20P5.mp4"
xkeep X31 "$VID/X31_dolby-vision-p8.4_1080p60.mp4" "Dolby Vision profile 8.4 (HLG base) 1080p60" "-" \
    jellyfin-1080p-dv-p8.4.mp4 "$JF_SAMPLES/HDR/Dolby%20Vision/Test%20Jellyfin%201080p%20DV%20P8.4.mp4"
ent X32 X32_hevc-hdr10plus_1080p30.mkv "HEVC Main 10 HDR10+ 1080p30" "AAC 2.0"
hdr10plus() {
    local work="$LOGS/X32" frames=$((DUR * 30)) i
    mkdir -p "$work"
    {
        printf '{"JSONInfo":{"HDR10plusProfile":"B","Version":"1.0"},"SceneInfo":['
        for ((i = 0; i < frames; i++)); do
            ((i)) && printf ','
            printf '{"BezierCurveData":{"Anchors":[102,205,307,410,512,614,717,819,922],"KneePointX":150,"KneePointY":300},'
            printf '"LuminanceParameters":{"AverageRGB":1000,"LuminanceDistributions":{"DistributionIndex":[1,5,10,25,50,75,90,95,99],'
            printf '"DistributionValues":[10,2000,50,500,2000,5000,8000,9000,10000]},"MaxScl":[10000,10000,10000]},'
            printf '"NumberOfWindows":1,"TargetedSystemDisplayMaximumLuminance":400,"SceneFrameIndex":%d,"SceneId":0,"SequenceFrameIndex":%d}' "$i" "$i"
        done
        printf '],"SceneInfoSummary":{"SceneFirstFrameIndex":[0],"SceneFrameNumbers":[%d]},"ToolInfo":{"Tool":"generate.sh","Version":"1"}}' "$frames"
    } > "$work/meta.json"
    if "$FFMPEG" -hide_banner -loglevel error -nostdin -y \
            -f lavfi -i "$(VFX=$PQ vsrc $HD 30 "X32  HEVC Main 10 HDR10+ 1080p30" "AAC 2.0  in  mkv")" \
            -c:v libx265 -preset veryfast -crf 24 -g 60 $PQ_FLAGS -x265-params "$HDR10P" -f hevc "$work/base.hevc" &&
        "$FFMPEG" -hide_banner -loglevel error -nostdin -y -f lavfi -i "$(asrc stereo)" $AAC2 "$work/audio.m4a" &&
        "$HDR10PLUS_TOOL" inject -i "$work/base.hevc" -j "$work/meta.json" -o "$work/plus.hevc" &&
        "$MKVMERGE" -q -o "$VID/X32_hevc-hdr10plus_1080p30.mkv" --title X32_hevc-hdr10plus_1080p30 \
            --default-duration 0:30fps "$work/plus.hevc" "$work/audio.m4a"
    then
        echo "ok    X32  X32_hevc-hdr10plus_1080p30.mkv"
        rm -rf "$work"
    else
        echo "FAIL  X32  X32_hevc-hdr10plus_1080p30.mkv"
        echo X32 >> "$LOGS/failed"
    fi > >(tee -a "$LOGS/X32.log") 2>> "$LOGS/X32.log"
}
if wanted X32; then
    if command -v "$HDR10PLUS_TOOL" > /dev/null && command -v "$MKVMERGE" > /dev/null; then
        hdr10plus
    else
        skip X32 X32_hevc-hdr10plus_1080p30.mkv "needs hdr10plus_tool and mkvmerge"
    fi
fi

# Music
mka M72 M72_wav-mulaw.wav mono 8000 "mu-law mono 8 kHz in WAV" "" "" -c:a pcm_mulaw
mka M73 M73_wav-ima-adpcm.wav stereo 44100 "IMA ADPCM 2.0 in WAV" "" "" -c:a adpcm_ima_wav
mka M74 M74_wav-gsm.wav mono 8000 "GSM mono 8 kHz in WAV" "" "" -c:a libgsm_ms
mka M80 M80_mp3-in-m4a.m4a stereo 44100 "MP3 2.0 in M4A" "" "" -c:a libmp3lame -b:a 192k -f mp4
mka M81 M81_amr-in-m4a.m4a mono 8000 "AMR-NB mono in M4A" "" "" -c:a libopencore_amrnb -b:a 12.2k -f mov
mka M82 M82_amr.3gp mono 8000 "AMR-NB mono in 3GP" "" "" -c:a libopencore_amrnb -b:a 12.2k
mka M83 M83_opus.webm stereo 48000 "Opus 2.0 in WebM" "" "" -c:a libopus -b:a 128k
wait
WMAPRO=$FF_SAMPLES/A-codecs/WMA9/wmapro
xsrc M75 "$MUS/M75_wma-pro-5.1.wma" - "WMA Pro 5.1" Classical_44_16_6_256000_0_20.wma "$WMAPRO/Classical_44_16_6_256000_0_20.wma" \
    -c copy -t "$DUR" "${XTAGS[@]}" -metadata title="M75 WMA Pro 5.1" -metadata track=75
xsrc M76 "$MUS/M76_wma-pro-24-96.wma" - "WMA Pro 2.0 24-bit 96 kHz" Classical_96_24_2_Q75_2_3.wma "$WMAPRO/Classical_96_24_2_Q75_2_3.wma" \
    -c copy -t "$DUR" "${XTAGS[@]}" -metadata title="M76 WMA Pro 2.0 24-bit 96 kHz" -metadata track=76
xsrc M77 "$MUS/M77_wma-lossless.wma" - "WMA Lossless 2.0" luckynight-wma-lossless.wma "$FF_SAMPLES/A-codecs/lossless/luckynight.wma" \
    -c copy -t "$DUR" "${XTAGS[@]}" -metadata title="M77 WMA Lossless 2.0" -metadata track=77
xkeep M78 "$MUS/M78_ape.ape" - "Monkey's Audio (APE) 2.0" luckynight.ape "$FF_SAMPLES/A-codecs/lossless/luckynight.ape"
xsrc M79 "$MUS/M79_eac3-7.1.eac3" - "E-AC3 7.1 (raw)" "$EAC371" "$FF_SAMPLES/A-codecs/AC3/eac3/$EAC371" -c copy -t "$DUR"

# ---- RESULTS.md ----------------------------------------------------------------------------
section() {
    local prefix=$1 title=$2 blurb=$3
    printf '\n## %s\n\n%s\n\n' "$title" "$blurb"
    echo "| ID | File | Video | Audio | Method seen (Y) | Picture | Sound | Seek | Notes |"
    echo "|----|------|-------|-------|-----------------|---------|-------|------|-------|"
    sort -t$'\t' -k1,1 "$OUT/manifest.tsv" | awk -F'\t' -v p="$prefix" \
        'substr($1,1,1)==p { printf "| %s | `%s` | %s | %s | | | | | |\n", $1, $2, $3, $4 }'
}

{
    cat << 'EOF'
# Gelatinarm codec test results

Console: ____________  Edition (standard / 4K): ________  App version: ________
Jellyfin server: ________  Display (HDR10 / HDR10+ / HLG / Dolby Vision): ________  Date: ________

For each clip, play it and press **Y** for the playback statistics.

- **Method seen**: Direct play / Remux / Transcode.
- **Picture**: the test pattern moves smoothly and the two label lines are readable. For HDR clips, note whether the display switched mode.
- **Sound**: one beep per channel per second, in channel order (5.1: FL, FR, C, LFE hum, then the two surrounds). Note silence or missing channels.
- **Seek**: jump forward and back; the on-screen timestamp should match the seek bar.

EOF
    section V "Video codecs" "MKV with AAC stereo unless the row says otherwise."
    section C "Containers" "The same streams in other containers."
    section A "Audio in video files" "H.264 High 720p30 video throughout, so only the audio varies."
    section M "Music" "In \`music/\`, tagged as artist *Gelatinarm Test*, album *Codec Tests*."
    section Z "Picture sizes above 1080p" "Standard edition. These can take the app down; play them last."
    section D "Dolby Vision" "Made with dovi_tool; absent when it is not installed."
    section X "Formats from sample collections" "Downloaded streams, and clips made with fdkaac and hdr10plus_tool. Some are shorter than 30 seconds."
} > "$OUT/RESULTS.new.md"
if [[ -e "$OUT/RESULTS.md" ]]; then
    echo "RESULTS.md exists, left alone; fresh sheet written to RESULTS.new.md"
else
    mv "$OUT/RESULTS.new.md" "$OUT/RESULTS.md"
fi

if [[ -s "$LOGS/failed" ]]; then
    echo "FAILED: $(tr '\n' ' ' < "$LOGS/failed")"
    exit 1
fi
echo "Done: $(find "$VID" "$MUS" -type f | wc -l) files, $(du -sh "$OUT" | cut -f1) in $OUT"
