using System.Runtime.CompilerServices;
using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.UI;

namespace Freppuccino.Selection;

/// Owns left clicks that land on F-rep geometry in views showing the Freppuccino display mode (plan section 3).
/// Acts only when the cursor is not on a gumball handle and no command is running (or the running command opened an
/// explicit F-rep selection session, see <see cref="BeginSession"/>). Anything else, and every click that hits no
/// F-rep, is left to Rhino.
///   plain click: the nearest root whose final field the click ray hits; replaces the selection.
/// The hit is decided on mouse-down (which is when Rhino's own pick has to be cancelled), the selection is made on
/// mouse-up, after Rhino has finished with the click.
public sealed class FrepPicker : MouseCallback
{
    static FrepPicker? instance;
    static int sessions;

    public static void Install() => instance ??= new FrepPicker { Enabled = true };

    /// While a session is open the picker also acts during a running command (FrepMesh's pick-blends prompt).
    public static IDisposable BeginSession()
    {
        sessions++;
        return new Session();
    }

    sealed class Session : IDisposable
    {
        bool done;
        public void Dispose()
        {
            if (done) return;
            done = true;
            sessions--;
        }
    }

    /// Raised after the picker changed the selection.
    public static event Action<RhinoDoc>? Picked;

    (RhinoDoc Doc, FrepOperand Root)? pending;

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
        pending = null;
        if (e.MouseButton != MouseButton.Left || e.View is not { } view) return;
        var doc = view.Document;
        if (doc == null || !IsFrepView(view.ActiveViewport)) return;
        if (e.IsOverGumball() != Rhino.UI.Gumball.GumballMode.None) return;
        if (Command.InCommand() && sessions == 0) return;
        if (e.ShiftKeyDown || e.CtrlKeyDown) return; // leaf picking: step 4

        var hit = PickRoot(doc, view.ActiveViewport, new Point2d(e.ViewportPoint.X, e.ViewportPoint.Y));
        if (hit is not { } root)
        {
            // Rhino deselects its own objects on an empty click, but node roots aren't Rhino objects.
            UiController_ClearNodes(doc);
            return;
        }
        e.Cancel = true;
        pending = (doc, root);
    }

    protected override void OnMouseUp(MouseCallbackEventArgs e)
    {
        if (pending is not { } p || e.MouseButton != MouseButton.Left) return;
        pending = null;
        e.Cancel = true;
        FrepSelection.SelectRoot(p.Doc, p.Root, add: false);
        Picked?.Invoke(p.Doc);
    }

    static void UiController_ClearNodes(RhinoDoc doc) => Ui.UiController.SetSelectedNodes(doc, []);

    public static bool IsFrepView(RhinoViewport vp) =>
        FreppuccinoDisplayMode.Id != Guid.Empty && vp.DisplayMode?.Id == FreppuccinoDisplayMode.Id;

    // Per scene snapshot (FrepScene.Get returns a new instance whenever anything changed): one field per root.
    static readonly ConditionalWeakTable<FrepSceneData, List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)>> fields = new();

    static List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)> RootFields(FrepSceneData scene) =>
        fields.GetValue(scene, s =>
        {
            var result = new List<(FrepOperand, FrepField, (V3, V3))>();
            FrepFieldBuilder.Build(s, null, out var roots);
            foreach (var r in roots)
            {
                var f = FrepFieldBuilder.Build(s, [r], out var got);
                if (got.Count == 1) result.Add((r, f, f.Bounds()));
            }
            return result;
        });

    /// Nearest root whose field the ray through <paramref name="client"/> (viewport client coordinates) hits.
    public static FrepOperand? PickRoot(RhinoDoc doc, RhinoViewport vp, Point2d client)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Program == null) return null;
        var ray = Ray(vp, client);
        FrepOperand? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var (root, field, box) in RootFields(scene))
            if (March(field, box, ray, bestT) is { } t && t < bestT)
            {
                bestT = t;
                best = root;
            }
        return best;
    }

    /// Ray origin/direction plus the shader's hit tolerance (half a pixel footprint: eps = c + slope · t).
    public readonly record struct PickRay(V3 Origin, V3 Dir, double EpsConst, double EpsSlope);

    public static PickRay Ray(RhinoViewport vp, Point2d client)
    {
        var line = vp.ClientToWorld(client);
        // ClientToWorld runs far → near; march away from the camera, from the near end.
        if (line.Direction * vp.CameraDirection < 0) line.Flip();
        var dir = line.Direction;
        dir.Unitize();
        vp.GetFrustum(out var l, out var r, out _, out _, out var near, out _);
        double slope = (r - l) / Math.Max(1, vp.Size.Width) * 0.5;
        if (vp.IsPerspectiveProjection)
        {
            var o = vp.CameraLocation;
            return new PickRay(new V3(o.X, o.Y, o.Z), new V3(dir.X, dir.Y, dir.Z), 0, slope / near);
        }
        // Parallel: start well behind the scene so nothing in front of the near plane is missed.
        var from = line.From;
        return new PickRay(new V3(from.X, from.Y, from.Z), new V3(dir.X, dir.Y, dir.Z), slope, 0);
    }

    /// Sphere-traces <paramref name="f"/> along the ray inside <paramref name="box"/>; distance along the ray or null.
    public static double? March(FrepField f, (V3 Min, V3 Max) box, PickRay ray, double tLimit = double.PositiveInfinity)
    {
        if (!Slab(box, ray.Origin, ray.Dir, out var t0, out var t1)) return null;
        double t = Math.Max(t0, 0), tMax = Math.Min(t1, tLimit);
        for (int i = 0; i < 512 && t <= tMax; i++)
        {
            double eps = ray.EpsConst + ray.EpsSlope * t;
            double d = f.Value(ray.Origin + ray.Dir * t);
            if (d < eps) return t;
            t += Math.Max(d, eps * 0.5);
        }
        return null;
    }

    static bool Slab((V3 Min, V3 Max) b, V3 o, V3 d, out double t0, out double t1)
    {
        t0 = double.NegativeInfinity;
        t1 = double.PositiveInfinity;
        // Pad the box slightly: it bounds the surface, and the hit tolerance can reach just outside it.
        double pad = 1e-9 + 1e-6 * Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        for (int a = 0; a < 3; a++)
        {
            double lo = (a == 0 ? b.Min.X : a == 1 ? b.Min.Y : b.Min.Z) - pad, hi = (a == 0 ? b.Max.X : a == 1 ? b.Max.Y : b.Max.Z) + pad;
            double oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            if (Math.Abs(da) < 1e-300)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            double ta = (lo - oa) / da, tb = (hi - oa) / da;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Math.Max(t0, ta);
            t1 = Math.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }
}
