# Windows (D3D11) validation

Code: `src/Freppuccino/Gpu/D3D11/` (`D3D11Backend.cs`, `D3D11Native.cs`). Written and compiled on a Mac; **never run**. Everything below is unverified until a Windows run says otherwise.

## What the backend does

```
PostDrawObjects (ShaderConduit, only in "Freppuccino" mode)
  dev = pipeline.GetDirect3d11Device()          ID3D11Device*, not AddRef'd
  ctx = pipeline.GetDirect3d11DeviceContext()   ID3D11DeviceContext1*, not AddRef'd
  per device (re-init if pointer changes): QI ID3D11Device1, RS (solid, cull none), DSS (depth+stencil off)
  per ShaderSource.Hash: GLSL → SPIR-V → HLSL (ShaderCompiler) → D3DCompile vs_5_0/ps_5_0 (d3dcompiler_47.dll) → Create{Vertex,Pixel}Shader
  state isolation, D3D11Backend.StateMode (Auto | Swap | Manual; env FREPPUCINO_D3D11_STATE):
    Swap:   get Rhino's RTVs/DSV + viewports → SwapDeviceContextState(ours) → set RTVs/DSV/viewports → bind+draw
            → unbind RTs → swap back.  Our state: CreateDeviceContextState(emulate ID3D11Device1, Rhino's FL, SINGLETHREADED iff device is)
    Manual: save topology, input layout, VS/PS/GS/HS/DS, RS, blend(+factor, mask), DSS(+stencil ref) → bind+draw → restore
    Auto:   Swap if context is immediate and CreateDeviceContextState succeeds, else Manual (logged once)
  bind: TriangleList, no input layout, no VB, our VS/PS, GS/HS/DS null, RS ours, blend null (opaque), DSS ours → Draw(3, 0)
  wrapped in ID3DUserDefinedAnnotation event "Freppuccino"; our objects named "Freppuccino …" (WKPDID_D3DDebugObjectName)
then ShaderConduit calls RhinoStateReset.Apply (dummy clipped line) as on Mac
```
Failures (compile, FL < 11_0, null device) log once to the Rhino command line and draw nothing.

## (a) Assumptions to validate

