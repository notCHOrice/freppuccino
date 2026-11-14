using Freppuccino.Display;
using Freppuccino.Gpu;
using Freppuccino.Sdf;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Freppuccino.Ui;
using Rhino.PlugIns;
using Rhino.UI;

namespace Freppuccino;

public class FreppuccinoPlugin : PlugIn
{
    public FreppuccinoPlugin() => Instance = this;

    public static FreppuccinoPlugin? Instance { get; private set; }
    public static ShaderConduit? Conduit { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        NativeLibs.Install();
        FreppuccinoDisplayMode.Ensure();
        DevAutostart.Install();
        SdfTransformWatcher.Install();
        FrepScene.Install();
        Conduit = new ShaderConduit { Enabled = true };
        Panels.RegisterPanel(this, typeof(FrepPanel), FrepPanel.Caption, GetType().Assembly,
            "Freppuccino.EmbeddedResources.plugin-utility.ico", PanelType.System);
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        SdfTransformWatcher.Uninstall();
        if (Conduit != null) Conduit.Enabled = false;
        GpuBackends.DisposeAll();
    }
}
