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
| A | At the bottom of the panel is an "F-rep display mode button", try clicking that |
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
