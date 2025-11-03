// ChromaKey Compute Shader for DirectX 11
// Processes BGRA pixels and sets alpha based on brightness threshold

// Input texture (read-only)
Texture2D<float4> InputTexture : register(t0);

// Output texture (read-write)
RWTexture2D<float4> OutputTexture : register(u0);

// Constant buffer for parameters
cbuffer Parameters : register(b0)
{
    uint ChromaKeyThreshold;  // 0-255 brightness threshold
    uint Width;                // Image width
    uint Height;               // Image height
    uint Padding;              // Padding for alignment
};

// Compute shader - processes 8x8 pixel blocks
[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    // Get pixel coordinates
    uint2 pixelCoord = dispatchThreadID.xy;

    // Bounds check
    if (pixelCoord.x >= Width || pixelCoord.y >= Height)
        return;

    // Read pixel (BGRA format as float4)
    float4 pixel = InputTexture[pixelCoord];

    // Convert from [0,1] float to [0,255] uint for brightness calculation
    uint r = uint(pixel.r * 255.0f);
    uint g = uint(pixel.g * 255.0f);
    uint b = uint(pixel.b * 255.0f);

    // Calculate brightness (average of RGB)
    uint brightness = (r + g + b) / 3;

    // Apply chroma key: if brightness <= threshold, make transparent (alpha=0), else opaque (alpha=1)
    pixel.a = (brightness <= ChromaKeyThreshold) ? 0.0f : 1.0f;

    // Write result
    OutputTexture[pixelCoord] = pixel;
}
