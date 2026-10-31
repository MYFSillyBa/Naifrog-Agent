using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Naifrog.Animation;

internal sealed record AnimationFrame(string Path, string Sha256);
internal sealed record AnimationClip(string Name, string Emotion, double Fps, bool Loop, int Priority,
    string? TransitionIn, string? TransitionOut, int Width, int Height, double GroundY,
    IReadOnlyList<AnimationFrame> Frames)
{
    public double DurationMs => Frames.Count / Fps * 1000;
}

/// <summary>Lazy, bounded reader for a trusted-format but untrusted-content PNG animation pack.</summary>
internal sealed class AnimationLibrary : IDisposable
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private const long MaxClipBytes = 64L * 1024 * 1024;
    private const long MaxDecodedClipBytes = 128L * 1024 * 1024;
    private const long MaxCacheBytes = 192L * 1024 * 1024;
    private readonly string root;
    private readonly Dictionary<string, AnimationClip?> metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> rejectedFrames = new(StringComparer.Ordinal);
    private long cacheBytes, sequence;
    private sealed record CacheEntry(IReadOnlyList<BitmapSource> Frames, long Bytes) { public long Used { get; set; } }
    public string LastError { get; private set; } = "";
    internal long DecodedCacheBytes => cacheBytes;

    public AnimationLibrary(string root) { this.root = Path.GetFullPath(root); }

    public AnimationClip? TryGetInfo(string name)
    {
        if (!SafeName(name)) return null;
        if (metadata.TryGetValue(name, out var known)) return known;
        if (metadata.Count >= 128) return null;
        AnimationClip? result = null;
        try
        {
            var directory = Path.Combine(root, name);
            CheckNoLinks(directory);
            var manifest = Path.Combine(directory, "manifest.json");
            CheckNoLinks(manifest);
            using var document = JsonDocument.Parse(ReadBounded(manifest, 256 * 1024), new JsonDocumentOptions { MaxDepth = 16 });
            var json = document.RootElement;
            string clipName = Text(json, "name");
            if (clipName != name) throw new InvalidDataException("片段名称不匹配");
            var emotion = Text(json, "emotion");
            if (!SafeName(emotion)) throw new InvalidDataException("情绪名称无效");
            double fps = Number(json, "fps");
            int frameCount = Integer(json, "frameCount");
            if (fps < 1 || fps > 60 || frameCount < 1 || frameCount > 240) throw new InvalidDataException("帧数或帧率超出限制");
            var canvas = Property(json, "canvas");
            int width = Integer(canvas, "width"), height = Integer(canvas, "height");
            if (width < 1 || width > 2048 || height < 1 || height > 2048 || (long)width * height * 4 * frameCount > MaxDecodedClipBytes)
                throw new InvalidDataException("画布或解码内存超出限制");
            double groundY = Number(json, "groundY");
            if (groundY < 0 || groundY > height) throw new InvalidDataException("脚底基线无效");
            bool loop = Property(json, "loop").GetBoolean();
            int priority = Integer(json, "priority");
            if (priority < 0 || priority > 100) throw new InvalidDataException("优先级无效");
            string? entry = OptionalName(json, "transitionIn"), exit = OptionalName(json, "transitionOut");
            var frames = Property(json, "frames");
            if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() != frameCount) throw new InvalidDataException("帧数不匹配");
            var list = new List<AnimationFrame>(frameCount);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long encodedBytes = 0;
            foreach (var frame in frames.EnumerateArray())
            {
                string relative = Text(frame, "file"), hash = Text(frame, "sha256");
                if (relative.Length > 240 || Path.IsPathRooted(relative) || relative.Contains(':') ||
                    relative.Split('/', '\\').Any(part => part is ".." or "." or "") || !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("帧路径无效");
                string path = Path.GetFullPath(Path.Combine(directory, relative));
                if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !unique.Add(path))
                    throw new InvalidDataException("帧路径越界或重复");
                CheckNoLinks(path);
                if (!Regex.IsMatch(hash, "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidDataException("帧哈希无效");
                long bytes = new FileInfo(path).Length;
                if (bytes < 33 || bytes > MaxFrameBytes || (encodedBytes += bytes) > MaxClipBytes) throw new InvalidDataException("图片文件超出限制");
                list.Add(new AnimationFrame(path, hash));
            }
            result = new AnimationClip(name, emotion, fps, loop, priority, entry, exit, width, height, groundY, list.AsReadOnly());
        }
        catch (Exception ex) when (IsResourceError(ex)) { LastError = $"片段 {name} 不可用：{ResourceReason(ex)}"; }
        metadata.Add(name, result);
        return result;
    }

    public IReadOnlyList<BitmapSource>? GetFrames(AnimationClip clip)
    {
        if (cache.TryGetValue(clip.Name, out var cached)) { cached.Used = ++sequence; return cached.Frames; }
        if (rejectedFrames.Contains(clip.Name)) return null;
        try
        {
            long bytesNeeded = (long)clip.Width * clip.Height * 4 * clip.Frames.Count;
            while (cache.Count > 0 && (cacheBytes + bytesNeeded > MaxCacheBytes || cache.Count >= 4))
            {
                var oldest = cache.OrderBy(pair => pair.Value.Used).First();
                cacheBytes -= oldest.Value.Bytes; cache.Remove(oldest.Key);
            }
            var frames = new List<BitmapSource>(clip.Frames.Count);
            foreach (var frame in clip.Frames)
            {
                CheckNoLinks(frame.Path);
                var bytes = ReadBounded(frame.Path, MaxFrameBytes);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(frame.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图片哈希不符");
                ValidatePngHeader(bytes, clip.Width, clip.Height);
                using var stream = new MemoryStream(bytes, false);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != clip.Width || decoder.Frames[0].PixelHeight != clip.Height)
                    throw new InvalidDataException("图片画布不匹配");
                BitmapSource bitmap = decoder.Frames[0];
                bitmap.Freeze(); frames.Add(bitmap);
            }
            var result = frames.AsReadOnly();
            cache.Add(clip.Name, new CacheEntry(result, bytesNeeded) { Used = ++sequence });
            cacheBytes += bytesNeeded;
            return result;
        }
        catch (Exception ex) when (IsResourceError(ex))
        {
            rejectedFrames.Add(clip.Name); LastError = $"片段 {clip.Name} 不可用：{ResourceReason(ex)}"; return null;
        }
    }

    private static void ValidatePngHeader(byte[] bytes, int width, int height)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
            throw new InvalidDataException("不是 PNG 图片");
        int pngWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int pngHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (pngWidth != width || pngHeight != height || bytes[24] > 8) throw new InvalidDataException("PNG 尺寸或位深超出限制");
    }

    private static byte[] ReadBounded(string path, int max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max || stream.Length < 1) throw new InvalidDataException("资源大小超出限制");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1) throw new InvalidDataException("资源在读取时变化");
        return data;
    }

    // Check every ancestor: a pack under a junction is also rejected; no directory traversal is performed.
    private static void CheckNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不允许符号链接或目录联接");
            current = Path.GetDirectoryName(current);
        }
    }
    private static bool SafeName(string value) => value.Length is > 0 and <= 64 && Regex.IsMatch(value, "\\A[a-z0-9_-]+\\z");
    private static JsonElement Property(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new InvalidDataException("清单缺少字段 " + name);
    }
    private static string Text(JsonElement value, string name) => Property(value, name).GetString() ?? throw new InvalidDataException("清单文字无效");
    private static int Integer(JsonElement value, string name) => Property(value, name).GetInt32();
    private static double Number(JsonElement value, string name)
    {
        double number = Property(value, name).GetDouble();
        return double.IsFinite(number) ? number : throw new InvalidDataException("清单数值无效");
    }
    private static string? OptionalName(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var text = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                if (string.IsNullOrEmpty(text)) return null;
                if (!SafeName(text)) throw new InvalidDataException("过渡片段名称无效");
                return text;
            }
        return null;
    }
    private static bool IsResourceError(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException or FormatException or OverflowException;
    private static string ResourceReason(Exception ex) => ex is InvalidDataException ? ex.Message : "文件缺失、损坏或格式不受支持";
    public void Dispose() { cache.Clear(); metadata.Clear(); rejectedFrames.Clear(); cacheBytes = 0; }
}
