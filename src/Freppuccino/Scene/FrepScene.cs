using Freppuccino.Commands;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// sdSphere: center/frame + radius s.
public readonly record struct SdSphere(Plane Frame, double S);

/// sdBox: frame (origin = box center) + half-extents b along the frame axes.
public readonly record struct SdBox(Plane Frame, Vector3d B);

public sealed record FrepSceneData(IReadOnlyList<SdSphere> Spheres, IReadOnlyList<SdBox> Boxes)
{
    public static readonly FrepSceneData Empty = new([], []);
    public int Count => Spheres.Count + Boxes.Count;
}

/// Visible objects in the active doc carrying F-rep metadata (SDF.* user text, written by FSphere / FBox).
/// The SDF values are the source of truth, not the Rhino geometry. Rebuilt lazily after any doc/object/layer change.
/// Objects under a dynamic transform (gumball/Move drag) are reported at their in-progress position.
public static class FrepScene
{
    static FrepSceneData scene = FrepSceneData.Empty;
    // Parallel to scene.Spheres / scene.Boxes; safe since any delete/replace sets dirty.
    static List<RhinoObject> sphereOwners = [], boxOwners = [];
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

    static void Dirty(object? sender, RhinoObjectEventArgs e) => dirty = true;

    public static FrepSceneData Get(RhinoDoc doc)
    {
        if (dirty || doc.RuntimeSerialNumber != docSerial) Rebuild(doc);

        List<SdSphere>? spheres = null;
        for (int i = 0; i < sphereOwners.Count; i++)
        {
            if (!sphereOwners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            var s = scene.Spheres[i];
            // A transform sdSphere can't represent (non-uniform scale) previews at the original position.
            if (!FSphereCommand.TryTransform(s.Frame, s.S, xf, out var frame, out var r, out _)) continue;
            spheres ??= [.. scene.Spheres];
            spheres[i] = new SdSphere(frame, r);
        }

        List<SdBox>? boxes = null;
        for (int i = 0; i < boxOwners.Count; i++)
        {
            if (!boxOwners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            var b = scene.Boxes[i];
            if (!FBoxCommand.TryTransform(b.Frame, b.B, xf, out var frame, out var half, out _)) continue;
            boxes ??= [.. scene.Boxes];
            boxes[i] = new SdBox(frame, half);
        }

        return spheres == null && boxes == null ? scene : new FrepSceneData(spheres ?? scene.Spheres, boxes ?? scene.Boxes);
    }

    static void Rebuild(RhinoDoc doc)
    {
        dirty = false;
        docSerial = doc.RuntimeSerialNumber;
        var spheres = new List<SdSphere>();
        var boxes = new List<SdBox>();
        var sOwners = new List<RhinoObject>();
        var bOwners = new List<RhinoObject>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            var attributes = obj.Attributes;
            switch (SdfUserText.ReadType(attributes))
            {
                case FSphereCommand.SdfType when FSphereCommand.TryRead(attributes, out var frame, out var s) && s > 0:
                    spheres.Add(new SdSphere(frame, s));
                    sOwners.Add(obj);
                    break;
                case FBoxCommand.SdfType when FBoxCommand.TryRead(attributes, out var frame, out var b) && b.X > 0 && b.Y > 0 && b.Z > 0:
                    boxes.Add(new SdBox(frame, b));
                    bOwners.Add(obj);
                    break;
            }
        }
        scene = new FrepSceneData(spheres, boxes);
        sphereOwners = sOwners;
        boxOwners = bOwners;
    }
}
