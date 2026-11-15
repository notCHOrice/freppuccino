#!/bin/sh
# Clip 3 v2 encode: the normal encode (no super), then a label per boolean op, switching with the op.
#   encode-booleans.sh [clip-id] [out-dir]   (default c03_booleans -> ~/Downloads/freppuccino-sizzle/v2/c03_booleans)
# clip03-booleans.cs switches union -> difference -> intersection at 2.0 s and 3.6 s of the take. The edit runs
# 0.5 -> 5.2 s, so the labels switch at 1.5 s and 3.1 s of the edit, and it ends just before the switch back to union.
set -e
CLIP="${1:-c03_booleans}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${2:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/v2/$CLIP}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
EDIT_START=0.5 EDIT_LEN=4.7 sh "$HERE/encode.sh" "$CLIP" "$OUT" > /dev/null
L="$WORK/$CLIP/labels"; mkdir -p "$L"
for w in Union Difference Intersection; do [ -s "$L/$w.png" ] || sh "$HERE/super.sh" "$w" latte "$L/$w.png" > /dev/null; done
E="$OUT/sizzle_${CLIP}_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$E" -loop 1 -t 4.7 -i "$L/Union.png" -loop 1 -t 4.7 -i "$L/Difference.png" -loop 1 -t 4.7 -i "$L/Intersection.png" \
  -filter_complex "[1:v]format=rgba,fade=t=in:st=0.15:d=0.25:alpha=1[u];[0:v][u]overlay=enable='lt(t,1.5)'[a];[a][2:v]overlay=enable='between(t,1.5,3.1)'[b];[b][3:v]overlay=enable='gt(t,3.1)',format=yuv420p[v]" \
  -map "[v]" -frames:v 282 -c:v libx264 -preset slow -crf 16 -r 60 -movflags +faststart "$OUT/tmp_labels.mp4"
mv -f "$OUT/tmp_labels.mp4" "$E"
"$FF" -hide_banner -loglevel error -y -i "$E" -vf "select='not(mod(n\,47))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$WORK/$CLIP/contact.png"
echo "edit (with labels) -> $E ; contact sheet: $WORK/$CLIP/contact.png"
