using K4os.Compression.LZ4;

namespace ChromaNet.Compression;

/// <summary>
/// Ultra-fast LZ4 compression for real-time video streaming
/// LZ4 is faster than ZSTD with acceptable compression ratios for our use case
/// Compression: ~500 MB/s, Decompression: ~2 GB/s
/// </summary>
public static class LZ4Compressor
{
    /// <summary>
    /// Compress data using LZ4 high-speed mode
    /// </summary>
    /// <param name="source">Source data to compress</param>
    /// <returns>Compressed byte array</returns>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
            return Array.Empty<byte>();

        // Allocate worst-case size (source + header)
        int maxCompressedSize = LZ4Codec.MaximumOutputSize(source.Length);
        byte[] target = new byte[maxCompressedSize];

        // Use LZ4 Level 1 (fastest) for real-time performance
        int compressedSize = LZ4Codec.Encode(
            source,
            target,
            LZ4Level.L00_FAST);

        if (compressedSize < 0)
            throw new InvalidOperationException("LZ4 compression failed");

        // Trim to actual size
        if (compressedSize == target.Length)
            return target;

        byte[] result = new byte[compressedSize];
        Array.Copy(target, result, compressedSize);
        return result;
    }

    /// <summary>
    /// Decompress LZ4-compressed data
    /// </summary>
    /// <param name="source">Compressed data</param>
    /// <param name="uncompressedSize">Expected uncompressed size</param>
    /// <returns>Decompressed byte array</returns>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int uncompressedSize)
    {
        if (source.Length == 0)
            return Array.Empty<byte>();

        byte[] target = new byte[uncompressedSize];

        int decompressedSize = LZ4Codec.Decode(
            source,
            target);

        if (decompressedSize != uncompressedSize)
            throw new InvalidOperationException(
                $"LZ4 decompression size mismatch: expected {uncompressedSize}, got {decompressedSize}");

        return target;
    }

    /// <summary>
    /// Get maximum compressed size for buffer allocation
    /// </summary>
    public static int GetMaxCompressedSize(int sourceSize)
    {
        return LZ4Codec.MaximumOutputSize(sourceSize);
    }
}
