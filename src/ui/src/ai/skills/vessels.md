---
name: vessels
description: Cups, mugs, bowls, vases, bottles, pots: hollow shapes, wall thickness, handles, rims, saucers.
---
## Hollowing
- Outer body minus inner cavity. The cavity is the same shape, inset by the wall thickness on the sides and the bottom,
  and it extends ABOVE the rim (by 5-10 mm) so the top is open.
- Wall thickness: ceramic cup 3-5 mm, glass 2-3 mm, bowl 4-6 mm. Bottom thickness 5-8 mm.
- Tapered vessels: use a cone frustum (start = bottom, end = top, end_radius > start_radius). Inset both radii by the
  wall thickness. Because of the taper, keep the cavity's slope the same so the wall stays even.
- A small k (1-2 mm) on the subtract rounds the rim edge nicely.

## Handles
- A torus standing up beside the body: axis [0,1,0] puts the ring in the XZ plane, on the +X side when center.x > 0.
- Place center.x so the ring overlaps the wall: roughly outer_radius + major_radius - minor_radius. Center z at
  about 55% of the height. Typical mug: major_radius 18-22 mm, minor_radius 4-6 mm.
- Union the handle with the body (k 2-4 mm for a smooth joint) BEFORE subtracting the cavity. The cavity then removes
  the half of the torus that pokes inside, so you don't need a separate cutter.
- For a D-shaped or squarer handle: two short horizontal cylinders out from the wall plus one vertical cylinder joining
  their ends, unioned with k ≈ the cylinder radius.

## Proven recipe: coffee mug (mm)
1. body: cone, start [0,0,0], end [0,0,95], start_radius 36, end_radius 40
2. handle: torus, center [52,0,50], axis [0,1,0], major_radius 18, minor_radius 5
3. cavity: cone, start [0,0,7], end [0,0,105], start_radius 32, end_radius 36
4. N_a = union(body, handle, k 3), then N_b = subtract(N_a, cavity, k 1.5)
Check: front view shows the handle attached on the right and the walls; top view shows the open interior.

## Saucer / foot / base
- Saucer: a flat cone frustum (height 8-12 mm, radius ~1.8 × cup base) minus a shallow sphere or ellipsoid dish,
  under the cup (cup moved up by the saucer thickness). Keep it a separate root or union with k 0.
- Foot ring on a bowl: a short cylinder or frustum under the body, unioned with k ~2.
