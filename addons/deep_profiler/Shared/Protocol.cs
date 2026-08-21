using System;

namespace DeepProf;

public static class Protocol
{
    public const int Version = 1;
    public const string Prefix = "deepprof";

    public const string MsgHello = Prefix + ":hello";
    public const string MsgFrames = Prefix + ":frames";
    public const string MsgNames = Prefix + ":names";
    public const string MsgScopes = Prefix + ":scopes";
    public const string MsgTree = Prefix + ":tree";
    public const string MsgObject = Prefix + ":object";
    public const string MsgCensus = Prefix + ":census";
    public const string MsgSignals = Prefix + ":signals";
    public const string MsgEvents = Prefix + ":events";
    public const string MsgAblation = Prefix + ":ablation";
    public const string MsgFrameDetail = Prefix + ":framedetail";
    public const string MsgCommand = Prefix + ":cmd";
    public const string MsgInstances = Prefix + ":instances";
    public const string MsgHeap = Prefix + ":heap";
    public const string MsgFailure = Prefix + ":failure";

    public const string CmdHello = "hello";
    public const string CmdStop = "stop";
    public const string CmdRate = "rate";
    public const string CmdPause = "pause";
    public const string CmdScopes = "scopes";
    public const string CmdTree = "tree";
    public const string CmdObject = "object";
    public const string CmdCensus = "census";
    public const string CmdSignals = "signals";
    public const string CmdAblate = "ablate";
    public const string CmdWatch = "watch";
    public const string CmdCollect = "collect";
    public const string CmdHighlight = "highlight";
    public const string CmdFrameDetail = "framedetail";
    public const string CmdOverlay = "overlay";
    public const string CmdTimeScale = "timescale";
    public const string CmdPauseGame = "pausegame";
    public const string CmdInstances = "instances";
    public const string CmdHeapReset = "heapreset";
    public const string CmdAutoCrawl = "autocrawl";

    public const int WatchSlots = 8;

    public const int FFrameMs = 0;
    public const int FProcessMs = 1;
    public const int FPhysicsMs = 2;
    public const int FFps = 3;
    public const int FDrawCalls = 4;
    public const int FPrimitives = 5;
    public const int FObjectsDrawn = 6;
    public const int FStaticMem = 7;
    public const int FGcHeap = 8;
    public const int FGcAlloc = 9;
    public const int FNodes = 10;
    public const int FOrphans = 11;
    public const int FObjects = 12;
    public const int FResources = 13;
    public const int FVideoMem = 14;
    public const int FTextureMem = 15;
    public const int FBufferMem = 16;
    public const int FPhys2dActive = 17;
    public const int FPhys2dPairs = 18;
    public const int FPhys3dActive = 19;
    public const int FPhys3dPairs = 20;
    public const int FAudioLatency = 21;
    public const int FGc0 = 22;
    public const int FGc1 = 23;
    public const int FGc2 = 24;
    public const int FScopeMs = 25;
    public const int FNodesAdded = 26;
    public const int FNodesRemoved = 27;
    public const int FThreads = 28;
    public const int FWorkingSet = 29;
    public const int FOverlayMs = 30;
    public const int FTimeScale = 31;
    public const int FNavAgents = 32;
    public const int FPipelines = 33;
    public const int FOtherMs = 34;
    public const int FGen0 = 35;
    public const int FGen1 = 36;
    public const int FGen2 = 37;
    public const int FLoh = 38;
    public const int FPoh = 39;
    public const int FGcPause = 40;
    public const int FGcFrag = 41;
    public const int FWatch0 = 42;

    public const int Stride = FWatch0 + WatchSlots;

    public static readonly string[] FieldLabels =
    {
        "Frame", "Process", "Physics", "FPS",
        "Draw calls", "Primitives", "Objects drawn",
        "Static mem", "GC heap", "GC alloc",
        "Nodes", "Orphans", "Objects", "Resources",
        "Video mem", "Texture mem", "Buffer mem",
        "2D bodies", "2D pairs", "3D bodies", "3D pairs",
        "Audio latency", "GC gen0", "GC gen1", "GC gen2",
        "Scoped", "Nodes added", "Nodes freed", "Threads", "Working set",
        "Overlay", "Time scale", "Nav agents", "Pipelines", "Other",
        "Gen 0", "Gen 1", "Gen 2", "Large heap", "Pinned heap", "GC pause", "Fragmentation",
        "Watch 1", "Watch 2", "Watch 3", "Watch 4",
        "Watch 5", "Watch 6", "Watch 7", "Watch 8",
    };

    public static readonly FieldUnit[] FieldUnits =
    {
        FieldUnit.Milliseconds, FieldUnit.Milliseconds, FieldUnit.Milliseconds, FieldUnit.Count,
        FieldUnit.Count, FieldUnit.Count, FieldUnit.Count,
        FieldUnit.Megabytes, FieldUnit.Megabytes, FieldUnit.Kilobytes,
        FieldUnit.Count, FieldUnit.Count, FieldUnit.Count, FieldUnit.Count,
        FieldUnit.Megabytes, FieldUnit.Megabytes, FieldUnit.Megabytes,
        FieldUnit.Count, FieldUnit.Count, FieldUnit.Count, FieldUnit.Count,
        FieldUnit.Milliseconds, FieldUnit.Count, FieldUnit.Count, FieldUnit.Count,
        FieldUnit.Milliseconds, FieldUnit.Count, FieldUnit.Count, FieldUnit.Count, FieldUnit.Megabytes,
        FieldUnit.Milliseconds, FieldUnit.Ratio, FieldUnit.Count, FieldUnit.Count, FieldUnit.Milliseconds,
        FieldUnit.Megabytes, FieldUnit.Megabytes, FieldUnit.Megabytes, FieldUnit.Megabytes, FieldUnit.Megabytes,
        FieldUnit.Milliseconds, FieldUnit.Megabytes,
        FieldUnit.Raw, FieldUnit.Raw, FieldUnit.Raw, FieldUnit.Raw,
        FieldUnit.Raw, FieldUnit.Raw, FieldUnit.Raw, FieldUnit.Raw,
    };

    static Protocol()
    {
        if (FieldLabels.Length != Stride || FieldUnits.Length != Stride)
            throw new InvalidOperationException("Deep Profiler frame layout mismatch");
    }
}

public enum FieldUnit
{
    Raw,
    Count,
    Milliseconds,
    Kilobytes,
    Megabytes,
    Ratio,
}
