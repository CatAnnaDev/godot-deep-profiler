using System;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ManagedPane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;

    private Tree types;
    private Tree stats;
    private Label summary;
    private LineEdit filter;
    private int sortColumn = 1;
    private bool sortAscending;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color RateColor = new Color(0.85f, 0.65f, 0.95f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button reset = new Button { Text = "Restart sampling", TooltipText = "Clear the accumulated allocation totals" };
        reset.Pressed += () => Source?.ResetHeap();
        bar.AddChild(reset);

        Button collect = new Button { Text = "GC", TooltipText = "Force a full managed collection" };
        collect.Pressed += () => Source?.ForceCollect();
        bar.AddChild(collect);

        filter = new LineEdit { PlaceholderText = "filter type", CustomMinimumSize = new Vector2(150, 0) };
        filter.TextChanged += _ => Refresh();
        bar.AddChild(filter);

        summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        HSplitContainer split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        types = new Tree
        {
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        types.SetColumnTitle(0, "Type");
        types.SetColumnTitle(1, "Allocated");
        types.SetColumnTitle(2, "Share");
        types.SetColumnTitle(3, "Per second");
        types.SetColumnExpandRatio(0, 4);
        types.SetColumnCustomMinimumWidth(0, 150);
        for (int i = 1; i <= 3; i++)
        {
            types.SetColumnExpandRatio(i, 1);
            types.SetColumnCustomMinimumWidth(i, 76);
        }
        types.ColumnTitleClicked += OnColumnClicked;
        split.AddChild(types);

        stats = new Tree
        {
            Columns = 2,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(340, 0),
        };
        stats.SetColumnTitle(0, "Collector");
        stats.SetColumnTitle(1, "Value");
        stats.SetColumnExpandRatio(0, 3);
        stats.SetColumnExpandRatio(1, 2);
        stats.SetColumnCustomMinimumWidth(1, 110);
        split.AddChild(stats);
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
        if (Data == null || types == null)
            return;
        if (!IsVisibleInTree())
            return;
        types.Clear();
        stats.Clear();
        GDDict heap = Data.Heap;
        if (heap == null || heap.Count == 0)
        {
            summary.Text = "waiting for the first allocation window";
            return;
        }

        bool available = heap["available"].AsBool();
        double seconds = heap["seconds"].AsDouble();
        long total = heap["total"].AsInt64();
        GDArray rows = heap["types"].AsGodotArray();

        TreeItem root = types.CreateItem();
        string needle = filter.Text.Trim();
        int shown = 0;
        foreach (Variant entry in rows)
        {
            GDDict row = entry.AsGodotDictionary();
            string name = row["n"].AsString();
            if (needle.Length > 0 && !name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            long bytes = row["bytes"].AsInt64();
            TreeItem item = types.CreateItem(root);
            item.SetText(0, name);
            item.SetText(1, Fmt.Bytes(bytes));
            item.SetText(2, total > 0 ? Fmt.Percent(bytes / (double)total) : string.Empty);
            item.SetText(3, Fmt.Bytes(row["rate"].AsDouble()) + "/s");
            for (int i = 1; i <= 3; i++)
                item.SetTextAlignment(i, HorizontalAlignment.Right);
            item.SetCustomColor(2, DimColor);
            item.SetCustomColor(3, RateColor);
            item.SetTooltipText(0, row["full"].AsString());
            shown++;
        }

        summary.Text = available
            ? shown + " of " + heap["distinct"].AsInt32() + " types   " + Fmt.Bytes(total) + " sampled over "
              + seconds.ToString("0") + " s   " + Fmt.Bytes(total / Math.Max(0.001, seconds)) + "/s"
            : "allocation sampling is not reporting on this runtime";

        TreeItem statsRoot = stats.CreateItem();
        if (heap.TryGetValue("generations", out Variant generations))
        {
            foreach (Variant entry in generations.AsGodotArray())
            {
                GDDict row = entry.AsGodotDictionary();
                TreeItem generation = Row(statsRoot, row["n"].AsString(), Fmt.Bytes(row["size"].AsInt64()));
                long fragmented = row["frag"].AsInt64();
                if (fragmented > 0)
                    Row(generation, "unused", Fmt.Bytes(fragmented));
            }
        }
        Row(statsRoot, "heap", Fmt.Bytes(heap["heap"].AsInt64()));
        Row(statsRoot, "committed", Fmt.Bytes(heap["committed"].AsInt64()));
        Row(statsRoot, "fragmented", Fmt.Bytes(heap["frag"].AsInt64()));
        Row(statsRoot, "last pause", Fmt.Ms(heap["pause"].AsDouble()));
        Row(statsRoot, "last collection", "gen " + heap["gen"].AsInt32()
            + (heap["compacted"].AsBool() ? " compacted" : string.Empty)
            + (heap["concurrent"].AsBool() ? " concurrent" : string.Empty));
        Row(statsRoot, "time in gc", heap["pause_pct"].AsDouble().ToString("0.00") + "%");
        Row(statsRoot, "collections", Fmt.Count(heap["index"].AsInt64()));
        Row(statsRoot, "finalizers pending", Fmt.Count(heap["finalization"].AsInt64()));
        Row(statsRoot, "pinned objects", Fmt.Count(heap["pinned"].AsInt64()));
        Row(statsRoot, "process memory load", Fmt.Bytes(heap["load"].AsInt64()) + " of " + Fmt.Bytes(heap["threshold"].AsInt64()));
    }

    private TreeItem Row(TreeItem parent, string name, string value)
    {
        TreeItem item = stats.CreateItem(parent);
        item.SetText(0, name);
        item.SetText(1, value);
        item.SetTextAlignment(1, HorizontalAlignment.Right);
        item.SetCustomColor(0, DimColor);
        return item;
    }
}
