using System.Runtime.InteropServices;
using System.Text;

namespace Freppuccino.Shaders;

public enum ShaderStage { Vertex = 0, Fragment = 1 } // == shaderc_shader_kind

/// GLSL -> SPIR-V (shaderc) -> MSL 2.1 / HLSL SM5 (SPIRV-Cross). Entry point in output: "main0" (MSL), "main" (HLSL).
/// No clip-space fixups: shaders are authored in Metal/D3D convention.
public static class ShaderCompiler
{
    /// Throws ShaderCompileException with compiler log on failure.
    public static byte[] ToSpirv(string glsl, ShaderStage stage)
    {
        var c = Sc.shaderc_compiler_initialize();
        var o = Sc.shaderc_compile_options_initialize();
        var r = IntPtr.Zero;
        try
        {
            if (c == IntPtr.Zero || o == IntPtr.Zero) throw new ShaderCompileException("shaderc: init failed");
            Sc.shaderc_compile_options_set_target_env(o, Sc.TargetEnvVulkan, Sc.Vulkan10);
            var src = Encoding.UTF8.GetBytes(glsl);
            var file = stage == ShaderStage.Vertex ? "vertex.glsl" : "fragment.glsl";
            r = Sc.shaderc_compile_into_spv(c, src, (nuint)src.Length, (int)stage, file, "main", o);
            if (r == IntPtr.Zero) throw new ShaderCompileException("shaderc: null result");
            if (Sc.shaderc_result_get_compilation_status(r) != 0)
                throw new ShaderCompileException($"shaderc ({stage}): " + Marshal.PtrToStringUTF8(Sc.shaderc_result_get_error_message(r)));
            var bytes = new byte[checked((int)Sc.shaderc_result_get_length(r))];
            Marshal.Copy(Sc.shaderc_result_get_bytes(r), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (r != IntPtr.Zero) Sc.shaderc_result_release(r);
            if (o != IntPtr.Zero) Sc.shaderc_compile_options_release(o);
            if (c != IntPtr.Zero) Sc.shaderc_compiler_release(c);
        }
    }

    /// Uniform block (set 0, binding 0) -> [[buffer(Uniforms.MetalIndex)]].
    public static string ToMsl(byte[] spirv, ShaderStage stage) => Xc.Cross(spirv, Xc.BackendMsl, [(Xc.OptMslVersion, 20100)],
        comp => Xc.MslBind(comp, stage, 0, 0, Uniforms.MetalIndex));
    /// Uniform block -> cbuffer register(b0) (from the binding decoration).
    public static string ToHlsl(byte[] spirv) => Xc.Cross(spirv, Xc.BackendHlsl, [(Xc.OptHlslShaderModel, 50)]);
    public static string ToGlsl(byte[] spirv) => Xc.Cross(spirv, Xc.BackendGlsl, [(Xc.OptGlslVersion, 410), (Xc.OptGlsl420Pack, 0)]);

    static class Sc
    {
        const string L = NativeLibs.Shaderc;
        public const int TargetEnvVulkan = 0;
        public const uint Vulkan10 = 1u << 22;
        [DllImport(L)] public static extern IntPtr shaderc_compiler_initialize();
        [DllImport(L)] public static extern void shaderc_compiler_release(IntPtr c);
        [DllImport(L)] public static extern IntPtr shaderc_compile_options_initialize();
        [DllImport(L)] public static extern void shaderc_compile_options_release(IntPtr o);
        [DllImport(L)] public static extern void shaderc_compile_options_set_target_env(IntPtr o, int env, uint ver);
        [DllImport(L)] public static extern IntPtr shaderc_compile_into_spv(IntPtr c, byte[] src, nuint len, int kind,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string file, [MarshalAs(UnmanagedType.LPUTF8Str)] string entry, IntPtr opts);
        [DllImport(L)] public static extern int shaderc_result_get_compilation_status(IntPtr r);
        [DllImport(L)] public static extern nuint shaderc_result_get_length(IntPtr r);
        [DllImport(L)] public static extern IntPtr shaderc_result_get_bytes(IntPtr r);
        [DllImport(L)] public static extern IntPtr shaderc_result_get_error_message(IntPtr r);
        [DllImport(L)] public static extern void shaderc_result_release(IntPtr r);
    }

    static class Xc
    {
        const string L = NativeLibs.SpirvCross;
        public const int BackendGlsl = 1, BackendHlsl = 2, BackendMsl = 3; // spvc_backend
        public const int OptGlsl420Pack = 0x2000000 | 7;
        public const int OptGlslVersion = 0x2000000 | 8;
        const int CaptureTakeOwnership = 1;                          // spvc_capture_mode
        public const int OptMslVersion = 0x8000000 | 17;             // SPVC_COMPILER_OPTION_MSL_VERSION
        public const int OptHlslShaderModel = 0x4000000 | 13;        // SPVC_COMPILER_OPTION_HLSL_SHADER_MODEL
        [DllImport(L)] static extern int spvc_context_create(out IntPtr ctx);
        [DllImport(L)] static extern void spvc_context_destroy(IntPtr ctx);
        [DllImport(L)] static extern IntPtr spvc_context_get_last_error_string(IntPtr ctx);
        [DllImport(L)] static extern int spvc_context_parse_spirv(IntPtr ctx, uint[] spirv, nuint words, out IntPtr ir);
        [DllImport(L)] static extern int spvc_context_create_compiler(IntPtr ctx, int backend, IntPtr ir, int mode, out IntPtr comp);
        [DllImport(L)] static extern int spvc_compiler_create_compiler_options(IntPtr comp, out IntPtr opts);
        [DllImport(L)] static extern int spvc_compiler_options_set_uint(IntPtr opts, int opt, uint v);
        [DllImport(L)] static extern int spvc_compiler_install_compiler_options(IntPtr comp, IntPtr opts);
        [DllImport(L)] static extern int spvc_compiler_compile(IntPtr comp, out IntPtr src);
        [DllImport(L)] static extern int spvc_compiler_msl_add_resource_binding(IntPtr comp, in MslResourceBinding b);

        /// spvc_msl_resource_binding (v1 layout)
        [StructLayout(LayoutKind.Sequential)]
        struct MslResourceBinding { public int Stage; public uint DescSet, Binding, MslBuffer, MslTexture, MslSampler; }

        public static void MslBind(IntPtr comp, ShaderStage stage, uint set, uint binding, uint buffer)
        {
            var b = new MslResourceBinding { Stage = stage == ShaderStage.Vertex ? 0 : 4 /*SpvExecutionModel*/, DescSet = set, Binding = binding, MslBuffer = buffer };
            if (spvc_compiler_msl_add_resource_binding(comp, b) != 0) throw new ShaderCompileException("spirv-cross: msl_add_resource_binding failed");
        }

        /// All spvc allocations belong to the context; destroying it frees everything (incl. the returned string, copied first).
        public static string Cross(byte[] spirv, int backend, (int opt, uint value)[] options, Action<IntPtr>? configure = null)
        {
            if (spirv.Length == 0 || spirv.Length % 4 != 0) throw new ShaderCompileException($"spirv-cross: bad SPIR-V length {spirv.Length}");
            var words = new uint[spirv.Length / 4];
            Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
            if (spvc_context_create(out var ctx) != 0 || ctx == IntPtr.Zero) throw new ShaderCompileException("spirv-cross: context create failed");
            try
            {
                Check(spvc_context_parse_spirv(ctx, words, (nuint)words.Length, out var ir), ctx);
                Check(spvc_context_create_compiler(ctx, backend, ir, CaptureTakeOwnership, out var comp), ctx);
                Check(spvc_compiler_create_compiler_options(comp, out var opts), ctx);
                foreach (var (opt, value) in options) Check(spvc_compiler_options_set_uint(opts, opt, value), ctx);
                Check(spvc_compiler_install_compiler_options(comp, opts), ctx);
                configure?.Invoke(comp);
                Check(spvc_compiler_compile(comp, out var src), ctx);
                return Marshal.PtrToStringUTF8(src) ?? throw new ShaderCompileException("spirv-cross: null output");
            }
            finally { spvc_context_destroy(ctx); }
        }

        static void Check(int r, IntPtr ctx)
        {
            if (r != 0) throw new ShaderCompileException($"spirv-cross ({r}): " + Marshal.PtrToStringUTF8(spvc_context_get_last_error_string(ctx)));
        }
    }
}

public sealed class ShaderCompileException(string message) : Exception(message);
