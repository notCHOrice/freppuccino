#!/bin/sh
# Assemble the reel from the clips' edits in shot-list order: hard cuts, then a short fade into the end card.
# Skips clips that don't exist yet (and says which). Writes two versions:
#   <out>/sizzle_reel.mp4         clean
#   <out>/sizzle_reel_review.mp4  with the clip number + running time in the top-right corner, for feedback
#   <out>/sizzle_reel_clips.txt   the cut list (clip, in, length)
# Usage: reel.sh [out-dir]       (default <root>/reel; root = $SIZZLE_OUT or ~/Downloads/freppuccino-sizzle/v2). No music yet: silent.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
ROOT="${SIZZLE_OUT:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/v2}"   # v2 = the cut after the 2026-11-15 review; v1 is one level up
OUT="${1:-$ROOT/reel}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
FADE=0.5
mkdir -p "$OUT"
# shot-list order: label  file (relative to ROOT)
# v2 (2026-11-15 review): 4, 5, 8 and 12 dropped; 1, 2, 3, 6, 7, 13, 13b, 17 re-made (new clip ids); 11 + 16 copied from v1.
ORDER="1:c01_orbit/sizzle_c01_orbit_edit.mp4 2:c02_build/sizzle_c02_build_edit.mp4 3:c03_booleans/sizzle_c03_booleans_edit.mp4
6:c06_zebra_swirl/sizzle_c06_zebra_swirl_edit.mp4 7:c07_rope/sizzle_c07_rope_edit.mp4 9:c09_patterns/sizzle_c09_patterns_edit.mp4
10:c10_knurl/sizzle_c10_knurl_edit.mp4 11:c11_gyroid/sizzle_c11_gyroid_edit.mp4 13:c13_code_slow/sizzle_c13_code_slow_edit.mp4
13b:c13b_gallery/sizzle_c13b_gallery_edit.mp4 14:c14_ai/sizzle_c14_ai_edit.mp4 15:c15_image/sizzle_c15_image_edit.mp4
16:c16_mesh/sizzle_c16_mesh_edit.mp4 17:c17_end/sizzle_c17_end_edit.mp4"
dur() { "$FF" -hide_banner -i "$1" -map 0:v -c copy -f null - 2>&1 | grep -oE 'frame= *[0-9]+' | tail -1 | grep -oE '[0-9]+'; }
INPUTS=""; LABELS=""; FRAMES=""; MISSING=""; n=0
for e in $ORDER; do
  label="${e%%:*}"; f="$ROOT/${e#*:}"
  if [ -f "$f" ]; then INPUTS="$INPUTS -i $f"; LABELS="$LABELS $label"; FRAMES="$FRAMES $(dur "$f")"; n=$((n + 1))
  else MISSING="$MISSING $label"; fi
done
[ "$n" -ge 2 ] || { echo "need at least 2 clips"; exit 1; }
# filter: concat all but the last, then xfade into the last (the end card)
G=""; i=0; for _ in $LABELS; do [ $i -lt $((n - 1)) ] && G="$G[$i:v]"; i=$((i + 1)); done
G="${G}concat=n=$((n - 1)):v=1:a=0,settb=AVTB,fps=60[body];[$((n - 1)):v]settb=AVTB,fps=60[endcard]"   # xfade needs matching timebases
BODY_FRAMES=0; i=0; for fr in $FRAMES; do [ $i -lt $((n - 1)) ] && BODY_FRAMES=$((BODY_FRAMES + fr)); i=$((i + 1)); done
OFFSET=$(node -e "console.log(($BODY_FRAMES / 60 - $FADE).toFixed(3))")
G="$G;[body][endcard]xfade=transition=fade:duration=$FADE:offset=$OFFSET,format=yuv420p[v]"
# shellcheck disable=SC2086
"$FF" -hide_banner -loglevel error -y $INPUTS -filter_complex "$G" -map "[v]" -c:v libx264 -preset slow -crf 17 -r 60 -movflags +faststart "$OUT/sizzle_reel.mp4"

# cut list + review labels (drawtext, one enable window per clip)
: > "$OUT/sizzle_reel_clips.txt"
FONT="$(echo "$REPO/brand-kit/fonts/Fredoka.ttf" | sed 's|^\([A-Za-z]\):|\1\\\\:|')"
DT=""; t=0; i=0; set -- $FRAMES
for label in $LABELS; do
  fr="$1"; shift
  len=$(node -e "console.log(($fr / 60).toFixed(2))")
  [ $i -eq $((n - 1)) ] && t=$(node -e "console.log(($BODY_FRAMES / 60 - $FADE).toFixed(3))")
  end=$(node -e "console.log(($t + $len).toFixed(3))")
  printf '%-4s in %6.2f s  length %5.2f s\n' "$label" "$t" "$len" >> "$OUT/sizzle_reel_clips.txt"
  DT="$DT,drawtext=fontfile='$FONT':text='clip $label':x=w-tw-40:y=36:fontsize=34:fontcolor=white:box=1:boxcolor=0x1e1512@0.6:boxborderw=12:enable='between(t,$t,$end)'"
  t=$end; i=$((i + 1))
done
DT="$DT,drawtext=fontfile='$FONT':text='%{pts\\:hms}':x=w-tw-40:y=96:fontsize=26:fontcolor=white:box=1:boxcolor=0x1e1512@0.6:boxborderw=10"
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_reel.mp4" -vf "${DT#,}" -c:v libx264 -preset medium -crf 20 -movflags +faststart "$OUT/sizzle_reel_review.mp4"
TOTAL=$(node -e "console.log(($BODY_FRAMES / 60 - $FADE + $(echo $FRAMES | awk '{print $NF}') / 60).toFixed(2))")
echo "reel: $n clips, ${TOTAL} s -> $OUT/sizzle_reel.mp4 (+ _review.mp4, _clips.txt)"
[ -n "$MISSING" ] && echo "not in the reel yet (no edit file):$MISSING"
exit 0
