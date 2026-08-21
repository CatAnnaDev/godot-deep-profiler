using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public struct PropertySpec
{
    public StringName Key;
    public string Name;
    public Variant.Type Type;
    public PropertyUsageFlags Usage;
    public string ClassName;
    public bool Readable;
    public bool Followable;
}

public struct ClassStat
{
    public int Count;
    public long Bytes;
    public bool Estimated;
}

public struct SubtreeStats
{
    public int Nodes;
    public int Resources;
    public long Bytes;
    public bool Partial;
}

public struct HolderRef
{
    public ulong Holder;
    public string Property;
}

public static class ObjectGraph
{
    public const int FlagVisible = 1;
    public const int FlagProcess = 2;
    public const int FlagPhysics = 4;
    public const int FlagInTree = 8;
    public const int FlagDisabled = 16;
    public const int FlagScript = 32;
    public const int FlagInput = 64;
    public const int FlagQueuedFree = 128;

    private static readonly Dictionary<string, PropertySpec[]> SpecCache = new Dictionary<string, PropertySpec[]>(128, StringComparer.Ordinal);
    private static readonly Dictionary<string, StringName[]> SignalCache = new Dictionary<string, StringName[]>(128, StringComparer.Ordinal);
    private static readonly Dictionary<ulong, SubtreeStats> StatsCache = new Dictionary<ulong, SubtreeStats>(512);
    private static readonly HashSet<ulong> Visited = new HashSet<ulong>();
    private static readonly Stack<GodotObject> Pending = new Stack<GodotObject>();
    private static int statsGeneration;

    public static ulong ExcludedRoot;

