using Eto.Forms;
using Microsoft.Web.WebView2.Core;
using Rhino;

namespace Freppuccino.Ui;

/// <summary>
/// Windows: Rhino's Eto WebView is WebView2, so we reach <see cref="CoreWebView2"/> through the native control
/// (ControlObject is the WPF WebView2) by reflection. Compiles against Microsoft.Web.WebView2.Core only (no WPF),
/// so the project still builds on macOS; this type is never touched there.
/// Page API: window.chrome.webview.postMessage(object) / addEventListener("message").
/// </summary>
sealed class WebView2Transport : IWebTransport
{
    const string VirtualHost = "freppuccino.ui";

    readonly WebView control;
    readonly NavigationFilter allowNavigation;
    readonly Action<string> openNewWindow;
    CoreWebView2? core;
    Uri? pending;
    string? uiFolder;

    public event Action<string, string?>? Received;

    public WebView2Transport(WebView control, NavigationFilter allowNavigation, Action<string> openNewWindow)
    {
        this.control = control;
        this.allowNavigation = allowNavigation;
        this.openNewWindow = openNewWindow;
    }

    public Uri PageUri(string uiFolder)
    {
        this.uiFolder = uiFolder;
        return new Uri($"https://{VirtualHost}/index.html");
    }

    public void Load(Uri uri)
    {
        pending = uri;
        if (core != null) Navigate(core);
        else HookCore();
    }

    public void Post(string json)
    {
        if (core == null) return;
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView torn down / navigating; the page re-syncs on its next "ready".
        }
    }

    void HookCore()
    {
        // The WPF control creates CoreWebView2 asynchronously once it is in the visual tree.
        var native = control.ControlObject;
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
        c.Settings.AreDevToolsEnabled = WebViewHost.DevTools; // F12 / right-click → Inspect
        c.Settings.AreDefaultContextMenusEnabled = WebViewHost.DevTools;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsWebMessageEnabled = true;

        c.WebMessageReceived += OnWebMessageReceived;
        c.NavigationStarting += OnNavigationStarting;
        c.NewWindowRequested += OnNewWindowRequested;
        Navigate(c);
    }

    void Navigate(CoreWebView2 c)
    {
        if (pending == null) return;
        if (pending.Host == VirtualHost && uiFolder != null)
            c.SetVirtualHostNameToFolderMapping(VirtualHost, uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        c.Navigate(pending.AbsoluteUri);
        pending = null;
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!allowNavigation(e.Uri)) e.Cancel = true;
    }

    void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        openNewWindow(e.Uri);
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
        Received?.Invoke(e.WebMessageAsJson, e.Source);

    public void Dispose()
    {
        if (core != null)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core = null;
        }
        Received = null;
    }
}
