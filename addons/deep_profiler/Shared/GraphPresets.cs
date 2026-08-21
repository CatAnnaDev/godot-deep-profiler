using System;
using Godot;

namespace DeepProf;

public static class GraphPresets
{
    public static readonly string[] Names = { "Time", "Memory", "Render", "Counts", "Physics", "Object churn", "Managed heap", "Watches" };

    private static readonly Color[] Palette =
    {
        new Color(0.35f, 0.78f, 0.98f),
        new Color(0.98f, 0.72f, 0.35f),
        new Color(0.60f, 0.88f, 0.50f),
        new Color(0.88f, 0.55f, 0.95f),
        new Color(0.95f, 0.45f, 0.45f),
        new Color(0.45f, 0.95f, 0.85f),
        new Color(0.85f, 0.85f, 0.45f),
        new Color(0.65f, 0.68f, 0.95f),
    };

    public static Color ColorAt(int index) => Palette[index % Palette.Length];

    public static void Apply(GraphControl graph, int preset)
    {
        if (graph == null)
            return;
        graph.ClearSeries();
        switch (preset)
        {
            case 1:
                graph.Unit = FieldUnit.Megabytes;
                graph.MinRange = 8f;
                graph.AddSeries(Protocol.FGcHeap, Palette[0], "GC heap", true);
                graph.AddSeries(Protocol.FStaticMem, Palette[1], "Static");
                graph.AddSeries(Protocol.FWorkingSet, Palette[2], "Working set");
                graph.AddSeries(Protocol.FVideoMem, Palette[3], "Video");
                break;
            case 2:
                graph.Unit = FieldUnit.Count;
                graph.MinRange = 32f;
                graph.AddSeries(Protocol.FDrawCalls, Palette[0], "Draw calls", true);
                graph.AddSeries(Protocol.FObjectsDrawn, Palette[1], "Objects");
                graph.AddSeries(Protocol.FPrimitives, Palette[2], "Primitives");
                break;
            case 3:
                graph.Unit = FieldUnit.Count;
                graph.MinRange = 32f;
                graph.AddSeries(Protocol.FNodes, Palette[0], "Nodes", true);
                graph.AddSeries(Protocol.FObjects, Palette[1], "Objects");
                graph.AddSeries(Protocol.FResources, Palette[2], "Resources");
                graph.AddSeries(Protocol.FOrphans, Palette[4], "Orphans");
                graph.AddSeries(Protocol.FObjectsAdded, Palette[5], "Created per frame");
                break;
            case 6:
                graph.Unit = FieldUnit.Count;
                graph.MinRange = 4f;
                graph.AddSeries(Protocol.FObjectsAdded, Palette[0], "Created per frame", true);
                graph.AddSeries(Protocol.FObjectsProcess, Palette[1], "In process");
                graph.AddSeries(Protocol.FObjectsPhysics, Palette[2], "In physics");
                graph.AddSeries(Protocol.FObjectsOther, Palette[4], "Elsewhere");
                graph.AddSeries(Protocol.FObjectsInput, Palette[5], "During input");
                graph.AddSeries(Protocol.FInputEvents, Palette[6], "Input events");
                break;
            case 4:
                graph.Unit = FieldUnit.Count;
                graph.MinRange = 8f;
                graph.AddSeries(Protocol.FPhys2dActive, Palette[0], "2D bodies", true);
                graph.AddSeries(Protocol.FPhys2dPairs, Palette[1], "2D pairs");
                graph.AddSeries(Protocol.FPhys3dActive, Palette[2], "3D bodies");
                graph.AddSeries(Protocol.FPhys3dPairs, Palette[3], "3D pairs");
                break;
            case 7:
                graph.Unit = FieldUnit.Megabytes;
                graph.MinRange = 4f;
                graph.AddSeries(Protocol.FGen0, Palette[0], "Gen 0", true);
                graph.AddSeries(Protocol.FGen1, Palette[1], "Gen 1");
                graph.AddSeries(Protocol.FGen2, Palette[2], "Gen 2");
                graph.AddSeries(Protocol.FLoh, Palette[3], "Large heap");
                graph.AddSeries(Protocol.FGcFrag, Palette[4], "Fragmentation");
                break;
            case 8:
                graph.Unit = FieldUnit.Raw;
                graph.MinRange = 1f;
                for (int i = 0; i < Protocol.WatchSlots; i++)
                    graph.AddSeries(Protocol.FWatch0 + i, Palette[i % Palette.Length], "W" + (i + 1));
                break;
            default:
                graph.Unit = FieldUnit.Milliseconds;
                graph.MinRange = 8f;
                graph.AddSeries(Protocol.FFrameMs, Palette[0], "Frame", true);
                graph.AddSeries(Protocol.FProcessMs, Palette[1], "Process");
                graph.AddSeries(Protocol.FPhysicsMs, Palette[2], "Physics");
                graph.AddSeries(Protocol.FOtherMs, Palette[3], "Render and idle");
                graph.AddSeries(Protocol.FScopeMs, Palette[5], "Scoped");
                break;
        }
        graph.QueueRedraw();
    }
}
