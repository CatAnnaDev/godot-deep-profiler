using System;
using System.Collections.Generic;
using Godot;

namespace DeepProf;

[Tool]
public partial class GraphControl : Control
{
    public struct SeriesSpec
    {
        public int Field;
        public Color Color;
        public string Label;
        public bool Filled;
    }

    public FrameRing Ring;
    public readonly List<SeriesSpec> Series = new List<SeriesSpec>(8);
    public long Window = 600;
    public bool Follow = true;
    public bool ShowLegend = true;
    public bool ShowReadout = true;
    public bool ShowThresholds = true;
    private float fixedMax;

    public float FixedMax
    {
        get => fixedMax;
        set
        {
            fixedMax = value;
            Invalidate();
        }
    }
    public float MinRange = 1f;
    public FieldUnit Unit = FieldUnit.Milliseconds;
    public long Selected = -1;
    public Action<long> FrameSelected;
    public List<long> Markers;

    private float[] columnData = Array.Empty<float>();
    private Vector2[] linePoints = Array.Empty<Vector2>();
    private int cachedColumns;
    private int cachedSeries;
    private long cachedFirst = -1;
    private long cachedLast = -1;
    private long cachedTotal = -1;
    private float cachedMax;
    private float[] percentileScratch = Array.Empty<float>();
    private long hoverFrame = -1;
    private bool hovering;
    private long viewEnd = -1;

    private static readonly Color Background = new Color(0.09f, 0.10f, 0.13f);
    private static readonly Color GridColor = new Color(1f, 1f, 1f, 0.06f);
    private static readonly Color AxisColor = new Color(1f, 1f, 1f, 0.18f);
    private static readonly Color TextColor = new Color(0.72f, 0.75f, 0.82f);
    private static readonly Color SelectionColor = new Color(0.98f, 0.78f, 0.30f, 0.85f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(120, 90);
        ClipContents = true;
    }

    public void AddSeries(int field, Color color, string label, bool filled = false)
    {
        Series.Add(new SeriesSpec { Field = field, Color = color, Label = label, Filled = filled });
        Invalidate();
    }

    public void ClearSeries()
    {
        Series.Clear();
        Invalidate();
    }

    public void ResetView()
    {
        Follow = true;
        viewEnd = -1;
        Invalidate();
    }

    public void SetWindow(long frames)
    {
        Window = Math.Clamp(frames, 30, 60000);
        Invalidate();
    }

    public void Invalidate()
    {
        cachedFirst = -1;
        cachedLast = -1;
        cachedTotal = -1;
        QueueRedraw();
    }

