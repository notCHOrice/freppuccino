---
name: sizzle-capture
description: Render sizzle-reel clips of Freppuccino straight from Rhino 9 (scripted, frame by frame) and encode them with the brand background, supers and fades. Load before making or re-making any clip for the 1-minute sizzle reel, or any scripted video/still capture of the shader viewport.
---
# Sizzle reel capture

The plan (shot list, sign-up, viewport and colour standard, edit notes) lives in the team doc
**Freppuccino Sizzle Reel: Production Plan**: https://claude.ai/artifact/LkEvHTPQugrYsqcRJ4Ztfg . Read the clip's row
in the shot list and its "Per-clip setup" entry first. This skill is how to *produce* a clip by script.

Clips are rendered, not screen-recorded: a C# script inside Rhino builds the scene through the plugin's AI tool API,
steps the animation at 60 fps and saves a transparent capture per frame; ffmpeg adds the background, badge removal,
fade and super. Exact timing, no cursor, repeatable. Clip 01 took ~4 min to render and ~1 min to encode this way.

## Quick start (Windows, from the repo root, Git Bash)

```sh
S=.claude/skills/sizzle-capture/scripts
sh $S/setup-tools.sh                                             # once: ffmpeg into %LOCALAPPDATA%/freppuccino-sizzle/tools
powershell -ExecutionPolicy Bypass -File $S/launch-rhino.ps1     # build plugin, start Rhino 9 + script server, wait
cp $S/clip01-hook.cs $S/clip03-scoop.cs                          # new clip: copy the template, edit SCENE + ANIMATE
W="$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle"; mkdir -p "$W/open3dm"
echo NEW > "$W/open3dm/request.txt" && sh $S/run-clip.sh $S/open-3dm.cs open3dm   # empty doc (clip scripts clear the scene)
sh $S/run-clip.sh $S/clip03-scoop.cs c03_scoop test              # 2 poses (first, last) -> check framing
sh $S/run-clip.sh $S/clip03-scoop.cs c03_scoop full              # every frame
EDIT_LEN=4 SUPER_TEXT="..." sh $S/encode.sh c03_scoop            # -> ~/Downloads/freppuccino-sizzle/c03_scoop/
```

Look at `%LOCALAPPDATA%/freppuccino-sizzle/<clip>/frames/f0000_0.png` after a test run and at `<clip>/contact.png`
after encoding (6 frames of the edit) before calling a clip done. Clip 01 is the worked example:
`FADE_IN=0.5 SUPER_TEXT="Implicit geometry. Live in Rhino." sh $S/encode.sh c01_hook`.

## Scripts (`scripts/`)

| File | What it does |
| --- | --- |
| `launch-rhino.ps1` | `dotnet build -c Debug`, starts Rhino 9 with `RHINO_PACKAGE_DIRS` + `FREPPUCCINO_AUTOSTART=1` and `/runscript="_ScriptEditor"`, waits for `rhinocode list`. `-NoBuild` to skip the build. Refuses to build while Rhino runs (it locks the .rhp). |
| `clip01-hook.cs` | Clip 01, and the template: HARNESS (leave alone) + SCENE + ANIMATE. Refuses to run on a saved document (it clears the scene). |
| `open-3dm.cs` | Opens `$SIZZLE_WORK/open3dm/request.txt` (`<path>[|#colour]`, or `NEW` for an empty document) in its own run. |
| `check-script.sh` | Compiles a clip script against Rhino's RhinoCommon. `run-clip.sh` calls it. |
| `run-clip.sh` | check, `rhinocode script <abs path>`, nudge Rhino to the foreground, wait for `done.txt` / `error.txt`. |
| `nudge-rhino.ps1` | Brings Rhino forward ~4 s so it picks the script up, then gives focus back. |
| `encode.sh` | Averages the 4 jittered captures, clears the badge, lays over the gradient; writes `_t01.mp4` (whole take), `_edit.mp4` (the cut, fade + super), `_still.png`, and a contact sheet. Options are env vars, see its header. |
| `super.sh` + `super.html` | A super as a transparent 1920x1080 PNG (Fredoka SemiBold, Caramel bar) via headless Edge. |
| `bg.mjs` | Dithered 2-colour gradient PNG (Latte / Espresso). |
| `gallery.sh` (+ `gallery-stills.cs`, `stills.sh`) | One composited still per saved .3dm: `gallery.sh <out-dir> "<file.3dm>|#e0954a" ...` (the colour goes on every F-rep primitive). Opens each file in one run, captures it in the next. Made the FFunction examples preview in the plan doc (7 files, ~4.5 min). |
| `mock-frame.mjs` | Rhino-free mock-up frame with the shader's shading maths, for previews and the doc's reference frames. |
| `setup-tools.sh` | `npm install ffmpeg-static@5.2.0` into the work folder (no system install). |

