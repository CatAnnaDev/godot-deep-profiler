using System;
using Godot;

namespace DeepProf;

[Tool]
public partial class StatGrid : GridContainer
{
    private Label[] valueLabels = Array.Empty<Label>();
    private Label[] nameLabels = Array.Empty<Label>();
    private int fontSize = 11;

    private static readonly Color NameColor = new Color(0.62f, 0.66f, 0.75f);
    private static readonly Color ValueColor = new Color(0.93f, 0.94f, 0.97f);

    public void Configure(string[] labels, int pairsPerRow, int size = 11)
    {
        fontSize = size;
        foreach (Node child in GetChildren())
            child.QueueFree();
        Columns = Math.Max(1, pairsPerRow) * 2;
        AddThemeConstantOverride("h_separation", 10);
        AddThemeConstantOverride("v_separation", 2);
        valueLabels = new Label[labels.Length];
        nameLabels = new Label[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            Label name = new Label
            {
                Text = labels[i],
                HorizontalAlignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            name.AddThemeFontSizeOverride("font_size", fontSize);
            name.AddThemeColorOverride("font_color", NameColor);
            AddChild(name);
            nameLabels[i] = name;

            Label value = new Label
            {
                Text = "-",
                HorizontalAlignment = HorizontalAlignment.Right,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            value.AddThemeFontSizeOverride("font_size", fontSize);
            value.AddThemeColorOverride("font_color", ValueColor);
            AddChild(value);
            valueLabels[i] = value;
        }
    }

    public void SetValue(int index, string text)
    {
        if (index >= 0 && index < valueLabels.Length && valueLabels[index] != null)
            valueLabels[index].Text = text;
    }

    public void SetValue(int index, string text, Color color)
    {
        if (index < 0 || index >= valueLabels.Length || valueLabels[index] == null)
            return;
        valueLabels[index].Text = text;
        valueLabels[index].AddThemeColorOverride("font_color", color);
    }


}
