using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhino;

namespace Freppuccino.Ai;

/// <summary>
/// Saves each AI run (prompt, every model message and tool call, screenshots as PNG files) for reviewing failures
/// and writing skills (src/ui/src/ai/skills). Folder: FREPPUCCINO_AI_RUNS_DIR, else Documents/Freppuccino/ai-runs.
/// One subfolder per run: transcript.json + 01-perspective.png, ...
/// </summary>
public static partial class AiRunLog
{
    public const string DirEnvVar = "FREPPUCCINO_AI_RUNS_DIR";

    public static string Root =>
        Environment.GetEnvironmentVariable(DirEnvVar) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Freppuccino", "ai-runs");

    /// <summary>Writes the run; returns its folder. Image data URLs in <paramref name="images"/> become files.</summary>
    public static string Save(string title, JsonNode transcript, IReadOnlyList<(string Name, string DataUrl)> images)
    {
        var slug = Slug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        var dir = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{(slug.Length > 0 ? slug : "run")}");
        Directory.CreateDirectory(dir);

        foreach (var (name, dataUrl) in images)
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl.StartsWith("data:image/", StringComparison.Ordinal)) continue;
            var file = Path.GetFileName(name); // no directories from the page
            if (file.Length == 0) continue;
            File.WriteAllBytes(Path.Combine(dir, file), Convert.FromBase64String(dataUrl[(comma + 1)..]));
        }
        File.WriteAllText(Path.Combine(dir, "transcript.json"), transcript.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        RhinoApp.WriteLine($"Freppuccino AI: run saved to {dir}");
        return dir;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();
}
