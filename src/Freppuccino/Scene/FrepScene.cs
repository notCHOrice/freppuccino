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
public readonly record struct SdPrimitive(SdKind Kind, Plane Frame, Vector3d P);

/// Program == null: nothing to draw. Primitives/Ks feed <see cref="FrepShaderGen.SceneUniforms"/>.
public sealed record FrepSceneData(ShaderSource? Program, IReadOnlyList<SdPrimitive> Primitives, IReadOnlyList<double> Ks, int Dropped)
{
    public static readonly FrepSceneData Empty = new(null, [], [], 0);
    /// Rhino object ids, parallel to Primitives (what the CSG tree's leaves refer to).
    public IReadOnlyList<Guid> Ids { get; init; } = [];
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
    static List<Guid> ids = [];
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
            // A transform the SDF type can't represent (e.g. shearing a box) previews at the original position.
            if (!TryTransform(scene.Primitives[i], xf, out var moved)) continue;
            prims ??= [.. scene.Primitives];
            prims[i] = moved;
            reshaped |= moved.Kind != scene.Primitives[i].Kind;
        }
        if (prims == null) return scene;
        if (!reshaped) return scene with { Primitives = prims };

        // A sphere stretched into an ellipsoid changes the shader's structure, so recompile (cached by source, so once
        // per gesture). If the extra rows don't fit, preview those prims unchanged; the commit still converts them.
        if (prims.Sum(p => FrepShaderGen.Rows(p.Kind)) > FrepShaderGen.PrimRowBudget(tree.Nodes.Count))
        {
            for (int i = 0; i < prims.Count; i++)
                if (prims[i].Kind != scene.Primitives[i].Kind) prims[i] = scene.Primitives[i];
            return scene with { Primitives = prims };
        }
        var (program, ks) = FrepShaderGen.Compile(prims, ids, tree);
        return scene with { Program = program, Primitives = prims, Ks = ks };
    }

    /// Same rules as the per-command TransformSdf handlers, applied to in-memory values.
    static bool TryTransform(SdPrimitive p, Transform xf, out SdPrimitive result)
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

        // Prims first, so uniform rows for blend values are reserved up front; prims past the budget are dropped (= missing leaves).
        int budget = FrepShaderGen.PrimRowBudget(tree.Nodes.Count), rows = 0, dropped = 0;
        var prims = new List<SdPrimitive>();
        var newOwners = new List<RhinoObject>();
        var newIds = new List<Guid>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!TryRead(obj.Attributes, out var prim)) continue;
            int r = FrepShaderGen.Rows(prim.Kind);
            if (rows + r > budget) { dropped++; continue; }
            rows += r;
            newIds.Add(obj.Id);
            prims.Add(prim);
            newOwners.Add(obj);
        }

        var (program, ks) = FrepShaderGen.Compile(prims, newIds, tree);
        scene = new FrepSceneData(program, prims, ks, dropped) { Ids = newIds };
        owners = newOwners;
        ids = newIds;
    }

    /// Reads one object's SDF.* values; false if it isn't a drawable F-rep primitive (or its values are degenerate).
    static bool TryRead(ObjectAttributes a, out SdPrimitive prim)
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

