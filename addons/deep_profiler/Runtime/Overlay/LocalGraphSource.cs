using System;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public sealed class LocalGraphSource : IGraphSource
{
    private readonly ProfilerData data;
    private readonly ProfilerOverlay overlay;

    public LocalGraphSource(ProfilerData data, ProfilerOverlay overlay)
    {
        this.data = data;
        this.overlay = overlay;
    }

    public bool Live => ProfilerRuntime.Instance != null;

    private static ulong RootId()
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        return root != null ? root.GetInstanceId() : 0UL;
    }

    public void RequestTree(ulong id, int offset, int limit)
    {
        if (id == 0)
            id = RootId();
        data.ApplyTree(ObjectGraph.Describe(id, false, false, offset, limit, 60000));
    }

    public void RequestObject(ulong id)
    {
        data.ApplyObject(ObjectGraph.Describe(id, true, true, 0, 200, 60000));
    }

    public void RequestCensus()
    {
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        int budget = runtime?.CrawlBudget ?? 40000;
        data.ApplyCensus(ObjectGraph.Crawl(budget, 4000, 0));
    }

    public void RequestSignals()
    {
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        int budget = runtime?.CrawlBudget ?? 40000;
        data.ApplySignals(ObjectGraph.SignalGraph(budget, 6000));
    }

    public void RequestInstances(string className)
    {
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        int budget = runtime?.CrawlBudget ?? 40000;
        data.ApplyInstances(className, ObjectGraph.Instances(className, 500, budget));
    }

    public void RequestAblate(ulong id, int frames)
    {
        ProfilerRuntime runtime = ProfilerRuntime.Instance;
        string error = runtime?.StartAblation(id, frames);
        if (error != null)
            overlay?.Notify(error);
        else
            overlay?.Notify("measuring cost over " + frames * 4 + " frames");
    }

    public void RequestHighlight(ulong id)
    {
        overlay?.Highlight(id, 4f);
    }

    public void RequestWatch(int slot, ulong id, string path)
    {
        ProfilerRuntime.Instance?.Sampler.SetWatch(slot, id, path);
        overlay?.Notify("watch " + (slot + 1) + " tracks " + path);
    }

    public void RequestFrameCapture()
    {
        ProfilerRuntime.Instance?.CaptureNextFrame();
    }

    public void SetTimeScale(float scale)
    {
        Engine.TimeScale = Math.Clamp(scale, 0.01f, 8f);
    }

    public void SetGamePaused(bool paused)
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        if (tree != null)
            tree.Paused = paused;
    }

    public void ForceCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        overlay?.Notify("forced a full managed collection");
    }
}
