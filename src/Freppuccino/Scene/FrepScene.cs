using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Shaders;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

public enum PrimKind { Sphere, Box }

/// Sphere: frame origin = center, P.X = radius s. Box: frame origin = center, P = half-extents b.
public readonly record struct SdPrim(PrimKind Kind, Plane Frame, Vector3d P);

/// Program == null: nothing to draw. Prims/Ks feed <see cref="FrepShaderGen.SceneUniforms"/>.
public sealed record FrepSceneData(ShaderSource? Program, IReadOnlyList<SdPrim> Prims, IReadOnlyList<double> Ks, int Dropped)
{
    public static readonly FrepSceneData Empty = new(null, [], [], 0);
}

/// Visible objects in the active doc carrying F-rep metadata (SDF.* user text) + the doc's CSG tree, compiled to a shader.
/// The SDF values are the source of truth, not the Rhino geometry. Rebuilt lazily after any doc/object/layer/tree change;
/// blend edits rebuild too but hit the shader cache (k is a uniform).
/// Objects under a dynamic transform (gumball/Move drag) are reported at their in-progress position.
public static class FrepScene
{
    static FrepSceneData scene = FrepSceneData.Empty;
    static List<RhinoObject> owners = []; // parallel to scene.Prims; safe since any delete/replace sets dirty
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

        List<SdPrim>? prims = null;
        for (int i = 0; i < owners.Count; i++)
        {
            if (!owners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            // A transform the SDF type can't represent previews at the original position.
            if (!TryTransform(scene.Prims[i], xf, out var moved)) continue;
            prims ??= [.. scene.Prims];
            prims[i] = moved;
        }
        return prims == null ? scene : scene with { Prims = prims };
    }

    static bool TryTransform(SdPrim p, Transform xf, out SdPrim result)
    {
        result = p;
        switch (p.Kind)
        {
            case PrimKind.Sphere when FSphereCommand.TryTransform(p.Frame, p.P.X, xf, out var f, out var s, out _):
                result = p with { Frame = f, P = new Vector3d(s, 0, 0) };
                return true;
            case PrimKind.Box when FBoxCommand.TryTransform(p.Frame, p.P, xf, out var f, out var b, out _):
                result = p with { Frame = f, P = b };
                return true;
            default:
                return false;
        }
    }

    static void Rebuild(RhinoDoc doc)
    {
        dirty = false;
        docSerial = doc.RuntimeSerialNumber;
        var tree = FrepTrees.Get(doc);

        // Prims first, so uniform rows for blend values are reserved up front; prims past the budget are dropped (= missing leaves).
        int budget = FrepShaderGen.PrimRowBudget(tree.Nodes.Count), rows = 0, dropped = 0;
        var prims = new List<SdPrim>();
        var newOwners = new List<RhinoObject>();
        var ids = new List<Guid>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!TryRead(obj.Attributes, out var prim)) continue;
            int r = FrepShaderGen.Rows(prim.Kind);
            if (rows + r > budget) { dropped++; continue; }
            rows += r;
            ids.Add(obj.Id);
            prims.Add(prim);
            newOwners.Add(obj);
        }

        var (program, ks) = FrepShaderGen.Compile(prims, ids, tree);
        scene = new FrepSceneData(program, prims, ks, dropped);
        owners = newOwners;
    }

    static bool TryRead(ObjectAttributes a, out SdPrim prim)
    {
        prim = default;
        switch (SdfUserText.ReadType(a))
        {
            case FSphereCommand.SdfType when FSphereCommand.TryRead(a, out var frame, out var s) && s > 0:
                prim = new SdPrim(PrimKind.Sphere, frame, new Vector3d(s, 0, 0));
                return true;
            case FBoxCommand.SdfType when FBoxCommand.TryRead(a, out var frame, out var b) && b.X > 0 && b.Y > 0 && b.Z > 0:
                prim = new SdPrim(PrimKind.Box, frame, b);
                return true;
            default:
                return false;
        }
    }
}
