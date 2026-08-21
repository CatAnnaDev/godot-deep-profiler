using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ProfilerDock : VBoxContainer
{
    public ProfilerData Data;
    public ProfilerDebuggerPlugin Debugger;

    private RemoteGraphSource source;
    private ColorRect statusDot;
    private Label statusLabel;
    private Label infoLabel;
    private TabContainer tabs;

    private GraphControl mainGraph;
    private GraphControl memoryGraph;
    private GraphControl renderGraph;
    private OptionButton presetSelect;
    private OptionButton spanSelect;
    private StatGrid stats;
    private Label pinnedLabel;
    private Label worstLabel;

    private ScopePane scopePane;
    private SceneTreePane treePane;
    private ObjectInspectorPane inspector;
    private CensusPane censusPane;
    private ResourcePane resourcePane;
    private SignalPane signalPane;
    private EventPane eventPane;
    private ManagedPane heapPane;
    private InputPane inputPane;
    private Tree costTree;
    private int tabOverview;
    private int tabScopes;
    private int tabHeap;
    private int tabInput;
    private int tabScene;
    private int tabObjects;
    private int tabResources;
    private int tabSignals;
    private int tabCost;
    private int tabEvents;
    private readonly List<GDDict> costRows = new List<GDDict>(16);

    private double refreshTimer;
    private double greetTimer;
    private bool dirty = true;
    private long pinnedFrame = -1;
    private bool sceneRequested;
    private bool censusRequested;
    private bool signalsRequested;
    private bool recovering;

    private static readonly Color Dim = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color Online = new Color(0.35f, 0.85f, 0.45f);
    private static readonly Color Offline = new Color(0.55f, 0.58f, 0.65f);

    private const string DebuggerMeta = "deepprof_debugger";

    public override void _Ready()
    {
        Build();
    }

    private void Build()
    {
        if (Debugger != null)
            SetMeta(DebuggerMeta, Debugger);
        CustomMinimumSize = new Vector2(0, 340);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 3);
        source = new RemoteGraphSource(Debugger);

        BuildToolbar();

        tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.TabChanged += _ => dirty = true;
        AddChild(tabs);

        BuildOverview();
        BuildScopes();
        BuildHeap();
        BuildInput();
        BuildScene();
        BuildObjects();
        BuildResources();
        BuildSignals();
        BuildCost();
        BuildEvents();

        if (Data != null)
        {
            Data.Changed += () => dirty = true;
            Data.TreeReceived += payload => treePane.ApplyPayload(payload);
            Data.ObjectReceived += payload => inspector.SetPayload(payload);
            Data.CensusReceived += () => { censusPane.Refresh(); resourcePane.Refresh(); };
            Data.SignalsReceived += () => signalPane.Refresh();
            Data.InstancesReceived += () => censusPane.RefreshInstances();
            Data.AblationReceived += OnAblation;
        }
        if (Debugger != null)
            Debugger.ConnectionChanged += OnConnectionChanged;
        UpdateStatus();
    }

    private void BuildToolbar()
    {
        HBoxContainer bar = new HBoxContainer();
        AddChild(bar);

        statusDot = new ColorRect { CustomMinimumSize = new Vector2(10, 10), Color = Offline, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        bar.AddChild(statusDot);

        statusLabel = new Label { Text = "waiting for a running game" };
        statusLabel.AddThemeFontSizeOverride("font_size", 11);
        bar.AddChild(statusLabel);

        bar.AddChild(new VSeparator());

        CheckButton pause = new CheckButton { Text = "Pause capture", TooltipText = "Stop recording frames without stopping the game" };
        pause.Toggled += value => source.SetPaused(value);
        bar.AddChild(pause);

        Button clear = new Button { Text = "Clear", TooltipText = "Drop every recorded frame" };
        clear.Pressed += () =>
        {
            Data?.Reset();
            costRows.Clear();
            RefreshCost();
            dirty = true;
        };
        bar.AddChild(clear);

        OptionButton rate = new OptionButton { TooltipText = "How often the running game reports data" };
        rate.AddItem("2 Hz", 0);
        rate.AddItem("5 Hz", 1);
        rate.AddItem("10 Hz", 2);
        rate.AddItem("20 Hz", 3);
        rate.AddItem("30 Hz", 4);
        rate.Selected = 2;
        rate.ItemSelected += index => source.SetRate(index switch { 0 => 2, 1 => 5, 3 => 20, 4 => 30, _ => 10 });
        bar.AddChild(rate);

        CheckButton scopes = new CheckButton { Text = "Scopes", ButtonPressed = true, TooltipText = "Record Prof.Scope timings" };
        scopes.Toggled += value => source.SetScopeCapture(value);
        bar.AddChild(scopes);

        bar.AddChild(new VSeparator());

        Button crawl = new Button { Text = "Crawl", TooltipText = "Walk the whole live object graph now" };
        crawl.Pressed += () => source.RequestCensus();
        bar.AddChild(crawl);

        Button collect = new Button { Text = "GC", TooltipText = "Force a full managed collection in the game" };
        collect.Pressed += () => source.ForceCollect();
        bar.AddChild(collect);

        OptionButton speed = new OptionButton { TooltipText = "Engine time scale of the running game" };
        speed.AddItem("0.1x", 0);
        speed.AddItem("0.25x", 1);
        speed.AddItem("0.5x", 2);
        speed.AddItem("1x", 3);
        speed.AddItem("2x", 4);
        speed.Selected = 3;
        speed.ItemSelected += index => source.SetTimeScale(index switch { 0 => 0.1f, 1 => 0.25f, 2 => 0.5f, 4 => 2f, _ => 1f });
        bar.AddChild(speed);

        CheckButton pauseGame = new CheckButton { Text = "Pause game" };
        pauseGame.Toggled += value => source.SetGamePaused(value);
        bar.AddChild(pauseGame);

        CheckButton overlay = new CheckButton { Text = "Overlay", TooltipText = "Show the in game overlay panel" };
        overlay.Toggled += value => source.SetOverlay(value);
        bar.AddChild(overlay);

        Button export = new Button { Text = "Export", TooltipText = "Write frames and the current capture to user://deep_profiler" };
        export.Pressed += Export;
        bar.AddChild(export);

        infoLabel = new Label { Text = string.Empty, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        infoLabel.AddThemeFontSizeOverride("font_size", 10);
        infoLabel.AddThemeColorOverride("font_color", Dim);
        bar.AddChild(infoLabel);
    }

    private void BuildOverview()
    {
        VBoxContainer page = new VBoxContainer { Name = "Overview" };
        tabs.AddChild(page);
        tabOverview = page.GetIndex();

        HFlowContainer bar = new HFlowContainer();
        page.AddChild(bar);

        presetSelect = new OptionButton();
        for (int i = 0; i < GraphPresets.Names.Length; i++)
            presetSelect.AddItem(GraphPresets.Names[i], i);
        presetSelect.ItemSelected += index => GraphPresets.Apply(mainGraph, (int)index);
        bar.AddChild(presetSelect);

        spanSelect = new OptionButton();
        spanSelect.AddItem("120 frames", 0);
        spanSelect.AddItem("600 frames", 1);
        spanSelect.AddItem("1800 frames", 2);
        spanSelect.AddItem("all", 3);
        spanSelect.Selected = 1;
        spanSelect.ItemSelected += index =>
        {
            long window = index switch { 0 => 120, 2 => 1800, 3 => 60000, _ => 600 };
            mainGraph.SetWindow(window);
            memoryGraph.SetWindow(window);
            renderGraph.SetWindow(window);
        };
        bar.AddChild(spanSelect);


        OptionButton scale = new OptionButton { TooltipText = "Vertical scale of the graph" };
        scale.AddItem("Auto scale", 0);
        scale.AddItem("16.6 ms", 1);
        scale.AddItem("33 ms", 2);
        scale.AddItem("100 ms", 3);
        scale.ItemSelected += index => mainGraph.FixedMax = index switch { 1 => 16.667f, 2 => 33.333f, 3 => 100f, _ => 0f };
        bar.AddChild(scale);

        pinnedLabel = new Label { Text = "live", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pinnedLabel.AddThemeFontSizeOverride("font_size", 10);
        pinnedLabel.AddThemeColorOverride("font_color", Dim);
        bar.AddChild(pinnedLabel);

        worstLabel = new Label { Text = string.Empty };
        worstLabel.AddThemeFontSizeOverride("font_size", 10);
        worstLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.7f, 0.45f));
        bar.AddChild(worstLabel);

        mainGraph = new GraphControl
        {
            Ring = Data?.Frames,
            Markers = Data?.EventFrames,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2f,
        };
        mainGraph.FrameSelected = OnFramePinned;
        page.AddChild(mainGraph);
        GraphPresets.Apply(mainGraph, 0);

        HBoxContainer bottom = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 3f };
        page.AddChild(bottom);

        ScrollContainer scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        stats = new StatGrid { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(stats);
        bottom.AddChild(scroll);
        stats.Configure(Protocol.FieldLabels, 3);

        VBoxContainer side = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        bottom.AddChild(side);

        memoryGraph = new GraphControl { Ring = Data?.Frames, SizeFlagsVertical = SizeFlags.ExpandFill, ShowReadout = false };
        side.AddChild(memoryGraph);
        GraphPresets.Apply(memoryGraph, 1);

        renderGraph = new GraphControl { Ring = Data?.Frames, SizeFlagsVertical = SizeFlags.ExpandFill, ShowReadout = false };
        side.AddChild(renderGraph);
        GraphPresets.Apply(renderGraph, 2);
    }

    private void BuildScopes()
    {
        scopePane = new ScopePane { Name = "Scopes", Data = Data, Source = source };
        tabs.AddChild(scopePane);
        tabScopes = scopePane.GetIndex();
    }

    private void BuildHeap()
    {
        heapPane = new ManagedPane { Name = "Heap", Data = Data, Source = source };
        tabs.AddChild(heapPane);
        tabHeap = heapPane.GetIndex();
    }

    private void BuildInput()
    {
        inputPane = new InputPane { Name = "Input", Data = Data, Source = source };
        tabs.AddChild(inputPane);
        tabInput = inputPane.GetIndex();
    }

    private void BuildScene()
    {
        HSplitContainer split = new HSplitContainer { Name = "Scene" };
        treePane = new SceneTreePane { Source = source, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inspector = new ObjectInspectorPane { Source = source, CustomMinimumSize = new Vector2(340, 0) };
        treePane.ObjectPicked = id => inspector.Navigate(id);
        inspector.Navigated = id => treePane.SelectObject(id);
        inspector.Message = message => statusLabel.Text = message;
        split.AddChild(treePane);
        split.AddChild(inspector);
        tabs.AddChild(split);
        tabScene = split.GetIndex();
    }

    private void BuildObjects()
    {
        censusPane = new CensusPane { Name = "Objects", Data = Data, Source = source };
        censusPane.Navigate = OpenInScene;
        tabs.AddChild(censusPane);
        tabObjects = censusPane.GetIndex();
    }

    private void BuildResources()
    {
        resourcePane = new ResourcePane { Name = "Resources", Data = Data, Source = source };
        resourcePane.Navigate = OpenInScene;
        tabs.AddChild(resourcePane);
        tabResources = resourcePane.GetIndex();
    }

    private void BuildSignals()
    {
        signalPane = new SignalPane { Name = "Signals", Data = Data, Source = source };
        signalPane.Navigate = OpenInScene;
        tabs.AddChild(signalPane);
        tabSignals = signalPane.GetIndex();
    }

    private void BuildCost()
    {
        VBoxContainer page = new VBoxContainer { Name = "Cost" };
        tabs.AddChild(page);
        tabCost = page.GetIndex();

        Label hint = new Label
        {
            Text = "Pick a node in the Scene tab and press Measure cost. The node is disabled and hidden for a few frames each. The report gives the frame time it costs and how many engine objects it creates per frame, which is how you find what churns while you play.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.AddThemeColorOverride("font_color", Dim);
        page.AddChild(hint);

        costTree = new Tree
        {
            Columns = 7,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        costTree.SetColumnTitle(0, "Node");
        costTree.SetColumnTitle(1, "Class");
        costTree.SetColumnTitle(2, "Baseline");
        costTree.SetColumnTitle(3, "Logic");
        costTree.SetColumnTitle(4, "Render");
        costTree.SetColumnTitle(5, "Total");
        costTree.SetColumnTitle(6, "Objects");
        costTree.SetColumnExpandRatio(0, 3);
        costTree.SetColumnExpandRatio(1, 2);
        page.AddChild(costTree);
    }

    private void BuildEvents()
    {
        eventPane = new EventPane { Name = "Events", Data = Data };
        eventPane.FramePicked = OnFramePinned;
        tabs.AddChild(eventPane);
        tabEvents = eventPane.GetIndex();
    }

    private void OpenInScene(ulong id)
    {
        tabs.CurrentTab = tabScene;
        inspector.Navigate(id);
    }

    private void OnConnectionChanged(bool connected)
    {
        UpdateStatus();
        dirty = true;
        sceneRequested = false;
        censusRequested = false;
        signalsRequested = false;
        if (connected)
        {
            treePane.Clear();
            CallDeferred(MethodName.RequestInitial);
            sceneRequested = true;
        }
    }

    private void RequestInitial()
    {
        source.RequestTree(0, 0, 300);
    }

    private void UpdateStatus()
    {
        bool connected = Data != null && Data.Connected;
        statusDot.Color = connected ? Online : Offline;
        if (!connected)
        {
            statusLabel.Text = "waiting for a running game";
            infoLabel.Text = "run the project to start profiling";
            return;
        }
        statusLabel.Text = "profiling";
        if (Data.Hello.Count > 0)
        {
            infoLabel.Text = Data.Hello["engine"].AsString() + "   " + Data.Hello["platform"].AsString()
                             + "   " + Data.Hello["adapter"].AsString()
                             + "   pid " + Data.Hello["pid"].AsInt32()
                             + "   " + Fmt.Count(Data.Frames.Total) + " frames";
        }
    }

    public override void _Process(double delta)
    {
        if (source == null || tabs == null || statusLabel == null)
        {
            Recover();
            return;
        }
        if (Debugger != null && Debugger.IsRunning && (Data == null || !Data.Connected))
        {
            greetTimer += delta;
            if (greetTimer >= 0.5)
            {
                greetTimer = 0.0;
                source.Greet();
            }
        }
        refreshTimer += delta;
        if (refreshTimer < 0.1)
            return;
        refreshTimer = 0.0;
        if (!dirty)
            return;
        dirty = false;
        UpdateStatus();
        bool live = Data != null && Data.Connected;
        int tab = tabs.CurrentTab;
        if (tab == tabOverview)
        {
            RefreshOverview();
        }
        else if (tab == tabScopes)
        {
            scopePane.Refresh();
        }
        else if (tab == tabHeap)
        {
            heapPane.Refresh();
        }
        else if (tab == tabInput)
        {
            inputPane.Refresh();
        }
        else if (tab == tabScene)
        {
            if (live && !sceneRequested)
            {
                sceneRequested = true;
                treePane.RequestRoot();
            }
        }
        else if (tab == tabObjects || tab == tabResources)
        {
            if (live && !censusRequested)
            {
                censusRequested = true;
                source.RequestCensus();
            }
            censusPane.Refresh();
            resourcePane.Refresh();
        }
        else if (tab == tabSignals)
        {
            if (live && !signalsRequested)
            {
                signalsRequested = true;
                source.RequestSignals();
            }
            signalPane.Refresh();
        }
        else if (tab == tabEvents)
        {
            eventPane.Refresh();
        }
    }

    private void Recover()
    {
        if (recovering || !HasMeta(DebuggerMeta))
        {
            SetProcess(false);
            Visible = false;
            return;
        }
        recovering = true;
        Debugger = GetMeta(DebuggerMeta).As<GodotObject>() as ProfilerDebuggerPlugin;
        if (Debugger == null)
        {
            SetProcess(false);
            Visible = false;
            return;
        }
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        Data = new ProfilerData();
        Debugger.Data = Data;
        costRows.Clear();
        pinnedFrame = -1;
        sceneRequested = false;
        censusRequested = false;
        signalsRequested = false;
        Build();
        recovering = false;
    }

    private void RefreshOverview()
    {
        mainGraph.QueueRedraw();
        memoryGraph.QueueRedraw();
        renderGraph.QueueRedraw();
        FrameRing ring = Data?.Frames;
        if (ring == null || ring.Count == 0)
            return;
        long frame = pinnedFrame >= 0 && ring.Has(pinnedFrame) ? pinnedFrame : ring.Total - 1;
        for (int i = 0; i < Protocol.Stride; i++)
        {
            float value = ring.At(frame, i);
            string text = Fmt.Unit(value, Protocol.FieldUnits[i]);
            if (i == Protocol.FFrameMs)
            {
                text += "   p95 " + Fmt.Ms(ring.Percentile(i, ring.Total - 300, ring.Total, 0.95));
                stats.SetValue(i, text, Fmt.HeatColor(value / 33.0));
            }
            else if (i == Protocol.FProcessMs || i == Protocol.FPhysicsMs || i == Protocol.FScopeMs)
            {
                stats.SetValue(i, text, Fmt.HeatColor(value / 33.0));
            }
            else
            {
                stats.SetValue(i, text);
            }
        }
        if (Data.WorstMs > 0.0)
            worstLabel.Text = "worst frame " + Fmt.Ms(Data.WorstMs) + " at " + Data.WorstFrame;
    }

    private void OnFramePinned(long frame)
    {
        pinnedFrame = frame;
        memoryGraph.Selected = frame;
        renderGraph.Selected = frame;
        mainGraph.Selected = frame;
        pinnedLabel.Text = frame < 0 ? "live" : "pinned frame " + frame;
        dirty = true;
        if (tabs.CurrentTab != 0)
            tabs.CurrentTab = 0;
    }

    private void OnAblation(GDDict result)
    {
        if (result == null)
            return;
        if (!result.TryGetValue("ok", out Variant ok) || !ok.AsBool())
        {
            statusLabel.Text = "cost measurement failed: " + (result.TryGetValue("error", out Variant error) ? error.AsString() : "unknown");
            return;
        }
        costRows.Add(result);
        RefreshCost();
        tabs.CurrentTab = tabCost;
    }

    private void RefreshCost()
    {
        costTree.Clear();
        TreeItem root = costTree.CreateItem();
        for (int i = costRows.Count - 1; i >= 0; i--)
        {
            GDDict row = costRows[i];
            double baseline = row["baseline"].AsDouble();
            double total = row["total"].AsDouble();
            TreeItem item = costTree.CreateItem(root);
            item.SetText(0, row["name"].AsString());
            item.SetText(1, row["class"].AsString());
            item.SetText(2, Fmt.Ms(baseline));
            item.SetText(3, Fmt.Ms(row["logic"].AsDouble()));
            item.SetText(4, Fmt.Ms(row["render"].AsDouble()));
            item.SetText(5, Fmt.Ms(total));
            double objectsCost = row.TryGetValue("objects_cost", out Variant cost) ? cost.AsDouble() : 0.0;
            item.SetText(6, objectsCost > 0.01 ? Fmt.Number(Math.Round(objectsCost, 1)) + " per frame" : string.Empty);
            for (int column = 2; column <= 6; column++)
                item.SetTextAlignment(column, HorizontalAlignment.Right);
            item.SetCustomColor(1, Dim);
            item.SetCustomColor(5, Fmt.HeatColor(baseline > 0.0 ? total / baseline * 3.0 : 0.0));
            item.SetTooltipText(0, row["path"].AsString());
        }
    }

    private void Export()
    {
        if (Data == null)
            return;
        const string folder = "user://deep_profiler";
        DirAccess.MakeDirRecursiveAbsolute(folder);
        string stamp = Time.GetDatetimeStringFromSystem(false, false).Replace(":", "-").Replace("T", "_");
        string csvPath = folder + "/frames_" + stamp + ".csv";
        string jsonPath = folder + "/capture_" + stamp + ".json";

        using (FileAccess file = FileAccess.Open(csvPath, FileAccess.ModeFlags.Write))
        {
            if (file != null)
            {
                StringBuilder builder = new StringBuilder(4096);
                builder.Append("frame");
                for (int i = 0; i < Protocol.Stride; i++)
                    builder.Append(',').Append(Protocol.FieldLabels[i].Replace(',', ' '));
                file.StoreLine(builder.ToString());
                FrameRing ring = Data.Frames;
                for (long frame = ring.Oldest; frame < ring.Total; frame++)
                {
                    builder.Clear();
                    builder.Append(frame);
                    for (int i = 0; i < Protocol.Stride; i++)
                        builder.Append(',').Append(ring.At(frame, i).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    file.StoreLine(builder.ToString());
                }
            }
        }

        GDDict capture = new GDDict
        {
            { "hello", Data.Hello },
            { "census", Data.Census },
            { "counters", Data.Counters },
            { "worst_ms", Data.WorstMs },
            { "worst_frame", Data.WorstFrame },
            { "scopes", ScopeDump(Data.Window) },
            { "worst_scopes", ScopeDump(Data.Worst) },
        };
        using (FileAccess file = FileAccess.Open(jsonPath, FileAccess.ModeFlags.Write))
            file?.StoreString(Json.Stringify(capture, "  "));

        statusLabel.Text = "exported to " + ProjectSettings.GlobalizePath(folder);
        OS.ShellShowInFileManager(ProjectSettings.GlobalizePath(csvPath));
    }

    private static GDArray ScopeDump(ScopeView view)
    {
        GDArray rows = new GDArray();
        if (view == null)
            return rows;
        for (int i = 0; i < view.Count; i++)
        {
            rows.Add(new GDDict
            {
                { "name", view.NameOf(i) },
                { "parent", view.Parent[i] },
                { "total_ms", view.Total[i] },
                { "self_ms", view.Self[i] },
                { "calls", view.Calls[i] },
                { "alloc", view.Alloc[i] },
                { "frames", view.Frames },
            });
        }
        return rows;
    }
}
