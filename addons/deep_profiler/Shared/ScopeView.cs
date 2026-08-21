using System;
using Godot;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

public sealed class ScopeView
{
    public int Count;
    public int[] NameId = Array.Empty<int>();
    public int[] Parent = Array.Empty<int>();
    public int[] Calls = Array.Empty<int>();
    public int[] FirstChild = Array.Empty<int>();
    public int[] NextSibling = Array.Empty<int>();
    public int[] Depth = Array.Empty<int>();
    public double[] Total = Array.Empty<double>();
    public double[] Self = Array.Empty<double>();
    public double[] Alloc = Array.Empty<double>();
    public int Frames = 1;
    public long Overflow;
    public string Thread = "main";
    public string Kind = "window";
    public Func<int, string> Resolver = ScopeNames.Get;

    public bool IsEmpty => Count <= 1;
    public double RootTotal => Count > 0 ? Total[0] : 0.0;
    public double RootSelf => Count > 0 ? Self[0] : 0.0;

    public string NameOf(int index)
    {
        if (index < 0 || index >= Count)
            return "?";
        return Resolver(NameId[index]) ?? "?";
    }

    private void Allocate(int count)
    {
        Count = count;
        NameId = new int[count];
        Parent = new int[count];
        Calls = new int[count];
        Total = new double[count];
        Self = new double[count];
        Alloc = new double[count];
    }

    public void BuildLinks()
    {
        FirstChild = new int[Count];
        NextSibling = new int[Count];
        Depth = new int[Count];
        int[] lastChild = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            FirstChild[i] = -1;
            NextSibling[i] = -1;
            lastChild[i] = -1;
        }
        for (int i = 0; i < Count; i++)
        {
            int parent = Parent[i];
            if (parent < 0 || parent >= Count || parent == i)
            {
                Depth[i] = 0;
                continue;
            }
            Depth[i] = Depth[parent] + 1;
            if (FirstChild[parent] < 0)
                FirstChild[parent] = i;
            else
                NextSibling[lastChild[parent]] = i;
            lastChild[parent] = i;
        }
    }

    public static ScopeView FromDict(GDDict source, Func<int, string> resolver)
    {
        ScopeView view = new ScopeView { Resolver = resolver ?? ScopeNames.Get };
        if (source == null || source.Count == 0)
            return view;
        int count = source.TryGetValue("count", out Variant countValue) ? countValue.AsInt32() : 0;
        if (count <= 0)
            return view;
        int[] ints = source["ints"].AsInt32Array();
        double[] floats = source["floats"].AsFloat64Array();
        if (ints.Length < count * 3 || floats.Length < count * 3)
            return view;
        view.Allocate(count);
        for (int i = 0; i < count; i++)
        {
            view.NameId[i] = ints[i * 3];
            view.Parent[i] = ints[i * 3 + 1];
            view.Calls[i] = ints[i * 3 + 2];
            view.Total[i] = floats[i * 3];
            view.Self[i] = floats[i * 3 + 1];
            view.Alloc[i] = floats[i * 3 + 2];
        }
        view.Frames = source.TryGetValue("frames", out Variant frames) ? Math.Max(1, frames.AsInt32()) : 1;
        view.Thread = source.TryGetValue("thread", out Variant thread) ? thread.AsString() : "main";
        view.Kind = source.TryGetValue("kind", out Variant kind) ? kind.AsString() : "window";
        view.Overflow = source.TryGetValue("overflow", out Variant overflow) ? overflow.AsInt64() : 0;
        view.BuildLinks();
        return view;
    }

    public static ScopeView FromTree(ScopeTree tree, string thread, string kind, int frames)
    {
        ScopeView view = new ScopeView { Thread = thread, Kind = kind, Frames = Math.Max(1, frames), Resolver = ScopeNames.Get };
        if (tree == null || tree.Count == 0)
            return view;
        view.Allocate(tree.Count);
        for (int i = 0; i < tree.Count; i++)
        {
            ref ScopeNode node = ref tree.Nodes[i];
            long childTotal = 0;
            for (int child = node.FirstChild; child >= 0; child = tree.Nodes[child].NextSibling)
                childTotal += tree.Nodes[child].Total;
            view.NameId[i] = node.NameId;
            view.Parent[i] = node.Parent;
            view.Calls[i] = node.Calls;
            view.Total[i] = node.Total * ScopeTree.TicksToMs;
            view.Self[i] = Math.Max(0.0, (node.Total - childTotal) * ScopeTree.TicksToMs);
            view.Alloc[i] = node.Alloc;
        }
        view.Overflow = tree.Overflowed;
        view.BuildLinks();
        return view;
    }

}
