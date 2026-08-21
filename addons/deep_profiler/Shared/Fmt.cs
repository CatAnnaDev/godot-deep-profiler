using System;
using System.Globalization;
using Godot;

namespace DeepProf;

public static class Fmt
{
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB" };
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Bytes(double value)
    {
        if (value <= 0.0)
            return "0 B";
        int unit = 0;
        while (value >= 1024.0 && unit < ByteUnits.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        string format = value >= 100.0 || unit == 0 ? "0" : value >= 10.0 ? "0.0" : "0.00";
        return value.ToString(format, Culture) + " " + ByteUnits[unit];
    }

    public static string Ms(double value)
    {
        if (value >= 100.0)
            return value.ToString("0", Culture) + " ms";
        if (value >= 10.0)
            return value.ToString("0.0", Culture) + " ms";
        if (value >= 0.01 || value <= 0.0)
            return value.ToString("0.00", Culture) + " ms";
        return (value * 1000.0).ToString("0", Culture) + " us";
    }

    public static string Count(double value)
    {
        double abs = Math.Abs(value);
        if (abs >= 1_000_000_000.0)
            return (value / 1_000_000_000.0).ToString("0.00", Culture) + "G";
        if (abs >= 1_000_000.0)
            return (value / 1_000_000.0).ToString("0.00", Culture) + "M";
        if (abs >= 10_000.0)
            return (value / 1000.0).ToString("0.0", Culture) + "k";
        if (abs >= 1.0 || value == 0.0)
            return value.ToString("0", Culture);
        return value.ToString("0.###", Culture);
    }

    public static string Unit(double value, FieldUnit unit)
    {
        switch (unit)
        {
            case FieldUnit.Milliseconds: return Ms(value);
            case FieldUnit.Kilobytes: return Bytes(value * 1024.0);
            case FieldUnit.Megabytes: return Bytes(value * 1024.0 * 1024.0);
            case FieldUnit.Ratio: return value.ToString("0.00", Culture);
            case FieldUnit.Count: return Count(value);
            default: return Number(value);
        }
    }

    public static string Number(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return value.ToString("0", Culture);
        return value.ToString("0.###", Culture);
    }

    public static string Distance(double meters)
    {
        return meters.ToString(meters >= 10.0 ? "0" : "0.0", Culture) + " m";
    }

    public static string Percent(double ratio)
    {
        return (ratio * 100.0).ToString(ratio >= 0.1 ? "0.0" : "0.00", Culture) + "%";
    }


    public static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? string.Empty;
        return string.Concat(value.AsSpan(0, Math.Max(1, max - 1)), "…");
    }

    public static string Variant(Variant value, int max = 96)
    {
        switch (value.VariantType)
        {
            case Godot.Variant.Type.Nil:
                return "null";
            case Godot.Variant.Type.Object:
            {
                GodotObject obj = value.AsGodotObject();
                if (obj == null)
                    return "null";
                return Describe(obj);
            }
            case Godot.Variant.Type.Float:
                return Number(value.AsDouble());
            case Godot.Variant.Type.String:
            case Godot.Variant.Type.StringName:
                return "\"" + Trim(value.AsString(), max) + "\"";
            case Godot.Variant.Type.Array:
                return "Array[" + value.AsGodotArray().Count + "]";
            case Godot.Variant.Type.Dictionary:
                return "Dictionary[" + value.AsGodotDictionary().Count + "]";
            case Godot.Variant.Type.PackedByteArray:
                return "PackedByteArray[" + value.AsByteArray().Length + "]";
            case Godot.Variant.Type.PackedInt32Array:
                return "PackedInt32Array[" + value.AsInt32Array().Length + "]";
            case Godot.Variant.Type.PackedInt64Array:
                return "PackedInt64Array[" + value.AsInt64Array().Length + "]";
            case Godot.Variant.Type.PackedFloat32Array:
                return "PackedFloat32Array[" + value.AsFloat32Array().Length + "]";
            case Godot.Variant.Type.PackedFloat64Array:
                return "PackedFloat64Array[" + value.AsFloat64Array().Length + "]";
            case Godot.Variant.Type.PackedStringArray:
                return "PackedStringArray[" + value.AsStringArray().Length + "]";
            case Godot.Variant.Type.PackedVector2Array:
                return "PackedVector2Array[" + value.AsVector2Array().Length + "]";
            case Godot.Variant.Type.PackedVector3Array:
                return "PackedVector3Array[" + value.AsVector3Array().Length + "]";
            case Godot.Variant.Type.PackedColorArray:
                return "PackedColorArray[" + value.AsColorArray().Length + "]";
            default:
                return Trim(value.ToString(), max);
        }
    }

    public static string Describe(GodotObject obj)
    {
        if (obj == null)
            return "null";
        if (obj is Node node)
            return node.Name + " <" + obj.GetClass() + ">";
        if (obj is Resource res)
        {
            string path = res.ResourcePath;
            if (!string.IsNullOrEmpty(path))
                return obj.GetClass() + " \"" + path.GetFile() + "\"";
            string name = res.ResourceName;
            if (!string.IsNullOrEmpty(name))
                return obj.GetClass() + " \"" + name + "\"";
        }
        return obj.GetClass() + " #" + obj.GetInstanceId();
    }

    public static Color HeatColor(double ratio)
    {
        float t = (float)Math.Clamp(ratio, 0.0, 1.0);
        if (t < 0.5f)
            return new Color(0.35f, 0.78f, 0.45f).Lerp(new Color(0.95f, 0.79f, 0.30f), t * 2f);
        return new Color(0.95f, 0.79f, 0.30f).Lerp(new Color(0.94f, 0.32f, 0.32f), (t - 0.5f) * 2f);
    }
}
