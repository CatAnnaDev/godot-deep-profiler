using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;

namespace DeepProf;

public static class ScopeNames
{
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, int> Ids = new Dictionary<string, int>(512, StringComparer.Ordinal);
	private static string[] table = new string[512];
	private static int count;

	public static int Count => Volatile.Read(ref count);

	public static int Intern(string name)
	{
		if (string.IsNullOrEmpty(name))
			name = "<unnamed>";
		lock (Sync)
		{
			if (Ids.TryGetValue(name, out int id))
				return id;
			if (count == table.Length)
				Array.Resize(ref table, table.Length * 2);
			id = count;
			table[id] = name;
			Ids[name] = id;
			Volatile.Write(ref count, count + 1);
			return id;
		}
	}

	public static string Get(int id)
	{
		lock (Sync)
			return id >= 0 && id < count ? table[id] : "?";
	}

	public static string[] Range(int from, int to)
	{
		lock (Sync)
		{
			from = Math.Max(0, from);
			to = Math.Min(to, count);
			if (to <= from)
				return Array.Empty<string>();
			string[] slice = new string[to - from];
			Array.Copy(table, from, slice, 0, slice.Length);
			return slice;
		}
	}
}

public struct ScopeNode
{
	public int NameId;
	public int Parent;
	public int FirstChild;
	public int LastChild;
	public int NextSibling;
	public int Calls;
	public int Open;
	public long Start;
	public long AllocStart;
	public long Total;
	public long Alloc;
	public int ObjectStart;
	public int Objects;
}

public sealed class ScopeTree
{
	public const int MaxNodes = 8192;

	public ScopeNode[] Nodes = new ScopeNode[256];
	public int Count;
	public int Current;
	public int Frames;
	public long Overflowed;

	public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

	public void Reset(int rootNameId)
	{
		Count = 0;
		Frames = 0;
		Overflowed = 0;
		Current = NewNode(rootNameId, -1);
	}

	public bool IsEmpty => Count <= 1 && (Count == 0 || Nodes[0].Calls == 0);

