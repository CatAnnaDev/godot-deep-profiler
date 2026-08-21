using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace DeepProf;

public readonly struct ProfMarker
{
    public readonly int Id;

    internal ProfMarker(int id)
    {
        Id = id;
    }

    public bool IsValid => Id >= 0;
}

public readonly struct ProfScope : IDisposable
{
    private readonly bool active;

    internal ProfScope(bool active)
    {
        this.active = active;
    }

    public void Dispose()
    {
        if (active)
            Prof.End();
    }
}

public struct CounterSlot
{
    public double Last;
    public double Min;
    public double Max;
    public double Sum;
    public int Samples;
    public bool Used;
}

public struct ProfEvent
{
    public int NameId;
    public string Detail;
    public long Frame;
    public double AtMs;
}

internal sealed class ThreadContext
{
    public readonly ScopeTree Live = new ScopeTree();
    public readonly ScopeTree Window = new ScopeTree();
    public readonly ScopeTree Published = new ScopeTree();
    public readonly object PublishLock = new object();
    public bool IsMain;
    public string Name = "thread";
    public int Depth;
    public bool PublishRequested;
    public bool HasPublished;
    public int RootNameId;
}

public static class Prof
{
    public static bool Enabled = true;
    public static bool CaptureScopes = true;
    public static bool TrackAllocations = true;
    public static bool TrackObjects = true;
    public static int MainManagedThreadId = -1;

    internal static readonly List<ThreadContext> Contexts = new List<ThreadContext>(8);
    internal static readonly object ContextsLock = new object();

    [ThreadStatic] private static ThreadContext local;

    private static readonly object CounterLock = new object();
    private static CounterSlot[] counters = new CounterSlot[64];
    private static readonly Queue<ProfEvent> Events = new Queue<ProfEvent>(64);
    private const int MaxEvents = 256;
    internal static long CurrentFrame;

    public static ProfMarker Marker(string name)
    {
        return new ProfMarker(ScopeNames.Intern(name));
    }

    public static ProfScope Scope(string name)
    {
        if (!Enabled || !CaptureScopes)
            return default;
        BeginId(ScopeNames.Intern(name));
        return new ProfScope(true);
    }

    public static ProfScope Scope(ProfMarker marker)
    {
        if (!Enabled || !CaptureScopes || !marker.IsValid)
            return default;
        BeginId(marker.Id);
        return new ProfScope(true);
    }

    public static ProfScope Scope(GodotObject owner, string name)
    {
        if (!Enabled || !CaptureScopes)
            return default;
        string label = owner == null ? name : owner.GetClass() + "." + name;
        BeginId(ScopeNames.Intern(label));
        return new ProfScope(true);
    }

    public static void Begin(string name)
    {
        if (!Enabled || !CaptureScopes)
            return;
        BeginId(ScopeNames.Intern(name));
    }

    public static void Begin(ProfMarker marker)
    {
        if (!Enabled || !CaptureScopes || !marker.IsValid)
            return;
        BeginId(marker.Id);
    }

    public static void End()
    {
        if (!Enabled || !CaptureScopes)
            return;
        ThreadContext context = local;
        if (context == null || context.Depth == 0)
            return;
        context.Depth--;
        context.Live.End(TrackAllocations, TrackObjects);
    }

    private static void BeginId(int nameId)
    {
        ThreadContext context = local ?? Register();
        if (!context.IsMain && context.Depth == 0 && Volatile.Read(ref context.PublishRequested))
            PublishWorker(context);
        context.Depth++;
        context.Live.Begin(nameId, TrackAllocations, TrackObjects);
    }

    public static void ThreadTick()
    {
        ThreadContext context = local;
        if (context != null && !context.IsMain && context.Depth == 0 && Volatile.Read(ref context.PublishRequested))
            PublishWorker(context);
    }

    private static void PublishWorker(ThreadContext context)
    {
        context.Live.CloseAll(TrackAllocations, TrackObjects);
        lock (context.PublishLock)
        {
            context.Published.CopyFrom(context.Live);
            context.HasPublished = true;
        }
        context.Live.Reset(context.RootNameId);
        Volatile.Write(ref context.PublishRequested, false);
    }

    internal static ThreadContext Register()
    {
        ThreadContext context = new ThreadContext();
        Thread thread = Thread.CurrentThread;
        context.IsMain = OS.GetThreadCallerId() == OS.GetMainThreadId();
        if (context.IsMain)
            MainManagedThreadId = System.Environment.CurrentManagedThreadId;
        context.Name = context.IsMain ? "main" : string.IsNullOrEmpty(thread.Name) ? "thread " + thread.ManagedThreadId : thread.Name;
        context.RootNameId = ScopeNames.Intern(context.IsMain ? "Frame" : context.Name);
        context.Live.Reset(context.RootNameId);
        context.Window.Reset(context.RootNameId);
        local = context;
        lock (ContextsLock)
            Contexts.Add(context);
        return context;
    }

    internal static ThreadContext MainContext()
    {
        ThreadContext context = local;
        if (context == null || !context.IsMain)
        {
            context = null;
            lock (ContextsLock)
            {
                for (int i = 0; i < Contexts.Count; i++)
                {
                    if (Contexts[i].IsMain)
                    {
                        context = Contexts[i];
                        break;
                    }
                }
            }
        }
        return context;
    }

    public static void Counter(string name, double value)
    {
        if (!Enabled)
            return;
        int id = ScopeNames.Intern(name);
        lock (CounterLock)
        {
            EnsureCounter(id);
            ref CounterSlot slot = ref counters[id];
            if (!slot.Used)
            {
                slot.Used = true;
                slot.Min = value;
                slot.Max = value;
            }
            else
            {
                if (value < slot.Min) slot.Min = value;
                if (value > slot.Max) slot.Max = value;
            }
            slot.Last = value;
            slot.Sum += value;
            slot.Samples++;
        }
    }

    public static void CounterAdd(string name, double delta)
    {
        if (!Enabled)
            return;
        int id = ScopeNames.Intern(name);
        lock (CounterLock)
        {
            EnsureCounter(id);
            Counter(name, counters[id].Last + delta);
        }
    }

    private static void EnsureCounter(int id)
    {
        if (id >= counters.Length)
            Array.Resize(ref counters, Math.Max(id + 1, counters.Length * 2));
    }

    public static void Event(string name, string detail = null)
    {
        if (!Enabled)
            return;
        int id = ScopeNames.Intern(name);
        lock (CounterLock)
        {
            if (Events.Count >= MaxEvents)
                Events.Dequeue();
            Events.Enqueue(new ProfEvent
            {
                NameId = id,
                Detail = detail ?? string.Empty,
                Frame = CurrentFrame,
                AtMs = Time.GetTicksMsec(),
            });
        }
    }

    internal static ProfEvent[] DrainEvents()
    {
        lock (CounterLock)
        {
            if (Events.Count == 0)
                return Array.Empty<ProfEvent>();
            ProfEvent[] drained = Events.ToArray();
            Events.Clear();
            return drained;
        }
    }

    internal static void SnapshotCounters(List<(int Id, CounterSlot Slot)> destination, bool reset)
    {
        destination.Clear();
        lock (CounterLock)
        {
            for (int i = 0; i < counters.Length; i++)
            {
                if (!counters[i].Used)
                    continue;
                destination.Add((i, counters[i]));
                if (reset)
                {
                    double last = counters[i].Last;
                    counters[i] = new CounterSlot { Used = true, Last = last, Min = last, Max = last, Sum = last, Samples = 1 };
                }
            }
        }
    }
}
