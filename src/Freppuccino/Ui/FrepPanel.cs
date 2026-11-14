using System.Runtime.InteropServices;
using Eto.Forms;

namespace Freppuccino.Ui;

/// <summary>
/// Freppuccino side panel: hosts the React app from src/ui in a WebView2 (see <see cref="WebViewHost"/>).
/// Registered in FreppuccinoPlugin.OnLoad; open with the FrepPanel command or Rhino's panel menu.
/// </summary>
[Guid("3f6c1b52-8e4a-4d7f-b0a9-6d2e91c47a38")]
public class FrepPanel : Panel
{
    public const string Caption = "Freppuccino";

    readonly WebViewHost? web;
    readonly UiController? controller;

    public FrepPanel()
    {
        Padding = 0;
        if (!OperatingSystem.IsWindows())
        {
            // macOS: Eto's WebView is WKWebView there; a WKScriptMessageHandler bridge is still TODO.
            Content = new Label { Text = "The Freppuccino panel is Windows-only for now.", Wrap = WrapMode.Word };
            return;
        }
        web = new WebViewHost();
        controller = new UiController(web);
        Content = web.Control;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            controller?.Dispose();
            web?.Dispose();
        }
        base.Dispose(disposing);
    }
}
