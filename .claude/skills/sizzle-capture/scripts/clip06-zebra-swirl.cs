// Sizzle reel clip c06_zebra_swirl. Clip 6 v2: FZebra over a soft-serve swirl.
// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c06_zebra_swirl";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 4.5;               // the raw take, with handles; the edit is cut from it at encode time
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
    // Clip 6 v2: FZebra on a soft-serve swirl (the deformations skill's recipe, mm / 100): four cone petals, filleted,
    // twisted 420 degrees, blended into a dollop and a waffle cone. Many fillets and a twist, so the stripes have
    // something to flow over. At 1.0 s FZebra turns on (Espresso / Whip, 10 vertical stripes) as the camera orbits
    // clockwise. Zebra is a session setting (FrepSettings.Zebra*), set by reflection and always switched off again.
    Tool("clear_scene", "{}");
    Tool("create_primitive", "{\"type\":\"cone\",\"name\":\"waffle\",\"start\":[0,0,0],\"end\":[0,0,1.1],\"start_radius\":0.03,\"end_radius\":0.3,\"color\":\"#e0954a\"}");
    Tool("create_primitive", "{\"type\":\"ellipsoid\",\"name\":\"dollop\",\"center\":[0,0,1.14],\"radii\":[0.33,0.33,0.12],\"color\":\"#efd3ae\"}");
    var petals = new System.Collections.Generic.List<string>();
    for (int q = 0; q < 4; q++)
    {
        double a = q * Math.PI / 2, c = Math.Cos(a), s = Math.Sin(a);
        Tool("create_primitive", $"{{\"type\":\"cone\",\"name\":\"petal{q}\",\"start\":[{N(0.11 * c)},{N(0.11 * s)},1.1],\"end\":[{N(0.015 * c)},{N(0.015 * s)},1.78],\"start_radius\":0.15,\"end_radius\":0.008,\"color\":\"#efd3ae\"}}");
        petals.Add($"\"petal{q}\"");
    }
    var swirl = (string)Tool("combine", $"{{\"op\":\"union\",\"operands\":[{string.Join(",", petals)}],\"k\":0.03}}")["ref"]!;
    var twisted = (string)Tool("deform", $"{{\"op\":\"twist\",\"target\":\"{swirl}\",\"axis_start\":[0,0,1.1],\"axis_end\":[0,0,1.78],\"angle_degrees\":420}}")["ref"]!;
    var cream = (string)Tool("combine", $"{{\"op\":\"union\",\"operands\":[\"{twisted}\",\"dollop\"],\"k\":0.05}}")["ref"]!;
    Tool("combine", $"{{\"op\":\"union\",\"operands\":[\"{cream}\",\"waffle\"],\"k\":0.02}}");
    var settings = asm.GetType("Freppuccino.Display.FrepSettings")!;
    void Zebra(bool on)
    {
        settings.GetField("ZebraStripes")!.SetValue(null, 10);
        settings.GetField("ZebraVertical")!.SetValue(null, true);
        settings.GetField("ZebraColor1")!.SetValue(null, System.Drawing.ColorTranslator.FromHtml("#3b2620"));
        settings.GetField("ZebraColor2")!.SetValue(null, System.Drawing.ColorTranslator.FromHtml("#fffdf9"));
        settings.GetField("Zebra")!.SetValue(null, on);
    }
    var center = new Point3d(0, 0, 0.98);
    const double dist = 4.2, elev = 0.36;      // about 20 degrees above
    const double az0 = -70, az1 = -130;        // 60 degrees; decreasing = clockwise seen from above

    // ───────────── ANIMATE (per clip) ─────────────
    try
    {
        for (int i = 0; i < frames; i++)
        {
            double t = TimeOf(i);
            Zebra(t >= 1.0);
            double az = (az0 + (az1 - az0) * Smooth(t / Seconds)) * Math.PI / 180;
            var toCam = new Vector3d(Math.Cos(az), Math.Sin(az), elev); toCam.Unitize();
            Capture(i, center, center + toCam * dist);
        }
    }
    finally { Zebra(false); }

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
