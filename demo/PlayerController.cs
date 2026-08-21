using DeepProf;
using Godot;

public partial class PlayerController : CharacterBody3D
{
    [Export] public float WalkSpeed = 6.5f;
    [Export] public float SprintSpeed = 11f;
    [Export] public float Acceleration = 14f;
    [Export] public float JumpVelocity = 5.2f;
    [Export] public float MouseSensitivity = 0.0022f;
    [Export] public float AimRange = 80f;
    [Export] public float PushStrength = 9f;

    public Node3D AimedNode { get; private set; }
    public Vector3 AimPoint { get; private set; }
    public float AimDistance { get; private set; }
    public Camera3D Head => camera;

    private Node3D pivot;
    private Camera3D camera;
    private RayCast3D aim;
    private float pitch;
    private bool mouseCaptured;
    private float gravity = 9.8f;

    public override void _Ready()
    {
        Name = "Player";
        CollisionLayer = 2;
        CollisionMask = 1;

        CapsuleShape3D capsule = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f };
        CollisionShape3D shape = new CollisionShape3D { Shape = capsule, Name = "Body" };
        AddChild(shape);

        MeshInstance3D visual = new MeshInstance3D
        {
            Name = "Silhouette",
            Mesh = new CapsuleMesh { Radius = 0.4f, Height = 1.8f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.72f, 0.35f) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        AddChild(visual);

        pivot = new Node3D { Name = "Head", Position = new Vector3(0, 0.7f, 0) };
        AddChild(pivot);

        camera = new Camera3D { Name = "Eye", Fov = 74f, Far = 400f };
        pivot.AddChild(camera);

        aim = new RayCast3D
        {
            Name = "Aim",
            TargetPosition = new Vector3(0, 0, -AimRange),
            CollideWithAreas = true,
            CollideWithBodies = true,
            CollisionMask = 1,
        };
        camera.AddChild(aim);

        gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        using ProfScope scope = Prof.Scope("Player.Input");
        if (@event is InputEventMouseMotion motion && mouseCaptured)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);
            pitch = Mathf.Clamp(pitch - motion.Relative.Y * MouseSensitivity, -1.45f, 1.45f);
            pivot.Rotation = new Vector3(pitch, 0f, 0f);
            return;
        }
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Escape:
                    CaptureMouse(false);
                    break;
                case Key.E:
                    InspectAimed();
                    break;
                case Key.F:
                    HighlightAimed();
                    break;
            }
            return;
        }
        if (@event is InputEventMouseButton button && button.Pressed)
        {
            if (!mouseCaptured)
            {
                CaptureMouse(true);
                return;
            }
            if (button.ButtonIndex == MouseButton.Left)
                PushAimed();
            else if (button.ButtonIndex == MouseButton.Right)
                SpawnProjectile();
        }
    }

    private void CaptureMouse(bool capture)
    {
        mouseCaptured = capture;
        Input.MouseMode = capture ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }

    public override void _PhysicsProcess(double delta)
    {
        using (Prof.Scope("Player.Move"))
        {
            Vector3 velocity = Velocity;
            if (!IsOnFloor())
                velocity.Y -= gravity * (float)delta;
            else if (Input.IsPhysicalKeyPressed(Key.Space))
                velocity.Y = JumpVelocity;

            Vector2 input = new Vector2(
                (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.A) ? 1f : 0f),
                (Input.IsPhysicalKeyPressed(Key.S) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.W) ? 1f : 0f));
            Vector3 direction = (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized();
            float speed = Input.IsPhysicalKeyPressed(Key.Shift) ? SprintSpeed : WalkSpeed;
            Vector3 target = direction * speed;
            velocity.X = Mathf.MoveToward(velocity.X, target.X, Acceleration * (float)delta * 3f);
            velocity.Z = Mathf.MoveToward(velocity.Z, target.Z, Acceleration * (float)delta * 3f);
            Velocity = velocity;
            MoveAndSlide();
            Prof.Counter("player speed", new Vector2(velocity.X, velocity.Z).Length());
        }
    }

    public override void _Process(double delta)
    {
        using (Prof.Scope("Player.Aim"))
        {
            aim.ForceRaycastUpdate();
            if (aim.IsColliding())
            {
                AimedNode = aim.GetCollider() as Node3D;
                AimPoint = aim.GetCollisionPoint();
                AimDistance = GlobalPosition.DistanceTo(AimPoint);
            }
            else
            {
                AimedNode = null;
                AimDistance = 0f;
            }
        }
    }

    private void PushAimed()
    {
        if (AimedNode is RigidBody3D body)
        {
            Vector3 direction = -camera.GlobalTransform.Basis.Z;
            body.ApplyImpulse(direction * PushStrength, AimPoint - body.GlobalPosition);
            Prof.Event("push", body.Name + " at " + Fmt.Distance(AimDistance));
        }
    }

    private void SpawnProjectile()
    {
        using (Prof.Scope("Player.Shoot"))
        {
            RigidBody3D bullet = new RigidBody3D { Name = "Pellet", Mass = 0.6f };
            bullet.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.18f } });
            bullet.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.55f, 0.3f), EmissionEnabled = true, Emission = new Color(0.6f, 0.2f, 0.05f) },
            });
            GetParent().AddChild(bullet);
            bullet.GlobalPosition = camera.GlobalPosition - camera.GlobalTransform.Basis.Z * 1.2f;
            bullet.LinearVelocity = -camera.GlobalTransform.Basis.Z * 24f;
            GetTree().CreateTimer(8.0).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(bullet))
                    bullet.QueueFree();
            };
            Prof.CounterAdd("pellets fired", 1);
        }
    }

    private void InspectAimed()
    {
        if (AimedNode == null || ProfilerRuntime.Instance == null)
            return;
        CaptureMouse(false);
        ProfilerRuntime.Instance.InspectObject(AimedNode.GetInstanceId());
    }

    private void HighlightAimed()
    {
        if (AimedNode != null)
            ProfilerRuntime.Instance?.EnsureOverlay();
        if (AimedNode != null)
            ProfilerRuntime.Instance?.HighlightObject(AimedNode.GetInstanceId(), 4f);
    }
}
