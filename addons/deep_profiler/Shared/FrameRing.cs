using System;

namespace DeepProf;

public sealed class FrameRing
{
    public readonly int Stride;
    public int Capacity { get; private set; }
    public int Count { get; private set; }
    public long Total { get; private set; }

    private float[] data;
    private int head;

    public FrameRing(int stride, int capacity)
    {
        Stride = stride;
        Capacity = Math.Max(16, capacity);
        data = new float[Stride * Capacity];
    }

    public long Oldest => Total - Count;
    public long Newest => Total - 1;

    public void Clear()
    {
        Count = 0;
        head = 0;
        Total = 0;
    }

    public void Resize(int capacity)
    {
        capacity = Math.Max(16, capacity);
        if (capacity == Capacity)
            return;
        float[] fresh = new float[Stride * capacity];
        int keep = Math.Min(Count, capacity);
        long start = Total - keep;
        for (int i = 0; i < keep; i++)
            CopyTo(start + i, fresh, i * Stride);
        data = fresh;
        Capacity = capacity;
        Count = keep;
        head = keep % capacity;
    }

    public void Push(ReadOnlySpan<float> sample)
    {
        int offset = head * Stride;
        int n = Math.Min(Stride, sample.Length);
        for (int i = 0; i < n; i++)
            data[offset + i] = sample[i];
        for (int i = n; i < Stride; i++)
            data[offset + i] = 0f;
        head = (head + 1) % Capacity;
        if (Count < Capacity)
            Count++;
        Total++;
    }

    public bool Has(long index) => index >= Oldest && index < Total;

    private int SlotOf(long index)
    {
        long offset = index - Oldest;
        int start = (head - Count + Capacity) % Capacity;
        return (int)((start + offset) % Capacity);
    }

    public float At(long index, int field)
    {
        if (!Has(index) || field < 0 || field >= Stride)
            return 0f;
        return data[SlotOf(index) * Stride + field];
    }

    public void CopyTo(long index, float[] destination, int destinationOffset)
    {
        if (!Has(index))
            return;
        Array.Copy(data, SlotOf(index) * Stride, destination, destinationOffset, Stride);
    }

    public float[] Range(long from, long to)
    {
        from = Math.Max(from, Oldest);
        to = Math.Min(to, Total);
        int n = (int)Math.Max(0, to - from);
        float[] result = new float[n * Stride];
        for (int i = 0; i < n; i++)
            CopyTo(from + i, result, i * Stride);
        return result;
    }


    public float Max(int field, long from, long to)
    {
        float best = float.MinValue;
        from = Math.Max(from, Oldest);
        to = Math.Min(to, Total);
        for (long i = from; i < to; i++)
        {
            float value = At(i, field);
            if (value > best)
                best = value;
        }
        return best == float.MinValue ? 0f : best;
    }

    public float Min(int field, long from, long to)
    {
        float best = float.MaxValue;
        from = Math.Max(from, Oldest);
        to = Math.Min(to, Total);
        for (long i = from; i < to; i++)
        {
            float value = At(i, field);
            if (value < best)
                best = value;
        }
        return best == float.MaxValue ? 0f : best;
    }

    public float Average(int field, long from, long to)
    {
        from = Math.Max(from, Oldest);
        to = Math.Min(to, Total);
        long n = to - from;
        if (n <= 0)
            return 0f;
        double sum = 0.0;
        for (long i = from; i < to; i++)
            sum += At(i, field);
        return (float)(sum / n);
    }

    public float Percentile(int field, long from, long to, double percentile)
    {
        from = Math.Max(from, Oldest);
        to = Math.Min(to, Total);
        int n = (int)(to - from);
        if (n <= 0)
            return 0f;
        float[] values = new float[n];
        for (int i = 0; i < n; i++)
            values[i] = At(from + i, field);
        Array.Sort(values);
        int index = (int)Math.Clamp(Math.Round((n - 1) * percentile), 0, n - 1);
        return values[index];
    }

}
