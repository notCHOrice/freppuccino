using Rhino.PlugIns;

namespace Freppucino;

public class FreppucinoPlugin : PlugIn
{
    public FreppucinoPlugin() => Instance = this;

    public static FreppucinoPlugin? Instance { get; private set; }
}
