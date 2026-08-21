using System;
using System.Collections.Generic;
using Godot;

namespace DeepProf;

[Tool]
public partial class FlameChart : Control
{
    public ScopeView View;
    public int RootIndex;
    public float RowHeight = 17f;
    public Action<int> NodeSelected;
    public bool SelfHeat = true;
    public bool FitRoot = true;

    private readonly List<Rect2> hitRects = new List<Rect2>(256);
    private readonly List<int> hitNodes = new List<int>(256);
    private int hovered = -1;
    private Vector2 mousePosition;

    private static readonly Color Background = new Color(0.09f, 0.10f, 0.13f);
    private static readonly Color TextColor = new Color(0.06f, 0.07f, 0.09f);
    private static readonly Color OutlineColor = new Color(0f, 0f, 0f, 0.35f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(160, 120);
        ClipContents = true;
    }

    public void SetView(ScopeView view)
    {
        View = view;
        RootIndex = 0;
        hovered = -1;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            mousePosition = motion.Position;
            int found = Pick(motion.Position);
            if (found != hovered)
            {
                hovered = found;
                QueueRedraw();
            }
            else
            {
                QueueRedraw();
            }
        }
        else if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (button.ButtonIndex == MouseButton.Left)
            {
                int found = Pick(button.Position);
                if (found >= 0)
                {
                    RootIndex = found;
                    NodeSelected?.Invoke(found);
                    QueueRedraw();
                }
            }
            else if (button.ButtonIndex == MouseButton.Right)
            {
                if (View != null && RootIndex > 0 && RootIndex < View.Count)
                {
                    RootIndex = Math.Max(0, View.Parent[RootIndex]);
                    NodeSelected?.Invoke(RootIndex);
                    QueueRedraw();
                }
            }
        }
    }

    private int Pick(Vector2 position)
    {
        for (int i = hitRects.Count - 1; i >= 0; i--)
        {
            if (hitRects[i].HasPoint(position))
                return hitNodes[i];
        }
        return -1;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Background);
        hitRects.Clear();
        hitNodes.Clear();
        Font font = GetThemeDefaultFont();
        int fontSize = Math.Max(9, GetThemeDefaultFontSize() - 3);

        if (View == null || View.Count == 0)
        {
            DrawString(font, new Vector2(8f, 20f), "no scope data captured", HorizontalAlignment.Left, -1, fontSize, new Color(0.6f, 0.63f, 0.7f));
            return;
        }
        if (RootIndex < 0 || RootIndex >= View.Count)
            RootIndex = 0;

        double rootTotal = View.Total[RootIndex];
        if (rootTotal <= 0.0)
            rootTotal = 1.0;

        float top = 2f;
        if (RootIndex != 0)
        {
            string crumb = "< " + BreadcrumbOf(RootIndex);
            DrawString(font, new Vector2(6f, 12f), crumb, HorizontalAlignment.Left, Size.X - 12f, fontSize, new Color(0.7f, 0.74f, 0.82f));
            top = 16f;
        }

        DrawNode(RootIndex, 0f, Size.X, top, rootTotal, font, fontSize, 0);

        if (hovered >= 0 && hovered < View.Count)
            DrawTooltip(font, fontSize, rootTotal);
    }

    private string BreadcrumbOf(int index)
    {
        List<string> parts = new List<string>(8);
        int current = index;
        int guard = 0;
        while (current > 0 && guard++ < 32)
        {
            current = View.Parent[current];
            if (current < 0)
                break;
            parts.Add(View.NameOf(current));
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private void DrawNode(int index, float x, float width, float y, double rootTotal, Font font, int fontSize, int depth)
    {
        if (width < 0.75f || y > Size.Y)
            return;
        double total = View.Total[index];
        double ratio = View.Total[RootIndex] > 0.0 ? View.Self[index] / Math.Max(0.0001, View.Total[RootIndex]) : 0.0;
        Color color = SelfHeat ? BlendHeat(index, ratio) : ColorFor(index);
        Rect2 rect = new Rect2(x, y, Math.Max(1f, width - 1f), RowHeight - 1f);
        DrawRect(rect, color);
        if (index == hovered)
            DrawRect(rect, new Color(1f, 1f, 1f, 0.85f), false, 1f);
        else
            DrawRect(rect, OutlineColor, false, 1f);
        hitRects.Add(rect);
        hitNodes.Add(index);

        double childrenTotal = 0.0;
        for (int child = View.FirstChild[index]; child >= 0; child = View.NextSibling[child])
            childrenTotal += View.Total[child];
        int frames = Math.Max(1, View.Frames);
        bool fitting = index == RootIndex && FitRoot && childrenTotal > 0.0;

        if (width > 34f)
        {
            string label = View.NameOf(index);
            string suffix = " " + Fmt.Ms(total / frames);
            if (fitting)
                suffix += "   instrumented " + Fmt.Ms(childrenTotal / frames);
            float available = width - 8f;
            string text = label + suffix;
            if (font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X > available)
                text = label;
            DrawString(font, new Vector2(x + 4f, y + RowHeight - 5f), text, HorizontalAlignment.Left, available, fontSize, TextColor);
        }

        float childY = y + RowHeight;
        if (childY > Size.Y)
            return;
        double scaleBase = fitting ? childrenTotal : Math.Max(total, childrenTotal);
        if (scaleBase <= 0.0)
            return;
        float cursor = x;
        for (int child = View.FirstChild[index]; child >= 0; child = View.NextSibling[child])
        {
            float childWidth = (float)(View.Total[child] / scaleBase * width);
            DrawNode(child, cursor, childWidth, childY, rootTotal, font, fontSize, depth + 1);
            cursor += childWidth;
        }
    }

    private static Color ColorFor(int index)
    {
        float hue = (index * 0.147f + 0.08f) % 1f;
        return Color.FromHsv(hue, 0.42f, 0.86f);
    }

    private Color BlendHeat(int index, double ratio)
    {
        Color baseColor = ColorFor(View.NameId[index]);
        Color heat = Fmt.HeatColor(ratio * 3.0);
        return baseColor.Lerp(heat, (float)Math.Clamp(ratio * 2.5, 0.0, 0.8));
    }

    private void DrawTooltip(Font font, int fontSize, double rootTotal)
    {
        int index = hovered;
        int frames = Math.Max(1, View.Frames);
        string[] lines =
        {
            View.NameOf(index),
            "total " + Fmt.Ms(View.Total[index] / frames) + "   " + Fmt.Percent(View.Total[index] / Math.Max(0.0001, rootTotal)),
            "self  " + Fmt.Ms(View.Self[index] / frames),
            "calls " + Fmt.Count(View.Calls[index] / (double)frames) + " per frame",
            "alloc " + Fmt.Bytes(View.Alloc[index] / frames),
        };
        float width = 0f;
        for (int i = 0; i < lines.Length; i++)
            width = Math.Max(width, font.GetStringSize(lines[i], HorizontalAlignment.Left, -1, fontSize).X);
        width += 14f;
        float lineHeight = fontSize + 4f;
        float height = lines.Length * lineHeight + 8f;
        float x = mousePosition.X + 14f + width > Size.X ? Math.Max(0f, mousePosition.X - width - 8f) : mousePosition.X + 14f;
        float y = Math.Min(mousePosition.Y + 10f, Math.Max(0f, Size.Y - height));
        Rect2 box = new Rect2(x, y, width, height);
        DrawRect(box, new Color(0.05f, 0.06f, 0.08f, 0.95f));
        DrawRect(box, new Color(1f, 1f, 1f, 0.16f), false, 1f);
        for (int i = 0; i < lines.Length; i++)
        {
            Color color = i == 0 ? new Color(0.98f, 0.98f, 1f) : new Color(0.74f, 0.78f, 0.86f);
            DrawString(font, new Vector2(x + 7f, y + lineHeight * (i + 1)), lines[i], HorizontalAlignment.Left, width - 14f, fontSize, color);
        }
    }
}
