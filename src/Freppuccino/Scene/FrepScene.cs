using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Shaders;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// Primitive kinds the viewport shader can draw.
public enum SdKind { Sphere, Box, Torus, Cylinder, Cone, Ellipsoid, Pyramid }

/// One F-rep primitive: local frame (as written in SDF.Frame.*) + up to three IQ parameters packed in P.
///   Sphere    P.X = s (radius)
///   Box       P = b (half-extents along frame X/Y/Z)
///   Torus     P.X = t.x (major radius), P.Y = t.y (minor radius); axis = frame Y
///   Cylinder  P.X = h (half height), P.Y = r; axis = frame Y
///   Cone      P.X = h (half height), P.Y = r1 (at y = -h), P.Z = r2 (at y = +h); axis = frame Y
///   Ellipsoid P = r (semi-axes along frame X/Y/Z)
///   Pyramid   P.X = h (height), P.Y = s (base side); base center at the origin, apex at +Y
/// Place: optional general affine placement (see <see cref="Placement"/>); world shape = Place × (Frame, P) shape. Null = identity.
public readonly record struct SdPrimitive(SdKind Kind, Plane Frame, Vector3d P)
{
    public Transform? Place { get; init; }
}

/// One live warp node: its reference geometry (Axis for Twist/Bend, Region for Repeat) + params (FrepNode.Params, padded to 3).
/// Extent: child's max distance from the axis (Lipschitz bound clamp). Anchor: child bbox center (Repeat lattice origin).
public readonly record struct SdWarp(FrepOp Op, Line Axis, Box Region, Point3d Anchor, double Extent, Vector3d P);

/// Program == null: nothing to draw. Primitives/Colors/Warps/Ks feed <see cref="FrepShaderGen.SceneUniforms"/>.
/// Colors parallel to Primitives: each owner's Rhino display color (object/layer/material/parent source resolved).
public sealed record FrepSceneData(ShaderSource? Program, IReadOnlyList<SdPrimitive> Primitives, IReadOnlyList<System.Drawing.Color> Colors,
    IReadOnlyList<SdWarp> Warps, IReadOnlyList<double> Ks, int Dropped)
{
    public static readonly FrepSceneData Empty = new(null, [], [], [], [], 0);
    /// Rhino object ids, parallel to Primitives (what the CSG tree's leaves refer to).
    public IReadOnlyList<Guid> Ids { get; init; } = [];
    /// Tree node ids, parallel to Warps.
    public IReadOnlyList<int> WarpIds { get; init; } = [];
    /// Non-empty roots in the shader's map() order (its selection flags follow it).
    public IReadOnlyList<FrepOperand> Roots { get; init; } = [];
    /// The tree this was compiled from.
    public FrepTree Tree { get; init; } = FrepTree.Empty;
    public int Count => Primitives.Count;
}

/// Visible objects in the active doc carrying F-rep metadata (SDF.* user text, written by the F* commands) + the doc's CSG tree,
/// compiled to a shader. The SDF values are the source of truth, not the Rhino geometry. Rebuilt lazily after any
/// doc/object/layer/tree change; blend edits rebuild too but hit the shader cache (k is a uniform).
/// Objects under a dynamic transform (gumball/Move drag) are reported at their in-progress position.
public static class FrepScene
{
    static FrepSceneData scene = FrepSceneData.Empty;
    // Parallel to scene.Primitives; safe since any delete/replace sets dirty.
    static List<RhinoObject> owners = [];
    // Parallel to scene.Warps: the referenced line/box objects.
    static List<RhinoObject> warpOwners = [];
    // Parallel to warpOwners: each object's applied root transforms A (Placement.AppliedKey).
    static List<Transform> warpApplied = [];
    static FrepTree tree = FrepTree.Empty; // the one scene was compiled from
    static uint docSerial;
    static bool dirty = true;

    public static void Install()
    {
        RhinoDoc.AddRhinoObject += Dirty;
        RhinoDoc.DeleteRhinoObject += Dirty;
        RhinoDoc.UndeleteRhinoObject += Dirty;
        RhinoDoc.ReplaceRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ModifyObjectAttributes += (_, _) => dirty = true; // hide/show, layer change, SDF.* user text edits
        RhinoDoc.LayerTableEvent += (_, _) => dirty = true;
        RhinoDoc.EndOpenDocument += (_, _) => dirty = true;
        RhinoDoc.NewDocument += (_, _) => dirty = true;
    }

    public static void MarkDirty() => dirty = true;
    static void Dirty(object? sender, RhinoObjectEventArgs e) => dirty = true;

