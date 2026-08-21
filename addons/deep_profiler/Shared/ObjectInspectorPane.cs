using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ObjectInspectorPane : VBoxContainer
{
    public IGraphSource Source;
    public Action<ulong> Navigated;
    public Action<string> Message;
    public int WatchSlot;

    private Tree tree;
    private Label title;
    private Label subtitle;
    private Button backButton;
    private Button forwardButton;
    private GDDict payload;
    private ulong currentId;
    private string currentPath = string.Empty;
    private readonly List<ulong> history = new List<ulong>(32);
    private int historyIndex = -1;
    private readonly Dictionary<string, bool> foldState = new Dictionary<string, bool>(16, StringComparer.Ordinal);

    private static readonly Color SectionColor = new Color(0.98f, 0.82f, 0.45f);
    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color LinkColor = new Color(0.55f, 0.80f, 1f);
    private static readonly Color ValueColor = new Color(0.88f, 0.90f, 0.95f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        backButton = new Button { Text = "<", TooltipText = "Back", Disabled = true };
        backButton.Pressed += GoBack;
        bar.AddChild(backButton);

        forwardButton = new Button { Text = ">", TooltipText = "Forward", Disabled = true };
        forwardButton.Pressed += GoForward;
        bar.AddChild(forwardButton);

        Button refresh = new Button { Text = "Refresh" };
        refresh.Pressed += () => { if (currentId != 0) Source?.RequestObject(currentId); };
        bar.AddChild(refresh);

        Button highlight = new Button { Text = "Highlight", TooltipText = "Outline this object inside the running game" };
        highlight.Pressed += () => { if (currentId != 0) Source?.RequestHighlight(currentId); };
        bar.AddChild(highlight);

        Button ablate = new Button { Text = "Measure cost", TooltipText = "Toggle this node off for a few frames and measure the frame time difference" };
        ablate.Pressed += () => { if (currentId != 0) Source?.RequestAblate(currentId, 40); };
        bar.AddChild(ablate);

        Button watch = new Button { Text = "Watch", TooltipText = "Graph the selected numeric property" };
        watch.Pressed += WatchSelected;
        bar.AddChild(watch);

        title = new Label { Text = "nothing selected", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 12);
        AddChild(title);

        subtitle = new Label { Text = string.Empty, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        subtitle.AddThemeFontSizeOverride("font_size", 10);
        subtitle.AddThemeColorOverride("font_color", DimColor);
        AddChild(subtitle);

        tree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            AllowReselect = true,
        };
        tree.SetColumnTitle(0, "Member");
        tree.SetColumnTitle(1, "Value");
        tree.SetColumnTitle(2, "Type");
        tree.SetColumnTitle(3, "Size");
        tree.SetColumnExpandRatio(0, 3);
        tree.SetColumnExpandRatio(1, 5);
        tree.SetColumnExpandRatio(2, 2);
        tree.SetColumnExpandRatio(3, 1);
        tree.SetColumnCustomMinimumWidth(3, 70);
        tree.ItemActivated += OnActivated;
        tree.ItemCollapsed += OnCollapsed;
        AddChild(tree);
    }

    public void Navigate(ulong id)
    {
        if (id == 0)
            return;
        if (historyIndex >= 0 && historyIndex < history.Count && history[historyIndex] == id)
        {
            Source?.RequestObject(id);
            return;
        }
        if (historyIndex < history.Count - 1)
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(id);
        if (history.Count > 64)
            history.RemoveAt(0);
        historyIndex = history.Count - 1;
        UpdateHistoryButtons();
        Source?.RequestObject(id);
    }

    private void GoBack()
    {
        if (historyIndex <= 0)
            return;
        historyIndex--;
        UpdateHistoryButtons();
        ulong id = history[historyIndex];
        Navigated?.Invoke(id);
        Source?.RequestObject(id);
    }

    private void GoForward()
    {
        if (historyIndex >= history.Count - 1)
            return;
        historyIndex++;
        UpdateHistoryButtons();
        ulong id = history[historyIndex];
        Navigated?.Invoke(id);
        Source?.RequestObject(id);
    }

    private void UpdateHistoryButtons()
    {
        backButton.Disabled = historyIndex <= 0;
        forwardButton.Disabled = historyIndex >= history.Count - 1;
    }

    public void SetPayload(GDDict data)
    {
        payload = data;
        tree.Clear();
        if (data == null || !data.TryGetValue("ok", out Variant ok) || !ok.AsBool())
        {
            title.Text = "object is gone";
            subtitle.Text = data != null && data.TryGetValue("id", out Variant lost) ? "instance " + lost.AsUInt64() + " no longer exists" : string.Empty;
            return;
        }

        currentId = data["id"].AsUInt64();
        string className = data["class"].AsString();
        string name = data["name"].AsString();
        currentPath = data.TryGetValue("path", out Variant pathValue) ? pathValue.AsString() : string.Empty;
        title.Text = name + "   <" + className + ">";

        long self = data["self"].AsInt64();
        long retained = data.TryGetValue("retained", out Variant retainedValue) ? retainedValue.AsInt64() : 0;
        string script = data["script"].AsString();
        subtitle.Text = (string.IsNullOrEmpty(currentPath) ? "instance " + currentId : currentPath)
                        + "   self " + Fmt.Bytes(self)
                        + "   retained " + Fmt.Bytes(retained)
                        + (string.IsNullOrEmpty(script) ? string.Empty : "   script " + script.GetFile());

        TreeItem root = tree.CreateItem();

        TreeItem identity = Section(root, "Identity");
        Row(identity, "instance id", currentId.ToString(), "int", string.Empty, 0);
        Row(identity, "class", className, data["kind"].AsString(), string.Empty, 0);
        if (!string.IsNullOrEmpty(currentPath))
            Row(identity, "path", currentPath, "NodePath", string.Empty, 0);
        if (data.TryGetValue("scene", out Variant scene) && !string.IsNullOrEmpty(scene.AsString()))
            Row(identity, "scene file", scene.AsString(), "String", string.Empty, 0);
        if (!string.IsNullOrEmpty(script))
            Row(identity, "script", script, "Script", string.Empty, 0);
        if (data.TryGetValue("owner", out Variant owner) && owner.AsUInt64() != 0)
            Row(identity, "owner", data["owner_name"].AsString(), "Node", string.Empty, owner.AsUInt64());
        if (data.TryGetValue("parent", out Variant parent) && parent.AsUInt64() != 0)
            Row(identity, "parent", "open parent", "Node", string.Empty, parent.AsUInt64());
        if (data.TryGetValue("rid", out Variant rid) && rid.AsUInt64() != 0)
            Row(identity, "rid", rid.AsUInt64().ToString(), "RID", string.Empty, 0);
        if (data.TryGetValue("refs", out Variant refs) && refs.AsInt32() > 0)
            Row(identity, "references", refs.AsInt32().ToString(), "int", string.Empty, 0);
        Row(identity, "flags", FlagText(data.TryGetValue("flags", out Variant flags) ? flags.AsInt32() : 0), "bitfield", string.Empty, 0);
        if (data.TryGetValue("groups", out Variant groups) && groups.AsGodotArray().Count > 0)
            Row(identity, "groups", string.Join(", ", ToStrings(groups.AsGodotArray())), "Array", string.Empty, 0);

        TreeItem memory = Section(root, "Memory");
        Row(memory, "self", Fmt.Bytes(self), data["self_est"].AsBool() ? "estimate" : "exact", Fmt.Bytes(self), 0);
        Row(memory, "retained", Fmt.Bytes(retained) + (data.TryGetValue("partial", out Variant partial) && partial.AsBool() ? " (partial)" : string.Empty), "subtree", Fmt.Bytes(retained), 0);
        Row(memory, "subtree nodes", Fmt.Count(data.TryGetValue("sub_nodes", out Variant subNodes) ? subNodes.AsInt32() : 0), "count", string.Empty, 0);
        Row(memory, "subtree resources", Fmt.Count(data.TryGetValue("sub_res", out Variant subRes) ? subRes.AsInt32() : 0), "count", string.Empty, 0);

        if (data.TryGetValue("extra", out Variant extra) && extra.AsGodotDictionary().Count > 0)
        {
            TreeItem extras = Section(root, "Class details");
            foreach (System.Collections.Generic.KeyValuePair<Variant, Variant> pair in extra.AsGodotDictionary())
                Row(extras, pair.Key.AsString(), Fmt.Variant(pair.Value), string.Empty, string.Empty, 0);
        }

        if (data.TryGetValue("props", out Variant props))
            BuildProperties(root, props.AsGodotArray());

        if (data.TryGetValue("children", out Variant children) && children.AsGodotArray().Count > 0)
        {
            GDArray childArray = children.AsGodotArray();
            TreeItem section = Section(root, "Children (" + data["child_count"].AsInt32() + ")");
            foreach (Variant entry in childArray)
            {
                GDDict child = entry.AsGodotDictionary();
                Row(section, child["name"].AsString(), child["class"].AsString(),
                    child["children"].AsInt32() > 0 ? child["children"].AsInt32() + " children" : string.Empty,
                    Fmt.Bytes(child["bytes"].AsInt64()), child["id"].AsUInt64());
            }
        }

        if (data.TryGetValue("signals", out Variant signals))
            BuildSignals(root, signals.AsGodotArray());
        if (data.TryGetValue("incoming", out Variant incoming))
            BuildIncoming(root, incoming.AsGodotArray());

        if (data.TryGetValue("meta", out Variant meta) && meta.AsGodotArray().Count > 0)
        {
            TreeItem section = Section(root, "Metadata");
            foreach (Variant entry in meta.AsGodotArray())
            {
                GDDict row = entry.AsGodotDictionary();
                Row(section, row["n"].AsString(), row["v"].AsString(), string.Empty, string.Empty, 0);
            }
        }
    }

    private void BuildProperties(TreeItem root, GDArray props)
    {
        if (props.Count == 0)
            return;
        TreeItem section = Section(root, "Properties (" + props.Count + ")");
        Dictionary<string, TreeItem> groups = new Dictionary<string, TreeItem>(8, StringComparer.Ordinal);
        foreach (Variant entry in props)
        {
            GDDict property = entry.AsGodotDictionary();
            string group = property["g"].AsString();
            TreeItem parent = section;
            if (!string.IsNullOrEmpty(group))
            {
                if (!groups.TryGetValue(group, out parent))
                {
                    parent = tree.CreateItem(section);
                    parent.SetText(0, group);
                    parent.SetCustomColor(0, DimColor);
                    parent.Collapsed = true;
                    groups[group] = parent;
                }
            }
            ulong link = property.TryGetValue("o", out Variant o) ? o.AsUInt64() : 0UL;
            string size = property.TryGetValue("b", out Variant bytes) ? Fmt.Bytes(bytes.AsInt64()) : string.Empty;
            string type = property.TryGetValue("c", out Variant className) && !string.IsNullOrEmpty(className.AsString())
                ? className.AsString()
                : property["tn"].AsString();
            Row(parent, property["n"].AsString(), property["v"].AsString(), type, size, link);
        }
    }

    private void BuildSignals(TreeItem root, GDArray signals)
    {
        if (signals.Count == 0)
            return;
        int total = 0;
        foreach (Variant entry in signals)
            total += entry.AsGodotDictionary()["c"].AsGodotArray().Count;
        TreeItem section = Section(root, "Outgoing connections (" + total + ")");
        foreach (Variant entry in signals)
        {
            GDDict signal = entry.AsGodotDictionary();
            GDArray connections = signal["c"].AsGodotArray();
            TreeItem item = tree.CreateItem(section);
            item.SetText(0, signal["n"].AsString());
            item.SetText(1, connections.Count + " listener" + (connections.Count == 1 ? string.Empty : "s"));
            item.SetCustomColor(1, DimColor);
            foreach (Variant connectionEntry in connections)
            {
                GDDict connection = connectionEntry.AsGodotDictionary();
                Row(item, connection["to_name"].AsString(), connection["method"].AsString() + "()", FlagsOfConnection(connection), string.Empty, connection["to"].AsUInt64());
            }
        }
    }

    private void BuildIncoming(TreeItem root, GDArray incoming)
    {
        if (incoming.Count == 0)
            return;
        TreeItem section = Section(root, "Incoming connections (" + incoming.Count + ")");
        foreach (Variant entry in incoming)
        {
            GDDict connection = entry.AsGodotDictionary();
            Row(section, connection["from_name"].AsString(), connection["signal"].AsString() + " -> " + connection["method"].AsString() + "()",
                FlagsOfConnection(connection), string.Empty, connection["from"].AsUInt64());
        }
    }

    private static string FlagsOfConnection(GDDict connection)
    {
        int flags = connection.TryGetValue("flags", out Variant value) ? value.AsInt32() : 0;
        List<string> parts = new List<string>(3);
        if ((flags & (int)GodotObject.ConnectFlags.Deferred) != 0)
            parts.Add("deferred");
        if ((flags & (int)GodotObject.ConnectFlags.OneShot) != 0)
            parts.Add("one shot");
        if ((flags & (int)GodotObject.ConnectFlags.Persist) != 0)
            parts.Add("persist");
        return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
    }

    private static string[] ToStrings(GDArray array)
    {
        string[] values = new string[array.Count];
        for (int i = 0; i < array.Count; i++)
            values[i] = array[i].AsString();
        return values;
    }

    private static string FlagText(int flags)
    {
        List<string> parts = new List<string>(6);
        if ((flags & ObjectGraph.FlagInTree) != 0) parts.Add("in tree");
        if ((flags & ObjectGraph.FlagVisible) != 0) parts.Add("visible");
        if ((flags & ObjectGraph.FlagProcess) != 0) parts.Add("process");
        if ((flags & ObjectGraph.FlagPhysics) != 0) parts.Add("physics");
        if ((flags & ObjectGraph.FlagInput) != 0) parts.Add("input");
        if ((flags & ObjectGraph.FlagDisabled) != 0) parts.Add("disabled");
        if ((flags & ObjectGraph.FlagQueuedFree) != 0) parts.Add("queued free");
        if ((flags & ObjectGraph.FlagScript) != 0) parts.Add("scripted");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string FoldKey(string label)
    {
        int cut = label.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? label.Substring(0, cut) : label;
    }

    private TreeItem Section(TreeItem root, string label)
    {
        TreeItem item = tree.CreateItem(root);
        item.SetText(0, label);
        item.SetCustomColor(0, SectionColor);
        item.SetSelectable(0, false);
        item.SetSelectable(1, false);
        if (foldState.TryGetValue(FoldKey(label), out bool collapsed))
            item.Collapsed = collapsed;
        return item;
    }

    private TreeItem Row(TreeItem parent, string name, string value, string type, string size, ulong link)
    {
        TreeItem item = tree.CreateItem(parent);
        item.SetText(0, name);
        item.SetText(1, value);
        item.SetText(2, type);
        item.SetText(3, size);
        item.SetTextAlignment(3, HorizontalAlignment.Right);
        item.SetCustomColor(1, link != 0 ? LinkColor : ValueColor);
        item.SetCustomColor(2, DimColor);
        item.SetCustomColor(3, DimColor);
        if (link != 0)
        {
            item.SetMetadata(0, link);
            item.SetTooltipText(0, "double click to open instance " + link);
        }
        return item;
    }

    private void OnCollapsed(TreeItem item)
    {
        if (item == null || item.GetParent() != tree.GetRoot())
            return;
        foldState[FoldKey(item.GetText(0))] = item.Collapsed;
    }

    private void OnActivated()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        Variant meta = item.GetMetadata(0);
        if (meta.VariantType != Variant.Type.Int)
            return;
        ulong id = meta.AsUInt64();
        if (id == 0)
            return;
        Navigate(id);
        Navigated?.Invoke(id);
    }

    private void WatchSelected()
    {
        TreeItem item = tree.GetSelected();
        if (item == null || currentId == 0)
        {
            Message?.Invoke("select a property row first");
            return;
        }
        string property = item.GetText(0);
        Source?.RequestWatch(WatchSlot, currentId, property);
        Message?.Invoke("watching " + property + " on instance " + currentId + " in slot " + (WatchSlot + 1));
        WatchSlot = (WatchSlot + 1) % Protocol.WatchSlots;
    }

}
