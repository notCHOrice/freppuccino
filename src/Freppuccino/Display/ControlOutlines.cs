using Freppuccino.Sdf;
using Freppuccino.Selection;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Display;

/// Outline-only drawing of F-rep control breps in the Freppuccino display mode (plan section 5).
/// Rhino doesn't draw them there at all (<see cref="Suppress"/>): no faces, edges, isocurves or selection highlight,
/// but they stay pickable and keep their gumball. We draw each one's view silhouette instead, cached per object
/// version and camera:
///   unselected: thin grey, before the shader pass, so blends cover them (only uncovered outlines show);
///   selected leaves: selection colour, after the shader, on top.
public static class ControlOutlines
{
    const SilhouetteType Kinds = SilhouetteType.Projecting | SilhouetteType.TangentProjects | SilhouetteType.Crease | SilhouetteType.Boundary;
    static readonly System.Drawing.Color Unselected = System.Drawing.Color.FromArgb(70, 70, 70);

    readonly record struct Key(uint Serial, bool Persp, Point3d Eye, Vector3d Dir);
    static readonly Dictionary<Guid, (Key Key, Curve[] Curves)> cache = [];

    public static bool IsControl(RhinoObject? o) => o != null && SdfUserText.ReadType(o.Attributes) != null;

    /// For DisplayConduit.PreDrawObject: hide control breps in Freppuccino-mode views.
    public static void Suppress(DrawObjectEventArgs e)
    {
        if (FreppuccinoDisplayMode.IsActive(e.Display) && IsControl(e.RhinoObject)) e.DrawObject = false;
    }

    /// <paramref name="selected"/>: false = every unselected visible control; true = the selected ones.
    public static void Draw(DisplayPipeline dp, RhinoDoc doc, bool selected)
    {
        var vp = dp.Viewport;
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion };
        var color = selected ? Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor : Unselected;
        int thickness = selected ? 2 : 1;
        foreach (var o in doc.Objects.GetObjectList(settings))
        {
            if (!IsControl(o) || (o.IsSelected(false) > 0) != selected) continue;
            foreach (var c in Outline(o, vp, doc)) dp.DrawCurve(c, color, thickness);
        }
    }

    static Curve[] Outline(RhinoObject o, RhinoViewport vp, RhinoDoc doc)
    {
        bool persp = vp.IsPerspectiveProjection;
        var key = new Key(o.RuntimeSerialNumber, persp, persp ? vp.CameraLocation : Point3d.Origin, persp ? Vector3d.Zero : vp.CameraDirection);
        if (cache.TryGetValue(o.Id, out var hit) && hit.Key == key) return hit.Curves;
        Silhouette[]? s = null;
        try
        {
            double tol = doc.ModelAbsoluteTolerance, angle = doc.ModelAngleToleranceRadians;
            s = persp ? Silhouette.Compute(o.Geometry, Kinds, vp.CameraLocation, tol, angle)
                      : Silhouette.Compute(o.Geometry, Kinds, vp.CameraDirection, tol, angle);
        }
        catch (Exception)
        {
            // Unsupported geometry: draw nothing rather than break the frame.
        }
        var curves = s?.Select(x => x.Curve).OfType<Curve>().ToArray() ?? [];
        cache[o.Id] = (key, curves);
        if (cache.Count > 4096) cache.Clear(); // deleted objects; a cheap bound
        return curves;
    }
}
