using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ChromaNet.Capture;

/// <summary>
/// High-performance screen capture using DXGI Desktop Duplication API
/// Provides FREE dirty rectangle detection from Windows display driver
/// This is much faster than custom motion detection
/// </summary>
public class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly IDXGIOutput1 _output;
    private ID3D11Texture2D? _stagingTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Create a desktop duplicator for specified monitor
    /// </summary>
    /// <param name="monitorIndex">Monitor index (0 = primary monitor)</param>
    public DesktopDuplicator(int monitorIndex = 0)
    {
        // Create D3D11 device
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.None,
            null,
            out _device!,
            out _context!);

        if (result.Failure)
            throw new Exception($"Failed to create D3D11 device: {result}");

        // Get DXGI device
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        // Get specified output (monitor)
        var enumResult = adapter.EnumOutputs(monitorIndex, out var output);
        if (enumResult.Failure)
            throw new Exception($"Failed to get monitor {monitorIndex}. Is it connected?");

        _output = output.QueryInterface<IDXGIOutput1>();
        output.Dispose();

        // Get output description for dimensions
        var desc = _output.Description;
        _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        // Create desktop duplication
        _duplication = _output.DuplicateOutput(_device);

        Console.WriteLine($"[DesktopDuplicator] Initialized monitor {monitorIndex}: {_width}x{_height} ({desc.DeviceName})");
    }

    /// <summary>
    /// Capture result containing frame data and dirty rectangles
    /// </summary>
    public class CaptureResult
    {
        public byte[]? FrameData { get; set; }
        public List<Rectangle> DirtyRegions { get; set; } = new();
        public bool Success { get; set; }
        public bool NoChanges { get; set; }
    }

    /// <summary>
    /// Capture next frame with dirty rectangle information
    /// Returns null if no new frame available (timeout)
    /// </summary>
    public CaptureResult? CaptureFrame(int timeoutMs = 0)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DesktopDuplicator));

        IDXGIResource? desktopResource = null;
        ID3D11Texture2D? desktopTexture = null;

        try
        {
            // Acquire next frame
            var result = _duplication.AcquireNextFrame(timeoutMs, out var frameInfo, out desktopResource!);

            if (result.Failure)
            {
                // Timeout or no new frame
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    return null;

                throw new Exception($"AcquireNextFrame failed: {result}");
            }

            // Check if there are any changes
            if (frameInfo.TotalMetadataBufferSize == 0)
            {
                // No changes in this frame
                _duplication.ReleaseFrame();
                return new CaptureResult { Success = true, NoChanges = true };
            }

            // Get desktop texture
            desktopTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();

            // Get dirty rectangles (changed regions)
            var dirtyRects = GetDirtyRectangles(frameInfo);

            // Copy frame to CPU
            byte[] frameData = CopyTextureToCPU(desktopTexture);

            // Release frame
            _duplication.ReleaseFrame();

            return new CaptureResult
            {
                FrameData = frameData,
                DirtyRegions = dirtyRects,
                Success = true,
                NoChanges = false
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopDuplicator] Capture error: {ex.Message}");

            // Try to release frame on error
            try { _duplication.ReleaseFrame(); } catch { }

            // If access lost, need to recreate duplication
            if (ex.Message.Contains("DXGI_ERROR_ACCESS_LOST"))
            {
                RecreateDuplication();
            }

            return new CaptureResult { Success = false };
        }
        finally
        {
            desktopTexture?.Dispose();
            desktopResource?.Dispose();
        }
    }

    /// <summary>
    /// Get dirty rectangles from frame metadata
    /// These are provided FREE by Windows - no custom motion detection needed!
    /// </summary>
    private List<Rectangle> GetDirtyRectangles(OutduplFrameInfo frameInfo)
    {
        var dirtyRects = new List<Rectangle>();

        if (frameInfo.TotalMetadataBufferSize == 0)
            return dirtyRects;

        // Allocate buffer for dirty rects (use Vortice.RawRect)
        int maxRects = frameInfo.TotalMetadataBufferSize / Marshal.SizeOf<Vortice.RawRect>();
        if (maxRects == 0)
            return dirtyRects;

        Vortice.RawRect[] dirtyRectsBuffer = new Vortice.RawRect[maxRects];

        // Get dirty rectangles - note the API signature
        int bufferSize = dirtyRectsBuffer.Length * Marshal.SizeOf<Vortice.RawRect>();
        _duplication.GetFrameDirtyRects(bufferSize, dirtyRectsBuffer, out int dirtyRectsSize);

        // Calculate actual rect count
        int dirtyRectsCount = dirtyRectsSize / Marshal.SizeOf<Vortice.RawRect>();

        // Convert to System.Drawing.Rectangle list
        for (int i = 0; i < dirtyRectsCount && i < dirtyRectsBuffer.Length; i++)
        {
            var rect = dirtyRectsBuffer[i];
            dirtyRects.Add(new Rectangle(
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top));
        }

        return dirtyRects;
    }

    /// <summary>
    /// Copy D3D11 texture to CPU memory
    /// </summary>
    private byte[] CopyTextureToCPU(ID3D11Texture2D texture)
    {
        // Create staging texture if needed
        if (_stagingTexture == null)
        {
            var desc = texture.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;

            _stagingTexture = _device.CreateTexture2D(desc);
        }

        // Copy to staging
        _context.CopyResource(_stagingTexture, texture);

        // Map staging texture
        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            // Allocate buffer for BGRA pixels
            int bufferSize = _width * _height * 4;
            byte[] pixels = new byte[bufferSize];

            // Copy pixels row by row (handle pitch)
            unsafe
            {
                byte* srcPtr = (byte*)mapped.DataPointer;
                fixed (byte* destPtr = pixels)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int srcOffset = y * mapped.RowPitch;
                        int destOffset = y * _width * 4;
                        Buffer.MemoryCopy(
                            srcPtr + srcOffset,
                            destPtr + destOffset,
                            _width * 4,
                            _width * 4);
                    }
                }
            }

            return pixels;
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    /// <summary>
    /// Recreate duplication after access loss
    /// </summary>
    private void RecreateDuplication()
    {
        try
        {
            _duplication?.Dispose();
            // Recreate would go here - simplified for now
            Console.WriteLine("[DesktopDuplicator] Duplication lost - would recreate");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopDuplicator] Recreate failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _stagingTexture?.Dispose();
        _duplication?.Dispose();
        _output?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _disposed = true;
    }
}
