using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ScopePane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;
    public bool Compact;

    private FlameChart flame;
    private Tree tree;
    private Tree counters;
    private OptionButton sourceSelect;
    private OptionButton scaleSelect;
    private Label summary;
    private CheckButton flatMode;
    private CheckButton objectToggle;
    private int shareColumn = 6;
    private readonly Dictionary<string, bool> collapsed = new Dictionary<string, bool>(64, StringComparer.Ordinal);
    private readonly List<int> childScratch = new List<int>(32);
    private bool perFrame = true;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color AllocColor = new Color(0.85f, 0.65f, 0.95f);
    private static readonly Color ObjectColor = new Color(0.95f, 0.72f, 0.45f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        sourceSelect = new OptionButton { TooltipText = "Which capture to display" };
        sourceSelect.AddItem("Window average", 0);
        sourceSelect.AddItem("Worst frame", 1);
        sourceSelect.AddItem("Captured frame", 2);
        sourceSelect.ItemSelected += _ => Refresh();
        bar.AddChild(sourceSelect);

        scaleSelect = new OptionButton { TooltipText = "Per frame divides the window totals by the number of frames" };
        scaleSelect.AddItem("Per frame", 0);
        scaleSelect.AddItem("Per window", 1);
        scaleSelect.ItemSelected += index => { perFrame = index == 0; Refresh(); };
        bar.AddChild(scaleSelect);

        flatMode = new CheckButton { Text = "Flat", TooltipText = "Show a flat list sorted by self time instead of the call tree" };
        flatMode.Toggled += _ => Refresh();
        bar.AddChild(flatMode);

        objectToggle = new CheckButton { Text = "Objects", ButtonPressed = true, TooltipText = "Count the engine objects created inside each scope" };
        objectToggle.Toggled += value => Source?.SetTrackObjects(value);
        bar.AddChild(objectToggle);

        Button capture = new Button { Text = "Capture frame", TooltipText = "Ask the game for the full scope tree of the next frame" };
        capture.Pressed += () => Source?.RequestFrameCapture();
        bar.AddChild(capture);

        summary = new Label { Text = string.Empty, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        flame = new FlameChart { CustomMinimumSize = new Vector2(0, Compact ? 96 : 150), SizeFlagsVertical = SizeFlags.Fill };
        AddChild(flame);

        HSplitContainer split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        shareColumn = Compact ? -1 : 6;
        tree = new Tree
        {
            Columns = Compact ? 6 : 7,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        tree.SetColumnTitle(0, "Scope");
        tree.SetColumnTitle(1, "Total");
        tree.SetColumnTitle(2, "Self");
        tree.SetColumnTitle(3, "Calls");
        tree.SetColumnTitle(4, "Alloc");
        tree.SetColumnTitle(5, "Objects");
        if (shareColumn >= 0)
            tree.SetColumnTitle(shareColumn, "Share");
        tree.SetColumnExpandRatio(0, 6);
        tree.SetColumnCustomMinimumWidth(0, Compact ? 104 : 150);
        for (int i = 1; i < tree.Columns; i++)
        {
            tree.SetColumnExpandRatio(i, 1);
            tree.SetColumnCustomMinimumWidth(i, Compact ? (i <= 2 ? 58 : 46) : 62);
        }
        tree.ItemCollapsed += OnCollapsed;
        split.AddChild(tree);

        counters = new Tree
        {
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(270, 0),
        };
        counters.SetColumnTitle(0, "Counter");
        counters.SetColumnTitle(1, "Last");
        counters.SetColumnTitle(2, "Min");
        counters.SetColumnTitle(3, "Max");
        counters.SetColumnExpandRatio(0, 3);
        counters.SetColumnCustomMinimumWidth(0, 90);
        for (int i = 1; i <= 3; i++)
            counters.SetColumnExpandRatio(i, 1);
        split.AddChild(counters);
    }

    private ScopeView Current()
    {
        if (Data == null)
            return null;
        return sourceSelect.Selected switch
        {
            1 => Data.Worst,
            2 => Data.Captured,
            _ => Data.Window,
        };
    }

    public void Refresh()
    {
        ScopeView view = Current();
        if (view == null)
            return;
        flame.SetView(view);
        int frames = perFrame ? Math.Max(1, view.Frames) : 1;
        BuildTree(view, frames);
        BuildCounters();
        summary.Text = view.Count <= 1
            ? "no instrumented scopes"
            : view.Count + " scopes over " + view.Frames + " frame" + (view.Frames == 1 ? string.Empty : "s")
              + "   root " + Fmt.Ms(view.RootTotal / Math.Max(1, view.Frames))
              + (view.Overflow > 0 ? "   overflow " + view.Overflow : string.Empty);
    }

    private void BuildTree(ScopeView view, int frames)
    {
        tree.Clear();
        if (view.Count == 0)
            return;
        TreeItem root = tree.CreateItem();
        double rootTotal = Math.Max(0.0001, view.Total[0]);

        if (flatMode.ButtonPressed)
        {
            Dictionary<int, (double Total, double Self, double Alloc, int Calls, int Objects)> merged = new Dictionary<int, (double, double, double, int, int)>(view.Count);
            for (int i = 1; i < view.Count; i++)
            {
                merged.TryGetValue(view.NameId[i], out (double Total, double Self, double Alloc, int Calls, int Objects) entry);
                merged[view.NameId[i]] = (entry.Total + view.Total[i], entry.Self + view.Self[i], entry.Alloc + view.Alloc[i],
                    entry.Calls + view.Calls[i], entry.Objects + view.Objects[i]);
            }
            List<KeyValuePair<int, (double Total, double Self, double Alloc, int Calls, int Objects)>> rows =
                new List<KeyValuePair<int, (double, double, double, int, int)>>(merged);
            rows.Sort((a, b) => b.Value.Self.CompareTo(a.Value.Self));
            foreach (KeyValuePair<int, (double Total, double Self, double Alloc, int Calls, int Objects)> row in rows)
            {
                TreeItem item = tree.CreateItem(root);
                FillRow(item, view.Resolver(row.Key), row.Value.Total, row.Value.Self, row.Value.Calls, row.Value.Alloc,
                    row.Value.Objects, rootTotal, frames);
            }
            return;
        }

        Stack<(int Node, TreeItem Parent)> stack = new Stack<(int, TreeItem)>(64);
        stack.Push((0, root));
        while (stack.Count > 0)
        {
            (int node, TreeItem parent) = stack.Pop();
            TreeItem item = tree.CreateItem(parent);
            string name = view.NameOf(node);
            FillRow(item, name, view.Total[node], view.Self[node], view.Calls[node], view.Alloc[node], view.Objects[node], rootTotal, frames);
            if (collapsed.TryGetValue(name, out bool state))
                item.Collapsed = state;
            childScratch.Clear();
            for (int child = view.FirstChild[node]; child >= 0; child = view.NextSibling[child])
                childScratch.Add(child);
            childScratch.Sort((a, b) => view.Total[b].CompareTo(view.Total[a]));
            for (int i = childScratch.Count - 1; i >= 0; i--)
                stack.Push((childScratch[i], item));
        }
    }

    private void FillRow(TreeItem item, string name, double total, double self, int calls, double alloc, int objects, double rootTotal, int frames)
    {
        item.SetText(0, name);
        item.SetText(1, Fmt.Ms(total / frames));
        item.SetText(2, Fmt.Ms(self / frames));
        item.SetText(3, Fmt.Count(calls / (double)frames));
        item.SetText(4, alloc > 0.0 ? Fmt.Bytes(alloc / frames) : string.Empty);
        item.SetText(5, objects > 0 ? Fmt.Count(objects / (double)frames) : string.Empty);
        if (shareColumn >= 0)
        {
            item.SetText(shareColumn, Fmt.Percent(total / rootTotal));
            item.SetCustomColor(shareColumn, DimColor);
        }
        for (int i = 1; i < tree.Columns; i++)
            item.SetTextAlignment(i, HorizontalAlignment.Right);
        item.SetCustomColor(2, Fmt.HeatColor(self / Math.Max(0.0001, rootTotal) * 4.0));
        item.SetCustomColor(3, DimColor);
        item.SetCustomColor(4, AllocColor);
        item.SetCustomColor(5, objects > 0 ? ObjectColor : DimColor);
    }

    private void OnCollapsed(TreeItem item)
    {
        if (item != null)
            collapsed[item.GetText(0)] = item.Collapsed;
    }

    private void BuildCounters()
    {
        counters.Clear();
        if (Data == null)
            return;
        TreeItem root = counters.CreateItem();
        foreach (Variant entry in Data.Counters)
        {
            GDDict row = entry.AsGodotDictionary();
            TreeItem item = counters.CreateItem(root);
            item.SetText(0, row["n"].AsString());
            item.SetText(1, Fmt.Number(row["last"].AsDouble()));
            item.SetText(2, Fmt.Number(row["min"].AsDouble()));
            item.SetText(3, Fmt.Number(row["max"].AsDouble()));
            for (int i = 1; i <= 3; i++)
            {
                item.SetTextAlignment(i, HorizontalAlignment.Right);
                item.SetCustomColor(i, i == 1 ? new Color(0.9f, 0.92f, 0.96f) : DimColor);
            }
        }
        foreach (ScopeView thread in Data.Threads)
        {
            if (thread.Count == 0)
                continue;
            TreeItem item = counters.CreateItem(root);
            item.SetText(0, "thread " + thread.Thread);
            item.SetText(1, Fmt.Ms(thread.RootTotal));
            item.SetCustomColor(0, new Color(0.65f, 0.85f, 0.75f));
        }
    }
}
