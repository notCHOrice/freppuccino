---
name: visual-validation
description: How to read capture_views screenshots and catch common mistakes. Load before your first capture.
---
## What the screenshots show
- The F-rep result as the user sees it: CSG applied, blends applied. White matte surfaces on a grey background.
  Subtracted tools are invisible. The XY grid shows in perspective and top views.
- front: looking along +Y, so X points right and Z up. right: looking along -X, so Y points right and Z up.
  top: looking down, X right, Y up. back / left / bottom are the mirror views.
- perspective looks from front-right-above; perspective_back from back-left-above; perspective_below shows the underside.
- The axis icon in the corner shows the orientation.

## How to check
1. Compare silhouettes with your plan. front and right show heights, widths and whether parts line up. top shows
   footprint and centering.
2. Look for floating parts: a gap between a part and what it should attach to (handle, head, legs). Check two
   orthogonal views; one view can hide a gap behind the object.
3. Look for missing parts. Usually something consumed by the wrong operation, hidden, or placed inside another part.
4. Cavities and holes: is the opening really open? Look at top or perspective for the dark interior. A thin skin
   over an opening means the cutter doesn't stick out far enough.
5. Proportions: compare part sizes with each other and with the real object.
6. Blends: blobby, melted look means k is too big. A hard crease where it should be smooth means k is too small or the
   parts are in different operations.
7. Zoom in on small details with focus: ["part_ref"] and size 768.

## When something is wrong
- Call get_scene and compare the numbers with what you see before changing anything. The screenshot shows
  WHAT is wrong; the numbers tell you WHY.
- Fix with update_primitive / transform / edit_operation. Don't rebuild from scratch.
- Capture again after fixing, with just the views that show the problem.
- Two or three fix rounds is usually enough. If it still isn't right, say what's off instead of looping.
