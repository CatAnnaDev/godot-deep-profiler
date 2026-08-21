using System;
using System.Collections.Generic;
using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class EventPane : VBoxContainer
{
    public ProfilerData Data;
    public Action<long> FramePicked;

    private Tree tree;
    private LineEdit filter;
    private Label summary;
    private int shownCount;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button clear = new Button { Text = "Clear" };
        clear.Pressed += () =>
        {
            Data?.Events.Clear();
            Data?.EventFrames.Clear();
            Refresh();
        };
        bar.AddChild(clear);

        filter = new LineEdit { PlaceholderText = "filter", CustomMinimumSize = new Vector2(140, 0) };
        filter.TextChanged += _ => Refresh();
        bar.AddChild(filter);

        summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeFontSizeOverride("font_size", 10);
        summary.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(summary);

        tree = new Tree
        {
            Columns = 3,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        tree.SetColumnTitle(0, "Frame");
        tree.SetColumnTitle(1, "Kind");
        tree.SetColumnTitle(2, "Detail");
        tree.SetColumnExpandRatio(0, 1);
        tree.SetColumnExpandRatio(1, 1);
        tree.SetColumnExpandRatio(2, 8);
        tree.SetColumnCustomMinimumWidth(0, 70);
        tree.ItemSelected += OnSelected;
        AddChild(tree);
    }

    public void Refresh()
    {
        if (Data == null || tree == null)
            return;
        if (shownCount == Data.Events.Count && filter.Text.Length == 0)
            return;
        shownCount = Data.Events.Count;
        tree.Clear();
        TreeItem root = tree.CreateItem();
        string needle = filter.Text.Trim();
        int shown = 0;
        for (int i = Data.Events.Count - 1; i >= 0; i--)
        {
            GDDict row = Data.Events[i];
            string kind = row["kind"].AsString();
            string detail = row["detail"].AsString();
            string name = row.TryGetValue("name", out Variant nameValue) ? nameValue.AsString() : kind;
            if (needle.Length > 0 && !kind.Contains(needle, StringComparison.OrdinalIgnoreCase) && !detail.Contains(needle, StringComparison.OrdinalIgnoreCase) && !name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            TreeItem item = tree.CreateItem(root);
            long frame = row["frame"].AsInt64();
            item.SetText(0, frame.ToString());
            item.SetText(1, kind);
            item.SetText(2, kind == "mark" ? name + " " + detail : detail);
            item.SetCustomColor(1, ColorOf(kind));
            item.SetCustomColor(0, DimColor);
            item.SetMetadata(0, frame);
            shown++;
            if (shown > 300)
                break;
        }
        summary.Text = shown + " events";
    }

    private static Color ColorOf(string kind)
    {
        switch (kind)
        {
            case "spike": return new Color(0.95f, 0.55f, 0.45f);
            case "gc": return new Color(0.85f, 0.65f, 0.95f);
            case "mark": return new Color(0.55f, 0.85f, 0.95f);
            default: return new Color(0.75f, 0.78f, 0.85f);
        }
    }

    private void OnSelected()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Int)
            FramePicked?.Invoke(meta.AsInt64());
    }
}
