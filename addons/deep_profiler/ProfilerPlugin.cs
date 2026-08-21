using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ProfilerPlugin : EditorPlugin
{
    public const string AutoloadName = "DeepProf";
    public const string RuntimePath = "res://addons/deep_profiler/Runtime/ProfilerRuntime.cs";
    public const string HostName = "DeepProfiler";

    private ProfilerDock dock;
    private EditorDock host;
    private ProfilerDebuggerPlugin debugger;

    public override void _EnterTree()
    {
        DefineSettings();
        DropStaleDock();
        ProfilerData data = new ProfilerData();
        debugger = new ProfilerDebuggerPlugin { Data = data };
        AddDebuggerPlugin(debugger);
        dock = new ProfilerDock { Data = data, Debugger = debugger, Name = "DeepProfilerDock" };
        host = new EditorDock
        {
            Name = HostName,
            Title = "Deep Profiler",
            DefaultSlot = EditorDock.DockSlot.Bottom,
            LayoutKey = "deep_profiler",
            AvailableLayouts = EditorDock.DockLayout.All,
        };
        host.AddChild(dock);
        AddDock(host);
    }

    public override void _ExitTree()
    {
        EditorDock existing = host ?? FindHost();
        if (existing != null)
        {
            RemoveDock(existing);
            existing.QueueFree();
        }
        host = null;
        dock = null;
        if (debugger != null)
        {
            debugger.SendCommand(Protocol.CmdStop);
            RemoveDebuggerPlugin(debugger);
            debugger = null;
        }
    }

    private void DropStaleDock()
    {
        EditorDock stale = FindHost();
        if (stale == null)
            return;
        RemoveDock(stale);
        stale.QueueFree();
    }

    private static EditorDock FindHost()
    {
        Control baseControl = EditorInterface.Singleton?.GetBaseControl();
        return baseControl?.FindChild(HostName, true, false) as EditorDock;
    }

    public override void _EnablePlugin()
    {
        AddAutoloadSingleton(AutoloadName, RuntimePath);
    }

    public override void _DisablePlugin()
    {
        RemoveAutoloadSingleton(AutoloadName);
    }

    public override string _GetPluginName()
    {
        return "Deep Profiler";
    }

    private static void DefineSettings()
    {
        Define("deep_profiler/runtime/enabled", true, Variant.Type.Bool);
        Define("deep_profiler/runtime/capture_scopes", true, Variant.Type.Bool);
        Define("deep_profiler/runtime/track_allocations", true, Variant.Type.Bool);
        Define("deep_profiler/runtime/track_objects", true, Variant.Type.Bool);
        Define("deep_profiler/runtime/history_frames", 1800, Variant.Type.Int, PropertyHint.Range, "120,60000,60");
        Define("deep_profiler/runtime/send_rate_hz", 10, Variant.Type.Int, PropertyHint.Range, "1,60,1");
        Define("deep_profiler/runtime/spike_ms", 33.0, Variant.Type.Float, PropertyHint.Range, "4,500,0.5");
        Define("deep_profiler/crawl/max_objects", 40000, Variant.Type.Int, PropertyHint.Range, "512,400000,512");
        Define("deep_profiler/overlay/enabled", true, Variant.Type.Bool);
        Define("deep_profiler/overlay/start_visible", false, Variant.Type.Bool);
        Define("deep_profiler/overlay/hotkey", (int)Key.F3, Variant.Type.Int);
    }

    private static void Define(string path, Variant value, Variant.Type type, PropertyHint hint = PropertyHint.None, string hintString = "")
    {
        if (!ProjectSettings.HasSetting(path))
            ProjectSettings.SetSetting(path, value);
        ProjectSettings.SetInitialValue(path, value);
        ProjectSettings.AddPropertyInfo(new GDDict
        {
            { "name", path },
            { "type", (int)type },
            { "hint", (int)hint },
            { "hint_string", hintString },
        });
    }
}
