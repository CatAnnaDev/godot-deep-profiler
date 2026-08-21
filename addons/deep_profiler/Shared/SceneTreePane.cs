using System;
using System.Collections.Generic;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class SceneTreePane : VBoxContainer
{
    public IGraphSource Source;
    public Action<ulong> ObjectPicked;
    public int PageSize = 300;

    private Tree tree;
    private LineEdit filter;
    private Label status;
    private readonly Dictionary<ulong, TreeItem> items = new Dictionary<ulong, TreeItem>(512);
    private readonly HashSet<ulong> loaded = new HashSet<ulong>();
    private readonly HashSet<ulong> pending = new HashSet<ulong>();
    private ulong rootId;
    private ulong selectedId;
    private bool autoRefresh;
    private double refreshTimer;

    private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.65f);
    private static readonly Color ScriptColor = new Color(0.62f, 0.82f, 1f);
    private static readonly Color HiddenColor = new Color(0.6f, 0.6f, 0.62f);
    private static readonly Color HeavyColor = new Color(0.95f, 0.6f, 0.45f);

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 2);

        HFlowContainer bar = new HFlowContainer();
        AddChild(bar);

        Button refresh = new Button { Text = "Reload", TooltipText = "Reload the remote scene tree" };
        refresh.Pressed += RequestRoot;
        bar.AddChild(refresh);

        Button collapse = new Button { Text = "Collapse", TooltipText = "Collapse every expanded node" };
        collapse.Pressed += CollapseAll;
        bar.AddChild(collapse);

        CheckButton auto = new CheckButton { Text = "Auto", TooltipText = "Reload the tree every two seconds" };
        auto.Toggled += value => autoRefresh = value;
        bar.AddChild(auto);

        filter = new LineEdit
        {
            PlaceholderText = "filter loaded nodes",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0),
        };
        filter.TextChanged += _ => ApplyFilter();
        bar.AddChild(filter);

        status = new Label { Text = string.Empty };
        status.AddThemeFontSizeOverride("font_size", 10);
        status.AddThemeColorOverride("font_color", DimColor);
        bar.AddChild(status);

        tree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Columns = 6,
            ColumnTitlesVisible = true,
            HideRoot = false,
            SelectMode = Tree.SelectModeEnum.Row,
            AllowReselect = true,
        };
        tree.SetColumnTitle(0, "Node");
        tree.SetColumnTitle(1, "Class");
        tree.SetColumnTitle(2, "Children");
        tree.SetColumnTitle(3, "Subtree");
        tree.SetColumnTitle(4, "Retained");
        tree.SetColumnTitle(5, "State");
        tree.SetColumnExpandRatio(0, 7);
        tree.SetColumnExpandRatio(1, 4);
        tree.SetColumnExpandRatio(2, 1);
        tree.SetColumnExpandRatio(3, 1);
        tree.SetColumnExpandRatio(4, 2);
        tree.SetColumnExpandRatio(5, 2);
        tree.SetColumnCustomMinimumWidth(0, 110);
        tree.SetColumnCustomMinimumWidth(1, 70);
        tree.SetColumnCustomMinimumWidth(4, 64);
        tree.ItemSelected += OnItemSelected;
        tree.ItemCollapsed += OnItemCollapsed;
        tree.ItemActivated += OnItemActivated;
        AddChild(tree);
    }

    public override void _Process(double delta)
    {
        if (!autoRefresh || Source == null || !Source.Live)
            return;
        refreshTimer += delta;
        if (refreshTimer < 2.0)
            return;
        refreshTimer = 0.0;
        RequestRoot();
    }

    public void Clear()
    {
        tree?.Clear();
        items.Clear();
        loaded.Clear();
        pending.Clear();
        rootId = 0;
    }

    public void RequestRoot()
    {
        if (Source == null)
            return;
        pending.Clear();
        Source.RequestTree(rootId, 0, PageSize);
    }

    public void ApplyPayload(GDDict payload)
    {
        if (payload == null || !payload.TryGetValue("ok", out Variant ok) || !ok.AsBool())
            return;
        ulong id = payload["id"].AsUInt64();
        pending.Remove(id);
        TreeItem parentItem;
        if (items.TryGetValue(id, out TreeItem existing))
        {
            parentItem = existing;
            ClearChildren(parentItem);
        }
        else
        {
            tree.Clear();
            items.Clear();
            loaded.Clear();
            rootId = id;
            parentItem = tree.CreateItem();
            int subtree = payload.TryGetValue("sub_nodes", out Variant sub) ? sub.AsInt32() - 1 : 0;
            long retained = payload.TryGetValue("retained", out Variant retainedValue) ? retainedValue.AsInt64() : 0;
            int rootFlags = payload.TryGetValue("flags", out Variant flagsValue) ? flagsValue.AsInt32() : 0;
            bool rootPartial = payload.TryGetValue("partial", out Variant partialValue) && partialValue.AsBool();
            bool rootScript = !string.IsNullOrEmpty(payload["script"].AsString());
            FillRow(parentItem, id, payload["name"].AsString(), payload["class"].AsString(),
                payload["child_count"].AsInt32(), subtree, retained, rootFlags, rootPartial, rootScript);
            items[id] = parentItem;
            parentItem.Collapsed = false;
        }

        GDArray children = payload["children"].AsGodotArray();
        foreach (Variant entry in children)
        {
            GDDict child = entry.AsGodotDictionary();
            ulong childId = child["id"].AsUInt64();
            TreeItem item = tree.CreateItem(parentItem);
            FillRow(item, childId, child["name"].AsString(), child["class"].AsString(),
                child["children"].AsInt32(), child["desc"].AsInt32(), child["bytes"].AsInt64(),
                child["flags"].AsInt32(), child["partial"].AsBool(), child["script"].AsBool());
            items[childId] = item;
            if (child["children"].AsInt32() > 0)
            {
                TreeItem placeholder = tree.CreateItem(item);
                placeholder.SetText(0, "loading");
                placeholder.SetSelectable(0, false);
                placeholder.SetCustomColor(0, DimColor);
                item.Collapsed = true;
            }
        }
        loaded.Add(id);

        int shown = children.Count;
        int total = payload["child_count"].AsInt32();
        if (shown < total)
        {
            TreeItem more = tree.CreateItem(parentItem);
            more.SetText(0, "... " + (total - shown) + " more children");
            more.SetMetadata(0, id);
            more.SetCustomColor(0, DimColor);
        }
        status.Text = items.Count + " nodes loaded";
        ApplyFilter();
        if (selectedId != 0 && items.TryGetValue(selectedId, out TreeItem restore))
            restore.SetCustomBgColor(0, new Color(0.35f, 0.45f, 0.75f, 0.35f));
    }

    private void ClearChildren(TreeItem item)
    {
        TreeItem child = item.GetFirstChild();
        while (child != null)
        {
            TreeItem next = child.GetNext();
            ulong childId = MetaId(child);
            if (childId != 0)
                items.Remove(childId);
            item.RemoveChild(child);
            child.Free();
            child = next;
        }
    }

    private void FillRow(TreeItem item, ulong id, string name, string className, int childCount, int descendants, long bytes, int flags, bool partial, bool hasScript)
    {
        item.SetText(0, name);
        item.SetText(1, className);
        item.SetText(2, childCount > 0 ? childCount.ToString() : string.Empty);
        item.SetText(3, descendants > 0 ? Fmt.Count(descendants) : string.Empty);
        item.SetText(4, (partial ? "> " : string.Empty) + Fmt.Bytes(bytes));
        item.SetText(5, StateText(flags));
        item.SetTextAlignment(2, HorizontalAlignment.Right);
        item.SetTextAlignment(3, HorizontalAlignment.Right);
        item.SetTextAlignment(4, HorizontalAlignment.Right);
        item.SetMetadata(0, id);
        item.SetTooltipText(0, "instance " + id);
        if (hasScript)
            item.SetCustomColor(0, ScriptColor);
        if ((flags & ObjectGraph.FlagVisible) == 0)
            item.SetCustomColor(1, HiddenColor);
        if (bytes > 8 * 1024 * 1024)
            item.SetCustomColor(4, HeavyColor);
        item.SetCustomColor(2, DimColor);
        item.SetCustomColor(3, DimColor);
        item.SetCustomColor(5, DimColor);
    }

    private static string StateText(int flags)
    {
        Span<char> buffer = stackalloc char[6];
        int index = 0;
        buffer[index++] = (flags & ObjectGraph.FlagVisible) != 0 ? 'v' : '-';
        buffer[index++] = (flags & ObjectGraph.FlagProcess) != 0 ? 'p' : '-';
        buffer[index++] = (flags & ObjectGraph.FlagPhysics) != 0 ? 'f' : '-';
        buffer[index++] = (flags & ObjectGraph.FlagInput) != 0 ? 'i' : '-';
        buffer[index++] = (flags & ObjectGraph.FlagDisabled) != 0 ? 'x' : '-';
        buffer[index++] = (flags & ObjectGraph.FlagQueuedFree) != 0 ? 'q' : '-';
        return new string(buffer);
    }

    private static ulong MetaId(TreeItem item)
    {
        Variant meta = item.GetMetadata(0);
        return meta.VariantType == Variant.Type.Int ? meta.AsUInt64() : 0UL;
    }

    private void OnItemSelected()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        ulong id = MetaId(item);
        if (id == 0)
            return;
        selectedId = id;
        ObjectPicked?.Invoke(id);
        Source?.RequestObject(id);
    }

    private void OnItemActivated()
    {
        TreeItem item = tree.GetSelected();
        if (item == null)
            return;
        ulong id = MetaId(item);
        if (id == 0)
            return;
        if (item.GetText(0).StartsWith("...", StringComparison.Ordinal))
        {
            Source?.RequestTree(id, 0, PageSize * 4);
            return;
        }
        Source?.RequestHighlight(id);
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (item == null || item.Collapsed)
            return;
        ulong id = MetaId(item);
        if (id == 0 || loaded.Contains(id) || pending.Contains(id))
            return;
        pending.Add(id);
        Source?.RequestTree(id, 0, PageSize);
    }

    private void CollapseAll()
    {
        foreach (KeyValuePair<ulong, TreeItem> pair in items)
        {
            if (pair.Key != rootId && pair.Value != null)
                pair.Value.Collapsed = true;
        }
    }

    private void ApplyFilter()
    {
        string needle = filter.Text.Trim();
        bool empty = needle.Length == 0;
        foreach (KeyValuePair<ulong, TreeItem> pair in items)
        {
            TreeItem item = pair.Value;
            if (item == null)
                continue;
            bool match = empty
                         || item.GetText(0).Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || item.GetText(1).Contains(needle, StringComparison.OrdinalIgnoreCase);
            item.SetCustomColor(0, match ? (item.GetText(0).Length > 0 ? new Color(0.93f, 0.94f, 0.97f) : DimColor) : DimColor);
            if (!empty && match)
            {
                TreeItem parent = item.GetParent();
                while (parent != null)
                {
                    parent.Collapsed = false;
                    parent = parent.GetParent();
                }
            }
        }
    }

    public void SelectObject(ulong id)
    {
        if (!items.TryGetValue(id, out TreeItem item) || item == null)
            return;
        TreeItem parent = item.GetParent();
        while (parent != null)
        {
            parent.Collapsed = false;
            parent = parent.GetParent();
        }
        item.Select(0);
        tree.ScrollToItem(item, true);
    }
}
