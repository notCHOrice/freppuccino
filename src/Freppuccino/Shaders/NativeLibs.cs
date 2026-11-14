using System.Reflection;
using System.Runtime.InteropServices;

namespace Freppuccino.Shaders;

/// Resolves shaderc_shared / spirv-cross from <plugin dir>/runtimes/<rid>/native/ (Rhino's ALC may ignore deps.json native probing).
public static class NativeLibs
{
    public const string Shaderc = "shaderc_shared", SpirvCross = "spirv-cross";
    static int installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(NativeLibs).Assembly, Resolve);
    }

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? search)
    {
        if (name != Shaderc && name != SpirvCross) return IntPtr.Zero; // default probing
        var rid = Rid();
        if (rid == null) return IntPtr.Zero;
        var file = OperatingSystem.IsWindows() ? name + ".dll" : "lib" + name + ".dylib";
        var path = Path.Combine(Path.GetDirectoryName(asm.Location)!, "runtimes", rid, "native", file);
        return NativeLibrary.TryLoad(path, out var h) ? h : IntPtr.Zero;
    }

    static string? Rid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => null,
        };
        if (arch == null) return null;
        if (OperatingSystem.IsMacOS()) return "osx-" + arch;
        if (OperatingSystem.IsWindows()) return "win-" + arch;
        return null;
    }
}
