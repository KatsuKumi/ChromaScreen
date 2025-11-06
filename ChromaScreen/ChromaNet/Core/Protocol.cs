using MemoryPack;

namespace ChromaNet.Core;

/// <summary>
/// Frame packet containing one or more dirty regions
/// Uses MemoryPack for zero-allocation serialization
/// </summary>
[MemoryPackable]
public partial class FramePacket
{
    /// <summary>
    /// Sequential frame ID for ordering
    /// </summary>
    public uint FrameId { get; set; }

    /// <summary>
    /// Total screen width in pixels
    /// </summary>
    public ushort ScreenWidth { get; set; }

    /// <summary>
    /// Total screen height in pixels
    /// </summary>
    public ushort ScreenHeight { get; set; }

    /// <summary>
    /// Chroma key mode
    /// </summary>
    public ChromaMode ChromaMode { get; set; }

    /// <summary>
    /// Chroma key threshold (0-255)
    /// Only used if ChromaMode != None
    /// </summary>
    public byte ChromaThreshold { get; set; }

    /// <summary>
    /// Timestamp when frame was captured (microseconds)
    /// </summary>
    public long TimestampUs { get; set; }

    /// <summary>
    /// List of changed regions in this frame
    /// Empty list = no changes this frame
    /// </summary>
    public List<DirtyRegion> Regions { get; set; } = new();

    public FramePacket() { }

    [MemoryPackConstructor]
    public FramePacket(uint frameId, ushort screenWidth, ushort screenHeight, ChromaMode chromaMode, byte chromaThreshold)
    {
        FrameId = frameId;
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
        ChromaMode = chromaMode;
        ChromaThreshold = chromaThreshold;
        TimestampUs = DateTimeOffset.UtcNow.Ticks / 10; // Convert to microseconds
    }
}

/// <summary>
/// A rectangular region of pixels that changed
/// Contains LZ4-compressed or uncompressed BGRA pixel data
/// </summary>
[MemoryPackable]
public partial class DirtyRegion
{
    /// <summary>
    /// X coordinate of top-left corner
    /// </summary>
    public ushort X { get; set; }

    /// <summary>
    /// Y coordinate of top-left corner
    /// </summary>
    public ushort Y { get; set; }

    /// <summary>
    /// Width of region in pixels
    /// </summary>
    public ushort Width { get; set; }

    /// <summary>
    /// Height of region in pixels
    /// </summary>
    public ushort Height { get; set; }

    /// <summary>
    /// Pixel data (may be LZ4-compressed or uncompressed, check IsCompressed)
    /// </summary>
    public byte[] CompressedPixels { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Original uncompressed size (for decompression)
    /// </summary>
    public int UncompressedSize { get; set; }

    /// <summary>
    /// Whether the pixel data is LZ4-compressed (true) or uncompressed (false)
    /// </summary>
    public bool IsCompressed { get; set; }

    public DirtyRegion() { }

    [MemoryPackConstructor]
    public DirtyRegion(ushort x, ushort y, ushort width, ushort height, byte[] compressedPixels, int uncompressedSize, bool isCompressed = true)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        CompressedPixels = compressedPixels;
        UncompressedSize = uncompressedSize;
        IsCompressed = isCompressed;
    }
}

/// <summary>
/// Packet type identifier for LiteNetLib
/// </summary>
public enum PacketType : byte
{
    Frame = 1,
    Stats = 2
}
