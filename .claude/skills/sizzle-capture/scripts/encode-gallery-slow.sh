#!/bin/sh
# Clip 13b v2: the three FFunction gallery takes (clip13b-*.cs, already encoded as $WORK/c13b_parts/*_t01.mp4) cut
# longer (1.9 s each instead of 1 s) and joined with 0.4 s dissolves instead of hard cuts, each labelled with its
# .glsl file name, so it reads as "three more functions" rather than three flashes.
#   encode-gallery-slow.sh [out-dir]   (default ~/Downloads/freppuccino-sizzle/v2/c13b_gallery)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${1:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/v2/c13b_gallery}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
SRC="$WORK/c13b_parts"; L="$WORK/c13b_gallery_v2"; mkdir -p "$OUT" "$L"
IN=0.8; LEN=1.9; D=0.4
calc() { node -e "console.log($1)"; }
for n in nautilus vase tet; do
  [ -s "$SRC/sizzle_c13b_${n}_t01.mp4" ] || { echo "missing $SRC/sizzle_c13b_${n}_t01.mp4 (encode the clip13b takes first)"; exit 1; }
done
sh "$HERE/super.sh" "nautilus.glsl" latte "$L/nautilus.png" > /dev/null
sh "$HERE/super.sh" "turing_vase.glsl" latte "$L/vase.png" > /dev/null
sh "$HERE/super.sh" "tet_goo.glsl" latte "$L/tet.png" > /dev/null
O1=$(calc "$LEN - $D"); O2=$(calc "2 * ($LEN - $D)"); TOT=$(calc "3 * $LEN - 2 * $D")
END=$(calc "$IN + $LEN"); LO=$(calc "$LEN - $D - 0.25")
seg() { # $1 = take input, $2 = label input. The label arrives after the dissolve in and leaves before the one out.
  printf '[%s:v]trim=start=%s:end=%s,setpts=PTS-STARTPTS[s%s];' "$1" "$IN" "$END" "$1"
  printf '[%s:v]format=rgba,fade=t=in:st=%s:d=0.25:alpha=1,fade=t=out:st=%s:d=0.25:alpha=1[l%s];' "$2" "$D" "$LO" "$1"
  printf '[s%s][l%s]overlay=shortest=1[g%s]' "$1" "$1" "$1"
}
G="$(seg 0 3);$(seg 1 4);$(seg 2 5)"
G="$G;[g0][g1]xfade=transition=fade:duration=$D:offset=$O1[x];[x][g2]xfade=transition=fade:duration=$D:offset=$O2,format=yuv420p[v]"
"$FF" -hide_banner -loglevel error -y \
  -i "$SRC/sizzle_c13b_nautilus_t01.mp4" -i "$SRC/sizzle_c13b_vase_t01.mp4" -i "$SRC/sizzle_c13b_tet_t01.mp4" \
  -loop 1 -t $LEN -i "$L/nautilus.png" -loop 1 -t $LEN -i "$L/vase.png" -loop 1 -t $LEN -i "$L/tet.png" \
  -filter_complex "$G" -map "[v]" -frames:v "$(calc "Math.round($TOT * 60)")" \
  -c:v libx264 -preset slow -crf 16 -r 60 -movflags +faststart "$OUT/sizzle_c13b_gallery_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_c13b_gallery_edit.mp4" -vf "select='not(mod(n\,49))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$L/contact.png"
echo "gallery ($TOT s) -> $OUT/sizzle_c13b_gallery_edit.mp4 ; contact sheet: $L/contact.png"
