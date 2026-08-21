using System;
using System.Collections.Generic;
using DeepProf;
using Godot;

public partial class DemoWorld : Node3D
{
    [Export] public int SpinnerCount = 240;
    [Export] public int CrateCount = 28;
    [Export] public int BarrelCount = 18;

    private readonly List<MeshInstance3D> spinners = new List<MeshInstance3D>(256);
    private readonly List<Node3D> sparks = new List<Node3D>(64);
    private readonly Random random = new Random(7);
    private readonly Dictionary<int, byte[]> leakedBuffers = new Dictionary<int, byte[]>(4096);
    private readonly List<Node> leakedNodes = new List<Node>(512);
    private static readonly ProfMarker SpinMarker = Prof.Marker("World.Spinners");

    private PlayerController player;
    private Node3D spinnerRoot;
    private Node3D sparkRoot;
    private Label statusLabel;
    private Label aimLabel;
    private Crosshair crosshair;
    private double elapsed;
    private int frame;
    private bool leaking;
    private int leakCounter;

    public override void _Ready()
    {
        Name = "DemoWorld";
        BuildEnvironment();
        BuildGround();
        BuildSpinners();
        BuildCrates();
        BuildBarrels();
        BuildPlayer();
        BuildHud();

        Timer churn = new Timer { Name = "SparkTimer", WaitTime = 0.6, Autostart = true };
        churn.Timeout += OnChurn;
        AddChild(churn);

        Prof.Event("world ready", SpinnerCount + " spinners, " + CrateCount + " crates, " + BarrelCount + " barrels");
    }

