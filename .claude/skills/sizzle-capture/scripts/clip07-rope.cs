// Sizzle reel clip c07_rope. Clip 7 v2: a rope is combined, twisted, then bent.
// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c07_rope";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 6.0;               // the raw take, with handles; the edit is cut from it at encode time
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
    // Clip 7 v2, the braided rope (deformations skill recipe, mm / 100, strands 1.4x thicker), built in three acts:
    //   combine: three straight strands slide together and the union's fillet grows (0.4 -> 1.5 s)
    //   twist:   0 -> 2400 degrees about the rope axis, the strands wind into a braid (1.7 -> 3.4 s)
    //   bend:    0 -> 180 degrees toward +y, the rope curls into a U (3.6 -> 5.2 s)
    // Rope along x (0 -> 3) at z 0.13; camera ~38 degrees above, easing from the straight rope to the U and orbiting.
    Tool("clear_scene", "{}");
    const double len = 3.0, axZ = 0.18, rStrand = 0.085, spread0 = 0.28, spread1 = 0.092;
    string[] strandColors = { "#e0954a", "#8a5a3c", "#efd3ae" };
    Point3d Off(int q, double spread)                                    // strand q's offset from the rope axis
    {
        double a = Math.PI / 2 + q * 2 * Math.PI / 3;
        return new Point3d(0, spread * Math.Cos(a), axZ + spread * Math.Sin(a));
    }
    string StrandSpec(int q, double spread)
    {
        var o = Off(q, spread);
        return $"\"start\":[0,{N(o.Y)},{N(o.Z)}],\"end\":[{N(len)},{N(o.Y)},{N(o.Z)}],\"radius\":{N(rStrand)}";
    }
    for (int q = 0; q < 3; q++)
        Tool("create_primitive", $"{{\"type\":\"cylinder\",\"name\":\"strand{q}\",{StrandSpec(q, spread0)},\"color\":\"{strandColors[q]}\"}}");
    var strands = (string)Tool("combine", "{\"op\":\"union\",\"operands\":[\"strand0\",\"strand1\",\"strand2\"],\"k\":0}")["ref"]!;
    string Axis() => $"\"axis_start\":[0,0,{N(axZ)}],\"axis_end\":[{N(len)},0,{N(axZ)}]";
    var braid = (string)Tool("deform", $"{{\"op\":\"twist\",\"target\":\"{strands}\",{Axis()},\"angle_degrees\":0}}")["ref"]!;
    var bent = (string)Tool("deform", $"{{\"op\":\"bend\",\"target\":\"{braid}\",{Axis()},\"angle_degrees\":0,\"toward\":[0,1,0]}}")["ref"]!;
    var c0 = new Point3d(1.5, 0, axZ); var c1 = new Point3d(0.5, 0.95, axZ);   // straight rope centre -> U centre
    const double dist0 = 6.4, dist1 = 5.0, elev = 0.78;
    const double az0 = -75, az1 = -115;

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        double j = Smooth((t - 0.4) / 1.1);
        for (int q = 0; q < 3; q++)
            Tool("update_primitive", $"{{\"ref\":\"strand{q}\",{StrandSpec(q, spread0 + (spread1 - spread0) * j)}}}");
        Tool("edit_operation", $"{{\"ref\":\"{strands}\",\"k\":{N(0.025 * j)}}}");
        Tool("edit_operation", $"{{\"ref\":\"{braid}\",\"angle_degrees\":{N(2400 * Smooth((t - 1.7) / 1.7))}}}");
        double b = Smooth((t - 3.6) / 1.6);
        Tool("edit_operation", $"{{\"ref\":\"{bent}\",\"angle_degrees\":{N(180 * b)},\"toward\":[0,1,0]}}");
        double m = Smooth((t - 3.3) / 2.2);
        var center = c0 + (c1 - c0) * m;
        double az = (az0 + (az1 - az0) * Smooth(t / Seconds)) * Math.PI / 180;
        var toCam = new Vector3d(Math.Cos(az), Math.Sin(az), elev); toCam.Unitize();
        Capture(i, center, center + toCam * (dist0 + (dist1 - dist0) * m));
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
