using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class CensusPane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;
    public Action<ulong> Navigate;

    private Tree classes;
    private Tree instances;
    private Label summary;
    private Label instanceHeader;
    private LineEdit filter;
    private int sortColumn = 4;
    private bool sortAscending;
    private string selectedClass = string.Empty;
    private CheckButton autoCrawl;
    private OptionButton autoInterval;
    private CheckButton lightCrawl;
    private Label verdict;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color GrowColor = new Color(0.95f, 0.55f, 0.45f);
    private static readonly Color ShrinkColor = new Color(0.45f, 0.85f, 0.6f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button crawl = new Button { Text = "Crawl now", TooltipText = "Walk the whole live object graph and count everything reachable" };
        crawl.Pressed += () => Source?.RequestCensus();
        bar.AddChild(crawl);

        Button baseline = new Button { Text = "Set baseline", TooltipText = "Remember current counts so the delta column shows growth" };
        baseline.Pressed += () =>
        {
            Data?.SnapshotCensusBaseline();
            Refresh();
        };
        bar.AddChild(baseline);

        Button clearBaseline = new Button { Text = "Clear baseline" };
        clearBaseline.Pressed += () =>
        {
            Data?.CensusBaseline.Clear();
            Refresh();
        };
        bar.AddChild(clearBaseline);

        autoCrawl = new CheckButton { Text = "Auto crawl", TooltipText = "Re-walk the object graph on a timer so the growth columns stay live" };
        autoCrawl.Toggled += value => Source?.SetAutoCrawl(value, IntervalSeconds(), lightCrawl.ButtonPressed);
        bar.AddChild(autoCrawl);

        autoInterval = new OptionButton { TooltipText = "Interval between automatic crawls" };
        autoInterval.AddItem("20 per second", 0);
        autoInterval.AddItem("0.1 s", 1);
        autoInterval.AddItem("0.25 s", 2);
        autoInterval.AddItem("0.5 s", 3);
        autoInterval.AddItem("1 s", 4);
        autoInterval.AddItem("2 s", 5);
        autoInterval.AddItem("5 s", 6);
        autoInterval.Selected = 2;
        autoInterval.ItemSelected += _ => PushAutoCrawl();
        bar.AddChild(autoInterval);

        lightCrawl = new CheckButton
        {
            Text = "Light",
            ButtonPressed = true,
            TooltipText = "Count classes only. Skips memory estimation and the resource table, which makes a fast interval affordable",
        };
        lightCrawl.Toggled += _ => PushAutoCrawl();
        bar.AddChild(lightCrawl);

        filter = new LineEdit { PlaceholderText = "filter class", CustomMinimumSize = new Vector2(140, 0) };
        filter.TextChanged += _ => Refresh();
        bar.AddChild(filter);

        summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        verdict = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        verdict.AddThemeFontSizeOverride("font_size", 11);
        AddChild(verdict);

        HSplitContainer split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        classes = new Tree
        {
            Columns = 6,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        classes.SetColumnTitle(0, "Class");
        classes.SetColumnTitle(1, "Count");
        classes.SetColumnTitle(2, "Since crawl");
        classes.SetColumnTitle(3, "Since base");
        classes.SetColumnTitle(4, "Bytes");
        classes.SetColumnTitle(5, "Average");
        classes.SetColumnExpandRatio(0, 4);
        classes.SetColumnCustomMinimumWidth(0, 108);
        classes.SetColumnExpandRatio(1, 1);
        classes.SetColumnExpandRatio(2, 1);
        classes.SetColumnExpandRatio(3, 1);
        classes.SetColumnExpandRatio(4, 2);
        classes.SetColumnExpandRatio(5, 2);
        classes.SetColumnCustomMinimumWidth(1, 48);
        classes.SetColumnCustomMinimumWidth(2, 54);
        classes.SetColumnCustomMinimumWidth(3, 54);
        classes.SetColumnCustomMinimumWidth(4, 64);
        classes.SetColumnCustomMinimumWidth(5, 64);
        classes.ColumnTitleClicked += OnColumnClicked;
        classes.ItemSelected += OnClassSelected;
        split.AddChild(classes);

        VBoxContainer right = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        split.AddChild(right);

        instanceHeader = new Label { Text = "select a class to list its live instances" };
        instanceHeader.AddThemeFontSizeOverride("font_size", 10);
        instanceHeader.AddThemeColorOverride("font_color", DimColor);
        right.AddChild(instanceHeader);

        instances = new Tree
        {
            Columns = 3,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        instances.SetColumnTitle(0, "Instance");
        instances.SetColumnTitle(1, "Path");
        instances.SetColumnTitle(2, "Retained");
        instances.SetColumnExpandRatio(0, 2);
        instances.SetColumnExpandRatio(1, 3);
        instances.SetColumnExpandRatio(2, 1);
        instances.ItemActivated += OnInstanceActivated;
        instances.ItemSelected += OnInstanceSelected;
        right.AddChild(instances);
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
        if (Data == null || classes == null)
            return;
        if (!IsVisibleInTree())
            return;
        classes.Clear();
        if (!Data.Census.TryGetValue("classes", out Variant classesValue))
        {
            summary.Text = "no crawl yet";
            return;
        }

        string needle = filter.Text.Trim();
        List<GDDict> rows = new List<GDDict>(128);
        foreach (Variant entry in classesValue.AsGodotArray())
        {
            GDDict row = entry.AsGodotDictionary();
            if (needle.Length > 0 && !row["class"].AsString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(row);
        }
        rows.Sort(Compare);

        TreeItem root = classes.CreateItem();
        foreach (GDDict row in rows)
        {
            string className = row["class"].AsString();
            int count = row["count"].AsInt32();
            long bytes = row["bytes"].AsInt64();
            int crawlDelta = Data.CrawlDelta(className, count);
            int baseDelta = Data.BaselineDelta(className, count);
            TreeItem item = classes.CreateItem(root);
            item.SetText(0, className);
            item.SetText(1, Fmt.Count(count));
            item.SetText(2, DeltaText(crawlDelta));
            item.SetText(3, DeltaText(baseDelta));
            item.SetText(4, (row["est"].AsBool() ? "~" : string.Empty) + Fmt.Bytes(bytes));
            item.SetText(5, Fmt.Bytes(count > 0 ? bytes / (double)count : 0));
            for (int i = 1; i <= 5; i++)
                item.SetTextAlignment(i, HorizontalAlignment.Right);
            Tint(item, 2, crawlDelta);
            Tint(item, 3, baseDelta);
            item.SetCustomColor(5, DimColor);
            item.SetMetadata(0, className);
        }

        bool light = Data.Census.TryGetValue("light", out Variant lightValue) && lightValue.AsBool();
        UpdateVerdict();
        summary.Text = (light ? "light crawl   " : string.Empty) + "objects " + Fmt.Count(Data.Census["objects"].AsInt32())
                       + "   nodes " + Fmt.Count(Data.Census["nodes"].AsInt32())
                       + "   estimated " + Fmt.Bytes(Data.Census["bytes"].AsInt64())
                       + "   crawl " + Fmt.Ms(Data.Census["ms"].AsDouble())
                       + (Data.Census["partial"].AsBool() ? "   (budget reached)" : string.Empty);
    }

    private int Compare(GDDict a, GDDict b)
    {
        int result = sortColumn switch
        {
            0 => string.CompareOrdinal(a["class"].AsString(), b["class"].AsString()),
            1 => a["count"].AsInt32().CompareTo(b["count"].AsInt32()),
            2 => Data.CrawlDelta(a["class"].AsString(), a["count"].AsInt32()).CompareTo(Data.CrawlDelta(b["class"].AsString(), b["count"].AsInt32())),
            3 => Data.BaselineDelta(a["class"].AsString(), a["count"].AsInt32()).CompareTo(Data.BaselineDelta(b["class"].AsString(), b["count"].AsInt32())),
            5 => Average(a).CompareTo(Average(b)),
            _ => a["bytes"].AsInt64().CompareTo(b["bytes"].AsInt64()),
        };
        return sortAscending ? result : -result;
    }

    private void UpdateVerdict()
    {
        FrameRing ring = Data.Frames;
        if (ring.Count < 60)
        {
            verdict.Text = string.Empty;
            return;
        }
        long last = ring.Total - 1;
        long from = Math.Max(ring.Oldest, last - 600);
        float now = ring.At(last, Protocol.FObjects);
        float before = ring.At(from, Protocol.FObjects);
        float seconds = Math.Max(1f, (last - from) / Math.Max(1f, ring.Average(Protocol.FFps, last - 60, last + 1)));
        float growth = now - before;
        float created = ring.Average(Protocol.FObjectsAdded, last - 120, last + 1);
        int reachable = Data.Census["objects"].AsInt32();
        float unreachable = Math.Max(0f, now - reachable);

        if (growth > Math.Max(64f, now * 0.02f))
        {
            verdict.AddThemeColorOverride("font_color", GrowColor);
            verdict.Text = "Growing: " + Fmt.Count(growth) + " more objects in " + seconds.ToString("0") + " s ("
                           + Fmt.Count(growth / seconds) + " per second). "
                           + (unreachable > growth * 0.5f
                               ? "They are not reachable from the scene tree, so the class table cannot name them. Use Measure cost on the suspect nodes to bisect, and check the Input tab."
                               : "Sort by Since crawl, the class at the top is the one accumulating.");
            return;
        }
        verdict.AddThemeColorOverride("font_color", ShrinkColor);
        verdict.Text = "Stable: " + Fmt.Number(Math.Round(created, 1)) + " objects created per frame and released again, "
                       + Fmt.Count(unreachable) + " alive outside the scene tree. This is churn, not a leak: it costs allocation time, not memory.";
    }

    private void PushAutoCrawl()
    {
        if (autoCrawl.ButtonPressed)
            Source?.SetAutoCrawl(true, IntervalSeconds(), lightCrawl.ButtonPressed);
    }

    private double IntervalSeconds()
    {
        return autoInterval.Selected switch
        {
            0 => 0.05,
            1 => 0.1,
            2 => 0.25,
            3 => 0.5,
            4 => 1.0,
            5 => 2.0,
            _ => 5.0,
        };
    }

    private static string DeltaText(int delta)
    {
        return delta == 0 ? string.Empty : (delta > 0 ? "+" : string.Empty) + delta;
    }

    private static void Tint(TreeItem item, int column, int delta)
    {
        if (delta > 0)
            item.SetCustomColor(column, GrowColor);
        else if (delta < 0)
            item.SetCustomColor(column, ShrinkColor);
    }

    private static double Average(GDDict row)
    {
        int count = row["count"].AsInt32();
        return count > 0 ? row["bytes"].AsInt64() / (double)count : 0.0;
    }

    private void OnClassSelected()
    {
        TreeItem item = classes.GetSelected();
        if (item == null)
            return;
        selectedClass = item.GetMetadata(0).AsString();
        instanceHeader.Text = "loading instances of " + selectedClass;
        Source?.RequestInstances(selectedClass);
    }

    public void RefreshInstances()
    {
        instances.Clear();
        if (Data == null)
            return;
        TreeItem root = instances.CreateItem();
        instanceHeader.Text = Data.Instances.Count + " live instances of " + Data.InstanceClass;
        foreach (Variant entry in Data.Instances)
        {
            GDDict row = entry.AsGodotDictionary();
            TreeItem item = instances.CreateItem(root);
            item.SetText(0, row["name"].AsString());
            item.SetText(1, row["path"].AsString());
            item.SetText(2, Fmt.Bytes(row["bytes"].AsInt64()));
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetCustomColor(1, DimColor);
            item.SetMetadata(0, row["id"].AsUInt64());
        }
    }

    private void OnInstanceSelected()
    {
        TreeItem item = instances.GetSelected();
        if (item == null)
            return;
        Navigate?.Invoke(item.GetMetadata(0).AsUInt64());
    }

    private void OnInstanceActivated()
    {
        TreeItem item = instances.GetSelected();
        if (item == null)
            return;
        Source?.RequestHighlight(item.GetMetadata(0).AsUInt64());
    }
}
