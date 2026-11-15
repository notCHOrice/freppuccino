using System.Collections.Immutable;

namespace Freppuccino.UserFn;

// STUB: replaced by the real compiler.
public static partial class UserFnCompiler
{
    public static partial UserFnResult Compile(string source) =>
        new(null, [new UserFnDiagnostic(1, 1, "FFunction compiler not implemented")]);
}

public static class UserFnLibrary
{
    /// GLSL library user code may call (smin, smax, dot2, sdBox, ...). Single source for the fragment shader too.
    public const string Glsl = "";
}
