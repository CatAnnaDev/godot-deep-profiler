using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public partial class ProfilerRuntime : Node
{
    public static ProfilerRuntime Instance { get; private set; }

    public FrameSampler Sampler { get; private set; }
    public ScopeTree WindowScopes { get; } = new ScopeTree();
    public ScopeTree WorstScopes { get; } = new ScopeTree();
    public double WorstFrameMs { get; private set; }
    public long WorstFrameIndex { get; private set; }
    public int WindowFrames { get; private set; }
    public ScopeTree LastWindow { get; } = new ScopeTree();
    public ScopeTree LastWorst { get; } = new ScopeTree();
    public double LastWorstMs { get; private set; }
    public long LastWorstFrame { get; private set; }
    public int LastWindowFrames { get; private set; }
    public GDArray LastCounters { get; private set; } = new GDArray();
    public ScopeTree CapturedFrame { get; } = new ScopeTree();
    public GDDict LastAblationResult { get; private set; }
    public long WindowSerial { get; private set; }
    public long CapturedSerial { get; private set; }
    public List<GDDict> OverlayEvents { get; } = new List<GDDict>(64);
    public long FrameIndex => frameIndex;
    public List<string> RecentEvents { get; } = new List<string>(64);
    public bool OverlayVisible => overlay != null && overlay.PanelVisible;

    private readonly Ablation ablation = new Ablation();
    private readonly ManagedHeapProbe heap = new ManagedHeapProbe();
    private readonly GrowthWatch[] growthWatches =
    {
        new GrowthWatch("engine objects", 64f, 0.05f),
        new GrowthWatch("nodes", 32f, 0.05f),
        new GrowthWatch("resources", 16f, 0.05f),
        new GrowthWatch("orphan nodes", 8f, 0.0f),
        new GrowthWatch("managed heap MB", 8f, 0.15f),
    };
    private static readonly int[] GrowthFields =
    {
        Protocol.FObjects, Protocol.FNodes, Protocol.FResources, Protocol.FOrphans, Protocol.FGcHeap,
    };
    private readonly List<(int Id, CounterSlot Slot)> counterScratch = new List<(int, CounterSlot)>(32);
    private readonly List<GDDict> pendingEvents = new List<GDDict>(64);
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private ProfilerOverlay overlay;
    private ProfilerTail tail;
    private ThreadContext mainContext;
    private long frameStartTicks;
    private long frameEndTicks;
    private long processStartTicks;
    private long physicsStartTicks;
    private long physicsTicks;
    private int processObjectStart;
    private int physicsObjectStart;
    private int processObjects;
    private int physicsObjects;
    private long processTicks;
    private long frameIndex;
    private long lastSentFrame;
    private int namesSent;
    private double sendAccumulator;
    private double sendInterval = 0.1;
    private double crawlAccumulator;
    private double growthAccumulator;
    private double heapAccumulator;
    private int nodesAdded;
    private int nodesRemoved;
    private double overlayMs;
    private bool connected;
    private bool handshake;
    private bool paused;
    private bool captureNextFrame;
    private bool registered;
    private bool sceneSignalsBound;
    private bool frameOpen;

    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    public int HistoryFrames { get; private set; } = 1800;
    public double SpikeMs { get; private set; } = 33.0;
    public int CrawlBudget { get; private set; } = 40000;
    public bool AutoCrawl { get; set; }
    public double AutoCrawlInterval { get; set; } = 5.0;
    public Key OverlayHotkey { get; private set; } = Key.F3;

    public override void _EnterTree()
    {
        Instance = this;
        Name = "DeepProf";
        ObjectGraph.ExcludedRoot = GetInstanceId();
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = int.MinValue;
        ProcessPhysicsPriority = int.MinValue;
        LoadSettings();
        Sampler = new FrameSampler(HistoryFrames);
        mainContext = Prof.MainContext() ?? Prof.Register();
        WindowScopes.Reset(ScopeNames.Intern("Frame"));
        connected = EngineDebugger.IsActive();
        if (connected && !EngineDebugger.HasCapture(Protocol.Prefix))
        {
            EngineDebugger.RegisterMessageCapture(Protocol.Prefix, Callable.From<string, GDArray, bool>(OnCapture));
            registered = true;
        }
    }

    public override void _Ready()
    {
        SceneTree tree = GetTree();
        if (tree != null)
        {
            tree.NodeAdded += OnNodeAdded;
            tree.NodeRemoved += OnNodeRemoved;
            sceneSignalsBound = true;
        }
        tail = new ProfilerTail { Runtime = this };
        AddChild(tail);
        if ((bool)GetSetting("deep_profiler/overlay/enabled", true))
            EnsureOverlay();
        if ((bool)GetSetting("deep_profiler/overlay/start_visible", false))
            SetOverlayVisible(true);
    }

    public override void _ExitTree()
    {
        ablation.Abort();
        SceneTree tree = GetTree();
        if (tree != null && sceneSignalsBound)
        {
            tree.NodeAdded -= OnNodeAdded;
            tree.NodeRemoved -= OnNodeRemoved;
            sceneSignalsBound = false;
        }
        if (registered)
        {
            EngineDebugger.UnregisterMessageCapture(Protocol.Prefix);
            registered = false;
        }
        heap.Dispose();
        if (Instance == this)
            Instance = null;
    }

    private void LoadSettings()
    {
        Prof.Enabled = (bool)GetSetting("deep_profiler/runtime/enabled", true);
        Prof.CaptureScopes = (bool)GetSetting("deep_profiler/runtime/capture_scopes", true);
        Prof.TrackAllocations = (bool)GetSetting("deep_profiler/runtime/track_allocations", true);
        Prof.TrackObjects = (bool)GetSetting("deep_profiler/runtime/track_objects", true);
        HistoryFrames = Math.Clamp((int)GetSetting("deep_profiler/runtime/history_frames", 1800), 120, 60000);
        SpikeMs = (double)GetSetting("deep_profiler/runtime/spike_ms", 33.0);
        CrawlBudget = Math.Clamp((int)GetSetting("deep_profiler/crawl/max_objects", 40000), 512, 400000);
        int rate = Math.Clamp((int)GetSetting("deep_profiler/runtime/send_rate_hz", 10), 1, 60);
        sendInterval = 1.0 / rate;
        OverlayHotkey = (Key)(int)GetSetting("deep_profiler/overlay/hotkey", (int)Key.F3);
    }

    private static Variant GetSetting(string path, Variant fallback)
    {
        if (!ProjectSettings.HasSetting(path))
            return fallback;
        Variant value = ProjectSettings.GetSetting(path);
        return value.VariantType == Variant.Type.Nil ? fallback : value;
    }

    public override void _Process(double delta)
    {
        OpenFrame();
        processStartTicks = Stopwatch.GetTimestamp();
        processObjectStart = ObjectCount();
    }

    public override void _PhysicsProcess(double delta)
    {
        OpenFrame();
        physicsStartTicks = Stopwatch.GetTimestamp();
        physicsObjectStart = ObjectCount();
    }

    private static int ObjectCount()
    {
        return (int)Performance.GetMonitor(Performance.Monitor.ObjectCount);
    }

    public void ClosePhysicsStep()
    {
        if (physicsStartTicks > 0)
        {
            physicsTicks += Stopwatch.GetTimestamp() - physicsStartTicks;
            physicsObjects += Math.Max(0, ObjectCount() - physicsObjectStart);
        }
        physicsStartTicks = 0;
    }

    public void CloseProcessStep()
    {
        if (!frameOpen)
            return;
        frameOpen = false;
        long now = Stopwatch.GetTimestamp();
        processTicks = processStartTicks > 0 ? now - processStartTicks : 0;
        processObjects = processStartTicks > 0 ? Math.Max(0, ObjectCount() - processObjectStart) : 0;
        double frameMs = (now - (frameEndTicks > 0 ? frameEndTicks : frameStartTicks)) * ScopeTree.TicksToMs;
        frameEndTicks = now;
        EndFrame(frameMs);
    }

    private void OpenFrame()
    {
        if (frameOpen)
            return;
        frameOpen = true;
        frameStartTicks = Stopwatch.GetTimestamp();
        physicsTicks = 0;
        processTicks = 0;
        processStartTicks = 0;
        physicsObjects = 0;
        processObjects = 0;
        frameIndex++;
        Prof.CurrentFrame = frameIndex;
        if (mainContext != null && Prof.CaptureScopes)
        {
            mainContext.Live.Reset(mainContext.RootNameId);
            mainContext.Depth = 0;
        }
    }

    private void EndFrame(double frameMs)
    {
        double delta = frameMs * 0.001;
        double scopeMs = 0.0;
        if (mainContext != null && Prof.CaptureScopes)
        {
            ScopeTree live = mainContext.Live;
            live.CloseAll(Prof.TrackAllocations, Prof.TrackObjects);
            live.StampRoot((long)(frameMs / ScopeTree.TicksToMs), 1);
            if (live.Count > 0)
            {
                for (int child = live.Nodes[0].FirstChild; child >= 0; child = live.Nodes[child].NextSibling)
                    scopeMs += live.Nodes[child].Total * ScopeTree.TicksToMs;
            }
            live.MergeInto(WindowScopes);
            if (frameMs > WorstFrameMs)
            {
                WorstFrameMs = frameMs;
                WorstFrameIndex = frameIndex;
                WorstScopes.CopyFrom(live);
            }
            if (captureNextFrame)
            {
                captureNextFrame = false;
                CapturedFrame.CopyFrom(live);
                CapturedSerial++;
                Send(Protocol.MsgFrameDetail, new GDArray { live.Serialize("main", "frame", 1), frameIndex });
            }
            WindowFrames++;
        }

        if (!paused)
        {
            Sampler.Sample(frameMs, processTicks * ScopeTree.TicksToMs, physicsTicks * ScopeTree.TicksToMs,
                scopeMs, overlayMs, nodesAdded, nodesRemoved, processObjects, physicsObjects, heap);
            TrackGrowth(delta);
        }

        if (frameMs > SpikeMs && frameIndex > 30)
            RecordSpike(frameMs, scopeMs);

        nodesAdded = 0;
        nodesRemoved = 0;

        GDDict ablationResult = ablation.Tick(frameMs);
        if (ablationResult != null)
        {
            LastAblationResult = ablationResult;
            Send(Protocol.MsgAblation, new GDArray { ablationResult });
        }

        if (overlay != null)
        {
            long overlayStart = Stopwatch.GetTimestamp();
            overlay.Refresh(delta);
            overlayMs = (Stopwatch.GetTimestamp() - overlayStart) * ScopeTree.TicksToMs;
        }
        else
        {
            overlayMs = 0.0;
        }

        heapAccumulator += delta;
        if (connected && handshake && heapAccumulator >= 1.0)
        {
            heapAccumulator = 0.0;
            SendHeap();
        }

        sendAccumulator += delta;
        if (sendAccumulator >= sendInterval)
        {
            sendAccumulator = 0.0;
            RotateWindow();
            if (connected && handshake)
                Flush();
        }

        if (AutoCrawl)
        {
            crawlAccumulator += delta;
            if (crawlAccumulator >= AutoCrawlInterval)
            {
                crawlAccumulator = 0.0;
                SendCensus(CrawlBudget);
            }
        }
    }

    private void TrackGrowth(double delta)
    {
        growthAccumulator += delta;
        if (growthAccumulator < 1.0)
            return;
        double elapsed = growthAccumulator;
        growthAccumulator = 0.0;
        FrameRing ring = Sampler.Ring;
        if (ring.Count == 0)
            return;
        long last = ring.Total - 1;
        for (int i = 0; i < growthWatches.Length; i++)
        {
            string message = growthWatches[i].Push(ring.At(last, GrowthFields[i]), elapsed, 1.0);
            if (message != null)
                PushEvent("growth", message);
        }
    }

    private void RecordSpike(double frameMs, double scopeMs)
    {
        string top = string.Empty;
        ScopeTree live = mainContext?.Live;
        if (live != null && live.Count > 1)
        {
            double best = 0.0;
            int bestIndex = -1;
            for (int child = live.Nodes[0].FirstChild; child >= 0; child = live.Nodes[child].NextSibling)
            {
                double value = live.Nodes[child].Total * ScopeTree.TicksToMs;
                if (value > best)
                {
                    best = value;
                    bestIndex = child;
                }
            }
            if (bestIndex >= 0)
                top = ScopeNames.Get(live.Nodes[bestIndex].NameId) + " " + Fmt.Ms(best);
        }
        PushEvent("spike", "frame " + frameIndex + " " + Fmt.Ms(frameMs) + (top.Length > 0 ? " top " + top : string.Empty));
    }

    private void PushEvent(string kind, string detail)
    {
        if (RecentEvents.Count >= 256)
            RecentEvents.RemoveAt(0);
        RecentEvents.Add(frameIndex + "|" + kind + "|" + detail);
    }

    private void OnNodeAdded(Node node)
    {
        nodesAdded++;
        ObjectGraph.InvalidateStats();
    }

    private void OnNodeRemoved(Node node)
    {
        nodesRemoved++;
        ObjectGraph.InvalidateStats();
    }

    private void SendHello()
    {
        if (!connected)
            return;
        GDDict info = new GDDict
        {
            { "version", Protocol.Version },
            { "stride", Protocol.Stride },
            { "pid", OS.GetProcessId() },
            { "engine", Engine.GetVersionInfo()["string"].AsString() },
            { "platform", OS.GetName() },
            { "debug", OS.IsDebugBuild() },
            { "adapter", RenderingServer.GetVideoAdapterName() },
            { "api", RenderingServer.GetVideoAdapterApiVersion() },
            { "cpu", OS.GetProcessorName() },
            { "cores", OS.GetProcessorCount() },
            { "scene", GetTree()?.CurrentScene?.SceneFilePath ?? string.Empty },
            { "rate", 1.0 / sendInterval },
            { "history", HistoryFrames },
        };
        Send(Protocol.MsgHello, new GDArray { info });
    }

    private void Flush()
    {
        SendNames();
        SendFrames();
        SendScopes();
        SendEvents();
    }

    private void SendNames()
    {
        int total = ScopeNames.Count;
        if (total <= namesSent)
            return;
        string[] slice = ScopeNames.Range(namesSent, total);
        Send(Protocol.MsgNames, new GDArray { namesSent, Variant.From(slice) });
        namesSent = total;
    }

    private void SendFrames()
    {
        FrameRing ring = Sampler.Ring;
        long from = Math.Max(lastSentFrame, ring.Oldest);
        long to = ring.Total;
        if (to <= from)
            return;
        const int maxBatch = 240;
        if (to - from > maxBatch)
            from = to - maxBatch;
        float[] packed = ring.Range(from, to);
        Send(Protocol.MsgFrames, new GDArray { from, (int)(to - from), Variant.From(packed) });
        lastSentFrame = to;
    }

    private void RotateWindow()
    {
        LastWindow.CopyFrom(WindowScopes);
        LastWindow.Frames = WindowFrames;
        LastWorst.CopyFrom(WorstScopes);
        LastWorstMs = WorstFrameMs;
        LastWorstFrame = WorstFrameIndex;
        LastWindowFrames = WindowFrames;
        LastCounters = SnapshotCounters();
        WindowSerial++;
        WindowScopes.Reset(mainContext?.RootNameId ?? 0);
        WindowFrames = 0;
        WorstFrameMs = 0.0;
        WorstScopes.Count = 0;
        DrainEventQueue();
    }

    private void DrainEventQueue()
    {
        ProfEvent[] marks = Prof.DrainEvents();
        foreach (ProfEvent mark in marks)
        {
            GDDict row = new GDDict
            {
                { "kind", "mark" },
                { "name", ScopeNames.Get(mark.NameId) },
                { "detail", mark.Detail },
                { "frame", mark.Frame },
            };
            pendingEvents.Add(row);
            OverlayEvents.Add(row);
        }
        foreach (string line in RecentEvents)
        {
            string[] parts = line.Split('|', 3);
            GDDict row = new GDDict
            {
                { "kind", parts.Length > 1 ? parts[1] : "event" },
                { "name", parts.Length > 1 ? parts[1] : "event" },
                { "detail", parts.Length > 2 ? parts[2] : string.Empty },
                { "frame", parts.Length > 0 && long.TryParse(parts[0], out long value) ? value : frameIndex },
            };
            pendingEvents.Add(row);
            OverlayEvents.Add(row);
        }
        RecentEvents.Clear();
        while (OverlayEvents.Count > 200)
            OverlayEvents.RemoveAt(0);
        while (pendingEvents.Count > 400)
            pendingEvents.RemoveAt(0);
    }

    private void SendScopes()
    {
        if (!Prof.CaptureScopes || LastWindowFrames == 0)
            return;
        GDArray payload = new GDArray
        {
            LastWindow.Serialize("main", "window", LastWindowFrames),
            LastWorst.Count > 0 ? LastWorst.Serialize("main", "worst", 1) : new GDDict(),
            LastWorstMs,
            LastWorstFrame,
            LastCounters,
            SerializeThreads(),
        };
        Send(Protocol.MsgScopes, payload);
    }

    private GDArray SnapshotCounters()
    {
        Prof.SnapshotCounters(counterScratch, true);
        GDArray rows = new GDArray();
        foreach ((int id, CounterSlot slot) in counterScratch)
        {
            rows.Add(new GDDict
            {
                { "n", ScopeNames.Get(id) },
                { "last", slot.Last },
                { "min", slot.Min },
                { "max", slot.Max },
                { "avg", slot.Samples > 0 ? slot.Sum / slot.Samples : 0.0 },
                { "samples", slot.Samples },
            });
        }
        return rows;
    }

    private GDArray SerializeThreads()
    {
        GDArray rows = new GDArray();
        lock (Prof.ContextsLock)
        {
            for (int i = 0; i < Prof.Contexts.Count; i++)
            {
                ThreadContext context = Prof.Contexts[i];
                if (context.IsMain)
                    continue;
                bool has;
                lock (context.PublishLock)
                {
                    has = context.HasPublished;
                    if (has)
                    {
                        rows.Add(context.Published.Serialize(context.Name, "thread", 1));
                        context.HasPublished = false;
                    }
                }
                if (!has)
                    System.Threading.Volatile.Write(ref context.PublishRequested, true);
            }
        }
        return rows;
    }

    private void SendHeap()
    {
        Send(Protocol.MsgHeap, new GDArray { heap.Snapshot(80) });
    }

    private void SendEvents()
    {
        if (pendingEvents.Count == 0)
            return;
        GDArray rows = new GDArray();
        foreach (GDDict row in pendingEvents)
            rows.Add(row);
        pendingEvents.Clear();
        Send(Protocol.MsgEvents, new GDArray { rows });
    }

    private void SendCensus(int budget)
    {
        GDDict census = ObjectGraph.Crawl(budget, 4000, 0);
        census["frame"] = frameIndex;
        Send(Protocol.MsgCensus, new GDArray { census });
    }

    private void Send(string message, GDArray data)
    {
        if (!connected || !handshake)
            return;
        try
        {
            EngineDebugger.SendMessage(message, data);
        }
        catch (Exception exception)
        {
            GD.PushWarning("Deep Profiler send failed: " + exception.Message);
        }
    }

    private bool OnCapture(string message, GDArray data)
    {
        string command = message.StartsWith(Protocol.Prefix + ":", StringComparison.Ordinal)
            ? message.Substring(Protocol.Prefix.Length + 1)
            : message;
        if (command != "cmd" || data.Count == 0)
            return true;
        string name = data[0].AsString();
        try
        {
            Execute(name, data);
        }
        catch (Exception exception)
        {
            Send(Protocol.MsgFailure, new GDArray { name, exception.Message });
        }
        return true;
    }

    private void Execute(string name, GDArray data)
    {
        switch (name)
        {
            case Protocol.CmdHello:
            {
                handshake = true;
                namesSent = 0;
                lastSentFrame = Sampler.Ring.Oldest;
                SendHello();
                break;
            }
            case Protocol.CmdStop:
                handshake = false;
                break;
            case Protocol.CmdRate:
            {
                int rate = Math.Clamp(data[1].AsInt32(), 1, 60);
                sendInterval = 1.0 / rate;
                break;
            }
            case Protocol.CmdPause:
                paused = data[1].AsBool();
                break;
            case Protocol.CmdScopes:
                Prof.CaptureScopes = data[1].AsBool();
                break;
            case Protocol.CmdTrackObjects:
                Prof.TrackObjects = data[1].AsBool();
                break;
            case Protocol.CmdTree:
            {
                ulong id = data[1].AsUInt64();
                int offset = data.Count > 2 ? data[2].AsInt32() : 0;
                int limit = data.Count > 3 ? data[3].AsInt32() : 200;
                if (id == 0)
                {
                    Node root = GetTree()?.Root;
                    id = root != null ? root.GetInstanceId() : 0;
                }
                GDDict payload = ObjectGraph.Describe(id, false, false, offset, limit, 60000);
                Send(Protocol.MsgTree, new GDArray { payload });
                break;
            }
            case Protocol.CmdObject:
            {
                ulong id = data[1].AsUInt64();
                GDDict payload = ObjectGraph.Describe(id, true, true, 0, 400, 60000);
                Send(Protocol.MsgObject, new GDArray { payload });
                break;
            }
            case Protocol.CmdSignals:
            {
                GDDict payload = ObjectGraph.SignalGraph(CrawlBudget, data.Count > 1 ? data[1].AsInt32() : 6000);
                Send(Protocol.MsgSignals, new GDArray { payload });
                break;
            }
            case Protocol.CmdInstances:
            {
                string className = data[1].AsString();
                GDArray rows = ObjectGraph.Instances(className, data.Count > 2 ? data[2].AsInt32() : 500, CrawlBudget);
                Send(Protocol.MsgInstances, new GDArray { className, rows });
                break;
            }
            case Protocol.CmdCensus:
                SendCensus(data.Count > 1 ? Math.Clamp(data[1].AsInt32(), 512, 400000) : CrawlBudget);
                break;
            case Protocol.CmdAblate:
            {
                ulong id = data[1].AsUInt64();
                int frames = data.Count > 2 ? data[2].AsInt32() : 40;
                string error = ablation.Start(id, frames);
                if (error != null)
                    Send(Protocol.MsgAblation, new GDArray { new GDDict { { "ok", false }, { "error", error } } });
                break;
            }
            case Protocol.CmdWatch:
            {
                int slot = data[1].AsInt32();
                ulong id = data[2].AsUInt64();
                string path = data.Count > 3 ? data[3].AsString() : string.Empty;
                Sampler.SetWatch(slot, id, path);
                break;
            }
            case Protocol.CmdHeapReset:
                heap.Reset();
                for (int i = 0; i < growthWatches.Length; i++)
                    growthWatches[i].Reset();
                break;
            case Protocol.CmdAutoCrawl:
                AutoCrawl = data[1].AsBool();
                if (data.Count > 2)
                    AutoCrawlInterval = Math.Clamp(data[2].AsDouble(), 1.0, 120.0);
                crawlAccumulator = 0.0;
                break;
            case Protocol.CmdCollect:
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                PushEvent("gc", "forced collection");
                break;
            case Protocol.CmdHighlight:
            {
                ulong id = data[1].AsUInt64();
                EnsureOverlay();
                overlay?.Highlight(id, data.Count > 2 ? data[2].AsSingle() : 2.5f);
                break;
            }
            case Protocol.CmdFrameDetail:
                captureNextFrame = true;
                break;
            case Protocol.CmdOverlay:
                SetOverlayVisible(data[1].AsBool());
                break;
            case Protocol.CmdTimeScale:
                Engine.TimeScale = Math.Clamp(data[1].AsSingle(), 0.01f, 8.0f);
                break;
            case Protocol.CmdPauseGame:
            {
                SceneTree tree = GetTree();
                if (tree != null)
                    tree.Paused = data[1].AsBool();
                break;
            }
        }
    }

    public void EnsureOverlay()
    {
        if (overlay != null && IsInstanceValid(overlay))
            return;
        overlay = new ProfilerOverlay();
        AddChild(overlay);
    }

    public void SetOverlayVisible(bool value)
    {
        if (value)
            EnsureOverlay();
        if (overlay != null)
            overlay.SetOverlayVisible(value);
    }

    public void HighlightObject(ulong id, float seconds)
    {
        EnsureOverlay();
        overlay.Highlight(id, seconds);
    }

    public void InspectObject(ulong id)
    {
        EnsureOverlay();
        overlay.Inspect(id);
    }

    public void ToggleOverlay()
    {
        EnsureOverlay();
        overlay.SetOverlayVisible(!overlay.PanelVisible);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;
        if (key.Keycode != OverlayHotkey)
            return;
        ToggleOverlay();
        GetViewport().SetInputAsHandled();
    }

    public string StartAblation(ulong id, int frames)
    {
        string error = ablation.Start(id, frames);
        if (error != null)
            LastAblationResult = new GDDict { { "ok", false }, { "error", error } };
        return error;
    }

    public void CaptureNextFrame()
    {
        captureNextFrame = true;
    }

    public GDDict HeapSnapshot(int limit)
    {
        return heap.Snapshot(limit);
    }

    public void ResetHeap()
    {
        heap.Reset();
        for (int i = 0; i < growthWatches.Length; i++)
            growthWatches[i].Reset();
    }

    public GDDict RunCrawl(int budget)
    {
        return ObjectGraph.Crawl(budget, 4000, 0);
    }

    public void ForceCensus()
    {
        SendCensus(CrawlBudget);
    }
}
