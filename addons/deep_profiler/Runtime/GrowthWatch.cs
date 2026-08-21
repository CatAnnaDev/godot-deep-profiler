using System;

namespace DeepProf;

public sealed class GrowthWatch
{
    private const int Capacity = 12;

    private readonly float[] history = new float[Capacity];
    private readonly string label;
    private readonly float minimumGrowth;
    private readonly float minimumRatio;
    private int count;
    private int head;
    private double cooldown;

    public GrowthWatch(string label, float minimumGrowth, float minimumRatio)
    {
        this.label = label;
        this.minimumGrowth = minimumGrowth;
        this.minimumRatio = minimumRatio;
    }

    public string Push(float value, double delta, double sampleSeconds)
    {
        if (cooldown > 0.0)
            cooldown -= delta;
        history[head] = value;
        head = (head + 1) % Capacity;
        if (count < Capacity)
            count++;
        if (count < Capacity || cooldown > 0.0)
            return null;

        float first = history[head];
        float last = history[(head + Capacity - 1) % Capacity];
        float growth = last - first;
        if (growth < minimumGrowth || first > 0f && growth / first < minimumRatio)
            return null;

        int rising = 0;
        float previous = first;
        for (int i = 1; i < count; i++)
        {
            float current = history[(head + i) % Capacity];
            if (current >= previous)
                rising++;
            previous = current;
        }
        if (rising < (count - 1) * 0.8f)
            return null;

        cooldown = 20.0;
        double seconds = sampleSeconds * (count - 1);
        return label + " +" + Fmt.Count(growth) + " in " + seconds.ToString("0") + " s ("
               + Fmt.Count(growth / seconds) + " per second, now " + Fmt.Count(last) + ")";
    }

    public void Reset()
    {
        count = 0;
        head = 0;
        cooldown = 0.0;
    }
}
