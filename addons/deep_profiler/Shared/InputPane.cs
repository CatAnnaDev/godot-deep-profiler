using System;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class InputPane : VBoxContainer
{
    public ProfilerData Data;
    public IGraphSource Source;

    private Tree classes;
    private Tree settings;
    private Label summary;
    private CheckButton trackToggle;
    private CheckButton accumulateToggle;
    private Label advice;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color HotColor = new Color(0.95f, 0.72f, 0.45f);
    private static readonly Color WarnColor = new Color(0.95f, 0.55f, 0.45f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        trackToggle = new CheckButton { Text = "Track input", ButtonPressed = true, TooltipText = "Count the events dispatched and the engine objects alive while they are handled" };
        trackToggle.Toggled += value => Source?.SetTrackInput(value);
        bar.AddChild(trackToggle);

        accumulateToggle = new CheckButton
        {
            Text = "Accumulate motion",
            ButtonPressed = true,
            TooltipText = "Merge every mouse motion sample of a frame into a single event. Toggle it and watch the numbers move",
        };
        accumulateToggle.Toggled += value => Source?.SetAccumulateInput(value);
        bar.AddChild(accumulateToggle);

        summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        Label hint = new Label
        {
            Text = "Input events are created and released inside the same frame, so a frame to frame object count never shows them. This tab counts what the engine dispatches and samples the live object count while the events are handled. Every viewport an event crosses makes one transformed copy of it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.AddThemeColorOverride("font_color", DimColor);
        AddChild(hint);

        advice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        advice.AddThemeFontSizeOverride("font_size", 11);
        advice.AddThemeColorOverride("font_color", HotColor);
        AddChild(advice);

        HSplitContainer split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        classes = new Tree
        {
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        classes.SetColumnTitle(0, "Event class");
        classes.SetColumnTitle(1, "Dispatched");
        classes.SetColumnTitle(2, "Per second");
        classes.SetColumnTitle(3, "Share");
        classes.SetColumnExpandRatio(0, 4);
        classes.SetColumnCustomMinimumWidth(0, 150);
        for (int i = 1; i <= 3; i++)
        {
            classes.SetColumnExpandRatio(i, 1);
            classes.SetColumnCustomMinimumWidth(i, 70);
        }
        split.AddChild(classes);

        settings = new Tree
        {
            Columns = 2,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(300, 0),
        };
        settings.SetColumnTitle(0, "Dispatch");
        settings.SetColumnTitle(1, "Value");
        settings.SetColumnExpandRatio(0, 3);
        settings.SetColumnExpandRatio(1, 2);
        split.AddChild(settings);
    }

    public void Refresh()
    {
        if (Data == null || classes == null)
            return;
        if (!IsVisibleInTree())
            return;
        classes.Clear();
        settings.Clear();
        if (!Data.Heap.TryGetValue("input", out Variant inputValue))
        {
            summary.Text = "waiting for the first window";
            return;
        }
        GDDict input = inputValue.AsGodotDictionary();
        int events = input["events"].AsInt32();
        int peak = input["peak"].AsInt32();
        double seconds = Data.Heap.TryGetValue("seconds", out Variant secondsValue) ? Math.Max(0.001, secondsValue.AsDouble()) : 1.0;
        double window = Math.Min(seconds, 1.0);

        TreeItem root = classes.CreateItem();
        GDArray rows = input["classes"].AsGodotArray();
        foreach (Variant entry in rows)
        {
            GDDict row = entry.AsGodotDictionary();
            int count = row["count"].AsInt32();
            TreeItem item = classes.CreateItem(root);
            item.SetText(0, row["n"].AsString());
            item.SetText(1, Fmt.Count(count));
            item.SetText(2, Fmt.Count(count / window));
            item.SetText(3, events > 0 ? Fmt.Percent(count / (double)events) : string.Empty);
            for (int i = 1; i <= 3; i++)
                item.SetTextAlignment(i, HorizontalAlignment.Right);
            item.SetCustomColor(2, count / window > 240.0 ? WarnColor : HotColor);
            item.SetCustomColor(3, DimColor);
        }

        long lastFrame = Data.Frames.Total - 1;
        float perFrame = Data.Frames.Count > 0 ? Data.Frames.Average(Protocol.FInputEvents, lastFrame - 120, lastFrame + 1) : 0f;
        float objectsPerFrame = Data.Frames.Count > 0 ? Data.Frames.Average(Protocol.FObjectsInput, lastFrame - 120, lastFrame + 1) : 0f;
        summary.Text = Fmt.Count(events / window) + " events per second   "
                       + Fmt.Number(Math.Round(perFrame, 1)) + " per frame   peak "
                       + Fmt.Count(peak) + " engine objects alive while dispatching";

        TreeItem settingsRoot = settings.CreateItem();
        bool accumulated = input["accumulated"].AsBool();
        if (accumulateToggle.ButtonPressed != accumulated)
            accumulateToggle.SetPressedNoSignal(accumulated);
        advice.Text = Diagnose(input, accumulated, perFrame, objectsPerFrame);
        Row(settingsRoot, "events per frame", Fmt.Number(Math.Round(perFrame, 1)));
        Row(settingsRoot, "objects during input", Fmt.Number(Math.Round(objectsPerFrame, 1)));
        Row(settingsRoot, "peak objects in window", Fmt.Count(peak));
        Row(settingsRoot, "accumulated input", accumulated ? "on" : "off", accumulated ? DimColor : WarnColor);
        Row(settingsRoot, "agile event flushing", input["agile"].AsBool() ? "on" : "off");
        Row(settingsRoot, "viewports in the input path", Fmt.Count(input["viewports"].AsInt32()));
    }

    private static string Diagnose(GDDict input, bool accumulated, float perFrame, float objectsPerFrame)
    {
        int viewports = input["viewports"].AsInt32();
        if (perFrame <= 0.01f)
            return "No input is being dispatched right now. Move the mouse to measure.";
        if (!accumulated)
            return "Accumulated input is off, so every sample your mouse sends becomes its own event and its own object. Turning it back on collapses them to one event per frame.";
        if (input["agile"].AsBool())
            return "Agile event flushing dispatches input again on every physics tick. That multiplies the events by the number of physics steps per frame.";
        if (perFrame > 2.5f)
            return "More than two events per frame while accumulation is on. Non motion events, several devices or a high physics rate are adding up.";
        if (objectsPerFrame > viewports + 2)
            return "Each event is costing more objects than the viewport count explains. Something in the input path is copying it further, for example a Control that re-emits or forwards events.";
        if (viewports > 1)
            return "Every viewport an event crosses makes one transformed copy. Set gui_disable_input on the viewports that do not need mouse input.";
        return "Input dispatch looks healthy. One event per frame is the floor with accumulation on. Consume the motion in _input and call get_viewport().set_input_as_handled() when the mouse is captured to stop the GUI pass as well.";
    }

    private void Row(TreeItem parent, string name, string value)
    {
        Row(parent, name, value, null);
    }

    private void Row(TreeItem parent, string name, string value, Color? color)
    {
        TreeItem item = settings.CreateItem(parent);
        item.SetText(0, name);
        item.SetText(1, value);
        item.SetTextAlignment(1, HorizontalAlignment.Right);
        item.SetCustomColor(0, DimColor);
        if (color.HasValue)
            item.SetCustomColor(1, color.Value);
    }
}
