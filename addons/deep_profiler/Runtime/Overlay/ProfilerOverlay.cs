using System;
using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public partial class ProfilerOverlay : CanvasLayer
{
    private const float DefaultWidth = 780f;
    private const float DefaultHeight = 440f;
    private const float RefreshInterval = 0.2f;

    private Control rootControl;
    private Panel window;
    private HighlightLayer highlight;
    private HBoxContainer header;
    private Control grip;
    private TabContainer tabs;
    private Label titleLabel;
    private Label liveLabel;
    private Label footer;
    private StatGrid stats;
    private GraphControl graph;
    private OptionButton graphPreset;
    private ScopePane scopePane;
    private SceneTreePane treePane;
    private ObjectInspectorPane inspector;
    private CensusPane censusPane;
    private ResourcePane resourcePane;
    private SignalPane signalPane;
    private EventPane eventPane;
    private ManagedPane heapPane;
    private InputPane inputPane;
    private int tabStats;
    private int tabGraph;
    private int tabScopes;
    private int tabHeap;
    private int tabInput;
    private int tabTree;
    private int tabObjects;
    private int tabResources;
    private int tabSignals;
    private int tabEvents;
    private OptionButton speedSelect;
    private CheckButton pauseToggle;

    private ProfilerData data;
    private LocalGraphSource source;
    private double accumulator;
    private double notifyTimer;
    private long windowSerial = -1;
    private long capturedSerial = -1;
    private bool dragging;
    private bool resizing;
    private Vector2 dragOffset;

    public bool PanelVisible => window != null && window.Visible;

    public override void _EnterTree()
    {
        Layer = 128;
        Name = "DeepProfOverlay";
        ProcessMode = Node.ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        data = new ProfilerData();
        source = new LocalGraphSource(data, this);
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        if (runtime != null)
            data.Frames = runtime.Sampler.Ring;
        data.Connected = true;

        rootControl = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        rootControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(rootControl);

        highlight = new HighlightLayer();
        rootControl.AddChild(highlight);

        window = new Panel
        {
            Position = new Vector2(24, 24),
            Size = new Vector2(DefaultWidth, DefaultHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        StyleBoxFlat style = new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.065f, 0.085f, 0.96f),
            BorderColor = new Color(0.28f, 0.32f, 0.40f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        window.AddThemeStyleboxOverride("panel", style);
        rootControl.AddChild(window);

        MarginContainer margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 6);
        margin.AddThemeConstantOverride("margin_right", 6);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 4);
        window.AddChild(margin);

        VBoxContainer column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        margin.AddChild(column);

        BuildHeader(column);

        tabs = new TabContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            TabsPosition = TabContainer.TabPosition.Top,
        };
        tabs.TabChanged += _ => RefreshActive(true);
        column.AddChild(tabs);

        BuildStatsTab();
        BuildGraphTab();
        BuildScopesTab();
        BuildHeapTab();
        BuildInputTab();
        BuildTreeTab();
        BuildObjectsTab();
        BuildResourcesTab();
        BuildSignalsTab();
        BuildEventsTab();

        footer = new Label { Text = HintText() };
        footer.AddThemeFontSizeOverride("font_size", 10);
        footer.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.62f));
        column.AddChild(footer);

        grip = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(16, 16),
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
        };
        grip.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        grip.Position = window.Size - new Vector2(16, 16);
        grip.GuiInput += OnGripInput;
        grip.Draw += OnGripDraw;
        window.AddChild(grip);
    }

    private void BuildHeader(VBoxContainer column)
    {
        header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        header.GuiInput += OnHeaderInput;
        column.AddChild(header);

        titleLabel = new Label { Text = "DEEP PROFILER" };
        titleLabel.AddThemeFontSizeOverride("font_size", 12);
        titleLabel.AddThemeColorOverride("font_color", new Color(0.98f, 0.82f, 0.45f));
        header.AddChild(titleLabel);

        liveLabel = new Label { Text = string.Empty, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        liveLabel.AddThemeFontSizeOverride("font_size", 11);
        header.AddChild(liveLabel);

        Button crawl = new Button { Text = "Crawl", TooltipText = "Walk the whole object graph now" };
        crawl.Pressed += () =>
        {
            source.RequestCensus();
            censusPane.Refresh();
            resourcePane.Refresh();
            Notify("crawl done in " + Fmt.Ms(data.Census.TryGetValue("ms", out Variant ms) ? ms.AsDouble() : 0.0));
        };
        header.AddChild(crawl);

        Button collect = new Button { Text = "GC", TooltipText = "Force a full managed collection" };
        collect.Pressed += () => source.ForceCollect();
        header.AddChild(collect);

        speedSelect = new OptionButton { TooltipText = "Engine time scale" };
        speedSelect.AddItem("0.1x", 0);
        speedSelect.AddItem("0.25x", 1);
        speedSelect.AddItem("0.5x", 2);
        speedSelect.AddItem("1x", 3);
        speedSelect.AddItem("2x", 4);
        speedSelect.Selected = 3;
        speedSelect.ItemSelected += index => source.SetTimeScale(index switch
        {
            0 => 0.1f,
            1 => 0.25f,
            2 => 0.5f,
            4 => 2f,
            _ => 1f,
        });
        header.AddChild(speedSelect);

        pauseToggle = new CheckButton { Text = "Pause", TooltipText = "Pause the scene tree" };
        pauseToggle.Toggled += value => source.SetGamePaused(value);
        header.AddChild(pauseToggle);

        Button close = new Button { Text = "x", TooltipText = "Hide the overlay" };
        close.Pressed += () => SetOverlayVisible(false);
        header.AddChild(close);
    }

    private void BuildStatsTab()
    {
        ScrollContainer scroll = new ScrollContainer { Name = "Stats" };
        stats = new StatGrid { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(stats);
        tabs.AddChild(scroll);
        tabStats = scroll.GetIndex();
        string[] labels = new string[Protocol.Stride];
        Array.Copy(Protocol.FieldLabels, labels, Protocol.Stride);
        stats.Configure(labels, 3);
    }

    private void BuildGraphTab()
    {
        VBoxContainer page = new VBoxContainer { Name = "Graph" };
        HFlowContainer bar = new HFlowContainer();
        page.AddChild(bar);

        graphPreset = new OptionButton();
        for (int i = 0; i < GraphPresets.Names.Length; i++)
            graphPreset.AddItem(GraphPresets.Names[i], i);
        graphPreset.ItemSelected += index => GraphPresets.Apply(graph, (int)index);
        bar.AddChild(graphPreset);

        OptionButton span = new OptionButton { TooltipText = "Visible history" };
        span.AddItem("120 frames", 0);
        span.AddItem("600 frames", 1);
        span.AddItem("1800 frames", 2);
        span.AddItem("all", 3);
        span.Selected = 1;
        span.ItemSelected += index => graph.SetWindow(index switch
        {
            0 => 120,
            2 => 1800,
            3 => 60000,
            _ => 600,
        });
        bar.AddChild(span);


        OptionButton scale = new OptionButton { TooltipText = "Vertical scale of the graph" };
        scale.AddItem("Auto scale", 0);
        scale.AddItem("16.6 ms", 1);
        scale.AddItem("33 ms", 2);
        scale.AddItem("100 ms", 3);
        scale.ItemSelected += index => graph.FixedMax = index switch { 1 => 16.667f, 2 => 33.333f, 3 => 100f, _ => 0f };
        bar.AddChild(scale);

        Label hint = new Label { Text = "wheel zooms, click pins a frame, right click resets" };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.62f));
        bar.AddChild(hint);

        graph = new GraphControl
        {
            Ring = data.Frames,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Markers = data.EventFrames,
        };
        page.AddChild(graph);
        tabs.AddChild(page);
        tabGraph = page.GetIndex();
        GraphPresets.Apply(graph, 0);
    }

    private void BuildScopesTab()
    {
        scopePane = new ScopePane { Name = "Scopes", Data = data, Source = source, Compact = true };
        tabs.AddChild(scopePane);
        tabScopes = scopePane.GetIndex();
    }

    private void BuildHeapTab()
    {
        heapPane = new ManagedPane { Name = "Heap", Data = data, Source = source };
        tabs.AddChild(heapPane);
        tabHeap = heapPane.GetIndex();
    }

    private void BuildInputTab()
    {
        inputPane = new InputPane { Name = "Input", Data = data, Source = source };
        tabs.AddChild(inputPane);
        tabInput = inputPane.GetIndex();
    }

    private void BuildTreeTab()
    {
        HSplitContainer split = new HSplitContainer { Name = "Tree" };
        treePane = new SceneTreePane { Source = source, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        inspector = new ObjectInspectorPane { Source = source, CustomMinimumSize = new Vector2(330, 0) };
        treePane.ObjectPicked = id => inspector.Navigate(id);
        inspector.Navigated = id => treePane.SelectObject(id);
        inspector.Message = Notify;
        split.AddChild(treePane);
        split.AddChild(inspector);
        tabs.AddChild(split);
        tabTree = split.GetIndex();
        data.TreeReceived += payload => treePane.ApplyPayload(payload);
        data.ObjectReceived += payload => inspector.SetPayload(payload);
        data.AblationReceived += ShowAblation;
    }

    private void BuildObjectsTab()
    {
        censusPane = new CensusPane { Name = "Objects", Data = data, Source = source };
        censusPane.Navigate = id =>
        {
            tabs.CurrentTab = tabTree;
            inspector.Navigate(id);
        };
        tabs.AddChild(censusPane);
        tabObjects = censusPane.GetIndex();
        data.InstancesReceived += () => censusPane.RefreshInstances();
        data.SignalsReceived += () => signalPane.Refresh();
        data.CensusReceived += () => censusPane.Refresh();
    }

    private void BuildResourcesTab()
    {
        resourcePane = new ResourcePane { Name = "Resources", Data = data, Source = source };
        resourcePane.Navigate = id =>
        {
            tabs.CurrentTab = tabTree;
            inspector.Navigate(id);
        };
        tabs.AddChild(resourcePane);
        tabResources = resourcePane.GetIndex();
    }

    private void BuildSignalsTab()
    {
        signalPane = new SignalPane { Name = "Signals", Data = data, Source = source };
        signalPane.Navigate = id =>
        {
            tabs.CurrentTab = tabTree;
            inspector.Navigate(id);
        };
        tabs.AddChild(signalPane);
        tabSignals = signalPane.GetIndex();
    }

    private void BuildEventsTab()
    {
        eventPane = new EventPane { Name = "Events", Data = data };
        eventPane.FramePicked = frame =>
        {
            graph.Selected = frame;
            graph.QueueRedraw();
        };
        tabs.AddChild(eventPane);
        tabEvents = eventPane.GetIndex();
    }

    private static string HintText()
    {
        Key hotkey = ProfilerRuntime.Instance?.OverlayHotkey ?? Key.F3;
        return hotkey + " toggles this overlay   drag the title bar to move   drag the bottom right corner to resize";
    }

    public void SetOverlayVisible(bool value)
    {
        if (window == null)
            return;
        window.Visible = value;
        if (value)
        {
            RefreshActive(true);
            if (treePane != null && ProfilerRuntime.Instance != null)
                treePane.RequestRoot();
        }
    }

    public void Inspect(ulong id)
    {
        if (id == 0)
            return;
        SetOverlayVisible(true);
        tabs.CurrentTab = tabTree;
        inspector.Navigate(id);
        treePane.SelectObject(id);
        highlight?.Add(id, 3f);
    }

    public void Highlight(ulong id, float seconds)
    {
        highlight?.Add(id, seconds);
    }

    public void Notify(string message)
    {
        if (footer == null)
            return;
        footer.Text = message;
        notifyTimer = 4.0;
    }

    public void Refresh(double delta)
    {
        highlight?.Tick(delta);
        if (notifyTimer > 0.0)
        {
            notifyTimer -= delta;
            if (notifyTimer <= 0.0)
                footer.Text = HintText();
        }
        if (window == null || !window.Visible)
            return;

        ClampWindow();
        UpdateLive();
        accumulator += delta;
        if (accumulator < RefreshInterval)
            return;
        accumulator = 0.0;
        RefreshActive(false);
    }

    private void ClampWindow()
    {
        Vector2 viewport = rootControl.Size;
        Vector2 size = window.Size;
        size.X = Math.Clamp(size.X, 360f, Math.Max(360f, viewport.X));
        size.Y = Math.Clamp(size.Y, 220f, Math.Max(220f, viewport.Y));
        window.Size = size;
        Vector2 position = window.Position;
        position.X = Math.Clamp(position.X, 0f, Math.Max(0f, viewport.X - size.X));
        position.Y = Math.Clamp(position.Y, 0f, Math.Max(0f, viewport.Y - size.Y));
        window.Position = position;
        if (grip != null)
            grip.Position = size - new Vector2(16, 16);
    }

    private void UpdateLive()
    {
        FrameRing ring = data.Frames;
        if (ring.Count == 0)
            return;
        long last = ring.Total - 1;
        float frameMs = ring.At(last, Protocol.FFrameMs);
        float fps = ring.At(last, Protocol.FFps);
        liveLabel.Text = "   " + Fmt.Number(fps) + " fps   " + Fmt.Ms(frameMs)
                         + "   draw " + Fmt.Count(ring.At(last, Protocol.FDrawCalls))
                         + "   heap " + Fmt.Bytes(ring.At(last, Protocol.FGcHeap) * 1048576.0)
                         + "   nodes " + Fmt.Count(ring.At(last, Protocol.FNodes));
        liveLabel.AddThemeColorOverride("font_color", Fmt.HeatColor(frameMs / 33.0));
    }

    private void RefreshActive(bool force)
    {
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        if (runtime == null)
            return;
        SyncScopes(runtime);
        int tab = tabs.CurrentTab;
        if (tab == tabStats)
        {
            RefreshStats();
        }
        else if (tab == tabGraph)
        {
            graph.QueueRedraw();
        }
        else if (tab == tabScopes)
        {
            scopePane.Refresh();
        }
        else if (tab == tabHeap || tab == tabInput)
        {
            ProfilerRuntime live = ProfilerRuntime.Instance;
            if (live != null)
            {
                GDDict snapshot = live.HeapSnapshot(80);
                snapshot["input"] = live.InputSnapshot();
                data.Heap = snapshot;
            }
            if (tab == tabHeap)
                heapPane.Refresh();
            else
                inputPane.Refresh();
        }
        else if (tab == tabTree)
        {
            if (force)
                treePane.RequestRoot();
        }
        else if (tab == tabObjects || tab == tabResources)
        {
            if (force && !data.Census.ContainsKey("classes"))
                source.RequestCensus();
            censusPane.Refresh();
            resourcePane.Refresh();
        }
        else if (tab == tabSignals)
        {
            if (force)
            {
                if (data.Signals.Count == 0)
                    source.RequestSignals();
                signalPane.Refresh();
            }
        }
        else if (tab == tabEvents)
        {
            eventPane.Refresh();
        }
    }

    private void SyncScopes(ProfilerRuntime runtime)
    {
        if (runtime.WindowSerial != windowSerial)
        {
            windowSerial = runtime.WindowSerial;
            data.Window = ScopeView.FromTree(runtime.LastWindow, "main", "window", runtime.LastWindowFrames);
            data.Worst = ScopeView.FromTree(runtime.LastWorst, "main", "worst", 1);
            data.WorstMs = runtime.LastWorstMs;
            data.WorstFrame = runtime.LastWorstFrame;
            data.Counters = runtime.LastCounters;
            data.Events.Clear();
            data.EventFrames.Clear();
            foreach (GDDict row in runtime.OverlayEvents)
            {
                data.Events.Add(row);
                data.EventFrames.Add(row["frame"].AsInt64());
            }
        }
        if (runtime.CapturedSerial != capturedSerial)
        {
            capturedSerial = runtime.CapturedSerial;
            data.Captured = ScopeView.FromTree(runtime.CapturedFrame, "main", "frame", 1);
        }
    }

    private void RefreshStats()
    {
        FrameRing ring = data.Frames;
        if (ring.Count == 0)
            return;
        long last = ring.Total - 1;
        for (int i = 0; i < Protocol.Stride; i++)
        {
            float value = ring.At(last, i);
            string text = Fmt.Unit(value, Protocol.FieldUnits[i]);
            if (i == Protocol.FFrameMs || i == Protocol.FProcessMs || i == Protocol.FPhysicsMs)
            {
                float peak = ring.Max(i, last - 120, last + 1);
                text += "   peak " + Fmt.Ms(peak);
                stats.SetValue(i, text, Fmt.HeatColor(value / 33.0));
            }
            else
            {
                stats.SetValue(i, text);
            }
        }
    }

    private void ShowAblation(GDDict result)
    {
        if (result == null)
            return;
        if (!result.TryGetValue("ok", out Variant ok) || !ok.AsBool())
        {
            Notify("cost measurement failed: " + (result.TryGetValue("error", out Variant error) ? error.AsString() : "unknown"));
            return;
        }
        Notify(result["name"].AsString() + " costs " + Fmt.Ms(result["logic"].AsDouble()) + " logic and "
               + Fmt.Ms(result["render"].AsDouble()) + " render per frame (baseline " + Fmt.Ms(result["baseline"].AsDouble()) + ")");
    }

    private void OnHeaderInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            dragging = button.Pressed;
            dragOffset = window.GetGlobalMousePosition() - window.Position;
        }
        else if (@event is InputEventMouseMotion && dragging)
        {
            window.Position = window.GetGlobalMousePosition() - dragOffset;
            ClampWindow();
        }
    }

    private void OnGripInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            resizing = button.Pressed;
        }
        else if (@event is InputEventMouseMotion && resizing)
        {
            window.Size = window.GetGlobalMousePosition() - window.Position + new Vector2(8, 8);
            ClampWindow();
        }
    }

    private void OnGripDraw()
    {
        Color color = new Color(0.55f, 0.60f, 0.70f, 0.8f);
        for (int i = 1; i <= 3; i++)
        {
            float offset = i * 4f;
            grip.DrawLine(new Vector2(16f - offset, 16f), new Vector2(16f, 16f - offset), color, 1f);
        }
    }
}
