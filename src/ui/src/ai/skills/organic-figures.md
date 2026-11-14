---
name: organic-figures
description: Animals, characters, toys (rubber duck, cat, snowman, mushroom...): smooth blended forms built from ellipsoids and spheres.
---
## Approach
- Block the big masses first (body, head) with ellipsoids and spheres, blended with a generous k. Then add mid-size
  parts (limbs, tail, beak, ears), then small details (eyes, nostrils) in their own operations with small k.
- Ellipsoids are the workhorse: stretch along the direction of the form. Tilt them with x_axis / y_axis
  (e.g. x_axis [1,0,0.4] tilts the long axis up toward +X).
- Blend k for big masses: 15-30% of the smaller mass's radius (neck between head and body). Limbs: ~10%. Details: 0-5%.
- Facing: put the front of the figure toward +X (the right in the front view) unless asked. Keep it symmetric about the
  XZ plane (y = 0): mirrored parts get y and -y.
- Keep the bottom on z = 0. For a flat base, intersect the final shape with a large box spanning z ≥ 0, or subtract a
  box below z = 0.
- Eyes: small spheres sitting slightly into the head surface, unioned with k 0. Or subtract small spheres for sockets.

## Proven recipe: rubber duck (mm, ~90 long, facing +X)
1. body: ellipsoid, center [0,0,28], radii [42,32,28]
2. tail: ellipsoid, center [-38,0,42], radii [14,12,9], x_axis [1,0,-0.8]  (a raised tail tip)
3. head: sphere, center [26,0,68], radius 20
4. beak: ellipsoid, center [48,0,64], radii [12,9,4]
5. eye_l / eye_r: spheres, radius 3.5, centers [40, 9, 74] and [40, -9, 74]
6. N_a = union(body, tail, k 8); N_b = union(N_a, head, k 12); N_c = union(N_b, beak, k 2); N_d = union(N_c, eye_l, eye_r, k 0)
7. Optional flat bottom: N_e = intersect(N_d, base_box) with base_box: box center [0,0,60], size [200,200,120]
Check: front view shows the head above the body's front half and the beak pointing +X; top view is symmetric.

## Snowman / stacked forms
- Spheres decreasing ~0.7× each level, each sunk ~15% into the one below, union k ~10% of the smaller radius.

## Mushroom / dome shapes
- Cap: ellipsoid intersected with a box above the cut line (or minus a box below). Stem: cone frustum, k blend.