| # | Assumption | Why it matters | How to check |
|---|---|---|---|
| A1 | Rhino's RTV/DSV and viewport are bound on the context during `PostDrawObjects` | We draw into whatever is bound; we never set RT/viewport ourselves (Swap copies them across) | Triangle visible and centred in the viewport; PIX/RenderDoc: our draw's OM targets = Rhino's back buffer / MSAA RT |
| A2 | Draw order same as Mac: unselected objects → PostDrawObjects → DrawForeground → highlight pass → DrawOverlay | Defines what is under/over us | Checklist C2–C4 |
| A3 | Context is **immediate** (not deferred) | SwapDeviceContextState is immediate-only-ish; Auto falls back to Manual otherwise | Command line shows no "not immediate" message; PIX shows draw on immediate context |
| A4 | Emulated interface `ID3D11Device1` at Rhino's feature level is compatible with Rhino's state (Rhino's device is FL ≥ 11_0, 11.1 runtime) | Wrong choice → CreateDeviceContextState fails or swap invalidates Rhino's state | No "CreateDeviceContextState failed" message; Swap mode renders same as Manual |
| A5 | Rhino's device is not `SINGLETHREADED`-mismatched (we mirror the flag) | Flag mismatch → E_INVALIDARG | as A4 |
| A6 | Rhino D3D11 has a **bound-state cache** like Metal | If yes, after Manual restore the GPU state matches the cache so no reset needed; after Swap back too. RhinoStateReset might be unnecessary on Windows (both modes restore exactly) — or Rhino might cache something we change but don't restore | Checklist C6 (RhinoStateReset off) in both Swap and Manual |
| A7 | Things we don't touch stay valid: VB/IB, CBs, SRVs, samplers, scissor rects, SO targets, predication | Manual mode doesn't save them; if Rhino has SO targets bound, our draw would stream out | Debug layer clean; PIX state view at our draw |
| A8 | Swap: our inactive state holding no Rhino views between frames is enough to avoid hazard warnings | Debug layer may warn about resources bound in both states | Debug layer messages around "Freppuccino" event |
| A9 | MSAA / RT format don't matter (D3D11 has no PSO; PS writes float4 to whatever RT) | sRGB or float/HDR RT changes colour; MSAA fine | PIX: RT format + sample count; colours look like pure R/G/B corners |
| A10 | Clip space y-up, z∈[0,1] ⇒ triangle upright, blue vertex top; SPIRV-Cross emits no y-flip / no base-vertex cbuffer | Upside-down triangle = convention mismatch | Checklist C1 |
| A11 | Rhino D3D depth convention (range, reversed-Z?) | Irrelevant now (depth off); needed before depth-tested shaders | PIX: Rhino's DSS DepthFunc + clear value |
| A12 | `SV_VertexID` starts at 0 for `Draw(3,0)` | Wrong indices → garbage | C1 |
| A13 | `d3dcompiler_47.dll` loadable by name (System32) | Compile failure otherwise | Command line clean |
| A14 | Native libs `shaderc_shared.dll` / `spirv-cross.dll` resolve from `<plugin dir>/runtimes/win-x64|win-arm64/native/` via `NativeLibs` resolver | Plugin's ALC may ignore deps.json | No DllNotFoundException on command line |
| A15 | Rhino 9 Windows runs .NET 10 (plugin is `net10.0`) | Plugin won't load on .NET 8/9 runtime | `Environment.Version` via run_csharp; `_SetDotNetRuntime` |
| A16 | csproj Windows RunArguments: `C:\Program Files\Rhino 9\System\Rhino.exe` + `RHINO_PACKAGE_DIRS` auto-loads the .rhp | Install path may differ (WIP / Rhino 9 Beta) | `_PlugInManager` lists Freppuccino as loaded |
| A17 | Device pointer stable; device recreation (TDR, GPU switch) produces a new pointer | We re-init on pointer change only (ABA reuse would keep stale objects) | Optional: `dxcap -forcetdr` not needed for slice; just note |
| A18 | View capture (`ViewCaptureToFile`, MCP `get_viewport_image`) runs PostDrawObjects with its own bound RT and works too | Second pipeline/RT | C7 |

## (b) Test instructions (for an autonomous agent on Windows)

You are on Windows with Rhino 9 and this repo. Do not modify code except where a step says so (and revert after). Record everything for the report (§Report).

### 1. Build
```powershell
cd <repo>\src\Freppuccino
dotnet --list-sdks              # need a 10.x SDK
dotnet build -c Debug
dir bin\Debug\net10.0\runtimes\win-x64\native   # expect shaderc_shared.dll, spirv-cross.dll
```
Expect 0 errors. Note any warnings.

### 2. Launch Rhino
Check `C:\Program Files\Rhino 9\System\Rhino.exe` exists; if not, find Rhino.exe and fix `RunArguments` in `Freppuccino.csproj` (report the path, A16).
```powershell
dotnet run -c Debug --no-build     # launches Rhino with RHINO_PACKAGE_DIRS=bin\Debug\net10.0\
```
In Rhino: confirm plugin loaded (`_PlugInManager`, or command `Freppuccino` exists). If not auto-loaded: drag `bin\Debug\net10.0\Freppuccino.rhp` onto Rhino, or `Rhino.PlugIns.PlugIn.LoadPlugIn(path, out _)` via run_csharp. Record which worked.

### 3. Connect to Rhino's MCP
In Rhino: `_MCPStart` → server at `http://localhost:10500/` (Streamable HTTP, JSON-RPC). Preferred: register it with your client and use its tools directly:
```
claude mcp add --transport http rhino http://localhost:10500/
```
(restart the session so tools appear). Tools of interest: `run_csharp`, `run_command`, `get_viewport_image`. Run `tools/list` first and use the actual parameter names (expected: `run_csharp{script}`, `run_command{command}`).

Fallback with curl (use files for bodies to avoid PowerShell quoting; use `curl.exe`, not the PowerShell alias):
```
# init.json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"validator","version":"0"}}}
curl.exe -s -D hdr.txt -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" --data @init.json http://localhost:10500/
#  → read Mcp-Session-Id from hdr.txt (if present); pass it on every later call: -H "Mcp-Session-Id: <id>"
# initialized.json
{"jsonrpc":"2.0","method":"notifications/initialized"}
# list.json
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
# call.json
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_command","arguments":{"command":"_Freppuccino"}}}
```
Responses may be SSE (`data: {...}` lines). If `/` 404s, try `/mcp`.

### 4. Environment facts (run_csharp)
```csharp
var v = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ActiveViewport;
Console.WriteLine($".NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, Rhino {Rhino.RhinoApp.Version}");
Console.WriteLine($"mode={v.DisplayMode.EnglishName}");
foreach (var m in Rhino.Display.DisplayModeDescription.GetDisplayModes()) if (m.EnglishName == "Freppuccino") Console.WriteLine("Freppuccino mode present " + m.Id);
```
If the tool's description says it injects a doc variable (e.g. `__rhino_doc__`), use that instead of `RhinoDoc.ActiveDoc`. Also check the Rhino display technology is D3D11 (`_SystemInfo` → look for Direct3D 11 / "Display technology"); if it says OpenGL, switch Rhino to D3D11 (Options → View → Display technology, or `_TestDisplayTechnology` if present), restart, record.

### 5. Scene setup (run_csharp)
```csharp
using Rhino; using Rhino.Geometry; using Rhino.DocObjects;
var doc = RhinoDoc.ActiveDoc;
foreach (var o in doc.Objects.GetObjectList(ObjectType.AnyObject)) doc.Objects.Delete(o, true);
ObjectAttributes A(string n) => new ObjectAttributes { Name = n };
doc.Objects.AddBrep(new Box(Plane.WorldXY, new Interval(-8, 8), new Interval(-8, 8), new Interval(0, 8)).ToBrep(), A("box"));
doc.Objects.AddSphere(new Sphere(new Point3d(12, 0, 4), 4), A("sphere"));
doc.Objects.AddTextDot(new TextDot("dot", new Point3d(0, 0, 12)), A("dot"));
doc.Views.Redraw();
Console.WriteLine(doc.Objects.Count);
```
Then: `run_command "_-Viewport _Maximize"` (or maximize Perspective manually), `run_command "_Freppuccino"`, `run_command "_Zoom _All _Extents"`.

### 6. Capturing
- **Screen capture is ground truth** (on-screen pipeline; gumball/highlight are on-screen only). PowerShell:
  ```powershell
  Add-Type -AssemblyName System.Windows.Forms,System.Drawing
  $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); $bmp.Save("$env:TEMP\frep_C1.png")
  ```
  Rhino must be foreground/unobscured. Then view the PNG.
- `get_viewport_image` = view capture (separate render). Use for C7 only. **Don't pass `displayMode`** (it would switch the mode away from Freppuccino).

### 7. Checklist
Expected baseline look (Freppuccino = copy of Wireframe): Rhino's normal wireframe viewport (grid, axes, black/dark wires), plus an opaque triangle covering the central part of the viewport: vertices at clip (−0.5,−0.5) **red** bottom-left, (0.5,−0.5) **green** bottom-right, (0,0.5) **blue** top-centre, smooth gradient between. Triangle is fixed to the viewport (doesn't move when orbiting). Same as the Mac result.

