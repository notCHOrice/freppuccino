# The PLAN

# Notes

- Project name is Freppuccino

# The Goal

We're working on adding support for implicit geometry modelling in Rhino 9.
Users should be able to model in real time and see changes to, and any transformations of the objects represented as F-Rep immediately. The user will edit the geometry using the gum-ball. Users edit rhino objects as they would normally, with the gum-ball and commands.

This project is for industrial designers, aerospace, boolean and fillet aficionados.

# High level Architecture

- Rhino plugin
- Custom commands for each geometry type (FBox, FSphere, etc.)
- F-reps are normal rhino (geometry) objects such as a Brep, Surface etc. that contain parameter metadata created during the commands, a cone would have a base diameter and a height, a sphere a diameter.
- All F-rep geometry types have a function defining them

# Technical details
- Users switch on a F-rep display mode
  - The display mode renders to the GPU using a shader (An F-rep pipeline)
    - The reason for this is the technical constraints of Rhino, i.e we cannot add a new geometry type that would work fast enough.

# Features
- Command to convert F-reps to mesh
- Commands to create F-rep objects


# Supported Rhino versions
- Rhino 9 (Windows + Mac): `net10.0` build, RhinoCommon 9. Viewport via Direct3D 11 (Windows) or Metal (Mac)
- Rhino 8.19+ (Windows): `net8.0` build, RhinoCommon 8.19. Viewport via OpenGL (`Gpu/OpenGL/`), shaders cross-compiled to GLSL 410
  - Rhino 9-only APIs are behind `#if RHINO9` (gumball settings, silhouettes, Direct3D 11); `Gpu/D3D11/` is not compiled for `net8.0`
  - .NET 10-only APIs have `net8.0` stand-ins in `Compat/Net8Compat.cs`
  - Rhino 8 on Mac is not shipped: untested, and it relies on undocumented Rhino Metal exports
- Rhino 7 is not supported (.NET Framework 4.8)

# Distribution
- Shipped as two Yak packages per version: `rh9_0-any` (Rhino 9, Windows + Mac) and `rh8_19-win` (Rhino 8, Windows)
  - `yak/build-yak.ps1` builds Release for both and packages the plugin, side panel UI and shaderc / SPIRV-Cross native libraries (`-Rhino 9` or `-Rhino 8` for one)
  - Push both `.yak` files for a release; the Package Manager picks the right one per Rhino version
  - `yak/manifest.yml` holds the package metadata: every contributor, description, keywords, `yak/icon.png`
  - New contributors must be added to both `yak/manifest.yml` and the README Authors list
- CI (`.github/workflows/yak.yml`) runs tests and builds the `.yak` on every push / PR, and attaches it to a GitHub Release on `v*` tags
- CI does NOT publish to the Yak server and holds no Yak token; pushing to the Package Manager is a manual `yak push`

# Glossary

- F-rep -> Function-REPresentation
