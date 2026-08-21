using System;
using System.Collections.Generic;
using Godot;

namespace DeepProf;

public partial class HighlightLayer : Control
{
    private struct Target
    {
        public ulong Id;
        public double Remaining;
    }

    private readonly List<Target> targets = new List<Target>(4);

    private static readonly Color OutlineColor = new Color(1f, 0.85f, 0.25f);
    private static readonly Color FillColor = new Color(1f, 0.85f, 0.25f, 0.12f);
    private static readonly Color LabelBackground = new Color(0.05f, 0.06f, 0.08f, 0.85f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 4096;
    }

    public void Add(ulong id, float seconds)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].Id != id)
                continue;
            targets[i] = new Target { Id = id, Remaining = seconds };
            return;
        }
        targets.Add(new Target { Id = id, Remaining = seconds });
    }

    public void Tick(double delta)
    {
        if (targets.Count == 0)
            return;
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            Target target = targets[i];
            target.Remaining -= delta;
            if (target.Remaining <= 0.0)
                targets.RemoveAt(i);
            else
                targets[i] = target;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (targets.Count == 0)
            return;
        Font font = GetThemeDefaultFont();
        int fontSize = Math.Max(10, GetThemeDefaultFontSize() - 2);
        foreach (Target target in targets)
        {
            GodotObject obj = ObjectGraph.Resolve(target.Id);
            if (obj is not Node node || !node.IsInsideTree())
                continue;
            if (!TryProject(node, out Rect2 rect))
                continue;
            float pulse = 0.55f + 0.45f * Mathf.Sin((float)(Time.GetTicksMsec() * 0.006));
            DrawRect(rect, FillColor);
            DrawRect(rect, OutlineColor with { A = pulse }, false, 2f);
            string label = node.Name + " <" + node.GetClass() + ">";
            Vector2 size = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
            Vector2 origin = new Vector2(rect.Position.X, Math.Max(12f, rect.Position.Y - 6f));
            DrawRect(new Rect2(origin - new Vector2(3f, fontSize), size + new Vector2(6f, 5f)), LabelBackground);
            DrawString(font, origin, label, HorizontalAlignment.Left, -1, fontSize, OutlineColor);
        }
    }

    private bool TryProject(Node node, out Rect2 rect)
    {
        rect = default;
        switch (node)
        {
            case Control control:
            {
                rect = control.GetGlobalRect();
                return rect.Size.LengthSquared() > 0.01f;
            }
            case Node2D node2d:
            {
                Transform2D transform = node2d.GetGlobalTransformWithCanvas();
                Vector2 center = transform.Origin;
                Vector2 extent = new Vector2(24f, 24f) * transform.Scale.Abs();
                rect = new Rect2(center - extent, extent * 2f);
                return true;
            }
            case Node3D node3d:
            {
                Viewport viewport = node3d.GetViewport();
                Camera3D camera = viewport?.GetCamera3D();
                if (camera == null)
                    return false;
                Aabb bounds = BoundsOf(node3d);
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 max = new Vector2(float.MinValue, float.MinValue);
                bool any = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = node3d.GlobalTransform * bounds.GetEndpoint(i);
                    if (camera.IsPositionBehind(corner))
                        continue;
                    Vector2 point = camera.UnprojectPosition(corner);
                    min = min.Min(point);
                    max = max.Max(point);
                    any = true;
                }
                if (!any)
                    return false;
                rect = new Rect2(min, max - min).Grow(4f);
                return true;
            }
            default:
                return false;
        }
    }

    private static Aabb BoundsOf(Node3D node)
    {
        if (node is VisualInstance3D visual)
            return visual.GetAabb();
        return new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.One);
    }
}
