using System.Runtime.InteropServices;
using Freppuccino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace Freppuccino.Commands;

/// Opens the F-rep Tree panel.
[Guid("e81f3a5c-6b29-4d07-9c4e-2a7d1f8b3e95")]
public class FTreeCommand : Command
{
    public override string EnglishName => "FTree";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(FrepTreePanel).GUID);
        return Result.Success;
    }
}
