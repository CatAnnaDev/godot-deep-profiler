using System;
using System.Diagnostics;
using Godot;

namespace DeepProf;

public struct WatchEntry
{
    public ulong Id;
    public string Path;
    public NodePath Cached;
    public bool Valid;
}

public sealed class FrameSampler
{
    public readonly FrameRing Ring;
    private readonly float[] sample = new float[Protocol.Stride];
    private readonly WatchEntry[] watches = new WatchEntry[Protocol.WatchSlots];
    private Process process;
    private long lastAllocated;
    private int slowTick;
    private float cachedWorkingSet;
    private float cachedThreads;
    private readonly float[] cachedHeap = new float[Protocol.Stride];

    public FrameSampler(int capacity)
    {
        Ring = new FrameRing(Protocol.Stride, capacity);
        lastAllocated = GC.GetTotalAllocatedBytes(false);
    }

    public ReadOnlySpan<float> Last => sample;

    public void SetWatch(int slot, ulong id, string path)
    {
        if (slot < 0 || slot >= Protocol.WatchSlots)
            return;
        watches[slot] = new WatchEntry
        {
            Id = id,
            Path = path,
            Cached = string.IsNullOrEmpty(path) ? null : new NodePath(path),
            Valid = id != 0 && !string.IsNullOrEmpty(path),
        };
    }

    public void ClearWatches()
    {
        for (int i = 0; i < watches.Length; i++)
            watches[i] = default;
    }

    public string WatchLabel(int slot)
    {
        if (slot < 0 || slot >= watches.Length || !watches[slot].Valid)
            return string.Empty;
        return watches[slot].Path;
    }

    public void Sample(double frameMs, double processMs, double physicsMs, double scopeMs, double overlayMs, int nodesAdded, int nodesRemoved, ManagedHeapProbe heap)
    {
        Array.Clear(sample, 0, sample.Length);
        sample[Protocol.FFrameMs] = (float)frameMs;
        sample[Protocol.FProcessMs] = (float)processMs;
        sample[Protocol.FPhysicsMs] = (float)physicsMs;
        sample[Protocol.FFps] = (float)Engine.GetFramesPerSecond();
        sample[Protocol.FDrawCalls] = (float)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        sample[Protocol.FPrimitives] = (float)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        sample[Protocol.FObjectsDrawn] = (float)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        sample[Protocol.FStaticMem] = (float)(Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0);
        sample[Protocol.FNodes] = (float)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        sample[Protocol.FOrphans] = (float)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        sample[Protocol.FObjects] = (float)Performance.GetMonitor(Performance.Monitor.ObjectCount);
        sample[Protocol.FResources] = (float)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount);
        sample[Protocol.FVideoMem] = (float)(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0);
        sample[Protocol.FTextureMem] = (float)(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0);
        sample[Protocol.FBufferMem] = (float)(Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0);
        sample[Protocol.FPhys2dActive] = (float)Performance.GetMonitor(Performance.Monitor.Physics2DActiveObjects);
        sample[Protocol.FPhys2dPairs] = (float)Performance.GetMonitor(Performance.Monitor.Physics2DCollisionPairs);
        sample[Protocol.FPhys3dActive] = (float)Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects);
        sample[Protocol.FPhys3dPairs] = (float)Performance.GetMonitor(Performance.Monitor.Physics3DCollisionPairs);
        sample[Protocol.FAudioLatency] = (float)Performance.GetMonitor(Performance.Monitor.AudioOutputLatency);
        sample[Protocol.FNavAgents] = (float)Performance.GetMonitor(Performance.Monitor.NavigationAgentCount);
        sample[Protocol.FPipelines] = (float)(Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas)
                                              + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
                                              + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface)
                                              + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)
                                              + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization));

        long allocated = GC.GetTotalAllocatedBytes(false);
        sample[Protocol.FGcAlloc] = (float)Math.Max(0L, allocated - lastAllocated) / 1024f;
        lastAllocated = allocated;
        sample[Protocol.FGcHeap] = (float)(GC.GetTotalMemory(false) / 1048576.0);
        sample[Protocol.FGc0] = GC.CollectionCount(0);
        sample[Protocol.FGc1] = GC.CollectionCount(1);
        sample[Protocol.FGc2] = GC.CollectionCount(2);

        sample[Protocol.FScopeMs] = (float)scopeMs;
        sample[Protocol.FOverlayMs] = (float)overlayMs;
        sample[Protocol.FNodesAdded] = nodesAdded;
        sample[Protocol.FNodesRemoved] = nodesRemoved;
        sample[Protocol.FTimeScale] = (float)Engine.TimeScale;
        sample[Protocol.FOtherMs] = (float)Math.Max(0.0, frameMs - processMs - physicsMs);

        if (--slowTick <= 0)
        {
            slowTick = 30;
            RefreshProcessStats();
            heap?.FillFrame(cachedHeap);
        }
        for (int i = Protocol.FGen0; i <= Protocol.FGcFrag; i++)
            sample[i] = cachedHeap[i];
        sample[Protocol.FWorkingSet] = cachedWorkingSet;
        sample[Protocol.FThreads] = cachedThreads;

        for (int i = 0; i < watches.Length; i++)
            sample[Protocol.FWatch0 + i] = EvaluateWatch(ref watches[i]);

        Ring.Push(sample);
    }

    private void RefreshProcessStats()
    {
        try
        {
            process ??= Process.GetCurrentProcess();
            process.Refresh();
            cachedWorkingSet = (float)(process.WorkingSet64 / 1048576.0);
            cachedThreads = process.Threads.Count;
        }
        catch (Exception)
        {
            cachedWorkingSet = (float)(System.Environment.WorkingSet / 1048576.0);
            cachedThreads = 0f;
        }
    }

    private static float EvaluateWatch(ref WatchEntry entry)
    {
        if (!entry.Valid)
            return 0f;
        GodotObject target = ObjectGraph.Resolve(entry.Id);
        if (target == null)
            return 0f;
        try
        {
            Variant value = entry.Cached != null && entry.Path.Contains(':') ? target.GetIndexed(entry.Cached) : target.Get(entry.Path);
            return ToFloat(value);
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    public static float ToFloat(Variant value)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Bool: return value.AsBool() ? 1f : 0f;
            case Variant.Type.Int: return value.AsInt64();
            case Variant.Type.Float: return (float)value.AsDouble();
            case Variant.Type.Vector2: return value.AsVector2().Length();
            case Variant.Type.Vector3: return value.AsVector3().Length();
            case Variant.Type.Vector4: return value.AsVector4().Length();
            case Variant.Type.String: return value.AsString().Length;
            case Variant.Type.Array: return value.AsGodotArray().Count;
            case Variant.Type.Dictionary: return value.AsGodotDictionary().Count;
            case Variant.Type.Color: return value.AsColor().Luminance;
            default: return 0f;
        }
    }
}
