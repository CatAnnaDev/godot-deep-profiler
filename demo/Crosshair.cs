using Godot;

public partial class Crosshair : Control
{
    public bool Locked;

    private static readonly Color Idle = new Color(0.9f, 0.92f, 0.96f, 0.65f);
    private static readonly Color Hot = new Color(0.98f, 0.78f, 0.30f, 0.95f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Draw()
    {
        Vector2 center = GetViewportRect().Size * 0.5f;
        Color color = Locked ? Hot : Idle;
        float gap = Locked ? 6f : 4f;
        float length = 7f;
        DrawLine(center + new Vector2(-gap - length, 0), center + new Vector2(-gap, 0), color, 1.5f);
        DrawLine(center + new Vector2(gap, 0), center + new Vector2(gap + length, 0), color, 1.5f);
        DrawLine(center + new Vector2(0, -gap - length), center + new Vector2(0, -gap), color, 1.5f);
        DrawLine(center + new Vector2(0, gap), center + new Vector2(0, gap + length), color, 1.5f);
        DrawCircle(center, 1.2f, color);
    }
}
