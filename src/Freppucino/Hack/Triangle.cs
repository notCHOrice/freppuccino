// Spike 2: compile MSL with Rhino's MTLDevice and draw a triangle on Rhino's borrowed encoder.
using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Display;

// THROWAWAY: port of the s3.cs spike. Delete Hack/ to remove.
namespace Freppucino.Hack;

static class O
{
    static readonly IntPtr objc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
    static readonly IntPtr core = NativeLibrary.Load("/Applications/Rhino 9.app/Contents/Frameworks/RhCore.framework/RhCore");
    static readonly IntPtr send = NativeLibrary.GetExport(objc, "objc_msgSend");
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr F0(IntPtr s, IntPtr sel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr F1(IntPtr s, IntPtr sel, IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr F2(IntPtr s, IntPtr sel, IntPtr a, IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr F3(IntPtr s, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr FS(IntPtr s, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr FN(string n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr G(IntPtr self);
    public static F0 M0 = D<F0>(send); public static F1 M1 = D<F1>(send); public static F2 M2 = D<F2>(send);
    public static F3 M3 = D<F3>(send); public static FS MS = D<FS>(send);
    static FN selReg = D<FN>(NativeLibrary.GetExport(objc, "sel_registerName"));
    static FN getCls = D<FN>(NativeLibrary.GetExport(objc, "objc_getClass"));
    public static G Dev = D<G>(NativeLibrary.GetExport(core, "_ZN21CRhinoDisplayPipeline14GetMetalDeviceEv"));
    public static G Enc = D<G>(NativeLibrary.GetExport(core, "_ZN21CRhinoDisplayPipeline28GetMetalRenderCommandEncoderEv"));
    static T D<T>(IntPtr p) => Marshal.GetDelegateForFunctionPointer<T>(p);
    public static IntPtr S(string n) => selReg(n);
    public static IntPtr C(string n) => getCls(n);
    public static IntPtr Str(string s) => MS(C("NSString"), S("stringWithUTF8String:"), s);
    public static string Desc(IntPtr o) => o == IntPtr.Zero ? "nil" : Marshal.PtrToStringUTF8(M0(M0(o, S("localizedDescription")), S("UTF8String")));
    public static IntPtr New(string cls) => M0(M0(C(cls), S("alloc")), S("init"));
}

class Tri : DisplayConduit
{
    public static Guid ModeId;
    static bool Active(DrawEventArgs e) => e.Display.DisplayPipelineAttributes?.Id == ModeId;
    public string Channel = "PostDrawObjects"; // after wireframe objects, before foreground/overlays
    public List<string> Log = new();
    IntPtr pso, dss, dev;
    const string VS = @"#pragma clang diagnostic ignored ""-Wmissing-prototypes""
#pragma clang diagnostic ignored ""-Wmissing-braces""

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

template<typename T, size_t Num>
struct spvUnsafeArray
{
    T elements[Num ? Num : 1];
    
    thread T& operator [] (size_t pos) thread
    {
        return elements[pos];
    }
    constexpr const thread T& operator [] (size_t pos) const thread
    {
        return elements[pos];
    }
    
    device T& operator [] (size_t pos) device
    {
        return elements[pos];
    }
    constexpr const device T& operator [] (size_t pos) const device
    {
        return elements[pos];
    }
    
    constexpr const constant T& operator [] (size_t pos) const constant
    {
        return elements[pos];
    }
    
    threadgroup T& operator [] (size_t pos) threadgroup
    {
        return elements[pos];
    }
    constexpr const threadgroup T& operator [] (size_t pos) const threadgroup
    {
        return elements[pos];
    }
};

constant spvUnsafeArray<float3, 3> _18 = spvUnsafeArray<float3, 3>({ float3(1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, 0.0, 1.0) });
constant spvUnsafeArray<float2, 3> _42 = spvUnsafeArray<float2, 3>({ float2(-0.5), float2(0.5, -0.5), float2(0.0, 0.5) });

struct main0_out
{
    float3 vColor [[user(locn0)]];
    float4 gl_Position [[position]];
};

vertex main0_out main0(uint gl_VertexIndex [[vertex_id]])
{
    main0_out out = {};
    out.vColor = _18[int(gl_VertexIndex)];
    out.gl_Position = float4(_42[int(gl_VertexIndex)], 0.0, 1.0);
    return out;
}";
    const string FS = @"#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float4 outColor [[color(0)]];
};

struct main0_in
{
    float3 vColor [[user(locn0)]];
};

fragment main0_out main0(main0_in in [[stage_in]])
{
    main0_out out = {};
    out.outColor = float4(in.vColor, 1.0);
    return out;
}";

    IntPtr Fn(string src)
    {
        IntPtr err = IntPtr.Zero;
        var lib = O.M3(dev, O.S("newLibraryWithSource:options:error:"), O.Str(src), IntPtr.Zero, Marshal.AllocHGlobal(8));
        if (lib == IntPtr.Zero) throw new Exception("newLibraryWithSource failed");
        return O.M1(lib, O.S("newFunctionWithName:"), O.Str("main0"));
    }
    void Build(IntPtr device)
    {
        dev = device;
        var d = O.New("MTLRenderPipelineDescriptor");
        O.M1(d, O.S("setVertexFunction:"), Fn(VS));
        O.M1(d, O.S("setFragmentFunction:"), Fn(FS));
        var ca0 = O.M1(O.M0(d, O.S("colorAttachments")), O.S("objectAtIndexedSubscript:"), (IntPtr)0);
        O.M1(ca0, O.S("setPixelFormat:"), (IntPtr)70);          // RGBA8Unorm
        O.M1(d, O.S("setDepthAttachmentPixelFormat:"), (IntPtr)260);   // Depth32Float_Stencil8
        O.M1(d, O.S("setStencilAttachmentPixelFormat:"), (IntPtr)260);
        O.M1(d, O.S("setRasterSampleCount:"), (IntPtr)4);
        var errp = Marshal.AllocHGlobal(8); Marshal.WriteIntPtr(errp, IntPtr.Zero);
        pso = O.M2(dev, O.S("newRenderPipelineStateWithDescriptor:error:"), d, errp);
        if (pso == IntPtr.Zero) throw new Exception("PSO: " + O.Desc(Marshal.ReadIntPtr(errp)));
        var dd = O.New("MTLDepthStencilDescriptor");
        O.M1(dd, O.S("setDepthCompareFunction:"), (IntPtr)7);   // Always
        O.M1(dd, O.S("setDepthWriteEnabled:"), (IntPtr)0);
        dss = O.M1(dev, O.S("newDepthStencilStateWithDescriptor:"), dd);
        Log.Add($"built pso={pso} dss={dss}");
    }
    void Draw(string ch, DrawEventArgs e)
    {
        if (ch != Channel || !Active(e)) return;
        try {
            var p = Rhino.Runtime.Interop.NativeNonConstPointer(e.Display);
            var enc = O.Enc(p); var d = O.Dev(p);
            if (enc == IntPtr.Zero) { Log.Add("no encoder"); return; }
            if (pso == IntPtr.Zero) Build(d);
            O.M1(enc, O.S("setRenderPipelineState:"), pso);
            O.M1(enc, O.S("setDepthStencilState:"), dss);
            O.M1(enc, O.S("setCullMode:"), (IntPtr)0);
            O.M3(enc, O.S("drawPrimitives:vertexStart:vertexCount:"), (IntPtr)3, (IntPtr)0, (IntPtr)3);
            Dirty(e.Display);
            if (Log.Count < 10) Log.Add($"drew in {ch} vp={e.Viewport.Name}");
        } catch (Exception ex) { if (Log.Count < 10) Log.Add("EX " + ex.Message); RhinoApp.WriteLine("Freppucino hack: " + ex); }
    }
        // HACK for Rhino's Metal state cache: a dummy Rhino draw with different state so its next real draw rebinds its own PSO/DSS
    public static int DirtyMode = 4;
    static void Dirty(DisplayPipeline dp)
    {
        var vp = dp.Viewport; var t = vp.CameraTarget; var x = vp.CameraX * (vp.CameraLocation.DistanceTo(t) * 1e-4);
        switch (DirtyMode) {
            case 1: dp.PushDepthTesting(false); dp.PushDepthWriting(false);
                dp.DrawPoint(t, PointStyle.Simple, 0, System.Drawing.Color.FromArgb(0,0,0,0));
                dp.PopDepthWriting(); dp.PopDepthTesting(); break;
            case 2: dp.DrawLine(t, t + x * 2000, System.Drawing.Color.Magenta, 3); break; // visible, to confirm mechanism
            case 3: dp.PushDepthTesting(false); dp.DrawLine(t, t + x * 2000, System.Drawing.Color.Magenta, 3); dp.PopDepthTesting(); break;
            case 4: { var c = vp.CameraLocation - vp.CameraDirection; dp.DrawLine(c, c + x, System.Drawing.Color.Magenta, 1); break; } // behind camera, GPU-clipped
        }
    }
    protected override void PreDrawObjects(DrawEventArgs e) => Draw("PreDrawObjects", e);
    protected override void PostDrawObjects(DrawEventArgs e) => Draw("PostDrawObjects", e);
    protected override void DrawForeground(DrawEventArgs e) => Draw("DrawForeground", e);
}


static class Boot
{
    public static Tri? Conduit;
    public static void Start()
    {
        // recreate from Wireframe each load (old one was a Shaded copy)
        var m = DisplayModeDescription.FindByName("Freppucino");
        if (m != null) DisplayModeDescription.DeleteDisplayMode(m.Id);
        Tri.ModeId = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.WireframeId, "Freppucino");
        var nm = DisplayModeDescription.GetDisplayMode(Tri.ModeId);
        if (nm != null) DisplayModeDescription.UpdateDisplayMode(nm); // persist
        Conduit = new Tri { Enabled = true };
        RhinoApp.WriteLine($"Freppucino hack: conduit on, mode {Tri.ModeId}");
    }
}
