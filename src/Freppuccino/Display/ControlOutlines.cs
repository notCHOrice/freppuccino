using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Selection;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Display;

/// Outline-only drawing of F-rep control shapes in the Freppuccino display mode (plan section 5).
/// Rhino doesn't draw the control breps there at all (<see cref="Suppress"/>): no faces, edges, isocurves or selection
/// highlight, but they stay pickable and keep their gumball. We draw view silhouettes instead, per control instance:
/// the real brep stands for its primary instance; an instance under a different root transform (a shared shape) is the
/// brep mapped by T_instance × T_primary⁻¹. Cached per object version, instance transform and camera.
///   unselected: thin grey, before the shader pass, so blends cover them (only uncovered outlines show);
///   selected (the picked instance of a selected leaf, or a selected standalone shape): selection colour, on top.
public static class ControlOutlines
{
    const SilhouetteType Kinds = SilhouetteType.Projecting | SilhouetteType.TangentProjects | SilhouetteType.Crease | SilhouetteType.Boundary;
    static readonly System.Drawing.Color Unselected = System.Drawing.Color.FromArgb(70, 70, 70);

    readonly record struct Key(Guid Obj, uint Serial, string Rel, bool Persp, Point3d Eye, Vector3d Dir);
    static readonly Dictionary<Key, Curve[]> cache = [];

    public static bool IsControl(RhinoObject? o) => o != null && SdfUserText.ReadType(o.Attributes) != null;

    /// For DisplayConduit.PreDrawObject: hide control breps in Freppuccino-mode views.
    public static void Suppress(DrawObjectEventArgs e)
    {
        if (FreppuccinoDisplayMode.IsActive(e.Display) && IsControl(e.RhinoObject)) e.DrawObject = false;
    }

    /// <paramref name="selected"/>: false = every unselected instance; true = the selected ones.
    public static void Draw(DisplayPipeline dp, RhinoDoc doc, bool selected)
    {
        var vp = dp.Viewport;
        var color = selected ? Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor : Unselected;
        int thickness = selected ? 2 : 1;
        var (roots, instances) = FrepSelection.Structure(doc);
        var rootSet = roots.ToHashSet();
        var selectedLeaves = FrepSelection.Leaves(doc);

        // Distinct (object, T) per object; the first is the primary instance (the real brep's placement).
        var byObject = new Dictionary<Guid, List<FrepInstance>>();
        foreach (var i in instances)
        {
            if (!byObject.TryGetValue(i.Object, out var list)) byObject[i.Object] = list = [];
            if (!list.Any(x => FrepInstances.Same(x.T, i.T))) list.Add(i);
        }

        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion };
        foreach (var o in doc.Objects.GetObjectList(settings))
        {
            if (!IsControl(o)) continue;
            bool objSelected = o.IsSelected(false) > 0;
            if (!byObject.TryGetValue(o.Id, out var list) || list.Count == 0)
            {
                // Not reached by any visible root (e.g. under a hidden node): its brep as is.
                if (objSelected == selected) Draw(dp, o, null, vp, doc, color, thickness);
                continue;
            }
            var primary = list[0];
            if (!primary.T.TryGetInverse(out var primaryInv)) continue;
            foreach (var inst in list)
            {
                // Selected: a standalone shape whose brep is selected, or this exact instance among the selected leaves
                // (picked through its path, or through its own instance carrier).
                bool isSel = (objSelected && rootSet.Contains(FrepOperand.Object(o.Id))) ||
                    selectedLeaves.Any(l => l.Object == o.Id && FrepInstances.Same(l.T, inst.T));
                if (isSel != selected) continue;
                Transform? rel = ReferenceEquals(inst, primary) ? null : inst.T * primaryInv;
                Draw(dp, o, rel, vp, doc, color, thickness);
            }
        }
    }

    static void Draw(DisplayPipeline dp, RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc, System.Drawing.Color color, int thickness)
    {
        foreach (var c in Outline(o, rel, vp, doc)) dp.DrawCurve(c, color, thickness);
    }

    static Curve[] Outline(RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc)
    {
        bool persp = vp.IsPerspectiveProjection;
        string relKey = rel is { } r ? string.Join(",", Enumerable.Range(0, 12).Select(i => r[i / 4, i % 4].ToString("G9"))) : "";
        var key = new Key(o.Id, o.RuntimeSerialNumber, relKey, persp, persp ? vp.CameraLocation : Point3d.Origin, persp ? Vector3d.Zero : vp.CameraDirection);
        if (cache.TryGetValue(key, out var hit)) return hit;
        Silhouette[]? s = null;
        try
        {
            var g = o.Geometry;
            if (rel is { } t)
            {
                g = g.Duplicate();
                if (!g.Transform(t)) g = null;
            }
            double tol = doc.ModelAbsoluteTolerance, angle = doc.ModelAngleToleranceRadians;
            if (g != null)
                s = persp ? Silhouette.Compute(g, Kinds, vp.CameraLocation, tol, angle)
                          : Silhouette.Compute(g, Kinds, vp.CameraDirection, tol, angle);
        }
        catch (Exception)
        {
            // Unsupported geometry: draw nothing rather than break the frame.
        }
        var curves = s?.Select(x => x.Curve).OfType<Curve>().ToArray() ?? [];
        if (cache.Count > 4096) cache.Clear(); // stale cameras / deleted objects; a cheap bound
        cache[key] = curves;
        return curves;
    }
}
