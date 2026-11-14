using Freppuccino.Display;
using Freppuccino.Gpu;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Rhino.PlugIns;

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
        SphereScene.Install();
        Conduit = new ShaderConduit { Enabled = true };
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        if (Conduit != null) Conduit.Enabled = false;
        GpuBackends.DisposeAll();
    }
}