| ID | Steps | PASS looks like |
|---|---|---|
| C0 | After launch, command history | No `Freppuccino D3D11:` messages, no exceptions |
| C1 | Baseline screenshot | Triangle as above; upright (blue top); fills ~half viewport width; correct colours (not washed out/darker → sRGB/format issue A9) |
| C2 | Nothing selected | Box/sphere wires and grid **under** triangle (hidden where triangle covers them), visible outside it |
| C3 | Select box (`_SelNone`, then select by name "box" via set_selection/run_csharp `doc.Objects.Select(id)`) | Box's highlighted (yellow) wires drawn **over** the triangle; sphere wires still under |
| C4 | Box selected, gumball on (`_Gumball` toggles; check status bar/screenshot) | Gumball arrows/arcs over everything, correct colours |
| C5 | Sanity: text dot "dot" renders correctly (white text in box); other viewports (restore 4-view layout `_4View`; set one to Shaded with `_SetDisplayMode _Mode=Shaded`) look normal and have **no** triangle; Shaded viewport shading/depth correct; orbit Freppuccino view → wires move, triangle doesn't; resize window → triangle stays centred | All normal; no flicker, no missing objects, no state bleed (e.g. culled faces or missing depth in Shaded view) |
| C6 | **RhinoStateReset off**: set `Freppuccino.Display.RhinoStateReset.Enabled = false` via reflection in run_csharp (or launch with env `FREPPUCINO_STATE_RESET=0`), redraw, repeat C2–C5 for StateMode=Swap and StateMode=Manual (see below). Set back to true after. | Report per mode whether selected wires/gumball/dot still render. If everything renders, reset not needed on D3D11 (A6) |
| C7 | `get_viewport_image` (Freppuccino active viewport, default size) and `_-ViewCaptureToFile` | Triangle present, same look as C1 |
| C8 | Switch StateMode at runtime and redraw, compare C1–C5 in each: | Identical in Swap and Manual |
| C9 | D3D debug layer (below): repeat C1–C5 | No errors/warnings that mention Freppuccino objects or occur inside the "Freppuccino" event |
| C10 | Graphics capture (PIX or RenderDoc, below): find event "Freppuccino" | Record: context type, RTV format + sample count, DSV format, viewport, Rhino's DSS DepthFunc/clear depth (A11), state before/after our draw identical, draw happens between object wires and highlight draws |

