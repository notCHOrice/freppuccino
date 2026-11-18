using Freppuccino.Shaders;
using Freppuccino.UserFn;

if (args.Length == 0)
{
    Console.WriteLine("usage: dotnet run -- <file.glsl>,<bx>,<by>,<bz>,<L>[,<param>=<value>...] ...   (b = clip box half extents in the local frame)");
    return;
}

foreach (var spec in args)
{
    var parts = spec.Split(',');
    var path = parts[0];
    double bx = double.Parse(parts[1]), by = double.Parse(parts[2]), bz = double.Parse(parts[3]), L = double.Parse(parts[4]);
    var overrides = parts.Skip(5).Select(s => s.Split('=')).ToDictionary(a => a[0], a => double.Parse(a[1]));
    var name = Path.GetFileNameWithoutExtension(path);
    var r = UserFnCompiler.Compile(File.ReadAllText(path));
    if (!r.Ok) { Console.WriteLine($"{name}: ERRORS\n  " + string.Join("\n  ", r.Diagnostics)); continue; }
    var prog = r.Program!;
    string glslOk;
    try
    {
        NativeLibs.Install();
        var sb = new System.Text.StringBuilder("#version 450\nlayout(location = 0) in vec2 vNdc;\nlayout(location = 0) out vec4 outColor;\nlayout(std140, set = 0, binding = 0) uniform Frame { vec4 data[8]; } u;\n");
        sb.Append(UserFnLibrary.Glsl).Append('\n').Append(prog.Glsl("ufn")).Append('\n');
        var a = string.Concat(Enumerable.Range(0, prog.Params.Length).Select(i => $", u.data[{1 + i / 4}][{i % 4}]"));
        sb.Append($"void main() {{ float d = ufn(vec3(vNdc, u.data[0].x){a}); outColor = vec4(d); }}\n");
        var spv = ShaderCompiler.ToSpirv(sb.ToString(), ShaderStage.Fragment);
        ShaderCompiler.ToHlsl(spv); ShaderCompiler.ToMsl(spv, ShaderStage.Fragment);
        glslOk = "glsl ok";
    }
    catch (Exception ex) { glslOk = "GLSL FAIL: " + ex.Message.Split('\n')[0]; }

    var u = prog.Params.Select(p => overrides.TryGetValue(p.Name, out var v) ? v : p.Default).ToArray();
    double F(double x, double y, double z)
    {
        double f = prog.Cpu(x, y, z, u) / L;
        double qx = Math.Abs(x) - bx, qy = Math.Abs(y) - by, qz = Math.Abs(z) - bz;
        double box = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2) + Math.Pow(Math.Max(qz, 0), 2)) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        return Math.Max(f, box);
    }
    var rng = new Random(1);
    int n = 60000, inside = 0;
    var ratios = new List<double>();
    double h = 1e-3 * Math.Max(bx, Math.Max(by, bz));
    for (int i = 0; i < n; i++)
    {
        double x = (rng.NextDouble() * 2 - 1) * bx, y = (rng.NextDouble() * 2 - 1) * by, z = (rng.NextDouble() * 2 - 1) * bz;
        double f0 = F(x, y, z);
        if (f0 < 0) inside++;
        double dx = rng.NextDouble() - .5, dy = rng.NextDouble() - .5, dz = rng.NextDouble() - .5, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double f1 = F(x + h * dx / l, y + h * dy / l, z + h * dz / l);
        ratios.Add(Math.Abs(f1 - f0) / h);
    }
    ratios.Sort();
    Console.WriteLine($"{name} [{string.Join(" ", overrides.Select(kv => kv.Key + "=" + kv.Value))}]: {glslOk}; params {prog.Params.Length}; fill {100.0 * inside / n:0.0}%; grad p99.9 {ratios[(int)(n * .999)]:0.00} max {ratios[^1]:0.00} (L={L})");
}
