#!/bin/sh
# Clip 13 v2 encode: clip13-code-slow.cs renders the tower three times along one camera path (wings 2, 3, 4 into
# frames_w2/3/4). This dissolves between the passes (0.45 s) and between the matching code cards at the same moments,
# so the highlighted number and the shape change together, then adds the super.
#   encode-code-slow.sh [clip-id] [out-dir]   (default c13_code_slow -> ~/Downloads/freppuccino-sizzle/v2/<clip-id>)
# Take times: wings 2 -> 3 at 2.0 s, 3 -> 4 at 3.8 s. The edit is 0.5 -> 6.0 s (5.5 s): each value holds ~1.5 s.
set -e
CLIP="${1:-c13_code_slow}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${2:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/v2/$CLIP}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
T1=2.0; T2=3.8; D=0.45; ES=0.5; EL=5.5
calc() { node -e "console.log($1)"; }
P="$WORK/$CLIP/passes"; mkdir -p "$P" "$OUT"
for w in 2 3 4; do   # each pass -> a full-take mp4 over the background (encode.sh's _t01)
  mkdir -p "$WORK/${CLIP}_w$w"
  FRAMES_DIR="$WORK/$CLIP/frames_w$w" EDIT_START=0 EDIT_LEN=1 sh "$HERE/encode.sh" "${CLIP}_w$w" "$P" > /dev/null
done
CARDS="$WORK/$CLIP/cards"
[ -s "$CARDS/card_w4.png" ] || sh "$HERE/code-card.sh" "$CARDS" > /dev/null
SUP="$WORK/$CLIP/super.png"
sh "$HERE/super.sh" "Write your own." latte "$SUP" > /dev/null
C1=$(calc "$T1 - $ES"); C2=$(calc "$T2 - $ES")
G="[0:v]trim=0:$(calc "$T1 + $D"),setpts=PTS-STARTPTS[a];[1:v]trim=start=$T1,setpts=PTS-STARTPTS[b]"
G="$G;[a][b]xfade=transition=fade:duration=$D:offset=$T1[ab];[2:v]trim=start=$T2,setpts=PTS-STARTPTS[c]"
G="$G;[ab][c]xfade=transition=fade:duration=$D:offset=$T2,trim=start=$ES:end=$(calc "$ES + $EL"),setpts=PTS-STARTPTS[shape]"
G="$G;[4:v]format=rgba,fade=t=in:st=$C1:d=$D:alpha=1[k3];[5:v]format=rgba,fade=t=in:st=$C2:d=$D:alpha=1[k4]"
G="$G;[6:v]format=rgba,fade=t=in:st=0.4:d=0.3:alpha=1,fade=t=out:st=$(calc "$EL - 0.8"):d=0.3:alpha=1[sup]"
G="$G;[shape][3:v]overlay[s1];[s1][k3]overlay[s2];[s2][k4]overlay[s3];[s3][sup]overlay,format=yuv420p[v]"
"$FF" -hide_banner -loglevel error -y \
  -i "$P/sizzle_${CLIP}_w2_t01.mp4" -i "$P/sizzle_${CLIP}_w3_t01.mp4" -i "$P/sizzle_${CLIP}_w4_t01.mp4" \
  -loop 1 -t $EL -i "$CARDS/card_w2.png" -loop 1 -t $EL -i "$CARDS/card_w3.png" -loop 1 -t $EL -i "$CARDS/card_w4.png" \
  -loop 1 -t $EL -i "$SUP" -filter_complex "$G" -map "[v]" -frames:v "$(calc "Math.round($EL * 60)")" \
  -c:v libx264 -preset slow -crf 16 -r 60 -movflags +faststart "$OUT/sizzle_${CLIP}_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_${CLIP}_edit.mp4" -vf "select='not(mod(n\,55))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$WORK/$CLIP/contact.png"
echo "edit -> $OUT/sizzle_${CLIP}_edit.mp4 ; contact sheet: $WORK/$CLIP/contact.png"
