using System;
using Godot;

namespace DeepProf;

public static class MemoryEstimator
{
    public const long NodeBase = 448;
    public const long ResourceBase = 232;
    public const long ObjectBase = 120;

    public static long SelfBytes(GodotObject obj, out bool estimated)
    {
        estimated = true;
        if (obj == null)
            return 0;
        switch (obj)
        {
            case Image image:
                estimated = false;
                return image.GetDataSize();
            case Texture2D texture:
                return TextureBytes(texture.GetWidth(), texture.GetHeight(), 1, texture.GetFormat(), texture.HasMipmaps());
            case Texture2DArray textureArray:
                return TextureBytes(textureArray.GetWidth(), textureArray.GetHeight(), textureArray.GetLayers(), textureArray.GetFormat(), textureArray.HasMipmaps());
            case TextureLayered layered:
                return TextureBytes(layered.GetWidth(), layered.GetHeight(), layered.GetLayers(), layered.GetFormat(), layered.HasMipmaps());
            case Texture3D volume:
                return TextureBytes(volume.GetWidth(), volume.GetHeight(), volume.GetDepth(), volume.GetFormat(), volume.HasMipmaps());
            case ArrayMesh mesh:
                return ArrayMeshBytes(mesh);
            case MultiMesh multi:
                return MultiMeshBytes(multi);
            case AudioStreamWav wav:
                return WavBytes(wav);
            case AudioStream stream:
                return (long)(stream.GetLength() * 16000.0) + ResourceBase;
            case PackedScene scene:
                return PackedSceneBytes(scene);
            case Shader shader:
                return (shader.Code?.Length ?? 0) * 2L + ResourceBase;
            case Curve curve:
                return curve.PointCount * 40L + ResourceBase;
            case Gradient gradient:
                return gradient.GetPointCount() * 20L + ResourceBase;
            case FontFile font:
                return FontBytes(font);
            case Animation animation:
                return AnimationBytes(animation);
            case Node node:
                return NodeBase + node.GetChildCount(true) * 8L;
            case Resource:
                return ResourceBase;
            default:
                return ObjectBase;
        }
    }

    public static long TextureBytes(int width, int height, int layers, Image.Format format, bool mipmaps)
    {
        if (width <= 0 || height <= 0)
            return 0;
        double bits = FormatBits(format);
        double bytes = width * (double)height * Math.Max(1, layers) * bits / 8.0;
        if (mipmaps)
            bytes *= 4.0 / 3.0;
        return (long)bytes;
    }

    public static double FormatBits(Image.Format format)
    {
        switch (format)
        {
            case Image.Format.L8:
            case Image.Format.R8:
                return 8;
            case Image.Format.La8:
            case Image.Format.Rg8:
            case Image.Format.Rgba4444:
            case Image.Format.Rgb565:
            case Image.Format.Rh:
            case Image.Format.R16:
            case Image.Format.R16I:
                return 16;
            case Image.Format.Rgb8:
                return 24;
            case Image.Format.Rgba8:
            case Image.Format.Rf:
            case Image.Format.Rgh:
            case Image.Format.Rgbe9995:
            case Image.Format.Rg16:
            case Image.Format.Rg16I:
                return 32;
            case Image.Format.Rgb16:
            case Image.Format.Rgb16I:
            case Image.Format.Rgbh:
                return 48;
            case Image.Format.Rgf:
            case Image.Format.Rgbah:
            case Image.Format.Rgba16:
            case Image.Format.Rgba16I:
                return 64;
            case Image.Format.Rgbf:
                return 96;
            case Image.Format.Rgbaf:
                return 128;
            case Image.Format.Dxt1:
            case Image.Format.Etc:
            case Image.Format.Etc2R11:
            case Image.Format.Etc2R11S:
            case Image.Format.Etc2Rgb8:
            case Image.Format.Etc2Rgb8A1:
            case Image.Format.RgtcR:
            case Image.Format.Astc4X4:
            case Image.Format.Astc4X4Hdr:
                return 4;
            case Image.Format.Dxt3:
            case Image.Format.Dxt5:
            case Image.Format.Dxt5RaAsRg:
            case Image.Format.RgtcRg:
            case Image.Format.BptcRgba:
            case Image.Format.BptcRgbf:
            case Image.Format.BptcRgbfu:
            case Image.Format.Etc2Rg11:
            case Image.Format.Etc2Rg11S:
            case Image.Format.Etc2Rgba8:
            case Image.Format.Etc2RaAsRg:
                return 8;
            case Image.Format.Astc8X8:
            case Image.Format.Astc8X8Hdr:
                return 2;
            default:
                return 32;
        }
    }