    private Rect2 PlotArea()
    {
        float left = 64f;
        float top = ShowLegend ? 20f : 6f;
        float right = 6f;
        float bottom = 16f;
        return new Rect2(left, top, Math.Max(8f, Size.X - left - right), Math.Max(8f, Size.Y - top - bottom));
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (Ring == null)
            return;
        if (@event is InputEventMouseMotion motion)
        {
            hovering = true;
            hoverFrame = FrameAt(motion.Position);
            QueueRedraw();
        }
        else if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (button.ButtonIndex == MouseButton.Left)
            {
                Selected = FrameAt(button.Position);
                FrameSelected?.Invoke(Selected);
                QueueRedraw();
            }
            else if (button.ButtonIndex == MouseButton.WheelUp)
            {
                SetWindow((long)(Window * 0.8));
            }
            else if (button.ButtonIndex == MouseButton.WheelDown)
            {
                SetWindow((long)(Window * 1.25) + 1);
            }
            else if (button.ButtonIndex == MouseButton.Right)
            {
                Selected = -1;
                ResetView();
                FrameSelected?.Invoke(-1);
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
        {
            hovering = false;
            hoverFrame = -1;
            QueueRedraw();
        }
    }

    private long FrameAt(Vector2 position)
    {
        Rect2 area = PlotArea();
        RangeOf(out long first, out long last);
        if (last <= first)
            return -1;
        float t = Mathf.Clamp((position.X - area.Position.X) / area.Size.X, 0f, 1f);
        return first + (long)Math.Round(t * (last - 1 - first));
    }

    private void RangeOf(out long first, out long last)
    {
        last = Follow || viewEnd < 0 ? Ring.Total : Math.Min(viewEnd, Ring.Total);
        first = Math.Max(Ring.Oldest, last - Window);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Background);
        Rect2 area = PlotArea();
        Font font = GetThemeDefaultFont();
        int fontSize = Math.Max(9, GetThemeDefaultFontSize() - 3);

        if (Ring == null || Ring.Count == 0 || Series.Count == 0)
        {
            DrawString(font, new Vector2(area.Position.X + 6f, area.Position.Y + 16f), "no data", HorizontalAlignment.Left, -1, fontSize, TextColor);
            return;
        }

        RangeOf(out long first, out long last);
        long span = Math.Max(1, last - first);
        int columns = Math.Max(1, (int)area.Size.X);
        BuildColumns(first, last, span, columns);

        float maximum = Math.Max(FixedMax > 0f ? FixedMax : cachedMax, MinRange);
        DrawGrid(area, font, fontSize, maximum);

        for (int i = 0; i < Series.Count; i++)
            DrawSeries(i, area, columns, maximum);

        DrawMarkers(area, first, last, span);

        if (Selected >= first && Selected < last)
        {
            float x = area.Position.X + (Selected - first) / (float)span * area.Size.X;
            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), SelectionColor, 1f);
            DrawRect(new Rect2(x - 2f, area.Position.Y, 4f, 4f), SelectionColor);
        }

        if (hovering && hoverFrame >= first && hoverFrame < last)
            DrawHover(area, font, fontSize, first, span);

        if (ShowLegend)
            DrawLegend(area, font, fontSize);
    }

    private void BuildColumns(long first, long last, long span, int columns)
    {
        int series = Series.Count;
        int needed = series * columns * 2;
        if (columnData.Length < needed)
            columnData = new float[needed];
        if (linePoints.Length < columns)
            linePoints = new Vector2[columns];
        if (cachedColumns == columns && cachedSeries == series && cachedFirst == first && cachedLast == last && cachedTotal == Ring.Total)
            return;
        cachedColumns = columns;
        cachedSeries = series;
        cachedFirst = first;
        cachedLast = last;
        cachedTotal = Ring.Total;

        for (int i = 0; i < needed; i++)
            columnData[i] = float.NaN;
        float maximum = 0f;
        for (long frame = first; frame < last; frame++)
        {
            int column = (int)((frame - first) * columns / span);
            if (column < 0 || column >= columns)
                continue;
            for (int s = 0; s < series; s++)
            {
                float value = Ring.At(frame, Series[s].Field);
                if (value > maximum)
                    maximum = value;
                int index = (s * columns + column) * 2;
                if (float.IsNaN(columnData[index]))
                {
                    columnData[index] = value;
                    columnData[index + 1] = value;
                }
                else
                {
                    if (value > columnData[index]) columnData[index] = value;
                    if (value < columnData[index + 1]) columnData[index + 1] = value;
                }
            }
        }
        cachedMax = ScaleFor(maximum, series, columns);
    }

    private float ScaleFor(float maximum, int series, int columns)
    {
        int total = series * columns;
        if (percentileScratch.Length < total)
            percentileScratch = new float[total];
        int count = 0;
        for (int s = 0; s < series; s++)
        {
            for (int i = 0; i < columns; i++)
            {
                float value = columnData[(s * columns + i) * 2];
                if (!float.IsNaN(value))
                    percentileScratch[count++] = value;
            }
        }
        if (count < 24)
            return NiceCeil(maximum * 1.15f);
        Array.Sort(percentileScratch, 0, count);
        float percentile = percentileScratch[(int)(count * 0.95f)];
        float limit = NiceCeil(percentile * 1.4f);
        return maximum <= limit ? NiceCeil(maximum * 1.15f) : limit;
    }

    private static float NiceCeil(float value)
    {
        if (value <= 0f)
            return 1f;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double normalized = value / magnitude;
        double step = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return (float)(step * magnitude);
    }

    private void DrawGrid(Rect2 area, Font font, int fontSize, float maximum)
    {
        DrawRect(area, new Color(0f, 0f, 0f, 0.25f));
        const int lines = 4;
        for (int i = 0; i <= lines; i++)
        {
            float t = i / (float)lines;
            float y = area.End.Y - t * area.Size.Y;
            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y), i == 0 ? AxisColor : GridColor, 1f);
            DrawString(font, new Vector2(2f, y + fontSize * 0.35f), Fmt.Unit(maximum * t, Unit), HorizontalAlignment.Right, area.Position.X - 8f, fontSize, TextColor);
        }
        if (ShowThresholds && Unit == FieldUnit.Milliseconds)
        {
            DrawThreshold(area, maximum, 16.667f, new Color(0.45f, 0.85f, 0.55f, 0.35f));
            DrawThreshold(area, maximum, 33.333f, new Color(0.95f, 0.55f, 0.35f, 0.35f));
        }
    }

    private void DrawThreshold(Rect2 area, float maximum, float value, Color color)
    {
        if (value > maximum)
            return;
        float y = area.End.Y - value / maximum * area.Size.Y;
        DrawDashedLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y), color, 1f, 4f);
    }

    private void DrawSeries(int index, Rect2 area, int columns, float maximum)
    {
        SeriesSpec series = Series[index];
        Color fill = series.Color with { A = 0.22f };
        Color band = series.Color with { A = 0.55f };
        int origin = index * columns * 2;
        float scale = area.Size.Y / maximum;
        int points = 0;
        for (int i = 0; i < columns; i++)
        {
            float high = columnData[origin + i * 2];
            if (float.IsNaN(high))
                continue;
            float low = columnData[origin + i * 2 + 1];
            float x = area.Position.X + i;
            float yMax = area.End.Y - Math.Clamp(high * scale, 0f, area.Size.Y);
            float yMin = area.End.Y - Math.Clamp(low * scale, 0f, area.Size.Y);
            if (series.Filled)
                DrawLine(new Vector2(x, area.End.Y), new Vector2(x, yMax), fill, 1f);
            if (yMin - yMax > 1f)
                DrawLine(new Vector2(x, yMin), new Vector2(x, yMax), band, 1f);
            if (high > maximum)
                DrawRect(new Rect2(x - 1f, area.Position.Y, 3f, 3f), series.Color);
            linePoints[points++] = new Vector2(x, yMax);
        }
        if (points >= 2)
            DrawPolyline(new ReadOnlySpan<Vector2>(linePoints, 0, points), series.Color, 1.2f, true);
        else if (points == 1)
            DrawRect(new Rect2(linePoints[0] - new Vector2(1f, 1f), new Vector2(2f, 2f)), series.Color);
    }

    private void DrawMarkers(Rect2 area, long first, long last, long span)
    {
        if (Markers == null)
            return;
        Color color = new Color(0.95f, 0.45f, 0.85f, 0.5f);
        for (int i = 0; i < Markers.Count; i++)
        {
            long marker = Markers[i];
            if (marker < first || marker >= last)
                continue;
            float x = area.Position.X + (marker - first) / (float)span * area.Size.X;
            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), color, 1f);
        }
    }

    private void DrawLegend(Rect2 area, Font font, int fontSize)
    {
        float x = area.Position.X + 2f;
        float y = 12f;
        long last = Ring.Total - 1;
        for (int i = 0; i < Series.Count; i++)
        {
            SeriesSpec series = Series[i];
            DrawRect(new Rect2(x, y - 7f, 8f, 8f), series.Color);
            string label = series.Label + " " + Fmt.Unit(Ring.At(last, series.Field), Unit);
            DrawString(font, new Vector2(x + 12f, y), label, HorizontalAlignment.Left, -1, fontSize, TextColor);
            x += 14f + font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize).X + 8f;
            if (x > area.End.X - 40f)
                break;
        }
    }

    private void DrawHover(Rect2 area, Font font, int fontSize, long first, long span)
    {
        float x = area.Position.X + (hoverFrame - first) / (float)span * area.Size.X;
        DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), new Color(1f, 1f, 1f, 0.35f), 1f);
        if (!ShowReadout)
            return;

        float lineHeight = fontSize + 3f;
        float width = 140f;
        float height = (Series.Count + 1) * lineHeight + 8f;
        float boxX = x + 8f + width > area.End.X ? x - width - 8f : x + 8f;
        float boxY = Math.Min(area.Position.Y + 4f, Math.Max(0f, area.End.Y - height));
        Rect2 box = new Rect2(boxX, boxY, width, height);
        DrawRect(box, new Color(0.05f, 0.06f, 0.08f, 0.94f));
        DrawRect(box, new Color(1f, 1f, 1f, 0.14f), false, 1f);
        float textY = boxY + lineHeight;
        DrawString(font, new Vector2(boxX + 6f, textY), "frame " + hoverFrame, HorizontalAlignment.Left, width - 12f, fontSize, new Color(0.95f, 0.95f, 0.95f));
        for (int i = 0; i < Series.Count; i++)
        {
            textY += lineHeight;
            SeriesSpec series = Series[i];
            DrawRect(new Rect2(boxX + 6f, textY - 7f, 7f, 7f), series.Color);
            DrawString(font, new Vector2(boxX + 17f, textY), series.Label + "  " + Fmt.Unit(Ring.At(hoverFrame, series.Field), Unit),
                HorizontalAlignment.Left, width - 22f, fontSize, TextColor);
        }
    }
}