    private static readonly HashSet<string> BlockedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "data", "image", "images", "storage", "polygon", "occluder", "packed_scene",
    };

    private static readonly HashSet<string> UnfollowedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "owner", "script", "theme_owner", "multiplayer", "scene_instance_load_placeholder",
    };

    public static void InvalidateStats()
    {
        statsGeneration++;
        if (StatsCache.Count > 0)
            StatsCache.Clear();
    }

    public static GodotObject Resolve(ulong id)
    {
        if (id == 0 || !GodotObject.IsInstanceIdValid(id))
            return null;
        return GodotObject.InstanceFromId(id);
    }

    public static string KindOf(GodotObject obj)
    {
        if (obj is Node)
            return "node";
        if (obj is Resource)
            return "resource";
        return "object";
    }

    public static string CacheKey(GodotObject obj)
    {
        string className = obj.GetClass();
        Variant script = obj.GetScript();
        if (script.VariantType != Variant.Type.Object)
            return className;
        if (script.AsGodotObject() is not Resource scriptResource)
            return className;
        return className + "|" + (string.IsNullOrEmpty(scriptResource.ResourcePath)
            ? scriptResource.GetInstanceId().ToString()
            : scriptResource.ResourcePath);
    }

    public static StringName[] SignalsFor(GodotObject obj)
    {
        string key = CacheKey(obj);
        if (SignalCache.TryGetValue(key, out StringName[] cached))
            return cached;
        Godot.Collections.Array<GDDict> list = obj.GetSignalList();
        StringName[] names = new StringName[list.Count];
        for (int i = 0; i < list.Count; i++)
            names[i] = new StringName(list[i]["name"].AsString());
        SignalCache[key] = names;
        return names;
    }

    public static PropertySpec[] SpecsFor(GodotObject obj)
    {
        string key = CacheKey(obj);
        if (SpecCache.TryGetValue(key, out PropertySpec[] cached))
            return cached;

        Godot.Collections.Array<GDDict> list = obj.GetPropertyList();
        List<PropertySpec> specs = new List<PropertySpec>(list.Count);
        foreach (GDDict entry in list)
        {
            string name = entry["name"].AsString();
            PropertyUsageFlags usage = (PropertyUsageFlags)entry["usage"].AsInt64();
            Variant.Type type = (Variant.Type)entry["type"].AsInt32();
            bool separator = (usage & (PropertyUsageFlags.Category | PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup)) != 0;
            bool internalOnly = name.StartsWith("_", StringComparison.Ordinal);
            bool heavy = IsHeavyType(type) || BlockedNames.Contains(name);
            specs.Add(new PropertySpec
            {
                Key = separator ? null : new StringName(name),
                Name = name,
                Type = type,
                Usage = usage,
                ClassName = entry["class_name"].AsString(),
                Readable = !separator && !internalOnly && !heavy,
                Followable = !separator && !internalOnly && type == Variant.Type.Object && !UnfollowedNames.Contains(name)
                             && (usage & (PropertyUsageFlags.Storage | PropertyUsageFlags.Editor)) != 0,
            });
        }
        PropertySpec[] array = specs.ToArray();
        SpecCache[key] = array;
        return array;
    }

    private static bool IsHeavyType(Variant.Type type)
    {
        switch (type)
        {
            case Variant.Type.PackedByteArray:
            case Variant.Type.PackedInt32Array:
            case Variant.Type.PackedInt64Array:
            case Variant.Type.PackedFloat32Array:
            case Variant.Type.PackedFloat64Array:
            case Variant.Type.PackedVector2Array:
            case Variant.Type.PackedVector3Array:
            case Variant.Type.PackedVector4Array:
            case Variant.Type.PackedColorArray:
                return true;
            default:
                return false;
        }
    }

    public static int FlagsOf(GodotObject obj)
    {
        int flags = 0;
        if (obj.GetScript().VariantType == Variant.Type.Object)
            flags |= FlagScript;
        if (obj is Node node)
        {
            if (node.IsInsideTree())
                flags |= FlagInTree;
            if (node.IsProcessing())
                flags |= FlagProcess;
            if (node.IsPhysicsProcessing())
                flags |= FlagPhysics;
            if (node.IsProcessingInput() || node.IsProcessingUnhandledInput())
                flags |= FlagInput;
            if (node.ProcessMode == Node.ProcessModeEnum.Disabled)
                flags |= FlagDisabled;
            if (node.IsQueuedForDeletion())
                flags |= FlagQueuedFree;
            if (node is CanvasItem canvasItem)
            {
                if (canvasItem.Visible)
                    flags |= FlagVisible;
            }
            else if (node is Node3D node3d)
            {
                if (node3d.Visible)
                    flags |= FlagVisible;
            }
            else if (node is Window window)
            {
                if (window.Visible)
                    flags |= FlagVisible;
            }
            else
            {
                flags |= FlagVisible;
            }
        }
        return flags;
    }

    public static long SelfBytes(GodotObject obj, out bool estimated)
    {
        try
        {
            return MemoryEstimator.SelfBytes(obj, out estimated);
        }
        catch (Exception)
        {
            estimated = true;
            return MemoryEstimator.ObjectBase;
        }
    }

    public static SubtreeStats Stats(GodotObject root, int budget)
    {
        SubtreeStats stats = default;
        if (root == null)
            return stats;
        ulong rootId = root.GetInstanceId();
        if (StatsCache.TryGetValue(rootId, out SubtreeStats cached))
            return cached;

        Visited.Clear();
        Pending.Clear();
        Pending.Push(root);
        Visited.Add(rootId);
        int visits = 0;

        while (Pending.Count > 0)
        {
            if (++visits > budget)
            {
                stats.Partial = true;
                break;
            }
            GodotObject current = Pending.Pop();
            if (!GodotObject.IsInstanceValid(current) || current.GetInstanceId() == ExcludedRoot)
                continue;
            stats.Bytes += SelfBytes(current, out bool estimated);
            if (current is Node node)
            {
                stats.Nodes++;
                int count = node.GetChildCount(true);
                for (int i = 0; i < count; i++)
                {
                    Node child = node.GetChild(i, true);
                    if (child != null && Visited.Add(child.GetInstanceId()))
                        Pending.Push(child);
                }
            }
            else
            {
                stats.Resources++;
            }
            PushFollowed(current);
        }

        if (!stats.Partial)
            StatsCache[rootId] = stats;
        return stats;
    }

    private static void PushFollowed(GodotObject obj)
    {
        PropertySpec[] specs = SpecsFor(obj);
        for (int i = 0; i < specs.Length; i++)
        {
            if (!specs[i].Followable)
                continue;
            GodotObject value = ReadObject(obj, specs[i].Key);
            if (value == null)
                continue;
            if (Visited.Add(value.GetInstanceId()))
                Pending.Push(value);
        }
    }

    private static GodotObject ReadObject(GodotObject obj, StringName property)
    {
        try
        {
            Variant value = obj.Get(property);
            if (value.VariantType != Variant.Type.Object)
                return null;
            GodotObject result = value.AsGodotObject();
            return GodotObject.IsInstanceValid(result) ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static GDDict Describe(ulong id, bool withProperties, bool withSignals, int childOffset, int childLimit, int statsBudget)
    {
        GDDict result = new GDDict();
        GodotObject obj = Resolve(id);
        result["id"] = id;
        if (obj == null)
        {
            result["ok"] = false;
            return result;
        }
        result["ok"] = true;
        result["class"] = obj.GetClass();
        result["kind"] = KindOf(obj);
        result["flags"] = FlagsOf(obj);

        long self = SelfBytes(obj, out bool estimated);
        result["self"] = self;
        result["self_est"] = estimated;

        Variant script = obj.GetScript();
        if (script.VariantType == Variant.Type.Object && script.AsGodotObject() is Resource scriptResource)
            result["script"] = string.IsNullOrEmpty(scriptResource.ResourcePath) ? scriptResource.GetClass() : scriptResource.ResourcePath;
        else
            result["script"] = string.Empty;

        if (obj is RefCounted refCounted)
            result["refs"] = refCounted.GetReferenceCount();
        else
            result["refs"] = 0;

        if (obj is Node node)
        {
            result["name"] = node.Name.ToString();
            result["path"] = node.IsInsideTree() ? node.GetPath().ToString() : string.Empty;
            result["scene"] = node.SceneFilePath;
            Node owner = node.GetOwnerOrNull<Node>();
            result["owner"] = owner != null ? owner.GetInstanceId() : 0UL;
            result["owner_name"] = owner != null ? owner.Name.ToString() : string.Empty;
            Node parent = node.GetParent();
            result["parent"] = parent != null ? parent.GetInstanceId() : 0UL;
            result["groups"] = GroupsOf(node);

            int childCount = node.GetChildCount(true);
            result["child_count"] = childCount;
            result["child_offset"] = childOffset;
            GDArray children = new GDArray();
            int end = childLimit <= 0 ? childCount : Math.Min(childCount, childOffset + childLimit);
            int perChildBudget = Math.Max(64, statsBudget / Math.Max(1, end - childOffset));
            for (int i = Math.Max(0, childOffset); i < end; i++)
            {
                Node child = node.GetChild(i, true);
                if (child == null)
                    continue;
                children.Add(ChildEntry(child, perChildBudget));
            }
            result["children"] = children;

            SubtreeStats stats = Stats(node, statsBudget);
            result["retained"] = stats.Bytes;
            result["sub_nodes"] = stats.Nodes;
            result["sub_res"] = stats.Resources;
            result["partial"] = stats.Partial;
        }
        else
        {
            result["child_count"] = 0;
            result["children"] = new GDArray();
            if (obj is Resource resource)
            {
                result["name"] = string.IsNullOrEmpty(resource.ResourceName) ? resource.ResourcePath.GetFile() : resource.ResourceName;
                result["path"] = resource.ResourcePath;
                result["rid"] = resource.GetRid().Id;
            }
            else
            {
                result["name"] = obj.GetClass() + " #" + id;
                result["path"] = string.Empty;
            }
            SubtreeStats stats = Stats(obj, Math.Min(statsBudget, 4096));
            result["retained"] = stats.Bytes;
            result["sub_nodes"] = stats.Nodes;
            result["sub_res"] = stats.Resources;
            result["partial"] = stats.Partial;
        }

        if (withProperties)
            result["props"] = Properties(obj);
        if (withSignals)
        {
            result["signals"] = Signals(obj);
            result["incoming"] = Incoming(obj);
        }
        result["meta"] = MetaOf(obj);
        result["extra"] = Extras(obj);
        return result;
    }

    private static GDDict ChildEntry(Node child, int budget)
    {
        SubtreeStats stats = Stats(child, budget);
        return new GDDict
        {
            { "id", child.GetInstanceId() },
            { "name", child.Name.ToString() },
            { "class", child.GetClass() },
            { "children", child.GetChildCount(true) },
            { "desc", stats.Nodes - 1 },
            { "res", stats.Resources },
            { "bytes", stats.Bytes },
            { "partial", stats.Partial },
            { "flags", FlagsOf(child) },
            { "script", child.GetScript().VariantType == Variant.Type.Object },
        };
    }

    private static GDArray GroupsOf(Node node)
    {
        GDArray groups = new GDArray();
        foreach (StringName group in node.GetGroups())
        {
            string name = group.ToString();
            if (!name.StartsWith("_", StringComparison.Ordinal))
                groups.Add(name);
        }
        return groups;
    }

    private static GDArray MetaOf(GodotObject obj)
    {
        GDArray meta = new GDArray();
        foreach (StringName key in obj.GetMetaList())
        {
            string name = key.ToString();
            GDDict entry = new GDDict { { "n", name } };
            try
            {
                entry["v"] = Fmt.Variant(obj.GetMeta(key));
            }
            catch (Exception)
            {
                entry["v"] = "<unreadable>";
            }
            meta.Add(entry);
        }
        return meta;
    }

    public static GDArray Properties(GodotObject obj)
    {
        GDArray properties = new GDArray();
        PropertySpec[] specs = SpecsFor(obj);
        string group = string.Empty;
        for (int i = 0; i < specs.Length; i++)
        {
            ref PropertySpec spec = ref specs[i];
            if ((spec.Usage & (PropertyUsageFlags.Category | PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup)) != 0)
            {
                group = spec.Name;
                continue;
            }
            GDDict entry = new GDDict
            {
                { "n", spec.Name },
                { "t", (int)spec.Type },
                { "tn", spec.Type.ToString() },
                { "g", group },
                { "u", (long)spec.Usage },
                { "cn", spec.ClassName },
            };
            if (!spec.Readable)
            {
                entry["v"] = "<skipped>";
                entry["o"] = 0UL;
            }
            else
            {
                try
                {
                    Variant value = obj.Get(spec.Key);
                    entry["v"] = Fmt.Variant(value);
                    if (value.VariantType == Variant.Type.Object)
                    {
                        GodotObject target = value.AsGodotObject();
                        if (GodotObject.IsInstanceValid(target))
                        {
                            entry["o"] = target.GetInstanceId();
                            entry["c"] = target.GetClass();
                            entry["b"] = SelfBytes(target, out bool _);
                        }
                        else
                        {
                            entry["o"] = 0UL;
                        }
                    }
                    else
                    {
                        entry["o"] = 0UL;
                        if (value.VariantType == Variant.Type.Float || value.VariantType == Variant.Type.Int || value.VariantType == Variant.Type.Bool)
                            entry["num"] = true;
                    }
                }
                catch (Exception exception)
                {
                    entry["v"] = "<error: " + exception.GetType().Name + ">";
                    entry["o"] = 0UL;
                }
            }
            properties.Add(entry);
        }
        return properties;
    }

    public static GDArray Signals(GodotObject obj)
    {
        GDArray signals = new GDArray();
        StringName[] names = SignalsFor(obj);
        for (int i = 0; i < names.Length; i++)
        {
            if (!obj.HasConnections(names[i]))
                continue;
            GDArray connections = new GDArray();
            foreach (GDDict connection in obj.GetSignalConnectionList(names[i]))
                connections.Add(ConnectionEntry(connection, true));
            if (connections.Count == 0)
                continue;
            signals.Add(new GDDict { { "n", names[i].ToString() }, { "c", connections } });
        }
        return signals;
    }

    public static GDArray Incoming(GodotObject obj)
    {
        GDArray incoming = new GDArray();
        foreach (GDDict connection in obj.GetIncomingConnections())
            incoming.Add(ConnectionEntry(connection, false));
        return incoming;
    }

    private static GDDict ConnectionEntry(GDDict connection, bool outgoing)
    {
        GDDict entry = new GDDict();
        Variant signalValue = connection.TryGetValue("signal", out Variant sv) ? sv : default;
        Variant callableValue = connection.TryGetValue("callable", out Variant cv) ? cv : default;
        entry["flags"] = connection.TryGetValue("flags", out Variant flags) ? flags.AsInt32() : 0;

        if (signalValue.VariantType == Variant.Type.Signal)
        {
            Signal signal = signalValue.AsSignal();
            entry["signal"] = signal.Name.ToString();
            GodotObject emitter = signal.Owner;
            entry["from"] = GodotObject.IsInstanceValid(emitter) ? emitter.GetInstanceId() : 0UL;
            entry["from_name"] = Fmt.Describe(emitter);
        }
        else
        {
            entry["signal"] = string.Empty;
            entry["from"] = 0UL;
            entry["from_name"] = string.Empty;
        }

        if (callableValue.VariantType == Variant.Type.Callable)
        {
            Callable callable = callableValue.AsCallable();
            GodotObject target = null;
            Delegate managed = null;
            string method = string.Empty;
            try
            {
                target = callable.Target;
                managed = callable.Delegate;
                StringName methodName = callable.Method;
                method = methodName != null ? methodName.ToString() : string.Empty;
            }
            catch (Exception)
            {
                method = "<unreadable>";
            }
            entry["to"] = GodotObject.IsInstanceValid(target) ? target.GetInstanceId() : 0UL;
            if (string.IsNullOrEmpty(method) && managed != null)
                method = managed.Method?.Name ?? "<lambda>";
            entry["to_name"] = target != null ? Fmt.Describe(target) : managed != null ? "<managed callable>" : "<engine internal>";
            entry["method"] = method;
            entry["internal"] = target == null && managed == null;
        }
        else
        {
            entry["to"] = 0UL;
            entry["to_name"] = string.Empty;
            entry["method"] = string.Empty;
            entry["internal"] = true;
        }
        entry["out"] = outgoing;
        return entry;
    }

    public static GDDict Extras(GodotObject obj)
    {
        GDDict extra = new GDDict();
        switch (obj)
        {
            case Viewport viewport:
            {
                Rid rid = viewport.GetViewportRid();
                extra["size"] = viewport.GetVisibleRect().Size.ToString();
                extra["draw calls"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
                extra["objects"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
                extra["primitives"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame);
                extra["shadow draw calls"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
                extra["canvas draw calls"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Canvas, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
                break;
            }
            case MeshInstance3D meshInstance:
            {
                Mesh mesh = meshInstance.Mesh;
                extra["surfaces"] = mesh != null ? mesh.GetSurfaceCount() : 0;
                extra["aabb"] = meshInstance.GetAabb().Size.ToString();
                extra["skeleton"] = meshInstance.Skeleton?.ToString() ?? string.Empty;
                extra["material override"] = meshInstance.MaterialOverride != null;
                extra["cast shadow"] = meshInstance.CastShadow.ToString();
                break;
            }
            case MultiMeshInstance3D multiInstance:
            {
                MultiMesh multi = multiInstance.Multimesh;
                extra["instances"] = multi != null ? multi.InstanceCount : 0;
                extra["visible instances"] = multi != null ? multi.VisibleInstanceCount : 0;
                break;
            }
            case GpuParticles3D particles3d:
            {
                extra["amount"] = particles3d.Amount;
                extra["emitting"] = particles3d.Emitting;
                extra["lifetime"] = particles3d.Lifetime;
                break;
            }
            case GpuParticles2D particles2d:
            {
                extra["amount"] = particles2d.Amount;
                extra["emitting"] = particles2d.Emitting;
                break;
            }
            case CollisionObject3D body3d:
            {
                extra["shapes"] = body3d.GetShapeOwners().Length;
                extra["layer"] = body3d.CollisionLayer;
                extra["mask"] = body3d.CollisionMask;
                break;
            }
            case CollisionObject2D body2d:
            {
                extra["shapes"] = body2d.GetShapeOwners().Length;
                extra["layer"] = body2d.CollisionLayer;
                extra["mask"] = body2d.CollisionMask;
                break;
            }
            case AnimationPlayer player:
            {
                extra["playing"] = player.IsPlaying();
                extra["current"] = player.CurrentAnimation;
                extra["animations"] = player.GetAnimationList().Length;
                extra["speed"] = player.SpeedScale;
                break;
            }
            case Skeleton3D skeleton:
            {
                extra["bones"] = skeleton.GetBoneCount();
                break;
            }
            case Camera3D camera:
            {
                extra["current"] = camera.Current;
                extra["fov"] = camera.Fov;
                extra["far"] = camera.Far;
                break;
            }
            case Light3D light:
            {
                extra["energy"] = light.LightEnergy;
                extra["shadows"] = light.ShadowEnabled;
                break;
            }
            case AudioStreamPlayer audio:
            {
                extra["playing"] = audio.Playing;
                extra["bus"] = audio.Bus.ToString();
                break;
            }
            case RichTextLabel richText:
            {
                extra["text length"] = richText.Text?.Length ?? 0;
                extra["lines"] = richText.GetLineCount();
                break;
            }
            case Label label:
            {
                extra["text length"] = label.Text?.Length ?? 0;
                extra["lines"] = label.GetLineCount();
                break;
            }
            case Timer timer:
            {
                extra["time left"] = timer.TimeLeft;
                extra["wait time"] = timer.WaitTime;
                extra["stopped"] = timer.IsStopped();
                break;
            }
            case Control control:
            {
                extra["rect"] = control.GetGlobalRect().ToString();
                extra["mouse filter"] = control.MouseFilter.ToString();
                break;
            }
            case Node3D node3d:
            {
                extra["global position"] = node3d.GlobalPosition.ToString();
                break;
            }
            case Node2D node2d:
            {
                extra["global position"] = node2d.GlobalPosition.ToString();
                extra["z index"] = node2d.ZIndex;
                break;
            }
            case Texture2D texture:
            {
                extra["size"] = texture.GetWidth() + " x " + texture.GetHeight();
                extra["format"] = texture.GetFormat().ToString();
                extra["mipmaps"] = texture.HasMipmaps();
                break;
            }
            case ArrayMesh arrayMesh:
            {
                extra["surfaces"] = arrayMesh.GetSurfaceCount();
                long vertices = 0;
                long indices = 0;
                for (int i = 0; i < arrayMesh.GetSurfaceCount(); i++)
                {
                    vertices += arrayMesh.SurfaceGetArrayLen(i);
                    indices += arrayMesh.SurfaceGetArrayIndexLen(i);
                }
                extra["vertices"] = vertices;
                extra["indices"] = indices;
                extra["blend shapes"] = arrayMesh.GetBlendShapeCount();
                break;
            }
            case Mesh genericMesh:
            {
                extra["surfaces"] = genericMesh.GetSurfaceCount();
                extra["aabb"] = genericMesh.GetAabb().Size.ToString();
                break;
            }
            case ShaderMaterial shaderMaterial:
            {
                extra["shader"] = shaderMaterial.Shader?.ResourcePath ?? string.Empty;
                break;
            }
            case PackedScene packedScene:
            {
                SceneState state = packedScene.GetState();
                extra["nodes"] = state != null ? state.GetNodeCount() : 0;
                extra["connections"] = state != null ? state.GetConnectionCount() : 0;
                break;
            }
            case Animation animation:
            {
                extra["tracks"] = animation.GetTrackCount();
                extra["length"] = animation.Length;
                break;
            }
        }
        return extra;
    }

    public static GDDict Crawl(int budget, int maxResources, int maxSignals)
    {
        return Crawl(budget, maxResources, maxSignals, false);
    }

    public static GDDict Crawl(int budget, int maxResources, int maxSignals, bool light)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<string, ClassStat> byClass = new Dictionary<string, ClassStat>(256, StringComparer.Ordinal);
        Dictionary<ulong, List<HolderRef>> holders = new Dictionary<ulong, List<HolderRef>>(256);
        List<GodotObject> resources = new List<GodotObject>(256);
        GDArray signalRows = new GDArray();

        HashSet<ulong> seen = new HashSet<ulong>();
        Stack<GodotObject> stack = new Stack<GodotObject>(256);
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        if (root == null)
            return new GDDict { { "ok", false } };

        stack.Push(root);
        seen.Add(root.GetInstanceId());
        int nodes = 0;
        int visits = 0;
        long totalBytes = 0;
        bool partial = false;

        while (stack.Count > 0)
        {
            if (++visits > budget)
            {
                partial = true;
                break;
            }
            GodotObject current = stack.Pop();
            if (!GodotObject.IsInstanceValid(current) || current.GetInstanceId() == ExcludedRoot)
                continue;
            string className = current.GetClass();
            long self = light ? 0L : SelfBytes(current, out bool estimated);
            bool estimatedLight = light || self > 0;
            totalBytes += self;
            byClass.TryGetValue(className, out ClassStat stat);
            stat.Count++;
            stat.Bytes += self;
            stat.Estimated |= !light && estimatedLight;
            byClass[className] = stat;

            if (current is Node node)
            {
                nodes++;
                if (signalRows.Count < maxSignals)
                    CollectSignals(node, signalRows, maxSignals);
                int count = node.GetChildCount(true);
                for (int i = 0; i < count; i++)
                {
                    Node child = node.GetChild(i, true);
                    if (child != null && seen.Add(child.GetInstanceId()))
                        stack.Push(child);
                }
            }
            else if (current is Resource resource && !light && resources.Count < maxResources)
            {
                resources.Add(resource);
            }

            PropertySpec[] specs = SpecsFor(current);
            ulong currentId = current.GetInstanceId();
            for (int i = 0; i < specs.Length; i++)
            {
                if (!specs[i].Followable)
                    continue;
                GodotObject value = ReadObject(current, specs[i].Key);
                if (value == null)
                    continue;
                ulong valueId = value.GetInstanceId();
                if (!holders.TryGetValue(valueId, out List<HolderRef> list))
                {
                    list = new List<HolderRef>(2);
                    holders[valueId] = list;
                }
                if (list.Count < 32)
                    list.Add(new HolderRef { Holder = currentId, Property = specs[i].Name });
                if (seen.Add(valueId))
                    stack.Push(value);
            }
        }

        GDArray classRows = new GDArray();
        foreach (KeyValuePair<string, ClassStat> pair in byClass)
        {
            classRows.Add(new GDDict
            {
                { "class", pair.Key },
                { "count", pair.Value.Count },
                { "bytes", pair.Value.Bytes },
                { "est", pair.Value.Estimated },
            });
        }

        GDArray resourceRows = new GDArray();
        foreach (GodotObject item in resources)
        {
            if (!GodotObject.IsInstanceValid(item) || item is not Resource resource)
                continue;
            ulong resourceId = resource.GetInstanceId();
            GDArray holderRows = new GDArray();
            if (holders.TryGetValue(resourceId, out List<HolderRef> list))
            {
                foreach (HolderRef holder in list)
                {
                    GodotObject holderObject = Resolve(holder.Holder);
                    holderRows.Add(new GDDict
                    {
                        { "id", holder.Holder },
                        { "prop", holder.Property },
                        { "name", Fmt.Describe(holderObject) },
                    });
                }
            }
            resourceRows.Add(new GDDict
            {
                { "id", resourceId },
                { "class", resource.GetClass() },
                { "path", resource.ResourcePath },
                { "name", resource.ResourceName },
                { "refs", resource.GetReferenceCount() },
                { "bytes", SelfBytes(resource, out bool _) },
                { "holders", holderRows },
                { "local", resource.ResourceLocalToScene },
            });
        }

        watch.Stop();
        return new GDDict
        {
            { "ok", true },
            { "classes", classRows },
            { "resources", resourceRows },
            { "signals", signalRows },
            { "nodes", nodes },
            { "objects", visits },
            { "bytes", totalBytes },
            { "partial", partial },
            { "ms", watch.Elapsed.TotalMilliseconds },
            { "unique_resources", resources.Count },
            { "light", light },
        };
    }

    public static GDDict SignalGraph(int budget, int max)
    {
        Stopwatch watch = Stopwatch.StartNew();
        GDArray rows = new GDArray();
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        if (root == null)
            return new GDDict { { "ok", false } };

        HashSet<ulong> seen = new HashSet<ulong>();
        Stack<GodotObject> stack = new Stack<GodotObject>(256);
        stack.Push(root);
        seen.Add(root.GetInstanceId());
        int visits = 0;
        bool partial = false;

        while (stack.Count > 0)
        {
            if (++visits > budget || rows.Count >= max)
            {
                partial = true;
                break;
            }
            GodotObject current = stack.Pop();
            if (!GodotObject.IsInstanceValid(current) || current.GetInstanceId() == ExcludedRoot)
                continue;
            CollectSignals(current, rows, max);
            if (current is Node node)
            {
                int count = node.GetChildCount(true);
                for (int i = 0; i < count; i++)
                {
                    Node child = node.GetChild(i, true);
                    if (child != null && seen.Add(child.GetInstanceId()))
                        stack.Push(child);
                }
            }
            PropertySpec[] specs = SpecsFor(current);
            for (int i = 0; i < specs.Length; i++)
            {
                if (!specs[i].Followable)
                    continue;
                GodotObject value = ReadObject(current, specs[i].Key);
                if (value != null && seen.Add(value.GetInstanceId()))
                    stack.Push(value);
            }
        }
        watch.Stop();
        return new GDDict
        {
            { "ok", true },
            { "rows", rows },
            { "objects", visits },
            { "partial", partial },
            { "ms", watch.Elapsed.TotalMilliseconds },
        };
    }

    public static GDArray Instances(string className, int limit, int budget)
    {
        GDArray rows = new GDArray();
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node root = tree?.Root;
        if (root == null || string.IsNullOrEmpty(className))
            return rows;

        HashSet<ulong> seen = new HashSet<ulong>();
        Stack<GodotObject> stack = new Stack<GodotObject>(256);
        stack.Push(root);
        seen.Add(root.GetInstanceId());
        int visits = 0;

        while (stack.Count > 0 && rows.Count < limit)
        {
            if (++visits > budget)
                break;
            GodotObject current = stack.Pop();
            if (!GodotObject.IsInstanceValid(current) || current.GetInstanceId() == ExcludedRoot)
                continue;
            if (current.GetClass() == className)
            {
                Node node = current as Node;
                rows.Add(new GDDict
                {
                    { "id", current.GetInstanceId() },
                    { "name", node != null ? node.Name.ToString() : Fmt.Describe(current) },
                    { "path", node != null && node.IsInsideTree() ? node.GetPath().ToString() : (current as Resource)?.ResourcePath ?? string.Empty },
                    { "bytes", node != null ? Stats(node, 4096).Bytes : SelfBytes(current, out bool _) },
                    { "class", className },
                });
            }
            if (current is Node parent)
            {
                int count = parent.GetChildCount(true);
                for (int i = 0; i < count; i++)
                {
                    Node child = parent.GetChild(i, true);
                    if (child != null && seen.Add(child.GetInstanceId()))
                        stack.Push(child);
                }
            }
            PropertySpec[] specs = SpecsFor(current);
            for (int i = 0; i < specs.Length; i++)
            {
                if (!specs[i].Followable)
                    continue;
                GodotObject value = ReadObject(current, specs[i].Key);
                if (value != null && seen.Add(value.GetInstanceId()))
                    stack.Push(value);
            }
        }
        return rows;
    }

    private static void CollectSignals(GodotObject node, GDArray rows, int max)
    {
        StringName[] signals = SignalsFor(node);
        ulong nodeId = 0;
        string nodeName = null;
        string nodeClass = null;
        for (int i = 0; i < signals.Length; i++)
        {
            if (!node.HasConnections(signals[i]))
                continue;
            if (nodeName == null)
            {
                nodeId = node.GetInstanceId();
                nodeName = node is Node named ? named.Name.ToString() : Fmt.Describe(node);
                nodeClass = node.GetClass();
            }
            foreach (GDDict connection in node.GetSignalConnectionList(signals[i]))
            {
                if (rows.Count >= max)
                    return;
                GDDict entry = ConnectionEntry(connection, true);
                entry["from"] = nodeId;
                entry["from_name"] = nodeName;
                entry["from_class"] = nodeClass;
                rows.Add(entry);
            }
        }
    }
}
