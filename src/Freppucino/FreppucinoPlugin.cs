using Freppucino.Display;
using Freppucino.Gpu;
using Freppucino.Shaders;
using Rhino.PlugIns;

namespace Freppucino;

public class FreppucinoPlugin : PlugIn
{
    public FreppucinoPlugin() => Instance = this;

    public static FreppucinoPlugin? Instance { get; private set; }
    public static ShaderConduit? Conduit { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        NativeLibs.Install();
        FreppucinoDisplayMode.Ensure();
        Conduit = new ShaderConduit { Enabled = true };
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        if (Conduit != null) Conduit.Enabled = false;
        GpuBackends.DisposeAll();
    }
}