StateMode switch (run_csharp; reflection because the script assembly can't reference the plugin directly):
```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Freppuccino");
var t = asm.GetType("Freppuccino.Gpu.D3D11.D3D11Backend");
var p = t.GetProperty("StateMode");
p.SetValue(null, Enum.Parse(p.PropertyType, "Manual"));   // or "Swap", "Auto"
Rhino.RhinoDoc.ActiveDoc.Views.Redraw();
Console.WriteLine(p.GetValue(null));
```
Alternatively launch with env `FREPPUCINO_D3D11_STATE=manual` (set it before `dotnet run`).

D3D debug layer (Rhino creates the device, so force it externally):
1. Install Graphics Tools: `Add-WindowsCapability -Online -Name Tools.Graphics.DirectX~~~~0.0.1.0` (admin).
2. `dxcpl.exe` → Edit List → add `Rhino.exe` → Direct3D 10/11 "Debug Layer: Force On", message mute none. Apply.
3. Capture debug output with Sysinternals DebugView (`Dbgview.exe`, Capture Win32) while Rhino runs, or launch Rhino under a debugger. Save the log; grep "Freppuccino" and "D3D11 ERROR"/"WARNING".
4. Disable "Force On" afterwards.

Graphics capture: PIX on Windows (attach/launch Rhino.exe, GPU capture) or RenderDoc (Launch Application → Rhino.exe; capture with F12 / Capture Frame). Our draw is inside event `Freppuccino`, our objects are named `Freppuccino VS <hash8>`, `Freppuccino PS …`, `Freppuccino RS`, `Freppuccino DSS`, `Freppuccino ContextState`. Visual Studio Graphics Diagnostics also works if available. If capture tools fail to hook Rhino, say so and move on.

### 8. If something fails
- Plugin not loading → check `.NET` version (A15), command line, `%APPDATA%\McNeel\Rhinoceros\9.0\` logs.
- `DllNotFoundException shaderc_shared/spirv-cross` → A14; list `runtimes\` in the output dir.
- `D3DCompile … failed` → message includes HLSL errors; dump HLSL with run_csharp calling `Freppuccino.Shaders.ShaderCompiler.ToHlsl(ToSpirv(...))` via reflection and include it.
- No triangle, no messages → check viewport mode is Freppuccino; check display technology is D3D11 (A3/A4); try `StateMode=Manual`; then PIX.
- Rhino crash → note the last action; retry with StateMode=Manual; collect `%LOCALAPPDATA%\CrashDumps` if any.

### Report
Return: environment (Windows build, GPU + driver, Rhino version, .NET version, arch, display technology), table C0–C10 with PASS/FAIL + one-line note each, screenshots for C1/C3/C4/C6 (both modes)/C7, debug-layer excerpt, PIX/RenderDoc facts (C10), status of each assumption A1–A18 (confirmed / refuted / untested), and every deviation from these instructions (paths, command names, tool param names).