    public static long ArrayMeshBytes(ArrayMesh mesh)
    {
        long total = ResourceBase;
        int surfaces = mesh.GetSurfaceCount();
        for (int i = 0; i < surfaces; i++)
        {
            Mesh.ArrayFormat format = mesh.SurfaceGetFormat(i);
            long vertices = mesh.SurfaceGetArrayLen(i);
            long indices = mesh.SurfaceGetArrayIndexLen(i);
            total += vertices * VertexStride(format) + indices * (vertices > 65535 ? 4 : 2);
        }
        total += mesh.GetBlendShapeCount() * 1024L;
        return total;
    }

    public static long VertexStride(Mesh.ArrayFormat format)
    {
        long stride = 0;
        bool flat2D = (format & Mesh.ArrayFormat.FlagUse2DVertices) != 0;
        if ((format & Mesh.ArrayFormat.FormatVertex) != 0)
            stride += flat2D ? 8 : 12;
        if ((format & Mesh.ArrayFormat.FormatNormal) != 0)
            stride += 4;
        if ((format & Mesh.ArrayFormat.FormatTangent) != 0)
            stride += 4;
        if ((format & Mesh.ArrayFormat.FormatColor) != 0)
            stride += 4;
        if ((format & Mesh.ArrayFormat.FormatTexUV) != 0)
            stride += 8;
        if ((format & Mesh.ArrayFormat.FormatTexUV2) != 0)
            stride += 8;
        if ((format & Mesh.ArrayFormat.FormatBones) != 0)
            stride += (format & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0 ? 16 : 8;
        if ((format & Mesh.ArrayFormat.FormatWeights) != 0)
            stride += (format & Mesh.ArrayFormat.FlagUse8BoneWeights) != 0 ? 16 : 8;
        for (int custom = 0; custom < 4; custom++)
        {
            if ((format & (Mesh.ArrayFormat)((long)Mesh.ArrayFormat.FormatCustom0 << custom)) != 0)
                stride += 8;
        }
        return stride == 0 ? 12 : stride;
    }

    public static long MultiMeshBytes(MultiMesh multi)
    {
        long perInstance = multi.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D ? 48 : 32;
        if (multi.UseColors)
            perInstance += 16;
        if (multi.UseCustomData)
            perInstance += 16;
        return multi.InstanceCount * perInstance + ResourceBase;
    }

    public static long WavBytes(AudioStreamWav wav)
    {
        long bytesPerSample = wav.Format switch
        {
            AudioStreamWav.FormatEnum.Format8Bits => 1,
            AudioStreamWav.FormatEnum.Format16Bits => 2,
            AudioStreamWav.FormatEnum.ImaAdpcm => 1,
            _ => 2,
        };
        long channels = wav.Stereo ? 2 : 1;
        return (long)(wav.GetLength() * wav.MixRate) * bytesPerSample * channels + ResourceBase;
    }

    public static long PackedSceneBytes(PackedScene scene)
    {
        SceneState state = scene.GetState();
        if (state == null)
            return ResourceBase;
        return state.GetNodeCount() * 320L + state.GetConnectionCount() * 96L + ResourceBase;
    }

    public static long FontBytes(FontFile font)
    {
        long sizes = 0;
        Godot.Collections.Array<Vector2I> cacheSizes = font.GetSizeCacheList(0);
        if (cacheSizes != null)
            sizes = cacheSizes.Count;
        return 262144 + sizes * 65536;
    }

    public static long AnimationBytes(Animation animation)
    {
        long total = ResourceBase;
        int tracks = animation.GetTrackCount();
        for (int i = 0; i < tracks; i++)
            total += animation.TrackGetKeyCount(i) * 48L + 96L;
        return total;
    }
}
