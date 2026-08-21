using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ProfilerPlugin : EditorPlugin
{
    public const string AutoloadName = "DeepProf";
    public const string RuntimePath = "res://addons/deep_profiler/Runtime/ProfilerRuntime.cs";

    private ProfilerDock dock;
    private EditorDock host;
    private ProfilerDebuggerPlugin debugger;

    public override void _EnterTree()
    {
        DefineSettings();
        ProfilerData data = new ProfilerData();
        debugger = new ProfilerDebuggerPlugin { Data = data };
        AddDebuggerPlugin(debugger);
        dock = new ProfilerDock { Data = data, Debugger = debugger, Name = "DeepProfilerDock" };
        host = new EditorDock
        {
            Name = "DeepProfiler",
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
        if (host != null)
        {
            RemoveDock(host);
            host.QueueFree();
            host = null;
            dock = null;
        }
        if (debugger != null)
        {
            debugger.SendCommand(Protocol.CmdStop);
            RemoveDebuggerPlugin(debugger);
            debugger = null;
        }
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
        bool added = false;
        added |= Define("deep_profiler/runtime/enabled", true, Variant.Type.Bool);
        added |= Define("deep_profiler/runtime/capture_scopes", true, Variant.Type.Bool);
        added |= Define("deep_profiler/runtime/track_allocations", true, Variant.Type.Bool);
        added |= Define("deep_profiler/runtime/history_frames", 1800, Variant.Type.Int, PropertyHint.Range, "120,60000,60");
        added |= Define("deep_profiler/runtime/send_rate_hz", 10, Variant.Type.Int, PropertyHint.Range, "1,60,1");
        added |= Define("deep_profiler/runtime/spike_ms", 33.0, Variant.Type.Float, PropertyHint.Range, "4,500,0.5");
        added |= Define("deep_profiler/crawl/max_objects", 40000, Variant.Type.Int, PropertyHint.Range, "512,400000,512");
        added |= Define("deep_profiler/overlay/enabled", true, Variant.Type.Bool);
        added |= Define("deep_profiler/overlay/start_visible", false, Variant.Type.Bool);
        added |= Define("deep_profiler/overlay/hotkey", (int)Key.F3, Variant.Type.Int);
        if (added)
            ProjectSettings.Save();
    }

    private static bool Define(string path, Variant value, Variant.Type type, PropertyHint hint = PropertyHint.None, string hintString = "")
    {
        bool added = !ProjectSettings.HasSetting(path);
        if (added)
            ProjectSettings.SetSetting(path, value);
        ProjectSettings.SetInitialValue(path, value);
        ProjectSettings.AddPropertyInfo(new GDDict
        {
            { "name", path },
            { "type", (int)type },
            { "hint", (int)hint },
            { "hint_string", hintString },
        });
        return added;
    }
}