    public static FrepSceneData Get(RhinoDoc doc)
    {
        if (dirty || doc.RuntimeSerialNumber != docSerial) Rebuild(doc);

        List<SdPrimitive>? prims = null;
        bool reshaped = false;
        for (int i = 0; i < owners.Count; i++)
        {
            if (!owners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            // Under transformed roots the leaf sits at T × canonical: the drag is T⁻¹ X T on its values.
            if (Selection.FrepSelection.InstanceOf(doc, owners[i].Id, tree, scene.Ids) is { } inst && !inst.T.IsIdentity && inst.T.TryGetInverse(out var ti))
                xf = ti * xf * inst.T;
            // A transform the SDF type can't represent (e.g. shearing a box) previews at the original position.
            if (!TryTransform(scene.Primitives[i], xf, out var moved)) continue;
            prims ??= [.. scene.Primitives];
            prims[i] = moved;
            reshaped |= Structure(moved) != Structure(scene.Primitives[i]);
        }
        List<SdWarp>? warps = null;
        for (int i = 0; i < warpOwners.Count; i++)
        {
            if (!warpOwners[i].GetDynamicTransform(out var world) || world.IsIdentity) continue;
            // The drag moves the real object (A × canonical); in canonical space that is A⁻¹ × drag × A.
            var a = warpApplied[i];
            var xf = a.TryGetInverse(out var ai) ? ai * world * a : world;
            var w = scene.Warps[i];
            var axis = w.Axis;
            var region = w.Region;
            if (!(w.Op == FrepOp.Repeat ? region.Transform(xf) : axis.Transform(xf))) continue;
            warps ??= [.. scene.Warps];
            warps[i] = w with { Axis = axis, Region = region };
        }
        // Blend carriers under a drag: preview their roots' matrices as drag × M (the tree is only written on commit).
        var previewTree = tree;
        foreach (var o in doc.Objects.GetSelectedObjects(false, false))
        {
            if (Selection.FrepCarriers.NodeOf(o) is not { } id || !o.GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            if (previewTree.Find(id) is { } n && Placement.IsUsable(xf * n.M)) previewTree = previewTree.WithM(id, xf * n.M);
        }
        if (!ReferenceEquals(previewTree, tree))
        {
            var p2 = prims ?? scene.Primitives;
            var (program2, ks2, _, roots2) = FrepShaderGen.Compile(p2, scene.Ids, previewTree, scene.WarpIds.ToHashSet());
            return scene with { Primitives = p2, Warps = warps ?? scene.Warps, Tree = previewTree, Program = program2, Ks = ks2, Roots = roots2 };
        }

        if (prims == null && warps == null) return scene;
        var next = scene with { Primitives = prims ?? scene.Primitives, Warps = warps ?? scene.Warps };
        if (!reshaped || prims == null) return next;

        // A sphere stretched into an ellipsoid changes the shader's structure, so recompile (cached by source, so once
        // per gesture). If the extra rows don't fit, preview those prims unchanged; the commit still converts them.
        if (prims.Sum(FrepShaderGen.Rows) + FrepShaderGen.ColorRows(prims.Count) > FrepShaderGen.PrimRowBudget(tree))
        {
            for (int i = 0; i < prims.Count; i++)
                if (Structure(prims[i]) != Structure(scene.Primitives[i])) prims[i] = scene.Primitives[i];
            return next;
        }
        var (program, ks, _, roots) = FrepShaderGen.Compile(prims, scene.Ids, tree, scene.WarpIds.ToHashSet());
        return next with { Program = program, Ks = ks, Roots = roots };
    }

    /// What the shader source depends on per primitive (kind, placed or not).
    static (SdKind, bool) Structure(SdPrimitive p) => (p.Kind, p.Place != null);

    /// Same rules as the per-command TransformSdf handlers, applied to in-memory values.
    static bool TryTransform(SdPrimitive p, Transform xf, out SdPrimitive result)
    {
        // Placement rules as in SdfTransforms.TryApply: compose into P once set; else handler, falling back to P.
        if (p.Place is { } place)
        {
            result = p with { Place = xf * place };
            return Placement.TryRows(xf * place, out _);
        }
        if (TryTransformValues(p, xf, out result)) return true;
        result = p with { Place = xf };
        return Placement.TryRows(xf, out _);
    }

    static bool TryTransformValues(SdPrimitive p, Transform xf, out SdPrimitive result)
    {
        result = p;
        var v = p.P;
        Plane f;
        bool ok;
        switch (p.Kind)
        {
            case SdKind.Sphere:
                if (FSphereCommand.TryTransform(p.Frame, v.X, xf, out f, out var s, out _))
                {
                    result = p with { Frame = f, P = new Vector3d(s, 0, 0) };
                    return true;
                }
                // Non-uniform: becomes an ellipsoid, same as the commit in FSphereCommand.TransformSdf.
                if (!FSphereCommand.TryTransformToEllipsoid(p.Frame, v.X, xf, out f, out var er, out _)) return false;
                result = new SdPrimitive(SdKind.Ellipsoid, f, er);
                return true;
            case SdKind.Box:
                ok = FBoxCommand.TryTransform(p.Frame, v, xf, out f, out v, out _);
                break;
            case SdKind.Torus:
                ok = FTorusCommand.TryTransform(p.Frame, new Vector2d(v.X, v.Y), xf, out f, out var t, out _);
                v = new Vector3d(t.X, t.Y, 0);
                break;
            case SdKind.Cylinder:
                ok = FCylinderCommand.TryTransform(p.Frame, v.X, v.Y, xf, out f, out var ch, out var cr, out _);
                v = new Vector3d(ch, cr, 0);
                break;
            case SdKind.Cone:
                ok = FConeCommand.TryTransform(p.Frame, v.X, v.Y, v.Z, xf, out f, out var kh, out var r1, out var r2, out _);
                v = new Vector3d(kh, r1, r2);
                break;
            case SdKind.Ellipsoid:
                ok = FEllipsoidCommand.TryTransform(p.Frame, v, xf, out f, out v, out _);
                break;
            case SdKind.Pyramid:
                ok = FPyramidCommand.TryTransform(p.Frame, v.X, v.Y, xf, out f, out var ph, out var ps, out _);
                v = new Vector3d(ph, ps, 0);
                break;
            default:
                return false;
        }
        if (!ok) return false;
        result = p with { Frame = f, P = v };
        return true;
    }

    static void Rebuild(RhinoDoc doc)
    {
        dirty = false;
        docSerial = doc.RuntimeSerialNumber;
        tree = FrepTrees.Get(doc);

        // Prims first, so uniform rows for blends/warps are reserved up front; prims past the budget are dropped (= missing leaves).
        int budget = FrepShaderGen.PrimRowBudget(tree), rows = 0, dropped = 0;
        var prims = new List<SdPrimitive>();
        var colors = new List<System.Drawing.Color>();
        var newOwners = new List<RhinoObject>();
        var newIds = new List<Guid>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!TryRead(obj.Attributes, out var prim)) continue;
            int r = FrepShaderGen.Rows(prim);
            // Each prim also takes a color slot (4 per row).
            if (rows + r + FrepShaderGen.ColorRows(prims.Count + 1) > budget) { dropped++; continue; }
            rows += r;
            newIds.Add(obj.Id);
            prims.Add(prim);
            colors.Add(obj.Attributes.DrawColor(doc));
            newOwners.Add(obj);
        }

        // Warp reference objects may be hidden (axis lines usually are); deleted = missing.
        var refs = new Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)>();
        var applied = new Dictionary<int, Transform>();
        foreach (var n in tree.Nodes)
            if (FrepWarpMath.TryResolve(doc, n, out var obj, out var axis, out var region, out var a))
            {
                refs[n.Id] = (obj!, axis, region);
                applied[n.Id] = a;
            }