Work files go to `%LOCALAPPDATA%/freppuccino-sizzle/<clip>/` (override with `SIZZLE_WORK`); deliverables to
`~/Downloads/freppuccino-sizzle/<clip>/`. Name clips `cNN_slug` (c01_hook, c03_scoop, ...).

## Writing a clip script

- Scene: `Tool("create_primitive" | "combine" | "deform" | "edit_operation" | "set_color" | ..., json)` is
  `Freppuccino.Ai.AiTools.Run` called by reflection; argument shapes are in `src/Freppuccino/Ai/AiShapes.cs` and
  `AiTools.cs` (sphere = center, radius; cylinder = start, end, radius; combine = op, operands, k; ...). Every call is
  an undo step; that's fine in a throwaway document. Animate a value by calling `edit_operation` (k) or the matching
  tool every frame. Colours: brand hex values from the plan doc, set at create time (`"color":"#3b2620"`).
- Camera: set `target` and the camera point per frame and call `Capture(i, target, camera)`. Ease every move with
  `Smooth()` (smootherstep). One move per clip, orbits clockwise (plan doc).
- `test` mode renders only the first and last pose (`TimeOf`), so write the animation as a function of `t` seconds.
- Shading is flat: the shader lights by surface facing only (78% to 100%), no lamps or shadows. Dark colours read as
  silhouettes, so form comes from the outline and colour blends. Camera 10 to 30 degrees above.
- Framing used in clip 01 (objects about 2.6 units wide): 50 mm lens, camera 6.1 -> 5.35 units away. 85 mm was far
  too tight at that distance. Put the subject right of centre when the clip has a super.
- Make the change visible: clip 01 starts the spheres 0.09 apart so the blend pulls them together.
- FFunction pieces (`examples/ffunction/*.3dm`, used by clips 11, 13, 13b): open the file with `open-3dm.cs` in its
  own run (NOT inside the clip script, see Gotchas), drop the template's clear_scene/create calls and its saved-document
  guard, then animate any `param` with
  `Tool("set_function_params", "{\"target\":\"<ref>\",\"params\":{\"cellTop\":4.2}}")` per frame (`get_scene` lists refs).
  Whole-number params (`wings`, `cells`, `seed`) only change in steps: cut on them. The pieces are 45-70 units across.
- Colour: the example files are default white (invisible on Latte); Espresso flattens detailed pieces into blobs.
  Use Caramel `#e0954a` or Mocha `#8a5a3c` (`FrepPrimitives.SetColor`, see `gallery-stills.cs`).

## Gotchas (each one cost time on clip 01)