    private void BuildEnvironment()
    {
        DirectionalLight3D sun = new DirectionalLight3D
        {
            Name = "Sun",
            Rotation = new Vector3(-1.05f, 0.7f, 0f),
            ShadowEnabled = true,
            LightEnergy = 1.1f,
        };
        AddChild(sun);

        ProceduralSkyMaterial sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.16f, 0.21f, 0.33f),
            SkyHorizonColor = new Color(0.32f, 0.34f, 0.40f),
            GroundHorizonColor = new Color(0.16f, 0.16f, 0.18f),
            GroundBottomColor = new Color(0.07f, 0.07f, 0.09f),
        };
        WorldEnvironment environment = new WorldEnvironment
        {
            Name = "Environment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.85f,
                SsaoEnabled = true,
                FogEnabled = true,
                FogDensity = 0.004f,
                FogLightColor = new Color(0.14f, 0.16f, 0.22f),
            },
        };
        AddChild(environment);
    }

    private void BuildGround()
    {
        StaticBody3D ground = new StaticBody3D { Name = "Ground", CollisionLayer = 1 };
        ground.AddChild(new CollisionShape3D
        {
            Name = "Shape",
            Shape = new BoxShape3D { Size = new Vector3(80, 1, 80) },
        });
        StandardMaterial3D checker = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.22f, 0.24f, 0.28f),
            Roughness = 0.9f,
            ResourceName = "ground",
        };
        ground.AddChild(new MeshInstance3D
        {
            Name = "Surface",
            Mesh = new BoxMesh { Size = new Vector3(80, 1, 80) },
            MaterialOverride = checker,
        });
        ground.Position = new Vector3(0, -0.5f, 0);
        AddChild(ground);
    }

    private void BuildSpinners()
    {
        spinnerRoot = new Node3D { Name = "Spinners" };
        AddChild(spinnerRoot);

        BoxMesh mesh = new BoxMesh { Size = new Vector3(0.6f, 0.6f, 0.6f), ResourceName = "spinner cube" };
        StandardMaterial3D shared = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.42f, 0.58f, 0.88f),
            Roughness = 0.5f,
            ResourceName = "spinner blue",
        };
        StandardMaterial3D textured = new StandardMaterial3D
        {
            AlbedoTexture = MakeNoiseTexture(256),
            ResourceName = "spinner textured",
        };

        for (int i = 0; i < SpinnerCount; i++)
        {
            float angle = i * 0.31f;
            float radius = 6f + i % 24 * 0.85f;
            MeshInstance3D cube = new MeshInstance3D
            {
                Name = "Spinner" + i,
                Mesh = mesh,
                Position = new Vector3(Mathf.Cos(angle) * radius, 1.2f + i % 5 * 0.8f, Mathf.Sin(angle) * radius),
                MaterialOverride = i % 19 == 0 ? textured : shared,
            };
            spinnerRoot.AddChild(cube);
            spinners.Add(cube);
        }
    }

    private void BuildCrates()
    {
        Node3D crates = new Node3D { Name = "Crates" };
        AddChild(crates);

        BoxMesh mesh = new BoxMesh { Size = new Vector3(1.2f, 1.2f, 1.2f), ResourceName = "crate" };
        for (int i = 0; i < CrateCount; i++)
        {
            StaticBody3D crate = new StaticBody3D { Name = "Crate" + i, CollisionLayer = 1 };
            crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.2f, 1.2f, 1.2f) } });
            crate.AddChild(new MeshInstance3D
            {
                Name = "Visual",
                Mesh = mesh,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.35f + random.NextSingle() * 0.4f, 0.30f, 0.22f),
                    Roughness = 0.75f,
                    ResourceName = "crate paint " + i,
                },
            });
            float angle = i * 0.72f;
            float radius = 4f + i % 7 * 2.2f;
            crate.Position = new Vector3(Mathf.Cos(angle) * radius, 0.6f + (i % 3) * 1.2f, Mathf.Sin(angle) * radius);
            crates.AddChild(crate);
        }
    }

    private void BuildBarrels()
    {
        Node3D barrels = new Node3D { Name = "Barrels" };
        AddChild(barrels);

        CylinderMesh mesh = new CylinderMesh { TopRadius = 0.45f, BottomRadius = 0.45f, Height = 1.1f, ResourceName = "barrel" };
        StandardMaterial3D metal = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.62f, 0.68f),
            Metallic = 0.7f,
            Roughness = 0.35f,
            ResourceName = "barrel metal",
        };
        for (int i = 0; i < BarrelCount; i++)
        {
            RigidBody3D barrel = new RigidBody3D { Name = "Barrel" + i, Mass = 3f, CollisionLayer = 1 };
            barrel.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.45f, Height = 1.1f } });
            barrel.AddChild(new MeshInstance3D { Name = "Visual", Mesh = mesh, MaterialOverride = metal });
            float spread = i * 0.83f;
            barrel.Position = new Vector3(Mathf.Cos(spread) * (3f + i % 5 * 2.4f), 1.6f + i * 0.15f, Mathf.Sin(spread) * (3f + i % 5 * 2.4f));
            barrels.AddChild(barrel);
        }
    }

    private void BuildPlayer()
    {
        player = new PlayerController();
        AddChild(player);
        player.GlobalPosition = new Vector3(0, 1.4f, 20f);
    }

    private void BuildHud()
    {
        CanvasLayer layer = new CanvasLayer { Name = "Hud" };
        AddChild(layer);

        crosshair = new Crosshair { Name = "Crosshair" };
        layer.AddChild(crosshair);

        statusLabel = new Label
        {
            Name = "Status",
            Position = new Vector2(18, 14),
            Text = string.Empty,
        };
        statusLabel.AddThemeColorOverride("font_color", new Color(0.86f, 0.89f, 0.95f));
        statusLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        statusLabel.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(statusLabel);

        aimLabel = new Label
        {
            Name = "AimInfo",
            Position = new Vector2(18, 132),
            Text = string.Empty,
        };
        aimLabel.AddThemeColorOverride("font_color", new Color(0.98f, 0.82f, 0.45f));
        aimLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        aimLabel.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(aimLabel);
    }

    private static ImageTexture MakeNoiseTexture(int size)
    {
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float value = (Mathf.Sin(x * 0.11f) * Mathf.Cos(y * 0.08f) + 1f) * 0.5f;
                image.SetPixel(x, y, new Color(value, value * 0.55f, 1f - value));
            }
        }
        ImageTexture texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = "procedural noise";
        return texture;
    }

    private void OnChurn()
    {
        using (Prof.Scope("World.Churn"))
        {
            sparkRoot ??= CreateSparkRoot();
            for (int i = sparks.Count - 1; i >= 0; i--)
            {
                sparks[i].QueueFree();
                sparks.RemoveAt(i);
            }
            for (int i = 0; i < 10; i++)
            {
                Node3D spark = new Node3D { Name = "Spark" + i };
                spark.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.16f, Height = 0.32f },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.4f, 0.9f, 0.7f),
                        EmissionEnabled = true,
                        Emission = new Color(0.1f, 0.5f, 0.35f),
                    },
                });
                spark.Position = new Vector3(random.NextSingle() * 16f - 8f, 2.5f + random.NextSingle() * 2f, random.NextSingle() * 16f - 8f);
                sparkRoot.AddChild(spark);
                sparks.Add(spark);
            }
            Prof.Counter("sparks", sparks.Count);
        }
    }

    private Node3D CreateSparkRoot()
    {
        Node3D root = new Node3D { Name = "Sparks" };
        AddChild(root);
        return root;
    }

    public override void _Process(double delta)
    {
        frame++;
        elapsed += delta;
        using (Prof.Scope("World.Update"))
        {
            using (Prof.Scope(SpinMarker))
            {
                float time = (float)elapsed;
                for (int i = 0; i < spinners.Count; i++)
                {
                    MeshInstance3D cube = spinners[i];
                    Vector3 position = cube.Position;
                    position.Y = 1.2f + Mathf.Sin(time * 1.1f + i * 0.19f) * 0.8f + i % 5 * 0.8f;
                    cube.Position = position;
                    cube.RotateY((float)delta * (0.5f + i % 7 * 0.06f));
                }
            }

            using (Prof.Scope("World.Garbage"))
            {
                if (frame % 4 == 0)
                {
                    List<byte[]> waste = new List<byte[]>(12);
                    for (int i = 0; i < 12; i++)
                        waste.Add(new byte[4096]);
                    Prof.Counter("garbage kb", waste.Count * 4);
                }
            }

            if (leaking && frame % 2 == 0)
                LeakStep();

            using (Prof.Scope("World.Hud"))
                UpdateHud();

            if (frame % 173 == 0)
            {
                using (Prof.Scope("World.Hitch"))
                {
                    double[] values = new double[140000];
                    for (int i = 0; i < values.Length; i++)
                        values[i] = Math.Sqrt(i * 0.5 + frame);
                    Array.Sort(values);
                    Prof.Event("hitch", "sorted " + values.Length + " values");
                }
            }
        }
        Prof.Counter("spinners", spinners.Count);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.L)
            ToggleLeak();
    }

    private void ToggleLeak()
    {
        leaking = !leaking;
        if (leaking)
        {
            Prof.Event("leak", "simulation demarree");
            return;
        }
        foreach (Node node in leakedNodes)
            node.Free();
        leakedNodes.Clear();
        leakedBuffers.Clear();
        Prof.Event("leak", "simulation arretee et memoire liberee");
    }

    private void LeakStep()
    {
        using (Prof.Scope("World.Leak"))
        {
            for (int i = 0; i < 24; i++)
                leakedBuffers[leakCounter++] = new byte[4096];
            for (int i = 0; i < 3; i++)
                leakedNodes.Add(new Node3D { Name = "Leaked" + leakedNodes.Count });
            Prof.Counter("leaked buffers", leakedBuffers.Count);
            Prof.Counter("leaked nodes", leakedNodes.Count);
        }
    }

    private void UpdateHud()
    {
        Key hotkey = ProfilerRuntime.Instance?.OverlayHotkey ?? Key.F3;
        statusLabel.Text = Fmt.Number(Engine.GetFramesPerSecond()) + " fps"
                           + "\nclick to capture the mouse, WASD move, shift sprint, space jump, escape frees the cursor"
                           + "\nleft click pushes, right click shoots, E inspects the aimed node, F outlines it"
                           + "\n" + hotkey + " toggles the profiler overlay, L toggles a deliberate leak"
                           + (leaking ? "   LEAKING " + leakedNodes.Count + " orphan nodes and " + leakedBuffers.Count + " buffers" : string.Empty);

        Node3D aimed = player.AimedNode;
        crosshair.Locked = aimed != null;
        crosshair.QueueRedraw();
        if (aimed == null)
        {
            aimLabel.Text = string.Empty;
            return;
        }
        SubtreeStats stats = ObjectGraph.Stats(aimed, 4096);
        aimLabel.Text = aimed.Name + "   <" + aimed.GetClass() + ">"
                        + "\n" + Fmt.Distance(player.AimDistance) + "   "
                        + aimed.GetChildCount() + " children   "
                        + stats.Nodes + " nodes   " + stats.Resources + " resources   "
                        + Fmt.Bytes(stats.Bytes) + " retained"
                        + "\nE opens it in the profiler";
    }
}