	public double RootMs => Count > 0 ? Nodes[0].Total * TicksToMs : 0.0;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int NewNode(int nameId, int parent)
	{
		if (Count == Nodes.Length)
		{
			if (Nodes.Length >= MaxNodes)
			{
				Overflowed++;
				return parent < 0 ? 0 : parent;
			}
			Array.Resize(ref Nodes, Math.Min(MaxNodes, Nodes.Length * 2));
		}
		int index = Count++;
		ref ScopeNode node = ref Nodes[index];
		node.NameId = nameId;
		node.Parent = parent;
		node.FirstChild = -1;
		node.LastChild = -1;
		node.NextSibling = -1;
		node.Calls = 0;
		node.Open = 0;
		node.Start = 0;
		node.AllocStart = 0;
		node.Total = 0;
		node.Alloc = 0;
		node.ObjectStart = 0;
		node.Objects = 0;
		if (parent >= 0)
		{
			ref ScopeNode p = ref Nodes[parent];
			if (p.FirstChild < 0)
				p.FirstChild = index;
			else
				Nodes[p.LastChild].NextSibling = index;
			p.LastChild = index;
		}
		return index;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int FindOrAdd(int parent, int nameId)
	{
		for (int child = Nodes[parent].FirstChild; child >= 0; child = Nodes[child].NextSibling)
		{
			if (Nodes[child].NameId == nameId)
				return child;
		}
		return NewNode(nameId, parent);
	}

	public void Begin(int nameId, bool trackAlloc, bool trackObjects)
	{
		if (Count == 0)
			Reset(nameId);
		int index = FindOrAdd(Current, nameId);
		ref ScopeNode node = ref Nodes[index];
		node.Calls++;
		if (node.Open++ == 0)
		{
			node.Start = Stopwatch.GetTimestamp();
			node.AllocStart = trackAlloc ? GC.GetAllocatedBytesForCurrentThread() : 0;
			node.ObjectStart = trackObjects ? ObjectCount() : 0;
		}
		Current = index;
	}

	public void End(bool trackAlloc, bool trackObjects)
	{
		int index = Current;
		if (index <= 0 && Count <= 1)
			return;
		ref ScopeNode node = ref Nodes[index];
		if (node.Open > 0 && --node.Open == 0)
		{
			node.Total += Stopwatch.GetTimestamp() - node.Start;
			if (trackAlloc)
				node.Alloc += GC.GetAllocatedBytesForCurrentThread() - node.AllocStart;
			if (trackObjects)
				node.Objects += Math.Max(0, ObjectCount() - node.ObjectStart);
		}
		Current = node.Parent >= 0 ? node.Parent : 0;
	}

	private static int ObjectCount()
	{
		if (System.Environment.CurrentManagedThreadId != Prof.MainManagedThreadId)
			return 0;
		return (int)Performance.GetMonitor(Performance.Monitor.ObjectCount);
	}

	public void CloseAll(bool trackAlloc, bool trackObjects)
	{
		int guard = 0;
		while (Current > 0 && guard++ < MaxNodes)
			End(trackAlloc, trackObjects);
		if (Count > 0 && Nodes[0].Open > 0)
			End(trackAlloc, trackObjects);
	}

	public void StampRoot(long ticks, int calls)
	{
		if (Count == 0)
			return;
		Nodes[0].Total = ticks;
		Nodes[0].Calls = calls;
	}

	public void MergeInto(ScopeTree destination)
	{
		if (Count == 0)
			return;
		if (destination.Count == 0)
			destination.Reset(Nodes[0].NameId);
		destination.Frames++;
		MergeNode(0, 0, destination);
	}

	private void MergeNode(int source, int target, ScopeTree destination)
	{
		ref ScopeNode src = ref Nodes[source];
		ref ScopeNode dst = ref destination.Nodes[target];
		dst.Calls += src.Calls;
		dst.Total += src.Total;
		dst.Alloc += src.Alloc;
		dst.Objects += src.Objects;
		for (int child = src.FirstChild; child >= 0; child = Nodes[child].NextSibling)
		{
			int mapped = destination.FindOrAdd(target, Nodes[child].NameId);
			MergeNode(child, mapped, destination);
		}
	}

	public void CopyFrom(ScopeTree source)
	{
		if (Nodes.Length < source.Count)
			Nodes = new ScopeNode[source.Count];
		Array.Copy(source.Nodes, Nodes, source.Count);
		Count = source.Count;
		Current = 0;
		Frames = source.Frames;
		Overflowed = source.Overflowed;
	}

	public Godot.Collections.Dictionary Serialize(string threadName, string kind, int frames)
	{
		int n = Count;
		int[] ints = new int[n * 4];
		double[] floats = new double[n * 3];
		for (int i = 0; i < n; i++)
		{
			ref ScopeNode node = ref Nodes[i];
			long childTotal = 0;
			for (int child = node.FirstChild; child >= 0; child = Nodes[child].NextSibling)
				childTotal += Nodes[child].Total;
			ints[i * 4] = node.NameId;
			ints[i * 4 + 1] = node.Parent;
			ints[i * 4 + 2] = node.Calls;
			ints[i * 4 + 3] = node.Objects;
			floats[i * 3] = node.Total * TicksToMs;
			floats[i * 3 + 1] = Math.Max(0.0, (node.Total - childTotal) * TicksToMs);
			floats[i * 3 + 2] = node.Alloc;
		}
		return new Godot.Collections.Dictionary
		{
			{ "kind", kind },
			{ "thread", threadName },
			{ "count", n },
			{ "frames", frames },
			{ "overflow", Overflowed },
			{ "ints", Variant.From(ints) },
			{ "floats", Variant.From(floats) },
		};
	}
}
