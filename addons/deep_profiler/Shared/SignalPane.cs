using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class SignalPane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;
    public Action<ulong> Navigate;

    private Tree tree;
    private LineEdit filter;
    private Label summary;
    private CheckButton groupBySource;
    private CheckButton userOnly;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color LinkColor = new Color(0.55f, 0.80f, 1f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button crawl = new Button { Text = "Collect", TooltipText = "Walk every reachable object and list its signal connections" };
        crawl.Pressed += () => Source?.RequestSignals();
        bar.AddChild(crawl);

        groupBySource = new CheckButton { Text = "Group", TooltipText = "Group connections by emitter" };
        groupBySource.Toggled += _ => Refresh();
        bar.AddChild(groupBySource);

        userOnly = new CheckButton { Text = "User only", ButtonPressed = true, TooltipText = "Hide connections handled inside the engine" };
        userOnly.Toggled += _ => Refresh();
        bar.AddChild(userOnly);

        filter = new LineEdit { PlaceholderText = "filter signal, emitter or method", CustomMinimumSize = new Vector2(200, 0) };
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
        tree.SetColumnTitle(0, "Emitter");
        tree.SetColumnTitle(1, "Signal");
        tree.SetColumnTitle(2, "Receiver");
        tree.SetColumnTitle(3, "Method");
        tree.SetColumnTitle(4, "Flags");
        tree.SetColumnExpandRatio(0, 3);
        tree.SetColumnExpandRatio(1, 3);
        tree.SetColumnExpandRatio(2, 3);
        tree.SetColumnExpandRatio(3, 3);
        tree.SetColumnExpandRatio(4, 1);
        tree.ItemSelected += OnSelected;
        tree.ItemActivated += OnActivated;
        AddChild(tree);
    }

    public void Refresh()
    {
        if (Data == null || tree == null)
            return;
        tree.Clear();
        GDArray rows = Data.Signals;
        if (rows.Count == 0)
        {
            summary.Text = "press Collect to walk the connection graph";
            return;
        }
        string needle = filter.Text.Trim();
        TreeItem root = tree.CreateItem();
        Dictionary<ulong, TreeItem> groups = new Dictionary<ulong, TreeItem>(64);
        int shown = 0;
        foreach (Variant entry in rows)
        {
            GDDict row = entry.AsGodotDictionary();
            string from = row["from_name"].AsString();
            string signal = row["signal"].AsString();
            string to = row["to_name"].AsString();
            string method = row["method"].AsString();
            if (userOnly.ButtonPressed && row.TryGetValue("internal", out Variant internalFlag) && internalFlag.AsBool())
                continue;
            if (needle.Length > 0
                && !from.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !signal.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !to.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !method.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            TreeItem parent = root;
            if (groupBySource.ButtonPressed)
            {
                ulong fromId = row["from"].AsUInt64();
                if (!groups.TryGetValue(fromId, out parent))
                {
                    parent = tree.CreateItem(root);
                    parent.SetText(0, from);
                    parent.SetText(1, row.TryGetValue("from_class", out Variant fromClass) ? fromClass.AsString() : string.Empty);
                    parent.SetCustomColor(0, new Color(0.98f, 0.82f, 0.45f));
                    parent.SetMetadata(0, fromId);
                    groups[fromId] = parent;
                }
            }

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, from);
            item.SetText(1, signal);
            item.SetText(2, to);
            item.SetText(3, string.IsNullOrEmpty(method) ? string.Empty : method + "()");
            item.SetText(4, FlagText(row));
            item.SetCustomColor(0, LinkColor);
            item.SetCustomColor(2, LinkColor);
            item.SetCustomColor(4, DimColor);
            item.SetMetadata(0, row["to"].AsUInt64());
            item.SetTooltipText(0, "emitter instance " + row["from"].AsUInt64());
            shown++;
        }
        summary.Text = shown + " of " + rows.Count + " connections   collected in " + Fmt.Ms(Data.SignalMs);
    }

    private static string FlagText(GDDict row)
    {
        int flags = row.TryGetValue("flags", out Variant value) ? value.AsInt32() : 0;
        List<string> parts = new List<string>(3);
        if ((flags & (int)GodotObject.ConnectFlags.Deferred) != 0) parts.Add("deferred");
        if ((flags & (int)GodotObject.ConnectFlags.OneShot) != 0) parts.Add("one shot");
        if ((flags & (int)GodotObject.ConnectFlags.Persist) != 0) parts.Add("persist");
        return string.Join(" ", parts);
    }

    private void OnSelected()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Int && meta.AsUInt64() != 0)
            Navigate?.Invoke(meta.AsUInt64());
    }

    private void OnActivated()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Int && meta.AsUInt64() != 0)
            Source?.RequestObject(meta.AsUInt64());
    }
}
