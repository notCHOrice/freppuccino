// Sizzle reel clip c02_build. Clip 2 v2: seven primitives pop in, then fly together into a cup and fuse.
// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c02_build";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 6.8;               // the raw take, with handles; the edit is cut from it at encode time
const double Lens = 50;
const bool ClearsScene = true;            // false = the clip works on an opened file (open-3dm.cs) and must not clear it

// ───────────── HARNESS (same in every clip script) ─────────────
var work = Environment.GetEnvironmentVariable("SIZZLE_WORK")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "freppuccino-sizzle");
var dir = Path.Combine(work, ClipId);
var framesDir = Path.Combine(dir, "frames");
Directory.CreateDirectory(framesDir);
IDisposable? clean = null;
try
{
    var cfgPath = Path.Combine(dir, "config.json");
    var mode = File.Exists(cfgPath) ? (string)JsonNode.Parse(File.ReadAllText(cfgPath))!["mode"]! : "full";
    foreach (var f in Directory.GetFiles(framesDir, "*.png")) File.Delete(f);

    var doc = RhinoDoc.ActiveDoc;
    // A SCENE block that clears the document must never run on a saved file (an example .3dm, someone's model).
    if (ClearsScene && !string.IsNullOrEmpty(doc.Path))
        throw new Exception($"the active document is {doc.Path}; this script clears the scene. Open an empty one first: echo NEW > $SIZZLE_WORK/open3dm/request.txt && run-clip.sh open-3dm.cs open3dm");
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Freppuccino")
              ?? throw new Exception("Freppuccino plugin not loaded (launch Rhino with launch-rhino.ps1)");
    var run = asm.GetType("Freppuccino.Ai.AiTools")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
    // The AI tool API: create_primitive, combine, deform, edit_operation, set_color, clear_scene, ... (see Ai/AiTools.cs).
    JsonNode Tool(string name, string json)
    {
        try { return (JsonNode)run.Invoke(null, new object[] { doc, name, JsonNode.Parse(json)!.AsObject() })!; }
        catch (TargetInvocationException e) { throw new Exception($"{name} {json}: {e.InnerException?.Message}"); }
    }
    string N(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * x * (x * (x * 6 - 15) + 10); } // smootherstep
    var dm = asm.GetType("Freppuccino.Display.FreppuccinoDisplayMode")!;
    var modeId = (Guid)dm.GetProperty("Id")!.GetValue(null)!;
    var capId = (Guid)dm.GetProperty("CaptureId")!.GetValue(null)!; // proxies invisible, no edges
    // Captures show just the model (no FREP MODE badge, no control outlines) while this scope is alive. Builds before
    // AiCapture.CleanCapture (the gyroid / displacements branches) return null: no outlines there anyway, and
    // encode.sh still clears the badge corner.
    clean = asm.GetType("Freppuccino.Ai.AiCapture")?.GetMethod("CleanCapture", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as IDisposable;

    var view = doc.Views.Find("Perspective", false) ?? doc.Views.ActiveView;
    doc.Views.ActiveView = view;
    view.Maximized = true;                                 // a 4-view layout makes the viewport too small (tiling)
    view.Redraw();
    RhinoApp.Wait();
    var vp = view.ActiveViewport;
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(capId);
    vp.ChangeToPerspectiveProjection(true, Lens);
    var screen = view.ScreenRectangle;
    if (W > screen.Width || H > screen.Height)
        throw new Exception($"capture {W}x{H} is larger than the viewport {screen.Width}x{screen.Height}: it would tile and repeat the FREP MODE badge. Maximise the view or capture smaller.");

    // One frame = 4 captures with the camera shifted by rotated-grid sub-pixel offsets; encode.sh averages them.
    double[][] jitter = { new[] { 0.125, 0.375 }, new[] { 0.375, -0.125 }, new[] { -0.125, -0.375 }, new[] { -0.375, 0.125 } };
    // The scene's shader must exist before a capture, or that frame shows bare wireframes. ShaderError compiles it now
    // (cached, so this is cheap once built; a cold cache after a restart can take seconds) and returns any build error.
    var shaderError = asm.GetType("Freppuccino.Display.ShaderConduit")!.GetMethod("ShaderError", BindingFlags.Public | BindingFlags.Static)!;
    void Capture(int frame, Point3d target, Point3d camera)
    {
        if (shaderError.Invoke(null, new object[] { doc }) is string err) throw new Exception($"frame {frame}: shader does not compile: {err}");
        for (int j = 0; j < jitter.Length; j++)
        {
            vp.SetCameraLocations(target, camera);
            vp.CameraUp = Vector3d.ZAxis;
            vp.GetFrustum(out _, out _, out _, out double ftop, out double fnear, out _);
            double px = 2 * ftop / fnear * camera.DistanceTo(target) / H;  // world units per pixel at the target
            var shift = vp.CameraX * (jitter[j][0] * px) + vp.CameraY * (jitter[j][1] * px);
            vp.SetCameraLocations(target + shift, camera + shift);
            vp.CameraUp = Vector3d.ZAxis;
            var vc = new ViewCapture { Width = W, Height = H, TransparentBackground = true, DrawGrid = false, DrawAxes = false, DrawGridAxes = false, ScaleScreenItems = false };
            // Don't write `CaptureToBitmap(view) ?? throw ...` or similar: it makes the compiler resolve Bitmap's
            // interfaces from assemblies the Rhino script environment doesn't reference, and the script then fails to
            // compile SILENTLY (no error.txt, nothing in the logs). `?.` is fine.
            var bmp = vc.CaptureToBitmap(view);
            bmp?.Save(Path.Combine(framesDir, $"f{frame:D4}_{j}.png"));
            bmp?.Dispose();
            if (!File.Exists(Path.Combine(framesDir, $"f{frame:D4}_{j}.png"))) throw new Exception($"capture failed at frame {frame}");
        }
    }
    int frames = mode == "full" ? (int)Math.Round(Seconds * Fps) : 2;
    double TimeOf(int i) => mode == "full" ? i / (double)Fps : (i == 0 ? 0 : Seconds); // test mode: first + last pose

    // ───────────── SCENE (per clip) ─────────────
    // Clip 2 v2, twice as long. First half: clip 2's pops (seven primitives, one every half beat, two rows). Second half:
    // they fly in one after another, bottom up, on arcs, reshaping as they go (update_primitive with interpolated
    // specs), and stack into a frappuccino cup: coaster, tapered body, lid rim, cream, tip, straw. Then the union's
    // blend ramps up and fuses them into one shape. All seven are put in one union (k 0) right after the last pop; with
    // k 0 a union of separate shapes looks like the shapes.
    Tool("clear_scene", "{}");
    var items = new (string Name, string Type, double X, double Y, string Color)[]
    {
        ("p_sphere", "sphere", -2.7, 0.85, "#efd3ae"), ("p_pyramid", "pyramid", -2.2, -0.85, "#8a5a3c"),
        ("p_box", "box", -0.9, 0.85, "#3b2620"), ("p_ellipsoid", "ellipsoid", -0.4, -0.85, "#efd3ae"),
        ("p_cylinder", "cylinder", 0.9, 0.85, "#3b2620"), ("p_torus", "torus", 1.4, -0.85, "#8a5a3c"),
        ("p_cone", "cone", 2.7, 0.85, "#e0954a"),
    };
    const double firstPop = 0.5, popStep = 0.25, popLen = 0.24;
    double Pop(double t, int i)                                           // easeOutBack 0 -> 1 with ~10% overshoot
    {
        double u = Math.Clamp((t - (firstPop + i * popStep)) / popLen, 0, 1);
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(u - 1, 3) + c1 * Math.Pow(u - 1, 2);
    }
    // A primitive's spec as a vector of numbers (the z entries get the flight arc), at pop scale s or as the cup part.
    double[] Popped(int i, double s)
    {
        var (_, type, x, y, _) = items[i];
        return type switch
        {
            "sphere" => new[] { x, y, 0.55 * s, 0.55 * s },
            "box" => new[] { x, y, 0.5 * s, 0.95 * s, 0.95 * s, s },
            "cylinder" => new[] { x, y, 0, x, y, 1.1 * s, 0.45 * s },
            "cone" => new[] { x, y, 0, x, y, 1.2 * s, 0.52 * s, 0 },
            "pyramid" => new[] { x, y, 0, 1.1 * s, 1.05 * s },
            "ellipsoid" => new[] { x, y, 0.45 * s, 0.7 * s, 0.42 * s, 0.45 * s },
            _ => new[] { x, y, 0.55 * s, 0, -0.55, 1, 0.46 * s, 0.19 * s },
        };
    }
    double[] Cup(int i) => items[i].Type switch
    {
        "box" => new[] { 0, 0, 0.04, 1.3, 1.3, 0.08 },                      // coaster
        "cone" => new[] { 0, 0, 0.08, 0, 0, 1.34, 0.4, 0.57 },             // tapered body
        "torus" => new[] { 0, 0, 1.36, 0, 0, 1, 0.56, 0.08 },               // lid rim
        "ellipsoid" => new[] { 0, 0, 1.44, 0.52, 0.52, 0.22 },             // cream
        "sphere" => new[] { 0, 0, 1.64, 0.32 },                            // cream, top
        "pyramid" => new[] { 0, 0, 1.74, 0.42, 0.4 },                      // tip
        _ => new[] { 0.12, 0.05, 1.2, 0.36, 0.16, 2.5, 0.055 },            // straw (cylinder)
    };
    int[] Zs(string type) => type switch { "cylinder" or "cone" => new[] { 2, 5 }, _ => new[] { 2 } };
    string Spec(int i, double[] v)
    {
        string P(int a) => $"[{N(v[a])},{N(v[a + 1])},{N(v[a + 2])}]";
        return items[i].Type switch
        {
            "sphere" => $"\"center\":{P(0)},\"radius\":{N(v[3])}",
            "box" => $"\"center\":{P(0)},\"size\":{P(3)}",
            "cylinder" => $"\"start\":{P(0)},\"end\":{P(3)},\"radius\":{N(v[6])}",
            "cone" => $"\"start\":{P(0)},\"end\":{P(3)},\"start_radius\":{N(v[6])},\"end_radius\":{N(v[7])}",
            "pyramid" => $"\"base_center\":{P(0)},\"height\":{N(v[3])},\"base_size\":{N(v[4])}",
            "ellipsoid" => $"\"center\":{P(0)},\"radii\":{P(3)}",
            _ => $"\"center\":{P(0)},\"axis\":{P(3)},\"major_radius\":{N(v[6])},\"minor_radius\":{N(v[7])}",
        };
    }
    // Flight order: bottom up (coaster, body, rim, cream, top, tip, straw), one every 0.14 s, 1.0 s each.
    string[] order = { "p_box", "p_cone", "p_torus", "p_ellipsoid", "p_sphere", "p_pyramid", "p_cylinder" };
    const double flyStart = 2.6, flyStep = 0.14, flyLen = 1.0, mergeStart = 4.35, mergeLen = 1.0, kMax = 0.1;
    double FlyU(double t, int i) => (t - (flyStart + Array.IndexOf(order, items[i].Name) * flyStep)) / flyLen;
    var created = new bool[items.Length];
    string? union = null;
    // Camera: clip 2's locked 3/4 view for the pops, then it eases in on the cup and orbits ~25 degrees clockwise.
    var target0 = new Point3d(-0.6, 0, 0.45); var target1 = new Point3d(0.02, 0.02, 1.18);
    const double dist0 = 9.2, dist1 = 8.0, elev = 0.66;
    double az0 = Math.Atan2(-1.0, -0.45) * 180 / Math.PI, az1 = az0 - 25;

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        for (int p = 0; p < items.Length; p++)
        {
            if (t < firstPop + p * popStep) continue;                     // not popped yet: nothing in the scene
            double s = Math.Max(Pop(t, p), 0.02);
            double fu = FlyU(t, p);
            double[] v;
            if (fu <= 0) v = Popped(p, s);
            else
            {
                double u = Smooth(fu);
                var a = Popped(p, 1); var b = Cup(p);
                v = new double[a.Length];
                for (int q = 0; q < a.Length; q++) v[q] = a[q] + (b[q] - a[q]) * u;
                foreach (int z in Zs(items[p].Type)) v[z] += (items[p].Type == "cylinder" ? 0.3 : 0.9) * Math.Sin(Math.PI * Math.Clamp(fu, 0, 1));   // arc (the straw stays in frame)
            }
            if (!created[p])
            {
                Tool("create_primitive", $"{{\"type\":\"{items[p].Type}\",\"name\":\"{items[p].Name}\",{Spec(p, v)},\"color\":\"{items[p].Color}\"}}");
                created[p] = true;
            }
            else if (t <= firstPop + p * popStep + popLen + 1.0 / Fps || (fu > 0 && fu < 1 + 1.0 / (Fps * flyLen)))
                Tool("update_primitive", $"{{\"ref\":\"{items[p].Name}\",{Spec(p, v)}}}");
        }
        if (union == null && created.All(c => c) && t > firstPop + (items.Length - 1) * popStep + popLen + 0.05)
            union = (string)Tool("combine", $"{{\"op\":\"union\",\"operands\":[{string.Join(",", items.Select(it => $"\"{it.Name}\""))}],\"k\":0}}")["ref"]!;
        if (union != null && t >= mergeStart)
            Tool("edit_operation", $"{{\"ref\":\"{union}\",\"k\":{N(kMax * Smooth((t - mergeStart) / mergeLen))}}}");
        double m = Smooth((t - 2.5) / 2.9);
        var target = target0 + (target1 - target0) * m;
        double az = (az0 + (az1 - az0) * m) * Math.PI / 180;
        var toCam = new Vector3d(Math.Cos(az), Math.Sin(az), elev); toCam.Unitize();
        Capture(i, target, target + toCam * (dist0 + (dist1 - dist0) * m));
    }

    // ───────────── HARNESS (end) ─────────────
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(modeId);
    view.Redraw();
    File.WriteAllText(Path.Combine(dir, "done.txt"), $"{ClipId}: {frames} frames x {jitter.Length} captures, {W}x{H}, mode {mode} -> {framesDir}");
}
catch (Exception e)
{
    File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString());
}
finally
{
    clean?.Dispose(); // always, or the user's views lose their badge and outlines until Rhino restarts
    RhinoDoc.ActiveDoc?.Views.Redraw();
}
