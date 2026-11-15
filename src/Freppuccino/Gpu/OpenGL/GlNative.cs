using System.Runtime.InteropServices;

namespace Freppuccino.Gpu.OpenGL;

static unsafe class GlNative
{
    const string Gl = "opengl32.dll";

    public const uint TRIANGLES = 0x0004, FRONT_AND_BACK = 0x0408, FILL = 0x1B02,
        CULL_FACE = 0x0B44, DEPTH_TEST = 0x0B71, DEPTH_WRITEMASK = 0x0B72, STENCIL_TEST = 0x0B90, BLEND = 0x0BE2, POLYGON_MODE = 0x0B40,
        VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84,
        CURRENT_PROGRAM = 0x8B8D, VERTEX_ARRAY_BINDING = 0x85B5, DYNAMIC_DRAW = 0x88E8,
        UNIFORM_BUFFER = 0x8A11, UNIFORM_BUFFER_BINDING = 0x8A28, UNIFORM_BUFFER_START = 0x8A29, UNIFORM_BUFFER_SIZE = 0x8A2A,
        MAX_UNIFORM_BUFFER_BINDINGS = 0x8A2F, ACTIVE_UNIFORM_BLOCKS = 0x8A36;

    [DllImport(Gl)] public static extern IntPtr wglGetCurrentContext();
    [DllImport(Gl)] static extern IntPtr wglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Gl)] public static extern void glGetIntegerv(uint pname, int* data);
    [DllImport(Gl)] public static extern byte glIsEnabled(uint cap);
    [DllImport(Gl)] public static extern void glEnable(uint cap);
    [DllImport(Gl)] public static extern void glDisable(uint cap);
    [DllImport(Gl)] public static extern void glDepthMask(byte flag);
    [DllImport(Gl)] public static extern void glPolygonMode(uint face, uint mode);
    [DllImport(Gl)] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport(Gl)] public static extern uint glGetError();

    public static delegate* unmanaged<uint, uint> glCreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> glShaderSource;
    public static delegate* unmanaged<uint, void> glCompileShader, glLinkProgram, glDeleteShader, glDeleteProgram, glUseProgram, glBindVertexArray;
    public static delegate* unmanaged<uint, uint, int*, void> glGetShaderiv, glGetProgramiv, glGetIntegeri_v;
    public static delegate* unmanaged<uint, int, int*, byte*, void> glGetShaderInfoLog, glGetProgramInfoLog;
    public static delegate* unmanaged<uint> glCreateProgram;
    public static delegate* unmanaged<uint, uint, void> glAttachShader, glDetachShader, glBindBuffer;
    public static delegate* unmanaged<int, uint*, void> glGenVertexArrays, glGenBuffers;
    public static delegate* unmanaged<uint, nint, void*, uint, void> glBufferData;
    public static delegate* unmanaged<uint, uint, uint, void> glBindBufferBase, glUniformBlockBinding;
    public static delegate* unmanaged<uint, uint, uint, nint, nint, void> glBindBufferRange;

    static bool loaded;
    static string? loadError;

    public static string? Load()
    {
        if (loaded) return loadError;
        if (wglGetCurrentContext() == IntPtr.Zero) return "no current OpenGL context";
        loaded = true;
        try
        {
            glCreateShader = (delegate* unmanaged<uint, uint>)Proc("glCreateShader");
            glShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Proc("glShaderSource");
            glCompileShader = (delegate* unmanaged<uint, void>)Proc("glCompileShader");
            glLinkProgram = (delegate* unmanaged<uint, void>)Proc("glLinkProgram");
            glDeleteShader = (delegate* unmanaged<uint, void>)Proc("glDeleteShader");
            glDeleteProgram = (delegate* unmanaged<uint, void>)Proc("glDeleteProgram");
            glUseProgram = (delegate* unmanaged<uint, void>)Proc("glUseProgram");
            glBindVertexArray = (delegate* unmanaged<uint, void>)Proc("glBindVertexArray");
            glGetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetShaderiv");
            glGetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetProgramiv");
            glGetIntegeri_v = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetIntegeri_v");
            glGetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetShaderInfoLog");
            glGetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetProgramInfoLog");
            glCreateProgram = (delegate* unmanaged<uint>)Proc("glCreateProgram");
            glAttachShader = (delegate* unmanaged<uint, uint, void>)Proc("glAttachShader");
            glDetachShader = (delegate* unmanaged<uint, uint, void>)Proc("glDetachShader");
            glBindBuffer = (delegate* unmanaged<uint, uint, void>)Proc("glBindBuffer");
            glGenVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc("glGenVertexArrays");
            glGenBuffers = (delegate* unmanaged<int, uint*, void>)Proc("glGenBuffers");
            glBufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)Proc("glBufferData");
            glBindBufferBase = (delegate* unmanaged<uint, uint, uint, void>)Proc("glBindBufferBase");
            glUniformBlockBinding = (delegate* unmanaged<uint, uint, uint, void>)Proc("glUniformBlockBinding");
            glBindBufferRange = (delegate* unmanaged<uint, uint, uint, nint, nint, void>)Proc("glBindBufferRange");
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }
        return loadError;
    }

    static IntPtr Proc(string name)
    {
        var p = wglGetProcAddress(name);
        if ((long)p is >= -1 and <= 3 && !(NativeLibrary.TryLoad(Gl, out var lib) && NativeLibrary.TryGetExport(lib, name, out p)))
            throw new EntryPointNotFoundException($"OpenGL function {name} unavailable");
        return p;
    }

    public static int GetInteger(uint pname)
    {
        int* v = stackalloc int[4];
        v[0] = 0;
        glGetIntegerv(pname, v);
        return v[0];
    }

    public static int GetIntegerIndexed(uint pname, uint index)
    {
        int v = 0;
        glGetIntegeri_v(pname, index, &v);
        return v;
    }
}