| Symptom | Cause | Fix (already in the scripts) |
| --- | --- | --- |
| `rhinocode list` is empty although Rhino is open | The RhinoCode server starts only when the Script Editor loads | Launch with `/runscript="_ScriptEditor"` (`launch-rhino.ps1`) |
| `rhinocode script` returns, nothing happens | Relative script path: silently ignored | Always pass an absolute Windows path (`cygpath -aw`) |
| Script runs minutes later, or only after clicking Rhino | Rhino starts queued scripts only while its window is in the foreground | `nudge-rhino.ps1` (focus ~4 s, then back) |
| The nudge "works" but Rhino stays behind | Windows blocks `SetForegroundWindow` from background processes | `nudge-rhino.ps1` uses the ALT-key + `AttachThreadInput` tricks and checks the foreground window, exits 1 if it failed |
| `run-clip.sh` waits forever, script did run | `<clip-id>` differed from the script's `ClipId`, so it watched the wrong folder (and the script wiped the other clip's frames) | `run-clip.sh` refuses a mismatch |
| Script never runs, no error anywhere | It failed to compile; Rhino reports compile errors nowhere (no log, no command line) | `check-script.sh`. Known trap: `CaptureToBitmap(view) ?? throw ...` needs `System.Private.Windows.*` refs (CS0012); use `?.` |
| Runtime errors invisible | `rhinocode` gives no output | The harness catches everything into `error.txt`; `run-clip.sh` prints it |
| Extra FREP MODE badges, one on the subject | Captures larger than the on-screen viewport render in tiles; the badge is drawn in each tile | Capture at 1920x1080 or less with Perspective maximised (the harness checks). The plan's 2560x1440 minimum doesn't apply to scripted clips |
| Jagged silhouettes, crawling on camera moves | One capture per frame has no anti-aliasing | 4 captures per frame with rotated-grid sub-pixel camera shifts, averaged (`Capture()` + `encode.sh`) |
| Dark or light fringes after averaging | Averaging straight alpha | `premultiply` -> `mix` -> `unpremultiply` in `encode.sh` |
| FREP MODE badge and grey control outlines in captures (outlines very visible on FFunction clip boxes) | The conduit draws both in every capture, unless `AiCapture.Active` | The harness holds `AiCapture.CleanCapture()` (plugin, main since 2026-11-14) while capturing: just the model. Dispose it in `finally`, or the user's views lose badge + outlines. Older builds (gyroid / displacements branches) lack it: there `encode.sh` still clears the badge corner, and those builds have no outlines |
| Captures right after opening a .3dm show bare wireframes (sometimes one file works) | Rhino finishes setting up a freshly opened document's views only after the opening script ends; redraws / `RhinoApp.Wait()` / sleeps inside the same run don't help | Open in one run (`open-3dm.cs`), capture in the next (`gallery.sh` does this) |
| Same, on the first frames after a restart | The shader cache is cold; FFunction code takes seconds to compile | The harness calls `ShaderConduit.ShaderError(doc)` before every capture: compiles now (cached), throws on a build error |
| Stills of a saved .3dm framed in a hidden view | Files save their own view layout; `Views.Find("Perspective")` can return a hidden view | Use `doc.Views.ActiveView` |
| `dotnet build`: MSB3027 "locked by Rhino 9" | A running Rhino holds `Freppuccino.rhp`; it compiles but the old plugin stays loaded | Close Rhino first (`launch-rhino.ps1` refuses to build while it runs); after a force-close, delete the `.3dm.rhl` lock file it leaves (read-only: `Remove-Item -Force`) |
| A clip script wipes an example or someone's model | The template's SCENE block starts with `clear_scene` | The harness refuses a document with a path; `open-3dm.cs` with `NEW` makes an empty one |
| ffmpeg "Cannot allocate memory" | A looped still `split` inside the graph buffers thousands of frames | Bound looped inputs with `-t`, no split of the background |
| Super in the wrong weight | ffmpeg `drawtext` can't choose a variable font's weight | `super.sh` renders it in Edge |
| Headless Edge hangs forever | It attaches to the user's running Edge profile | Isolated `--user-data-dir` (in `super.sh`); `--disable-gpu` avoids a silent GPU crash |
| Super PNG sometimes missing on the first run | Edge's first launch with a fresh profile is flaky | `super.sh` checks the file, retries up to 3 times |
| No ffmpeg on the machine | | `setup-tools.sh` (npm `ffmpeg-static`) |
| Banding in gradients | 8-bit gradient through H.264 | `bg.mjs` dithers +-0.5 LSB |

Also: the black coffee cup in `ai-runs/` is not a capture bug; colours render correctly in captures on Windows (D3D11).
Shell `rm` on a variable path is blocked by the agent safety check; use `"${DIR:?}/file"`. In Git Bash,
`VAR=x time cmd` fails (`time` is a keyword): put the env vars right before `sh`, without `time`.
Never take full-screen screenshots to debug Rhino: they capture the user's other apps. Capture only Rhino's own
windows (`PrintWindow` on its handles); its command line shows script output and progress.
Timings on the dev laptop: test run ~8 s; full 7 s take (1680 captures) ~1.5-4 min; encode ~1 min.

## After a clip

1. Check `contact.png` and the edges at 100% (`ffmpeg -i still.png -vf crop=480:300:X:Y,scale=960:600:flags=neighbor`).
2. Copy the clip script next to the deliverables if it isn't in the repo yet, and keep both.
3. Update the clip's batch status in the plan doc; add any new gotcha to the table above and to the doc's
   "Scripted capture" section.
