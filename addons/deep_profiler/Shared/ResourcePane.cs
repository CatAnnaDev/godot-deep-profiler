using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ResourcePane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;
    public Action<ulong> Navigate;

    private Tree tree;
    private LineEdit filter;
    private Label summary;
    private CheckButton groupByClass;
    private int sortColumn = 3;
    private bool sortAscending;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color HeavyColor = new Color(0.95f, 0.6f, 0.45f);
    private static readonly Color OrphanColor = new Color(0.95f, 0.8f, 0.4f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button crawl = new Button { Text = "Crawl now" };
        crawl.Pressed += () => Source?.RequestCensus();
        bar.AddChild(crawl);

        groupByClass = new CheckButton { Text = "Group", TooltipText = "Group resources by class" };
        groupByClass.Toggled += _ => Refresh();
        bar.AddChild(groupByClass);

        filter = new LineEdit { PlaceholderText = "filter path or class", CustomMinimumSize = new Vector2(160, 0) };
        filter.TextChanged += _ => Refresh();
        bar.AddChild(filter);

        summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        tree = new Tree
        {
            Columns = 5,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        tree.SetColumnTitle(0, "Resource");
        tree.SetColumnTitle(1, "Class");
        tree.SetColumnTitle(2, "Refs");
        tree.SetColumnTitle(3, "Size");
        tree.SetColumnTitle(4, "Held by");
        tree.SetColumnExpandRatio(0, 5);
        tree.SetColumnExpandRatio(1, 2);
        tree.SetColumnExpandRatio(2, 1);
        tree.SetColumnExpandRatio(3, 1);
        tree.SetColumnExpandRatio(4, 2);
        tree.SetColumnCustomMinimumWidth(2, 50);
        tree.SetColumnCustomMinimumWidth(3, 70);
        tree.ColumnTitleClicked += OnColumnClicked;
        tree.ItemActivated += OnActivated;
        tree.ItemSelected += OnSelected;
        AddChild(tree);
    }

    private void OnColumnClicked(long column, long button)
    {
        if (sortColumn == (int)column)
            sortAscending = !sortAscending;
        else
            sortAscending = false;
        sortColumn = (int)column;
        Refresh();
    }

    public void Refresh()
    {
        if (Data == null || tree == null)
            return;
        tree.Clear();
        if (!Data.Census.TryGetValue("resources", out Variant resourcesValue))
        {
            summary.Text = "no crawl yet";
            return;
        }

        string needle = filter.Text.Trim();
        List<GDDict> rows = new List<GDDict>(256);
        long totalBytes = 0;
        foreach (Variant entry in resourcesValue.AsGodotArray())
        {
            GDDict row = entry.AsGodotDictionary();
            string label = Label(row);
            if (needle.Length > 0
                && !label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row["class"].AsString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(row);
            totalBytes += row["bytes"].AsInt64();
        }
        rows.Sort(Compare);

        TreeItem root = tree.CreateItem();
        Dictionary<string, TreeItem> groups = new Dictionary<string, TreeItem>(32, StringComparer.Ordinal);
        foreach (GDDict row in rows)
        {
            TreeItem parent = root;
            if (groupByClass.ButtonPressed)
            {
                string className = row["class"].AsString();
                if (!groups.TryGetValue(className, out parent))
                {
                    parent = tree.CreateItem(root);
                    parent.SetText(0, className);
                    parent.SetCustomColor(0, new Color(0.98f, 0.82f, 0.45f));
                    groups[className] = parent;
                }
            }

            GDArray holders = row["holders"].AsGodotArray();
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, Label(row));
            item.SetText(1, row["class"].AsString());
            item.SetText(2, row["refs"].AsInt32().ToString());
            item.SetText(3, Fmt.Bytes(row["bytes"].AsInt64()));
            item.SetText(4, holders.Count.ToString());
            for (int i = 2; i <= 4; i++)
                item.SetTextAlignment(i, HorizontalAlignment.Right);
            item.SetCustomColor(1, DimColor);
            if (row["bytes"].AsInt64() > 4 * 1024 * 1024)
                item.SetCustomColor(3, HeavyColor);
            if (holders.Count == 0)
                item.SetCustomColor(4, OrphanColor);
            item.SetMetadata(0, row["id"].AsUInt64());
            string path = row["path"].AsString();
            item.SetTooltipText(0, string.IsNullOrEmpty(path) ? "instance " + row["id"].AsUInt64() : path);
            item.Collapsed = true;

            foreach (Variant holderEntry in holders)
            {
                GDDict holder = holderEntry.AsGodotDictionary();
                TreeItem child = tree.CreateItem(item);
                child.SetText(0, holder["name"].AsString());
                child.SetText(1, "." + holder["prop"].AsString());
                child.SetCustomColor(0, new Color(0.55f, 0.80f, 1f));
                child.SetCustomColor(1, DimColor);
                child.SetMetadata(0, holder["id"].AsUInt64());
            }
        }

        int shown = rows.Count;
        summary.Text = shown + " resources   " + Fmt.Bytes(totalBytes)
                       + "   unique " + Data.Census["unique_resources"].AsInt32();
    }

    private static string Label(GDDict row)
    {
        string path = row["path"].AsString();
        if (!string.IsNullOrEmpty(path))
            return path;
        string name = row["name"].AsString();
        return string.IsNullOrEmpty(name) ? "<unnamed " + row["class"].AsString() + ">" : name + " (built in)";
    }

    private int Compare(GDDict a, GDDict b)
    {
        int result = sortColumn switch
        {
            0 => string.CompareOrdinal(Label(a), Label(b)),
            1 => string.CompareOrdinal(a["class"].AsString(), b["class"].AsString()),
            2 => a["refs"].AsInt32().CompareTo(b["refs"].AsInt32()),
            4 => a["holders"].AsGodotArray().Count.CompareTo(b["holders"].AsGodotArray().Count),
            _ => a["bytes"].AsInt64().CompareTo(b["bytes"].AsInt64()),
        };
        return sortAscending ? result : -result;
    }

    private void OnSelected()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Int)
            Navigate?.Invoke(meta.AsUInt64());
    }

    private void OnActivated()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Int)
            Source?.RequestObject(meta.AsUInt64());
    }
}
