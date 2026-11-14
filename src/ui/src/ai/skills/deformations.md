---
name: deformations
description: Twist, bend and repeat (deform tool) - spirals, swirls, coils, ropes, hooks, curved parts, arrays and patterns. Recipes: soft-serve ice cream, braided rope, candy cane, spring, twisted column, toy brick, picket fence.
---
## What each deform does
A deform wraps ONE target (primitive or node) and is drawn instead of it. Build the straight, undeformed part first, then
deform its top node. Deforms stack: twist a node, then bend the twisted node.

- twist(axis_start, axis_end, angle_degrees): rotates the shape around the axis line. Rotation is 0 at axis_start and
  angle_degrees at axis_end, growing linearly (and it keeps growing past both ends). Turns = angle / 360.
  A shape ON the axis spins in place (fluted shapes become swirls). A shape OFF the axis winds around it (helix).
- bend(axis_start, axis_end, angle_degrees, toward): the stretch of the shape between axis_start and axis_end is curved into
  an arc of angle_degrees, bending toward the "toward" direction. Before axis_start nothing changes; past axis_end the shape
  continues straight (rigidly), at the new angle. Bend radius = axis length / angle in radians.
  Make the axis cover exactly the part you want curved, along the shape's centerline. 180° turns a straight bar into a U or hook.
- repeat(count, spacing, centered): copies the target count[i] times, spacing[i] apart, along the lattice axes (world X, Y, Z
  unless x_axis / y_axis are given). The target stays as one of the copies: the first one (centered false, default), or the
  middle one (centered true). Use count 1 and spacing 0 on axes you don't repeat along.
  Model ONE copy (for example the first stud), already in its first position, then repeat it.

Defaults: without axis_start / axis_end, twist and bend run along the target's bounding box from bottom center to top center.

## Rules of thumb
- Twist steepness: shear = (angle in radians / axis length) × the shape's distance from the axis. Up to ~10 renders fine;
  the tool warns past 12. A warning means: smaller angle, longer axis, or keep the shape closer to the axis.
- Off-axis strands (coils, rope strands, stripes) lean as they wind, so they look thinner. Make them ~1.5× the radius you want.
- Bend: keep the bend radius larger than the shape's half thickness, or the inside of the bend pinches (the tool warns).
- Combine strands BEFORE twisting (one union, then one twist), so they wind together.
- Repeat copies and the original share edits: update the original and every copy follows.
- Grey parts on the grey screenshot background are hard to judge. Give parts saturated colors (or white) while checking.
- Deform axis lines and repeat boxes are real Rhino objects the user can move to adjust the deform; they are named
  <target>_<op>_axis / _box and are deleted with the deform node.

## Proven recipe: soft-serve ice cream (mm)
1. waffle_cone: cone start [0,0,0] end [0,0,110], start_radius 3, end_radius 30, color "#D9A15B"
2. dollop: ellipsoid center [0,0,114], radii [33,33,12], color "#FFF1D0"
3. Four petals, color "#FFF1D0": cones from start [11,0,110] to end [1.5,0,178], start_radius 15, end_radius 0.8, and the
   same rotated 90° each time: start [0,11,110] end [0,1.5,178]; start [-11,0,110] end [-1.5,0,178]; start [0,-11,110] end [0,-1.5,178]
4. swirl = union(petals, k 3); twisted = twist(swirl, axis [0,0,110] → [0,0,178], angle 420)
5. cream = union(twisted, dollop, k 5); final = union(cream, waffle_cone, k 2)
Strawberry: cream color "#F6B7C8". Chocolate: "#6B3E26".

## Proven recipe: braided rope, bent into a U (mm, 300 long, along X)
1. Three strands, cylinders from x=0 to x=300, radius 6, around the rope axis (y=0, z=13), 120° apart:
   strand_a start [0,6.5,13] end [300,6.5,13]; strand_b start [0,-3.25,18.63] end [300,-3.25,18.63];
   strand_c start [0,-3.25,7.37] end [300,-3.25,7.37]. Colors "#C8A165", "#B8904F", "#D6B47A".
2. strands = union(a, b, c, k 2)
3. braid = twist(strands, axis [0,0,13] → [300,0,13], angle 2400)   (one turn per 45 mm)
4. bent = bend(braid, axis [0,0,13] → [300,0,13], angle 180, toward [0,1,0])   (radius 95, ends at y≈191)
For a straight rope stop after step 3. For a knot-like loop use angle 300-340 on a longer rope.

## Proven recipe: candy cane (mm)
1. cane_core: cylinder [0,0,0] → [0,0,165], radius 7, color "#FFFFFF"
2. stripe_a: cylinder [5,0,0] → [5,0,165], radius 2.8, color "#D0202E"; stripe_b: same at x = -5
3. stick = union(core, stripe_a, stripe_b, k 0.5)
4. striped = twist(stick, axis [0,0,0] → [0,0,165], angle 1440)   (4 turns)
5. cane = bend(striped, axis [0,0,100] → [0,0,165], angle 180, toward [1,0,0])   (hook of radius ≈ 21)

## Proven recipe: coil spring (mm)
1. wire: cylinder [15,0,0] → [15,0,100], radius 4, color "#C0392B" (15 = coil radius)
2. coil = twist(wire, axis [0,0,0] → [0,0,100], angle 1800)   (5 turns)
Flat ends: union small tori (axis [0,0,1], major 15, minor 4) at z = 2 and z = 98, or intersect with a box z 0…100.

## Proven recipe: twisted column (mm)
1. column_shaft: box center [80,0,100], size [30,30,200]; twisted_shaft = twist(column_shaft, angle 180) (default axis)
2. column_base / column_cap: boxes size [44,44,10] at z 5 and z 195
3. union(twisted_shaft, column_base, column_cap, k 2)

## Proven recipe: toy building brick, 2×4 (mm)
1. brick_shell: box center [0,0,4.8], size [31.8,15.8,9.6]; brick_hollow: box center [0,0,3], size [28.8,12.8,6.4]
2. stud: cylinder [-12,-4,9] → [-12,-4,11.4], radius 2.4; studs = repeat(stud, count [4,2,1], spacing [8,8,0])
3. tube = subtract(cylinder [-8,0,0] → [-8,0,8] radius 3.25, cylinder [-8,0,-1] → [-8,0,9] radius 2.4)
   tubes = repeat(tube, count [3,1,1], spacing [8,0,0])
4. shell = subtract(brick_shell, brick_hollow, k 0.3); brick = union(shell, studs, tubes, k 0.3). One color for all, e.g. "#D7262B".
Check perspective_below: the bottom is open with 3 hollow tubes.

## Proven recipe: picket fence (mm)
1. picket_board: box center [0,0,45], size [8,2,90]; picket_tip: pyramid base_center [0,0,90], height 8, base_size 8
2. picket = union(board, tip); pickets = repeat(picket, count [9,1,1], spacing [14,0,0], centered true)
3. rails: boxes center [0,-2,25] and [0,-2,70], size [126,2,6]; union(pickets, rail_low, rail_high)
