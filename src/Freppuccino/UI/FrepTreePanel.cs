using System.Globalization;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Rhino;

namespace Freppuccino.UI;

/// Lists the doc's CSG nodes. Blend column is editable (undoable). Row click while an F-CSG command waits sends the node
/// name to the command line; rows selected when FUnion/FIntersect starts act as preselection.
[Guid("6d1e4b8a-2f73-4c95-a0e8-3b9f7d5c1a24")]
public class FrepTreePanel : Panel
{
    public const string Caption = "F-rep Tree";

    static readonly Dictionary<uint, FrepTreePanel> panels = [];

    readonly uint docSerial;
    readonly GridView grid;

    sealed class Row
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Op { get; init; } = "";
        public string K { get; set; } = "";
        public string Operands { get; init; } = "";
    }

    public FrepTreePanel(uint documentSerialNumber)
    {
        docSerial = documentSerialNumber;
        panels[docSerial] = this;

        grid = new GridView { AllowMultipleSelection = true, ShowHeader = true };
        grid.Columns.Add(Column("Node", r => r.Name));
        grid.Columns.Add(Column("Op", r => r.Op));
        grid.Columns.Add(new GridColumn
        {
            HeaderText = "Blend",
            Editable = true,
            DataCell = new TextBoxCell { Binding = Binding.Property<Row, string>(r => r.K) },
        });
        grid.Columns.Add(Column("Operands", r => r.Operands));
        grid.CellEdited += OnCellEdited;
        grid.CellClick += OnCellClick;

        var delete = new Button { Text = "Delete" };
        delete.Click += (_, _) => DeleteSelected();

        Content = new TableLayout
        {
            Padding = 6,
            Spacing = new Size(4, 4),
            Rows =
            {
                new TableRow(grid) { ScaleHeight = true },
                new TableLayout(new TableRow(null, delete)),
            },
        };

        FrepTrees.Changed += OnTreeChanged;
        Refresh();
    }

    static GridColumn Column(string header, Func<Row, string> get) => new()
    {
        HeaderText = header,
        DataCell = new TextBoxCell { Binding = Binding.Delegate(get) },
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FrepTrees.Changed -= OnTreeChanged;
            if (panels.GetValueOrDefault(docSerial) == this) panels.Remove(docSerial);
        }
        base.Dispose(disposing);
    }

    RhinoDoc? Doc => RhinoDoc.FromRuntimeSerialNumber(docSerial);

    /// Node ids selected in the doc's panel; clears the selection (consumed like Rhino preselection).
    public static IReadOnlyList<int> TakeSelectedNodes(RhinoDoc doc)
    {
        if (!panels.TryGetValue(doc.RuntimeSerialNumber, out var p)) return [];
        var ids = p.grid.SelectedItems.OfType<Row>().Select(r => r.Id).ToList();
        p.grid.UnselectAll();
        return ids;
    }

    void OnTreeChanged(RhinoDoc doc)
    {
        if (doc.RuntimeSerialNumber == docSerial) Refresh();
    }

    void Refresh()
    {
        var doc = Doc;
        var tree = doc == null ? FrepTree.Empty : FrepTrees.Get(doc);
        var selected = grid.SelectedItems.OfType<Row>().Select(r => r.Id).ToHashSet();
        var rows = tree.Nodes.Select(n => new Row
        {
            Id = n.Id,
            Name = n.Name,
            Op = n.Op.ToString(),
            K = n.K.ToString("0.####", CultureInfo.InvariantCulture),
            Operands = Describe(doc, n),
        }).ToList();
        grid.DataStore = rows;
        grid.SelectedRows = rows.Select((r, i) => (r, i)).Where(x => selected.Contains(x.r.Id)).Select(x => x.i);
    }

    static string Describe(RhinoDoc? doc, FrepNode n)
    {
        var names = n.Operands.Select(o => Describe(doc, o)).ToList();
        return n.Op == FrepOp.Difference && names.Count > 0
            ? names[0] + " − " + string.Join(", ", names.Skip(1))
            : string.Join(", ", names);
    }

    static string Describe(RhinoDoc? doc, FrepOperand o)
    {
        if (o.IsNode) return FrepTree.NodeName(o.NodeId);
        var shortId = o.ObjectId.ToString("N")[..4];
        var obj = doc?.Objects.FindId(o.ObjectId);
        if (obj == null || obj.IsDeleted) return $"(deleted {shortId})";
        var label = string.IsNullOrEmpty(obj.Attributes.Name)
            ? $"{SdfUserText.ReadType(obj.Attributes) ?? "?"}·{shortId}"
            : obj.Attributes.Name;
        return obj.Visible ? label : label + " (hidden)";
    }

    void OnCellClick(object? sender, GridCellMouseEventArgs e)
    {
        if (FCsgCommand.Picking && e.Item is Row r)
            RhinoApp.SendKeystrokes(r.Name, true);
    }

    void OnCellEdited(object? sender, GridViewCellEventArgs e)
    {
        if (e.Item is not Row r) return;
        // Defer: Set() fires Changed, which replaces the DataStore; don't do that inside the grid's edit callback.
        Application.Instance.AsyncInvoke(() =>
        {
            var doc = Doc;
            if (doc == null) return;
            var tree = FrepTrees.Get(doc);
            var node = tree.Find(r.Id);
            if (node != null && double.TryParse(r.K, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k >= 0 && k != node.K)
                FrepTrees.Set(doc, tree.WithK(r.Id, k), $"Blend {node.Name}");
            else
                Refresh(); // invalid/unchanged: revert text
        });
    }

    /// Deletes selected nodes that nothing else references (selected referencers count as going away too).
    void DeleteSelected()
    {
        var doc = Doc;
        if (doc == null) return;
        var tree = FrepTrees.Get(doc);
        var pending = grid.SelectedItems.OfType<Row>().Select(r => r.Id).ToHashSet();
        bool removed;
        do
        {
            removed = false;
            foreach (var id in pending.ToList())
            {
                if (tree.IsReferenced(FrepOperand.Node(id))) continue;
                tree = tree.Remove(id);
                pending.Remove(id);
                removed = true;
            }
        } while (removed);

        if (pending.Count > 0)
            RhinoApp.WriteLine($"F-rep Tree: not deleted (still used by other nodes): {string.Join(", ", pending.Select(FrepTree.NodeName))}");
        FrepTrees.Set(doc, tree, "Delete F-rep node");
    }
}
