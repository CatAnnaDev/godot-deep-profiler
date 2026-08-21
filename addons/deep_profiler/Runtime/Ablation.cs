using System;
using System.Collections.Generic;
using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public sealed class Ablation
{
    private const int PhaseCount = 4;
    private const int Warmup = 3;

    private readonly List<double>[] samples = new List<double>[PhaseCount];
    private ulong targetId;
    private int framesPerPhase;
    private int phase = -1;
    private int frameCounter;
    private Node.ProcessModeEnum savedMode;
    private bool savedVisible;
    private bool hasVisible;
    private string targetName = string.Empty;
    private string targetPath = string.Empty;
    private string targetClass = string.Empty;

    public bool Running => phase >= 0;

    public Ablation()
    {
        for (int i = 0; i < PhaseCount; i++)
            samples[i] = new List<double>(64);
    }

    public string Start(ulong id, int frames)
    {
        if (Running)
            return "an ablation run is already in progress";
        Node node = ObjectGraph.Resolve(id) as Node;
        if (node == null)
            return "target is not a live node";
        if (node == ProfilerRuntime.Instance || IsAncestorOfProfiler(node))
            return "refusing to ablate the profiler itself";
        targetId = id;
        framesPerPhase = Math.Clamp(frames, 8, 600);
        targetName = node.Name.ToString();
        targetClass = node.GetClass();
        targetPath = node.IsInsideTree() ? node.GetPath().ToString() : string.Empty;
        savedMode = node.ProcessMode;
        hasVisible = TryGetVisible(node, out savedVisible);
        for (int i = 0; i < PhaseCount; i++)
            samples[i].Clear();
        phase = 0;
        frameCounter = 0;
        Apply(node, phase);
        return null;
    }

    private static bool IsAncestorOfProfiler(Node node)
    {
        Node profiler = ProfilerRuntime.Instance;
        while (profiler != null)
        {
            if (profiler == node)
                return true;
            profiler = profiler.GetParent();
        }
        return false;
    }

    public GDDict Tick(double frameMs)
    {
        if (!Running)
            return null;
        Node node = ObjectGraph.Resolve(targetId) as Node;
        if (node == null)
        {
            phase = -1;
            return new GDDict { { "ok", false }, { "error", "target disappeared" } };
        }
        frameCounter++;
        if (frameCounter > Warmup)
            samples[phase].Add(frameMs);
        if (frameCounter < framesPerPhase + Warmup)
            return null;

        frameCounter = 0;
        phase++;
        if (phase < PhaseCount)
        {
            Apply(node, phase);
            return null;
        }

        Restore(node);
        phase = -1;
        return Report();
    }

    public void Abort()
    {
        if (!Running)
            return;
        Node node = ObjectGraph.Resolve(targetId) as Node;
        if (node != null)
            Restore(node);
        phase = -1;
    }

    private void Apply(Node node, int index)
    {
        switch (index)
        {
            case 0:
                node.ProcessMode = savedMode;
                SetVisible(node, savedVisible);
                break;
            case 1:
                node.ProcessMode = Node.ProcessModeEnum.Disabled;
                SetVisible(node, savedVisible);
                break;
            case 2:
                node.ProcessMode = savedMode;
                SetVisible(node, false);
                break;
            case 3:
                node.ProcessMode = Node.ProcessModeEnum.Disabled;
                SetVisible(node, false);
                break;
        }
    }

    private void Restore(Node node)
    {
        node.ProcessMode = savedMode;
        SetVisible(node, savedVisible);
    }

    private static bool TryGetVisible(Node node, out bool visible)
    {
        switch (node)
        {
            case CanvasItem canvasItem:
                visible = canvasItem.Visible;
                return true;
            case Node3D node3d:
                visible = node3d.Visible;
                return true;
            default:
                visible = true;
                return false;
        }
    }

    private void SetVisible(Node node, bool visible)
    {
        if (!hasVisible)
            return;
        switch (node)
        {
            case CanvasItem canvasItem:
                canvasItem.Visible = visible;
                break;
            case Node3D node3d:
                node3d.Visible = visible;
                break;
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0.0;
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) * 0.5;
    }

    private GDDict Report()
    {
        double baseline = Median(samples[0]);
        double noProcess = Median(samples[1]);
        double noVisible = Median(samples[2]);
        double noBoth = Median(samples[3]);
        return new GDDict
        {
            { "ok", true },
            { "id", targetId },
            { "name", targetName },
            { "class", targetClass },
            { "path", targetPath },
            { "frames", framesPerPhase },
            { "baseline", baseline },
            { "no_process", noProcess },
            { "no_visible", hasVisible ? noVisible : baseline },
            { "no_both", hasVisible ? noBoth : noProcess },
            { "logic", baseline - noProcess },
            { "render", hasVisible ? baseline - noVisible : 0.0 },
            { "total", hasVisible ? baseline - noBoth : baseline - noProcess },
            { "has_visible", hasVisible },
        };
    }
}
