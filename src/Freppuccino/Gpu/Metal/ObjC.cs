using System.Runtime.InteropServices;
using System.Text;

namespace Freppuccino.Gpu.Metal;

/// Minimal ObjC runtime interop. objc_msgSend is called through typed unmanaged function pointers (one per signature).
/// BOOL is passed as byte (arm64 BOOL == bool, 1 byte).
static unsafe class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";
    static readonly IntPtr send = NativeLibrary.GetExport(NativeLibrary.Load(Lib), "objc_msgSend");

    [DllImport(Lib)] static extern IntPtr sel_registerName(byte* name);
    [DllImport(Lib)] static extern IntPtr objc_getClass(byte* name);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    public static IntPtr Sel(string name) { fixed (byte* p = Z(name)) return sel_registerName(p); }
    public static IntPtr Class(string name) { fixed (byte* p = Z(name)) return objc_getClass(p); }
    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    // signatures: receiver, selector, args...
    public static IntPtr Send(IntPtr o, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)send)(o, s);
    public static IntPtr Send(IntPtr o, IntPtr s, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)send)(o, s, a);
    public static IntPtr Send(IntPtr o, IntPtr s, nuint a) => ((delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr>)send)(o, s, a);
    public static void Send(IntPtr o, IntPtr s, byte a) => ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)send)(o, s, a);
    public static IntPtr Send(IntPtr o, IntPtr s, byte* a) => ((delegate* unmanaged<IntPtr, IntPtr, byte*, IntPtr>)send)(o, s, a);
    public static IntPtr Send(IntPtr o, IntPtr s, IntPtr a, IntPtr* err) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)send)(o, s, a, err);
    public static IntPtr Send(IntPtr o, IntPtr s, IntPtr a, IntPtr b, IntPtr* err) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)send)(o, s, a, b, err);
    public static void Send(IntPtr o, IntPtr s, nuint a, nuint b, nuint c) => ((delegate* unmanaged<IntPtr, IntPtr, nuint, nuint, nuint, void>)send)(o, s, a, b, c);

    static readonly IntPtr selAlloc = Sel("alloc"), selInit = Sel("init"), selRelease = Sel("release"),
        selInitUtf8 = Sel("initWithUTF8String:"), selUtf8 = Sel("UTF8String"), selDesc = Sel("localizedDescription"),
        selDescription = Sel("description"), selClassName = Sel("className");

    /// +1 retained instance of `cls`.
    public static IntPtr New(string cls) => Send(Send(Class(cls), selAlloc), selInit);

    /// +1 retained NSString; caller releases.
    public static IntPtr NewString(string s)
    {
        fixed (byte* p = Z(s)) return Send(Send(Class("NSString"), selAlloc), selInitUtf8, p);
    }

    public static void Release(ref IntPtr o)
    {
        if (o != IntPtr.Zero) Send(o, selRelease);
        o = IntPtr.Zero;
    }

    public static string? ToManaged(IntPtr nsString) => nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, selUtf8));
    public static string ErrorText(IntPtr nsError) => nsError == IntPtr.Zero ? "(no NSError)" : ToManaged(Send(nsError, selDesc)) ?? "(no description)";
    public static string? Description(IntPtr o) => o == IntPtr.Zero ? null : ToManaged(Send(o, selDescription));
    public static string? ClassName(IntPtr o) => o == IntPtr.Zero ? null : ToManaged(Send(o, selClassName));
}
