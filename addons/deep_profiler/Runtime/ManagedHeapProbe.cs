using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public struct TypeAllocation
{
    public long Bytes;
    public int Samples;
    public long LastBytes;
}

public sealed class ManagedHeapProbe : EventListener
{
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int EventAllocationTick = 10;
    private const int MaxTypes = 512;

    private readonly Dictionary<string, TypeAllocation> byType = new Dictionary<string, TypeAllocation>(256, StringComparer.Ordinal);
    private readonly object sync = new object();
    private readonly List<KeyValuePair<string, TypeAllocation>> sortScratch = new List<KeyValuePair<string, TypeAllocation>>(256);
    private EventSource pendingSource;
    private long totalBytes;
    private int totalSamples;
    private double windowStartMsec;
    private bool enabled;

    public bool Available { get; private set; }
    public long TotalBytes => totalBytes;

    public ManagedHeapProbe()
    {
        windowStartMsec = Time.GetTicksMsec();
        if (pendingSource != null)
            Enable(pendingSource);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != RuntimeSource)
            return;
        if (byType == null)
        {
            pendingSource = source;
            return;
        }
        Enable(source);
    }

    private void Enable(EventSource source)
    {
        try
        {
            EnableEvents(source, EventLevel.Verbose, GcKeyword);
            enabled = true;
        }
        catch (Exception)
        {
            enabled = false;
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (!enabled || data.EventId != EventAllocationTick)
            return;
        string typeName = null;
        long size = 0;
        System.Collections.ObjectModel.ReadOnlyCollection<string> names = data.PayloadNames;
        System.Collections.ObjectModel.ReadOnlyCollection<object> payload = data.Payload;
        if (names == null || payload == null)
            return;
        for (int i = 0; i < names.Count && i < payload.Count; i++)
        {
            switch (names[i])
            {
                case "TypeName":
                    typeName = payload[i] as string;
                    break;
                case "ObjectSize":
                    size = Convert.ToInt64(payload[i]);
                    break;
                case "AllocationAmount64":
                    if (size == 0)
                        size = Convert.ToInt64(payload[i]);
                    break;
            }
        }
        if (string.IsNullOrEmpty(typeName))
            return;
        if (size <= 0)
            size = 102400;

        lock (sync)
        {
            Available = true;
            byType.TryGetValue(typeName, out TypeAllocation entry);
            entry.Bytes += size;
            entry.Samples++;
            entry.LastBytes = size;
            if (byType.Count < MaxTypes || byType.ContainsKey(typeName))
                byType[typeName] = entry;
            totalBytes += size;
            totalSamples++;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            byType.Clear();
            totalBytes = 0;
            totalSamples = 0;
            windowStartMsec = Time.GetTicksMsec();
        }
    }

    public GDDict Snapshot(int limit)
    {
        GDArray rows = new GDArray();
        long total;
        int samples;
        double seconds;
        lock (sync)
        {
            total = totalBytes;
            samples = totalSamples;
            seconds = Math.Max(0.001, (Time.GetTicksMsec() - windowStartMsec) / 1000.0);
            sortScratch.Clear();
            foreach (KeyValuePair<string, TypeAllocation> pair in byType)
                sortScratch.Add(pair);
        }
        sortScratch.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
        int count = Math.Min(limit, sortScratch.Count);
        for (int i = 0; i < count; i++)
        {
            KeyValuePair<string, TypeAllocation> pair = sortScratch[i];
            rows.Add(new GDDict
            {
                { "n", Shorten(pair.Key) },
                { "full", pair.Key },
                { "bytes", pair.Value.Bytes },
                { "samples", pair.Value.Samples },
                { "rate", pair.Value.Bytes / seconds },
            });
        }

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        GDDict result = new GDDict
        {
            { "available", Available },
            { "sampled", true },
            { "types", rows },
            { "distinct", sortScratch.Count },
            { "total", total },
            { "samples", samples },
            { "seconds", seconds },
            { "heap", info.HeapSizeBytes },
            { "committed", info.TotalCommittedBytes },
            { "frag", info.FragmentedBytes },
            { "pause", info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0.0 },
            { "pause_pct", info.PauseTimePercentage },
            { "gen", info.Generation },
            { "index", info.Index },
            { "compacted", info.Compacted },
            { "concurrent", info.Concurrent },
            { "finalization", info.FinalizationPendingCount },
            { "pinned", info.PinnedObjectsCount },
            { "load", info.MemoryLoadBytes },
            { "threshold", info.HighMemoryLoadThresholdBytes },
        };
        GDArray generations = new GDArray();
        ReadOnlySpan<GCGenerationInfo> spans = info.GenerationInfo;
        for (int i = 0; i < spans.Length; i++)
        {
            generations.Add(new GDDict
            {
                { "n", GenerationName(i) },
                { "size", spans[i].SizeAfterBytes },
                { "frag", spans[i].FragmentationAfterBytes },
            });
        }
        result["generations"] = generations;
        return result;
    }

    public void FillFrame(float[] sample)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ReadOnlySpan<GCGenerationInfo> spans = info.GenerationInfo;
        for (int i = 0; i < spans.Length && i < 5; i++)
            sample[Protocol.FGen0 + i] = (float)(spans[i].SizeAfterBytes / 1048576.0);
        sample[Protocol.FGcFrag] = (float)(info.FragmentedBytes / 1048576.0);
        sample[Protocol.FGcPause] = info.PauseDurations.Length > 0 ? (float)info.PauseDurations[0].TotalMilliseconds : 0f;
    }

    public static string GenerationName(int index)
    {
        switch (index)
        {
            case 0: return "gen 0";
            case 1: return "gen 1";
            case 2: return "gen 2";
            case 3: return "large object heap";
            case 4: return "pinned object heap";
            default: return "generation " + index;
        }
    }

    private static string Shorten(string typeName)
    {
        int generic = typeName.IndexOf('[');
        string head = generic > 0 ? typeName.Substring(0, generic) : typeName;
        int dot = head.LastIndexOf('.');
        string tail = dot >= 0 ? head.Substring(dot + 1) : head;
        if (generic <= 0)
            return tail;
        return tail + typeName.Substring(generic).Replace("System.", string.Empty);
    }
}