        var (program, ks, warpIds, rootOps) = FrepShaderGen.Compile(prims, newIds, tree, refs.Keys.ToHashSet());
        var bounds = new Bounds(doc, tree, refs);
        var warps = new List<SdWarp>();
        var newWarpOwners = new List<RhinoObject>();
        var newWarpApplied = new List<Transform>();
        foreach (var id in warpIds)
        {
            var n = tree.Find(id)!;
            var (obj, axis, region) = refs[id];
            warps.Add(FrepWarpMath.Make(n, axis, region, bounds.Of(n.Operands[0])));
            newWarpOwners.Add(obj);
            newWarpApplied.Add(applied[id]);
        }

        scene = new FrepSceneData(program, prims, colors, warps, ks, dropped) { Ids = newIds, WarpIds = warpIds, Tree = tree, Roots = rootOps };
        owners = newOwners;
        warpOwners = newWarpOwners;
        warpApplied = newWarpApplied;
    }

    /// <see cref="Bounds"/> ignoring warps (no resolved refs); for command defaults.
    public static BoundingBox BoundsOf(RhinoDoc doc, FrepOperand o) => new Bounds(doc, FrepTrees.Get(doc), []).Of(o);

    /// Bounding box of an operand's result in its parent's space: leaves from their Rhino geometry mapped back by their
    /// primary instance's root transforms T (the real object sits at T × canonical), live warps grown like the shader
    /// bound, node transforms applied.
    sealed class Bounds(RhinoDoc doc, FrepTree tree, Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)> refs)
    {
        readonly Dictionary<FrepOperand, BoundingBox> memo = [];
        Dictionary<Guid, Transform>? primaryT;

        Transform PrimaryT(Guid id)
        {
            if (primaryT == null)
            {
                primaryT = [];
                var leaves = tree.Nodes.SelectMany(n => n.Operands).Where(x => !x.IsNode).Select(x => x.ObjectId).Distinct().ToList();
                foreach (var inst in Selection.FrepInstances.Compute(tree, leaves).Instances)
                    primaryT.TryAdd(inst.Object, inst.T);
            }
            return primaryT.GetValueOrDefault(id, Transform.Identity);
        }

        public BoundingBox Of(FrepOperand o)
        {
            if (memo.TryGetValue(o, out var b)) return b;
            b = BoundingBox.Empty;
            if (!o.IsNode)
            {
                if (doc.Objects.FindId(o.ObjectId) is { IsDeleted: false } obj)
                {
                    b = obj.Geometry.GetBoundingBox(true);
                    var t = PrimaryT(o.ObjectId);
                    if (!t.IsIdentity && t.TryGetInverse(out var ti)) b.Transform(ti);
                }
            }
            else if (tree.Find(o.NodeId) is { } n)
            {
                if (n.Op == FrepOp.Difference && n.Operands.Length > 0) b = Of(n.Operands[0]);
                else foreach (var c in n.Operands) b.Union(Of(c));
                if (b.IsValid && refs.TryGetValue(n.Id, out var r)) b = FrepShaderGen.Warped(b, FrepWarpMath.Make(n, r.Axis, r.Region, b));
                if (b.IsValid && n.HasMatrix) b.Transform(n.M);
            }
            return memo[o] = b;
        }
    }

    /// Reads one object's SDF.* values; false if it isn't a drawable F-rep primitive (or its values are degenerate).
    static bool TryRead(ObjectAttributes a, out SdPrimitive prim)
    {
        if (!TryReadValues(a, out prim)) return false;
        switch (Placement.ReadLeaf(a, out var place))
        {
            case Placement.Stored.Valid:
                prim = prim with { Place = place };
                return true;
            case Placement.Stored.Invalid:
                return false; // degenerate, like a zero radius; never silently unplaced
            default:
                return true;
        }
    }

    static bool TryReadValues(ObjectAttributes a, out SdPrimitive prim)
    {
        prim = default;
        switch (SdfUserText.ReadType(a))
        {
            case FSphereCommand.SdfType when FSphereCommand.TryRead(a, out var f, out var s) && s > 0:
                prim = new(SdKind.Sphere, f, new Vector3d(s, 0, 0));
                return true;
            case FBoxCommand.SdfType when FBoxCommand.TryRead(a, out var f, out var b) && b.X > 0 && b.Y > 0 && b.Z > 0:
                prim = new(SdKind.Box, f, b);
                return true;
            case FTorusCommand.SdfType when FTorusCommand.TryRead(a, out var f, out var t) && t.X > 0 && t.Y > 0:
                prim = new(SdKind.Torus, f, new Vector3d(t.X, t.Y, 0));
                return true;
            case FCylinderCommand.SdfType when FCylinderCommand.TryRead(a, out var f, out var h, out var r) && h > 0 && r > 0:
                prim = new(SdKind.Cylinder, f, new Vector3d(h, r, 0));
                return true;
            case FConeCommand.SdfType when FConeCommand.TryRead(a, out var f, out var h, out var r1, out var r2) &&
                                           h > 0 && r1 >= 0 && r2 >= 0 && r1 + r2 > 0:
                prim = new(SdKind.Cone, f, new Vector3d(h, r1, r2));
                return true;
            case FEllipsoidCommand.SdfType when FEllipsoidCommand.TryRead(a, out var f, out var r) && r.X > 0 && r.Y > 0 && r.Z > 0:
                prim = new(SdKind.Ellipsoid, f, r);
                return true;
            case FPyramidCommand.SdfType when FPyramidCommand.TryRead(a, out var f, out var h, out var s) && h > 0 && s > 0:
                prim = new(SdKind.Pyramid, f, new Vector3d(h, s, 0));
                return true;
            default:
                return false;
        }
    }
}

