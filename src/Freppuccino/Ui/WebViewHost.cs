using System.Diagnostics;
using System.Text.Json;
using Eto.Forms;
using Microsoft.Web.WebView2.Core;
using Rhino;

namespace Freppuccino.Ui;

/// <summary>
/// Eto <see cref="WebView"/> plus the WebView2 pieces Eto doesn't expose: serving the built UI from disk
/// and JSON postMessage both ways. On Windows, Rhino's Eto WebView is WebView2, so we reach
/// <see cref="CoreWebView2"/> through the native control (ControlObject is the WPF WebView2).
/// That keeps the project free of WPF references, so it still builds on macOS.
///
/// Page source, in order:
///   1. FREPPUCCINO_UI_DEV_URL (e.g. http://localhost:5173 from `npm run dev`), for hot reload
///   2. &lt;plugin dir&gt;/ui/index.html (copied from src/ui/dist by the build), served as https://freppuccino.ui/
///
/// The page uses window.chrome.webview.postMessage / addEventListener("message"). See src/ui/src/bridge.
/// </summary>
public sealed class WebViewHost : IDisposable
{
    public const string DevUrlEnvVar = "FREPPUCCINO_UI_DEV_URL";
    const string VirtualHost = "freppuccino.ui";

    public WebView Control { get; } = new();

    /// Raised on the UI thread with the message's "type" and the whole JSON payload.
    public event Action<string, JsonElement>? MessageReceived;

    CoreWebView2? core;
    readonly Uri startUri;

    public WebViewHost()
    {
        startUri = ResolveStartUri(out var missingUi);
        if (missingUi != null)
        {
            Control.LoadHtml(missingUi);
            return;
        }
        HookCore();
    }

    /// Posts a message to the page. Safe to call before the page loads (dropped; the page asks for state on "ready").
    public void Send(HostMessage message)
    {
        if (RhinoApp.InvokeRequired)
        {
            RhinoApp.InvokeOnUiThread(() => Send(message));
            return;
        }
        if (core == null) return;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, message.GetType(), UiProtocol.Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView torn down / navigating; the page re-syncs on its next "ready".
        }
    }

    void HookCore()
    {
        // The WPF control creates CoreWebView2 asynchronously once it is in the visual tree.
        var native = Control.ControlObject;
        if (TryGetCore(native) is { } ready)
        {
            Attach(ready);
            return;
        }
        var evt = native?.GetType().GetEvent("CoreWebView2InitializationCompleted");
        if (evt == null)
        {
            RhinoApp.WriteLine("Freppuccino: side panel needs WebView2 (Windows).");
            return;
        }
        EventHandler<CoreWebView2InitializationCompletedEventArgs> handler = null!;
        handler = (_, e) =>
        {
            evt.RemoveEventHandler(native, handler);
            if (!e.IsSuccess)
            {
                RhinoApp.WriteLine($"Freppuccino: WebView2 failed to start: {e.InitializationException?.Message}");
                return;
            }
            if (TryGetCore(native) is { } c) Attach(c);
        };
        evt.AddEventHandler(native, handler);
    }

    static CoreWebView2? TryGetCore(object? native) =>
        native?.GetType().GetProperty("CoreWebView2")?.GetValue(native) as CoreWebView2;

    void Attach(CoreWebView2 c)
    {
        core = c;
#if DEBUG
        const bool dev = true;
#else
        const bool dev = false;
#endif
        c.Settings.AreDevToolsEnabled = dev; // F12 / right-click → Inspect
        c.Settings.AreDefaultContextMenusEnabled = dev;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsWebMessageEnabled = true;

        if (startUri.Host == VirtualHost)
            c.SetVirtualHostNameToFolderMapping(VirtualHost, UiFolder, CoreWebView2HostResourceAccessKind.DenyCors);

        c.WebMessageReceived += OnWebMessageReceived;
        c.NavigationStarting += OnNavigationStarting;
        c.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        c.Navigate(startUri.AbsoluteUri);
    }

    // Only our own page may talk to Rhino; links elsewhere open in the system browser.
    bool IsOurs(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        string.Equals(u.GetLeftPart(UriPartial.Authority), startUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsOurs(e.Uri) || e.Uri.StartsWith("about:", StringComparison.Ordinal) || e.Uri.StartsWith("data:", StringComparison.Ordinal))
            return;
        e.Cancel = true;
        OpenExternally(e.Uri);
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOurs(e.Source)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
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

    static Uri ResolveStartUri(out string? missingUiHtml)
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
        return new Uri($"https://{VirtualHost}/index.html");
    }

    public void Dispose()
    {
        if (core != null)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core = null;
        }
        MessageReceived = null;
    }
}
