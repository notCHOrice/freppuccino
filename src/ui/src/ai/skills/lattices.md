---
name: lattices
description: Gyroids, TPMS lattices (Schwarz P, diamond, Neovius), foams and lattice infill with FFunctions; graded lattices, skins, infill inside a primitive body. Load with freppuccino-ffunction.
---
Lattices are FFunctions. Load `freppuccino-ffunction` too (DSL rules, Lipschitz).

## Start from an example
`get_function_example` with no name lists the bundled ones; with a name it returns the code and ready
`create_function` arguments. Adapt one (center, params, size) rather than writing lattice code from scratch:

| want | example | notes |
|---|---|---|
| lattice block, any TPMS | `tpms_block` | `surface` 1 gyroid, 2 Schwarz P, 3 diamond, 4 Neovius; `network` 1 = solid struts instead of a sheet; walls graded `thickBot` → `thickTop` |
| object with lattice infill + skin | `tpms_sphere` | infill inside a sphere skin, front cut open by `cutDepth` (0 = closed) |
| foam | `voronoi_foam` | closed-cell Voronoi walls in a rounded brick; `seed` gives a new foam |
| graded, swirled gyroid | `gyroid` | meant to be intersected with a body (see its note) |
| sculptural | `scherk_tower`, `nautilus`, `turing_vase`, `michell_bracket`, `tet_goo`, `helicatenoid` | showpieces; tune their params |

Examples are written Y-up (the DSL library convention), so their box uses `x_axis [1,0,0]`, `y_axis [0,0,1]`: local Y =
world Z. `size` is then [width, height, depth]. Keep those axes when you move one (change `center` only).

## Sizing
- Cell (period): 4–8 cells across the part reads as a lattice; fewer looks like holes, more looks like texture.
- Wall thickness: 8–15% of the cell for a sheet; ≥ 2% of the part size or it renders broken.
- The tpms examples pass L 1.5 (their fields are normalised to about 1.15). Keep it.
- Grade (vary cell / thickness with height) with `mix(a, b, t)`, t = clamp((p.y + halfH) / (2 * halfH), 0, 1). Grading the
  cell changes the phase: integrate the frequency as the `gyroid` example does, or the cells shear.

## Lattice inside a primitive body (two ways)
1. All in one function (best: one primitive, exact skin): copy `tpms_sphere` and replace `ball` with the body you need,
   built from library calls (`sdEllipsoid`, `sdBox(p, b) - r`, `sdCappedCylinder`, ...). Keep the skin as
   `max(body, -(body + skin))`.
2. With the tree (when the body is already modeled, or must stay editable as primitives): make a `tpms_block` box a
   little larger than the body, then `combine` op=intersect [lattice, body] with k≈0.5–1 mm for soft edges. For a skin,
   create a second, slightly smaller copy of the body, `combine` op=subtract [body_copy, inner] and union that with the
   intersection. Each copy is a primitive, so keep the count low.

## Check it
- capture_views; speckles or holes = L too low (raise by 0.25); flat cut faces you didn't want = clip box too small.
- Lattices hide their interior: use a cut (`cutDepth`, or subtract a box) when the user wants to see the infill.
- `set_zebra` on, capture, off: shows whether the infill blends smoothly into the skin.
- For a mesh (`mesh`), resolution ≈ 8 × (part size / cell), capped at 384.
