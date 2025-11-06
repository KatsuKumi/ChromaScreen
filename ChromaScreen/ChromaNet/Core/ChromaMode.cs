namespace ChromaNet.Core;

/// <summary>
/// Defines where chroma key processing occurs
/// </summary>
public enum ChromaMode : byte
{
    /// <summary>
    /// No chroma processing, send raw RGB
    /// </summary>
    None = 0,

    /// <summary>
    /// Server applies chroma key on GPU before sending
    /// Best for: Server has powerful GPU
    /// </summary>
    ServerSide = 1,

    /// <summary>
    /// Client applies chroma key on GPU after receiving
    /// Best for: Server is CPU-limited
    /// </summary>
    ClientSide = 2
}
