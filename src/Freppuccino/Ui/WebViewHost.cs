using System.Diagnostics;
using System.Text.Json;
using Eto.Forms;
using Rhino;

namespace Freppuccino.Ui;

/// <summary>
/// Eto <see cref="WebView"/> hosting the built UI, with JSON messages both ways. OS-specific bits live in an
/// <see cref="IWebTransport"/>: <see cref="WebView2Transport"/> (Windows), <see cref="EtoWebTransport"/> (macOS).
///
/// Page source, in order:
///   1. FREPPUCCINO_UI_DEV_URL (e.g. http://localhost:5173 from `npm run dev`), for hot reload
///   2. &lt;plugin dir&gt;/ui/index.html (copied from src/ui/dist by the build), at the transport's URL
///
/// Page side: src/ui/src/bridge/host.ts (createTransport).
/// </summary>
public sealed class WebViewHost : IDisposable
{
    public const string DevUrlEnvVar = "FREPPUCCINO_UI_DEV_URL";

#if DEBUG
    internal static readonly bool DevTools = true;
#else
    internal static readonly bool DevTools = false;
#endif

    public WebView Control { get; } = new();

    /// Raised on the UI thread with the message's "type" and the whole JSON payload.
    public event Action<string, JsonElement>? MessageReceived;

    readonly IWebTransport transport;
    readonly Uri startUri;

    public WebViewHost()
    {
        transport = OperatingSystem.IsWindows()
            ? new WebView2Transport(Control, AllowNavigation, OpenExternally)
            : new EtoWebTransport(Control, AllowNavigation, OpenExternally);
        transport.Received += OnReceived;

        startUri = ResolveStartUri(out var missingUi);
        if (missingUi != null)
        {
            Control.LoadHtml(missingUi);
            return;
        }
        transport.Load(startUri);
    }

    /// Posts a message to the page. Safe to call before the page loads (dropped; the page asks for state on "ready").
    public void Send(HostMessage message)
    {
        if (RhinoApp.InvokeRequired)
        {
            RhinoApp.InvokeOnUiThread(() => Send(message));
            return;
        }
        transport.Post(JsonSerializer.Serialize(message, message.GetType(), UiProtocol.Json));
    }

    // Only our own page may talk to Rhino: same origin as the start page, or for file:// a file inside the UI folder.
    bool IsOurs(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        if (startUri.IsFile)
            return u.IsFile && u.LocalPath.StartsWith(UiFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return string.Equals(u.GetLeftPart(UriPartial.Authority), startUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    // Links elsewhere open in the system browser.
    bool AllowNavigation(string uri)
    {
        if (IsOurs(uri) || uri.StartsWith("about:", StringComparison.Ordinal) || uri.StartsWith("data:", StringComparison.Ordinal))
            return true;
        OpenExternally(uri);
        return false;
    }

    void OnReceived(string json, string? source)
    {
        if (!IsOurs(source)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.GetString() is not { } type)
                return;
            MessageReceived?.Invoke(type, root.Clone());
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Freppuccino UI: bad message: {ex.Message}");
        }
    }

    static void OpenExternally(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    static string UiFolder =>
        Path.Combine(Path.GetDirectoryName(typeof(WebViewHost).Assembly.Location) ?? ".", "ui");

    Uri ResolveStartUri(out string? missingUiHtml)
    {
        missingUiHtml = null;
        var dev = Environment.GetEnvironmentVariable(DevUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(dev) && Uri.TryCreate(dev, UriKind.Absolute, out var devUri))
            return devUri;

        if (!File.Exists(Path.Combine(UiFolder, "index.html")))
            missingUiHtml = $"""
                <body style="font:13px system-ui;padding:12px;color:#888">
                <b>Freppuccino UI not built.</b><br>Run <code>npm install</code> and <code>npm run build</code> in
                <code>src/ui</code>, then rebuild the plugin.<br><br>Expected: <code>{System.Net.WebUtility.HtmlEncode(UiFolder)}</code>
                </body>
                """;
        return transport.PageUri(UiFolder);
    }

    public void Dispose()
    {
        transport.Received -= OnReceived;
        transport.Dispose();
        MessageReceived = null;
    }
}
