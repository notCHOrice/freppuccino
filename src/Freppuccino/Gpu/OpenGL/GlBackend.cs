using System.Text;
using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;
using static Freppuccino.Gpu.OpenGL.GlNative;

namespace Freppuccino.Gpu.OpenGL;

public sealed unsafe class GlBackend : IGpuBackend
{
    const uint PreferredBinding = 30;

    sealed class Context
    {
        public uint Vao, Ubo;
        public readonly Dictionary<string, uint> Programs = [];
    }

    readonly Dictionary<IntPtr, Context> contexts = [];
    readonly Dictionary<string, (string vertex, string fragment)> translated = [];
    readonly Dictionary<string, string> failed = [];
    readonly HashSet<string> logged = [];
    uint binding = uint.MaxValue;

    public bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return DrawGl(program, uniforms); }
        catch (Exception ex) { LogOnce("draw:" + ex.GetType().Name, "Freppuccino OpenGL: " + ex); return false; }
    }

    bool DrawGl(ShaderSource program, ReadOnlySpan<byte> uniforms)
    {
        if (Load() is { } error) { LogOnce("load:" + error, "Freppuccino OpenGL: " + error); return false; }
        var hglrc = wglGetCurrentContext();
        if (hglrc == IntPtr.Zero) { LogOnce("noctx", "Freppuccino OpenGL: no current context"); return false; }
        glGetError();
        if (binding == uint.MaxValue) binding = (uint)Math.Clamp(GetInteger(MAX_UNIFORM_BUFFER_BINDINGS) - 1, 0, (int)PreferredBinding);
        if (!contexts.TryGetValue(hglrc, out var ctx)) contexts[hglrc] = ctx = CreateContext();

        var prog = GetProgram(ctx, program);
        if (prog == 0) return false;

        int oldProgram = GetInteger(CURRENT_PROGRAM), oldVao = GetInteger(VERTEX_ARRAY_BINDING), oldUbo = GetInteger(UNIFORM_BUFFER_BINDING);
        int oldIndexed = GetIntegerIndexed(UNIFORM_BUFFER_BINDING, binding);
        int oldStart = GetIntegerIndexed(UNIFORM_BUFFER_START, binding), oldSize = GetIntegerIndexed(UNIFORM_BUFFER_SIZE, binding);
        bool depth = glIsEnabled(DEPTH_TEST) != 0, blend = glIsEnabled(BLEND) != 0, cull = glIsEnabled(CULL_FACE) != 0, stencil = glIsEnabled(STENCIL_TEST) != 0;
        bool depthMask = GetInteger(DEPTH_WRITEMASK) != 0;
        int polygonMode = GetInteger(POLYGON_MODE);
        glGetError();

        try
        {
            if (!uniforms.IsEmpty)
            {
                Span<byte> full = stackalloc byte[Uniforms.MaxBytes];
                full.Clear();
                uniforms.CopyTo(full);
                glBindBuffer(UNIFORM_BUFFER, ctx.Ubo);
                fixed (byte* p = full) glBufferData(UNIFORM_BUFFER, Uniforms.MaxBytes, p, DYNAMIC_DRAW);
                glBindBufferBase(UNIFORM_BUFFER, binding, ctx.Ubo);
            }
            glUseProgram(prog);
            glBindVertexArray(ctx.Vao);
            glDisable(DEPTH_TEST);
            glDisable(BLEND);
            glDisable(CULL_FACE);
            glDisable(STENCIL_TEST);
            glDepthMask(0);
            glPolygonMode(FRONT_AND_BACK, FILL);
            glDrawArrays(TRIANGLES, 0, (int)program.VertexCount);
        }
        finally
        {
            glUseProgram((uint)oldProgram);
            glBindVertexArray((uint)oldVao);
            if (oldIndexed != 0 && oldSize > 0) glBindBufferRange(UNIFORM_BUFFER, binding, (uint)oldIndexed, oldStart, oldSize);
            else glBindBufferBase(UNIFORM_BUFFER, binding, (uint)oldIndexed);
            glBindBuffer(UNIFORM_BUFFER, (uint)oldUbo);
            Set(DEPTH_TEST, depth);
            Set(BLEND, blend);
            Set(CULL_FACE, cull);
            Set(STENCIL_TEST, stencil);
            glDepthMask(depthMask ? (byte)1 : (byte)0);
            if (polygonMode != 0) glPolygonMode(FRONT_AND_BACK, (uint)polygonMode);
        }
        if (glGetError() is var err and not 0) LogOnce("glerr:" + err, $"Freppuccino OpenGL: GL error 0x{err:X4} during draw");
        return true;
    }

    static void Set(uint cap, bool on) { if (on) glEnable(cap); else glDisable(cap); }

    static Context CreateContext()
    {
        uint vao = 0, ubo = 0;
        glGenVertexArrays(1, &vao);
        glGenBuffers(1, &ubo);
        return new Context { Vao = vao, Ubo = ubo };
    }

    (string vertex, string fragment) Translate(ShaderSource src)
    {
        if (translated.TryGetValue(src.Hash, out var t)) return t;
        var vertex = ShaderCompiler.ToGlsl(ShaderCompiler.ToSpirv(src.Vertex, ShaderStage.Vertex));
        var fragment = ShaderCompiler.ToGlsl(ShaderCompiler.ToSpirv(src.Fragment, ShaderStage.Fragment));
        return translated[src.Hash] = (vertex, fragment);
    }

    uint GetProgram(Context ctx, ShaderSource src)
    {
        if (ctx.Programs.TryGetValue(src.Hash, out var p)) return p;
        if (failed.ContainsKey(src.Hash)) return 0;
        uint vs = 0, fs = 0, prog = 0;
        try
        {
            var (vertex, fragment) = Translate(src);
            vs = CompileShader(VERTEX_SHADER, vertex);
            fs = CompileShader(FRAGMENT_SHADER, fragment);
            prog = glCreateProgram();
            glAttachShader(prog, vs);
            glAttachShader(prog, fs);
            glLinkProgram(prog);
            int ok = 0;
            glGetProgramiv(prog, LINK_STATUS, &ok);
            if (ok == 0) throw new ShaderCompileException("link: " + Log(prog, program: true));
            glDetachShader(prog, vs);
            glDetachShader(prog, fs);
            int blocks = 0;
            glGetProgramiv(prog, ACTIVE_UNIFORM_BLOCKS, &blocks);
            for (uint i = 0; i < blocks; i++) glUniformBlockBinding(prog, i, binding);
            ctx.Programs[src.Hash] = prog;
            prog = 0;
            return ctx.Programs[src.Hash];
        }
        catch (Exception ex)
        {
            failed[src.Hash] = ex.Message;
            RhinoApp.WriteLine($"Freppuccino OpenGL: shader {src.Hash} failed: {ex.Message}");
            return 0;
        }
        finally
        {
            if (vs != 0) glDeleteShader(vs);
            if (fs != 0) glDeleteShader(fs);
            if (prog != 0) glDeleteProgram(prog);
        }
    }

    static uint CompileShader(uint stage, string source)
    {
        var shader = glCreateShader(stage);
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* b = bytes)
        {
            byte* ptr = b;
            int len = bytes.Length;
            glShaderSource(shader, 1, &ptr, &len);
        }
        glCompileShader(shader);
        int ok = 0;
        glGetShaderiv(shader, COMPILE_STATUS, &ok);
        if (ok != 0) return shader;
        var log = Log(shader, program: false);
        glDeleteShader(shader);
        throw new ShaderCompileException((stage == VERTEX_SHADER ? "vertex: " : "fragment: ") + log);
    }

    static string Log(uint handle, bool program)
    {
        int len = 0;
        if (program) glGetProgramiv(handle, INFO_LOG_LENGTH, &len); else glGetShaderiv(handle, INFO_LOG_LENGTH, &len);
        if (len <= 1) return "(no log)";
        var buf = new byte[len];
        fixed (byte* p = buf)
        {
            if (program) glGetProgramInfoLog(handle, len, null, p); else glGetShaderInfoLog(handle, len, null, p);
        }
        return Encoding.UTF8.GetString(buf).TrimEnd('\0', '\n', '\r');
    }

    void LogOnce(string key, string msg) { if (logged.Add(key)) RhinoApp.WriteLine(msg); }

    public string? CompileError(ShaderSource program) => failed.GetValueOrDefault(program.Hash);

    public string? Compile(ShaderSource program)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            Translate(program);
            if (Load() == null && wglGetCurrentContext() is var hglrc && hglrc != IntPtr.Zero && contexts.TryGetValue(hglrc, out var ctx))
                GetProgram(ctx, program);
        }
        catch (Exception ex)
        {
            failed[program.Hash] = ex.Message;
        }
        return CompileError(program);
    }

    public void Dispose()
    {
        contexts.Clear();
        translated.Clear();
        failed.Clear();
    }
}
