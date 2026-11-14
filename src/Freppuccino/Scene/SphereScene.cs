using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// Visible single-face spherical Breps in the active doc. Rebuilt lazily after any doc/object/layer change.
/// Objects under a dynamic transform (gumball/Move drag) are reported at their in-progress position.
public static class SphereScene
{
    static List<Sphere> spheres = [];
    static List<RhinoObject> owners = []; // parallel to spheres; safe since any delete/replace sets dirty
    static uint docSerial;
    static bool dirty = true;

    public static void Install()
    {
        RhinoDoc.AddRhinoObject += Dirty;
        RhinoDoc.DeleteRhinoObject += Dirty;
        RhinoDoc.UndeleteRhinoObject += Dirty;
        RhinoDoc.ReplaceRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ModifyObjectAttributes += (_, _) => dirty = true; // hide/show, layer change
        RhinoDoc.LayerTableEvent += (_, _) => dirty = true;
        RhinoDoc.EndOpenDocument += (_, _) => dirty = true;
        RhinoDoc.NewDocument += (_, _) => dirty = true;
    }

    static void Dirty(object? sender, RhinoObjectEventArgs e) => dirty = true;

    public static IReadOnlyList<Sphere> Get(RhinoDoc doc)
    {
        if (dirty || doc.RuntimeSerialNumber != docSerial) Rebuild(doc);
        List<Sphere>? moved = null;
        for (int i = 0; i < owners.Count; i++)
        {
            if (!owners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            moved ??= [.. spheres];
            moved[i] = Apply(spheres[i], xf);
        }
        return moved ?? spheres;
    }

    /// Non-uniform scale would make an ellipsoid; approximated by mean axis scale.
    static Sphere Apply(Sphere s, Transform xf)
    {
        var c = s.Center;
        c.Transform(xf);
        double k = (new Vector3d(xf.M00, xf.M10, xf.M20).Length + new Vector3d(xf.M01, xf.M11, xf.M21).Length + new Vector3d(xf.M02, xf.M12, xf.M22).Length) / 3;
        return new Sphere(c, s.Radius * k);
    }

    static void Rebuild(RhinoDoc doc)
    {
        dirty = false;
        docSerial = doc.RuntimeSerialNumber;
        var tol = doc.ModelAbsoluteTolerance;
        var list = new List<Sphere>();
        var objs = new List<RhinoObject>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, ObjectTypeFilter = ObjectType.Brep };
        foreach (var obj in doc.Objects.GetObjectList(settings))
            if (obj.Geometry is Brep { Faces.Count: 1 } b && b.Faces[0].TryGetSphere(out var s, tol) && s.IsValid)
            {
                list.Add(s);
                objs.Add(obj);
            }
        spheres = list;
        owners = objs;
    }
}
