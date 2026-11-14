using System.Runtime.InteropServices;
using Rhino.Display;

namespace Freppucino.Gpu.Metal;

/// Rhino's Metal objects via undocumented RhCore exports (CRhinoDisplayPipeline members, called with the native pipeline as `this`).
/// RhCore is located among the images already loaded in the process (no hardcoded app path).
static unsafe class RhinoMetal
{
    const string DeviceSym = "_ZN21CRhinoDisplayPipeline14GetMetalDeviceEv";
    const string EncoderSym = "_ZN21CRhinoDisplayPipeline28GetMetalRenderCommandEncoderEv";

    static readonly Lazy<(IntPtr dev, IntPtr enc, string? error)> exports = new(Resolve);

    /// Null error = available.
    public static string? Error => exports.Value.error;

    /// Borrowed (+0) id<MTLDevice>, id<MTLRenderCommandEncoder> for the pipeline's current pass. Zero if unavailable.
    public static (IntPtr device, IntPtr encoder) Get(DisplayPipeline dp)
    {
        var (d, e, err) = exports.Value;
        if (err != null) return default;
        var self = Rhino.Runtime.Interop.NativeNonConstPointer(dp);
        if (self == IntPtr.Zero) return default;
        return (((delegate* unmanaged<IntPtr, IntPtr>)d)(self), ((delegate* unmanaged<IntPtr, IntPtr>)e)(self));
    }

    static (IntPtr, IntPtr, string?) Resolve()
    {
        var path = FindLoadedImage("/RhCore.framework/RhCore") ?? FindLoadedImage("/RhCore");
        if (path == null) return (0, 0, "RhCore not loaded in process");
        if (!NativeLibrary.TryLoad(path, out var h)) return (0, 0, "cannot open " + path);
        if (!NativeLibrary.TryGetExport(h, DeviceSym, out var d)) return (0, 0, "missing export " + DeviceSym);
        if (!NativeLibrary.TryGetExport(h, EncoderSym, out var e)) return (0, 0, "missing export " + EncoderSym);
        return (d, e, null);
    }

    [DllImport("/usr/lib/libSystem.B.dylib")] static extern uint _dyld_image_count();
    [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr _dyld_get_image_name(uint i);

    static string? FindLoadedImage(string suffix)
    {
        for (uint i = 0, n = _dyld_image_count(); i < n; i++)
        {
            var name = Marshal.PtrToStringUTF8(_dyld_get_image_name(i));
            if (name != null && name.EndsWith(suffix, StringComparison.Ordinal)) return name;
        }
        return null;
    }
}
