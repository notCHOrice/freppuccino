using Freppuccino.Sdf;
using Freppuccino.UserFn;
using Rhino.DocObjects;

namespace Freppuccino.Ui;

/// <summary>
/// Reads an FFunction (SDF.Type = sdUser) straight from user text for the panel.
/// Doesn't go through FrepScene, so it works whether or not the scene supports sdUser.
/// </summary>
static class UserFnView
{
    /// Param key prefix without "SDF.": setParam names are "u.&lt;param&gt;".
    public static readonly string ParamPrefix = SdfUserText.UserParamPrefix[SdfUserText.Prefix.Length..];
    /// setParam name of SDF.L.
    public static readonly string LipschitzName = SdfUserText.LipschitzKey[SdfUserText.Prefix.Length..];

    public static bool IsUser(string? type) => type == SdfUserText.UserType;

    public static string Code(ObjectAttributes attributes) => attributes.GetUserString(SdfUserText.CodeKey) ?? "";

    /// SDF.L, or 1 when missing / not a positive number.
    public static double Lipschitz(ObjectAttributes attributes) =>
        SdfUserText.TryReadFloat(attributes, LipschitzName, out var l) && double.IsFinite(l) && l > 0 ? l : 1;

    /// Compiler contract says it never throws; guard anyway so a bad compiler can't break the panel.
    public static UserFnResult Compile(string code)
    {
        try { return UserFnCompiler.Compile(code); }
        catch (Exception ex) { return new UserFnResult(null, [new UserFnDiagnostic(1, 1, $"Compiler crashed: {ex.Message}")]); }
    }

    /// Declared default of param <paramref name="name"/> (no prefix); false if the code doesn't compile or doesn't declare it.
    public static bool TryDefault(ObjectAttributes attributes, string name, out double value)
    {
        value = 0;
        if (Compile(Code(attributes)).Program is not { } program) return false;
        foreach (var p in program.Params)
            if (p.Name == name) { value = p.Default; return true; }
        return false;
    }

    public static UserFnInfo Read(ObjectAttributes attributes)
    {
        var code = Code(attributes);
        var result = Compile(code);
        var program = result.Program;
        var parameters = program == null
            ? []
            : program.Params.Select(p => new UserFnParamInfo(
                p.Name,
                SdfUserText.TryReadFloat(attributes, ParamPrefix + p.Name, out var v) && double.IsFinite(v) ? v : p.Default,
                p.Default)).ToList();
        return new UserFnInfo(
            code,
            program != null,
            program?.Hash,
            result.Diagnostics.Select(d => new UserFnDiagnosticInfo(d.Line, d.Column, d.Message)).ToList(),
            parameters,
            Lipschitz(attributes));
    }
}
