<p align="center">
  <img src="art/logo.svg" alt="Freppuccino icon" width="80">
</p>

# Freppuccino

Freppuccino makes implicit geometry available natively in Rhino for everyone.

---

# What is implicit geometry?

Implicit geometry is a geometry type that is made parametrically using functions (don't worry, you don't need to know the functions!).

---

# How do I get it?

1. Check the latest releases on this project
1. Download the latest yak
1. Drag and drop the yak onto your Rhino

**OR**

1. Download the yak from the package manager inside Rhino by running the `PackageManager` command

---

# How do I use it?

1. Run the `Freppuccino` command to open the Panel.
1. If the panel shows "This view isn't showing F-reps" at the top, click **F-rep display mode** to switch the active viewport to the Freppuccino display mode
1. Use the buttons on the panel to create simple geometry types like a box or a sphere
1. Select 2+ F-reps and choose a blend style – Union, subtract or intersect.
1. Click "Apply Union"
1. Modify the blend radius
1. You're done! Move and rotate objects as you would normally in Rhino.
1. Run `FrepMesh` to mesh all of your F-reps to clean meshes

---

# FAQ

| Q/A | Answer |
|---|---|
| Q | I can't see the F-reps! |
| A | Click the "F-rep display mode" button at the top of the panel. It shows whenever the active viewport isn't in the Freppuccino display mode. You can also pick "Freppuccino" from Rhino's display mode menu |
| | |
| Q | I can't seem to connect two geometries? |
| A | The objects MUST be F-reps, regular boxes are not compatible (yet). |
| | |
| Q | The yak does not work for me? |
| A | This project only works for Rhino 9 or newer |
| | |
| Q | `The duck`? It haunts my nightmares and scares my cat! |
| A | Our research suggests that `The duck` does not exist, and as such, should be ignored. |

---

# BUILD

Freppuccino is very easy to build.
Run `dotnet build` from the root.
To debug, use the `Rhino 9 + Freppuccino` launch profile in VSCode.

## Yak package

Run `powershell -ExecutionPolicy Bypass -File yak/build-yak.ps1` (or the `build yak` VS Code task).
This builds Release, bundles the plugin, side panel UI and native shader libraries, and writes
`artifacts/yak/freppuccino-<version>-rh9_0-any.yak`.

- Install locally: drag the `.yak` onto Rhino 9, or run `"C:\Program Files\Rhino 9\System\Yak.exe" install artifacts\yak\<file>.yak`
- Options: `-Platform win|mac|any` (default `any`), `-Version 0.2.0` (default: `<Version>` in Freppuccino.csproj), `-Yak <path to Yak.exe>`
- Private repository: `-Repo \\server\share\rhino-packages` copies the `.yak` into that folder and runs `yak cache`. Add the folder to `Rhino.Options.PackageManager.Sources` (Options → Advanced), separated from `https://yak.rhino3d.com` by `;`.
- Package metadata (authors, description, keywords, icon) lives in `yak/manifest.yml` and `yak/icon.png`.

## CI/CD

`.github/workflows/yak.yml` runs on every push to `main`, every pull request, on `v*` tags, and manually (Actions → Yak → Run workflow).
CI never pushes to the Yak server and needs no secrets.

- Every run: unit tests + a `.yak` build, uploaded as a workflow artifact (version `<csproj Version>-ci.<run number>`).
- Tag `v1.2.3` (or `v1.2.3-beta.1`): also creates a GitHub Release with the `.yak` attached.

Releasing: bump `<Version>` in `Freppuccino.csproj`, then `git tag v0.2.0 && git push origin v0.2.0`.
Publishing to the Rhino Package Manager is done by hand: download the `.yak` from the release,
run `"C:\Program Files\Rhino 9\System\Yak.exe" login`, then `Yak.exe push <file>.yak`
(try `--source https://test.yak.rhino3d.com` first).

---

# Authors

- Ben Howes
- Callum Sykes
- Dan Cascaval
- Danniel Cho Valadao
- Luke Gehron
- Mathieu Huard
- Sam Potter
- Sergey Pigach
