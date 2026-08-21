using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public interface IGraphSource
{
    bool Live { get; }
    void RequestTree(ulong id, int offset, int limit);
    void RequestObject(ulong id);
    void RequestCensus();
    void RequestInstances(string className);
    void RequestSignals();
    void ResetHeap();
    void SetTrackObjects(bool enabled);
    void SetAutoCrawl(bool enabled, double interval);
    void RequestAblate(ulong id, int frames);
    void RequestHighlight(ulong id);
    void RequestWatch(int slot, ulong id, string path);
    void RequestFrameCapture();
    void SetTimeScale(float scale);
    void SetGamePaused(bool paused);
    void ForceCollect();
}

public sealed class ProfilerData
{
    public FrameRing Frames = new FrameRing(Protocol.Stride, 7200);
    public ScopeView Window = new ScopeView();
    public ScopeView Worst = new ScopeView();
    public ScopeView Captured = new ScopeView();
    public List<ScopeView> Threads = new List<ScopeView>(4);
    public List<string> Names = new List<string>(256);
    public GDArray Counters = new GDArray();
    public List<GDDict> Events = new List<GDDict>(256);
    public GDDict Hello = new GDDict();
    public GDDict Census = new GDDict();
    public GDDict LastObject;
    public GDDict LastAblation;
    public GDArray Instances = new GDArray();
    public GDArray Signals = new GDArray();
    public GDDict Heap = new GDDict();
    public double SignalMs;
    public string InstanceClass = string.Empty;
    public double WorstMs;
    public long WorstFrame;
    public bool Connected;
    public readonly List<long> EventFrames = new List<long>(64);
    public readonly Dictionary<string, int> CensusBaseline = new Dictionary<string, int>(128, StringComparer.Ordinal);
    public readonly Dictionary<string, int> PreviousCensus = new Dictionary<string, int>(128, StringComparer.Ordinal);

    public event Action Changed;
    public event Action<GDDict> TreeReceived;
    public event Action<GDDict> ObjectReceived;
    public event Action<GDDict> AblationReceived;
    public event Action CensusReceived;
    public event Action InstancesReceived;
    public event Action SignalsReceived;
    public event Action HeapReceived;

    public string NameOf(int id)
    {
        return id >= 0 && id < Names.Count ? Names[id] : "?";
    }

    public void Reset()
    {
        Frames.Clear();
        Window = new ScopeView();
        Worst = new ScopeView();
        Captured = new ScopeView();
        Threads.Clear();
        Counters = new GDArray();
        Events.Clear();
        EventFrames.Clear();
        Census = new GDDict();
        Instances = new GDArray();
        Signals = new GDArray();
        Heap = new GDDict();
        PreviousCensus.Clear();
        LastObject = null;
        LastAblation = null;
        WorstMs = 0.0;
        WorstFrame = 0;
        Notify();
    }

    public void Notify()
    {
        Changed?.Invoke();
    }

    public void ApplyNames(int start, string[] values)
    {
        while (Names.Count < start)
            Names.Add("?");
        for (int i = 0; i < values.Length; i++)
        {
            int index = start + i;
            if (index < Names.Count)
                Names[index] = values[i];
            else
                Names.Add(values[i]);
        }
    }

    public void ApplyFrames(long first, int count, float[] packed)
    {
        if (count <= 0 || packed.Length < count * Protocol.Stride)
            return;
        if (Frames.Total > 0 && first > Frames.Total)
            Frames.Clear();
        long skip = Frames.Total > first ? Frames.Total - first : 0;
        for (long i = skip; i < count; i++)
            Frames.Push(new ReadOnlySpan<float>(packed, (int)(i * Protocol.Stride), Protocol.Stride));
    }

    public void ApplyScopes(GDDict window, GDDict worst, double worstMs, long worstFrame, GDArray counters, GDArray threads)
    {
        Window = ScopeView.FromDict(window, NameOf);
        if (worst != null && worst.Count > 0)
        {
            Worst = ScopeView.FromDict(worst, NameOf);
            WorstMs = worstMs;
            WorstFrame = worstFrame;
        }
        Counters = counters ?? new GDArray();
        Threads.Clear();
        if (threads != null)
        {
            foreach (Variant entry in threads)
                Threads.Add(ScopeView.FromDict(entry.AsGodotDictionary(), NameOf));
        }
    }

    public void ApplyEvents(GDArray rows)
    {
        foreach (Variant entry in rows)
        {
            GDDict row = entry.AsGodotDictionary();
            Events.Add(row);
            if (row.TryGetValue("frame", out Variant frame))
                EventFrames.Add(frame.AsInt64());
        }
        while (Events.Count > 400)
            Events.RemoveAt(0);
        while (EventFrames.Count > 400)
            EventFrames.RemoveAt(0);
    }

    public void ApplyCensus(GDDict census)
    {
        PreviousCensus.Clear();
        if (Census.TryGetValue("classes", out Variant previous))
        {
            foreach (Variant entry in previous.AsGodotArray())
            {
                GDDict row = entry.AsGodotDictionary();
                PreviousCensus[row["class"].AsString()] = row["count"].AsInt32();
            }
        }
        Census = census;
        CensusReceived?.Invoke();
    }

    public void ApplyHeap(GDDict heap)
    {
        Heap = heap;
        HeapReceived?.Invoke();
    }

    public int CrawlDelta(string className, int count)
    {
        return PreviousCensus.TryGetValue(className, out int previous) ? count - previous : 0;
    }

    public void ApplySignals(GDDict payload)
    {
        if (payload != null && payload.TryGetValue("rows", out Variant rows))
        {
            Signals = rows.AsGodotArray();
            SignalMs = payload.TryGetValue("ms", out Variant ms) ? ms.AsDouble() : 0.0;
        }
        SignalsReceived?.Invoke();
    }

    public void ApplyInstances(string className, GDArray rows)
    {
        InstanceClass = className;
        Instances = rows;
        InstancesReceived?.Invoke();
    }

    public void ApplyTree(GDDict payload)
    {
        TreeReceived?.Invoke(payload);
    }

    public void ApplyObject(GDDict payload)
    {
        LastObject = payload;
        ObjectReceived?.Invoke(payload);
    }

    public void ApplyAblation(GDDict payload)
    {
        LastAblation = payload;
        AblationReceived?.Invoke(payload);
    }

    public void SnapshotCensusBaseline()
    {
        CensusBaseline.Clear();
        if (!Census.TryGetValue("classes", out Variant classes))
            return;
        foreach (Variant entry in classes.AsGodotArray())
        {
            GDDict row = entry.AsGodotDictionary();
            CensusBaseline[row["class"].AsString()] = row["count"].AsInt32();
        }
    }

    public int BaselineDelta(string className, int count)
    {
        return CensusBaseline.TryGetValue(className, out int baseline) ? count - baseline : 0;
    }
}
