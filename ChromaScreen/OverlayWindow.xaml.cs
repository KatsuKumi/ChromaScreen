using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ChromaScreen;

public partial class OverlayWindow : Window
{
    private readonly IntPtr _targetWindowHandle;
    private readonly int _chromaKeyThreshold;
    private readonly int _cropTop;
    private readonly int _cropLeft;
    private readonly int _cropRight;
    private readonly int _cropBottom;
    private readonly int _targetFrameTime;

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDirect3DDevice? _device;
    private SizeInt32 _lastSize;
    private readonly object _lock = new object();
    private long _lastProcessedFrameTime;

    // Performance metrics
    private readonly Stopwatch _performanceTimer = new Stopwatch();
    private long _frameCount = 0;
    private long _totalGpuCopyTime = 0;
    private long _totalChromaKeyTime = 0;
    private long _lastFpsReport = 0;

    // InteropBitmap with shared memory for ultra-fast rendering
    private InteropBitmap? _interopBitmap = null;
    private IntPtr _sharedMemorySection = IntPtr.Zero;
    private IntPtr _sharedMemoryMap = IntPtr.Zero;
    private int _lastBitmapWidth = 0;
    private int _lastBitmapHeight = 0;

    // Reusable buffer to eliminate allocations (14.7MB per frame!)
    private byte[]? _pixelBuffer = null;

    // P/Invoke for shared memory (InteropBitmap)
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0xF001F;

    public OverlayWindow(IntPtr targetWindowHandle, int chromaKeyThreshold, int updateRate,
        int cropTop = 0, int cropLeft = 0, int cropRight = 0, int cropBottom = 0)
    {
        InitializeComponent();

        _targetWindowHandle = targetWindowHandle;
        _chromaKeyThreshold = chromaKeyThreshold;
        _cropTop = cropTop;
        _cropLeft = cropLeft;
        _cropRight = cropRight;
        _cropBottom = cropBottom;
        _targetFrameTime = updateRate; // Frame time in milliseconds
        _lastProcessedFrameTime = 0;

        Loaded += OverlayWindow_Loaded;
        Closed += OverlayWindow_Closed;
        MouseLeftButtonDown += OverlayWindow_MouseLeftButtonDown;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Increase process priority to prevent Windows Efficiency Mode throttling
            using (var process = Process.GetCurrentProcess())
            {
                try
                {
                    // Set to High priority to compete with GPU-intensive games
                    process.PriorityClass = ProcessPriorityClass.High;
                }
                catch
                {
                    // Fallback to AboveNormal if High fails
                    try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                }
            }

            // Increase current thread priority for capture operations
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            StartCapture();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start capture: {ex.Message}\n\nStack: {ex.StackTrace}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void StartCapture()
    {
        // Create D3D11 device
        var result = D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Array.Empty<Vortice.Direct3D.FeatureLevel>(),
            out _d3dDevice,
            out _d3dContext);

        if (result.Failure)
        {
            throw new Exception($"Failed to create D3D11 device: {result}");
        }

        // Get IDXGIDevice from ID3D11Device
        using var dxgiDevice = _d3dDevice!.QueryInterface<IDXGIDevice>();

        // Create IDirect3DDevice wrapper
        _device = CreateDirect3DDevice(dxgiDevice);

        // Create capture item from window handle
        _captureItem = CaptureHelper.CreateItemForWindow(_targetWindowHandle);

        if (_captureItem == null)
        {
            throw new InvalidOperationException("Failed to create GraphicsCaptureItem");
        }

        // Create frame pool
        _lastSize = _captureItem.Size;
        _framePool = Direct3D11CaptureFramePool.Create(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _lastSize);

        // Handle frames
        _framePool.FrameArrived += OnFrameArrived;

        // Start capture session
        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // IMPORTANT: Always consume frames from pool to prevent buffer exhaustion
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        // Frame rate throttling - skip processing if too soon
        long currentTime = Environment.TickCount64;
        long timeSinceLastFrame = currentTime - _lastProcessedFrameTime;

        if (_lastProcessedFrameTime > 0 && timeSinceLastFrame < _targetFrameTime)
        {
            return; // Frame consumed but not processed (throttled)
        }

        // Use TryEnter to avoid blocking - skip frame if already processing
        bool lockTaken = false;
        try
        {
            Monitor.TryEnter(_lock, ref lockTaken);
            if (!lockTaken) return; // Frame consumed but not processed (busy)

            // Update timestamp BEFORE processing to prevent frame pile-up
            _lastProcessedFrameTime = currentTime;

            // Check if size changed
            if (frame.ContentSize.Width != _lastSize.Width ||
                frame.ContentSize.Height != _lastSize.Height)
            {
                _lastSize = frame.ContentSize;

                // Recreate frame pool
                _framePool?.Dispose();
                _framePool = Direct3D11CaptureFramePool.Create(
                    _device!,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _lastSize);
                _framePool.FrameArrived += OnFrameArrived;

                // Recreate session
                _session?.Dispose();
                _session = _framePool.CreateCaptureSession(_captureItem!);
                _session.StartCapture();
                return; // Skip this frame after resize
            }

            // Process frame synchronously (DirectX requires same-thread access)
            ProcessFrame(frame);
        }
        catch
        {
            // Silently ignore frame errors to avoid debug overhead
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_lock);
            }
        }
    }

    private void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        try
        {
            _performanceTimer.Restart();

            // Get the Direct3D11 surface
            var surface = GetDXGISurface(frame.Surface);
            var desc = surface.Description;
            var fullWidth = (int)desc.Width;
            var fullHeight = (int)desc.Height;

            // Calculate cropped dimensions
            var croppedWidth = fullWidth - _cropLeft - _cropRight;
            var croppedHeight = fullHeight - _cropTop - _cropBottom;

            // Validate crop margins
            if (croppedWidth <= 0 || croppedHeight <= 0) return;

            // GPU-SIDE CROPPING: Create smaller staging texture for cropped region only
            using var croppedStagingTexture = CreateStagingTexture(croppedWidth, croppedHeight);

            // Copy only the cropped region using GPU
            var srcBox = new Vortice.Mathematics.Box(
                _cropLeft,
                _cropTop,
                0,
                _cropLeft + croppedWidth,
                _cropTop + croppedHeight,
                1
            );

            _d3dContext!.CopySubresourceRegion(
                croppedStagingTexture,
                0,
                0, 0, 0,
                surface,
                0,
                srcBox
            );

            long gpuCopyTime = _performanceTimer.ElapsedTicks;

            // Map the cropped staging texture
            var mapped = _d3dContext.Map(croppedStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                _performanceTimer.Restart();

                // Reuse buffer to eliminate allocations (14.7MB per frame!)
                int bufferSize = croppedWidth * croppedHeight * 4;
                if (_pixelBuffer == null || _pixelBuffer.Length != bufferSize)
                {
                    _pixelBuffer = new byte[bufferSize];
                }
                var croppedData = _pixelBuffer;

                // Copy pixel data efficiently using unsafe code
                unsafe
                {
                    fixed (byte* destPtr = croppedData)
                    {
                        byte* srcPtr = (byte*)mapped.DataPointer;
                        int rowBytes = croppedWidth * 4;

                        // Copy row by row to handle potential stride differences
                        for (int y = 0; y < croppedHeight; y++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + (y * mapped.RowPitch),
                                destPtr + (y * rowBytes),
                                rowBytes,
                                rowBytes
                            );
                        }
                    }
                }

                long chromaKeyStart = _performanceTimer.ElapsedTicks;

                // Apply chroma key processing
                ApplyChromaKeySIMD(croppedData, croppedWidth, croppedHeight, croppedWidth * 4);

                long chromaKeyTime = _performanceTimer.ElapsedTicks - chromaKeyStart;

                // Update performance metrics
                _frameCount++;
                _totalGpuCopyTime += gpuCopyTime;
                _totalChromaKeyTime += chromaKeyTime;

                // Report FPS every 60 frames
                if (_frameCount % 60 == 0)
                {
                    long currentTime = Environment.TickCount64;
                    if (_lastFpsReport > 0)
                    {
                        double elapsed = (currentTime - _lastFpsReport) / 1000.0;
                        double fps = 60.0 / elapsed;
                        double avgGpuCopy = (_totalGpuCopyTime / (double)_frameCount) / (Stopwatch.Frequency / 1000.0);
                        double avgChromaKey = (_totalChromaKeyTime / (double)_frameCount) / (Stopwatch.Frequency / 1000.0);

                        System.Diagnostics.Debug.WriteLine(
                            $"FPS: {fps:F1} | GPU Copy: {avgGpuCopy:F2}ms | ChromaKey: {avgChromaKey:F2}ms | Resolution: {croppedWidth}x{croppedHeight}"
                        );
                    }
                    _lastFpsReport = currentTime;
                }

                // Update UI immediately (synchronous) - no frame skipping!
                // Synchronous Invoke means no queue backup, so always process
                Dispatcher.Invoke(() => UpdateImage(croppedData, croppedWidth, croppedHeight, croppedWidth * 4),
                    System.Windows.Threading.DispatcherPriority.Send);
            }
            finally
            {
                _d3dContext.Unmap(croppedStagingTexture, 0);
            }
        }
        catch
        {
            // Silently ignore errors to minimize overhead
        }
    }

    private ID3D11Texture2D CreateStagingTexture(int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        return _d3dDevice!.CreateTexture2D(desc);
    }

    private unsafe void ApplyChromaKeySIMD(byte[] pixels, int width, int height, int stride)
    {
        int threshold = _chromaKeyThreshold;

        // Row-based parallel processing (faster than chunk-based for this workload)
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, y =>
        {
            unsafe
            {
                fixed (byte* basePtr = pixels)
                {
                    byte* row = basePtr + (y * stride);
                    int x = 0;

                    // Process 8 pixels at a time (loop unrolling for better performance)
                    int maxX = (width / 8) * 8;
                    for (; x < maxX; x += 8)
                    {
                        int offset = x * 4;

                        // Pixel 0
                        int brightness0 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness0 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 1
                        offset += 4;
                        int brightness1 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness1 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 2
                        offset += 4;
                        int brightness2 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness2 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 3
                        offset += 4;
                        int brightness3 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness3 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 4
                        offset += 4;
                        int brightness4 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness4 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 5
                        offset += 4;
                        int brightness5 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness5 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 6
                        offset += 4;
                        int brightness6 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness6 <= threshold ? (byte)0 : (byte)255;

                        // Pixel 7
                        offset += 4;
                        int brightness7 = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness7 <= threshold ? (byte)0 : (byte)255;
                    }

                    // Process remaining pixels
                    for (; x < width; x++)
                    {
                        int offset = x * 4;
                        int brightness = (row[offset + 2] + row[offset + 1] + row[offset]) / 3;
                        row[offset + 3] = brightness <= threshold ? (byte)0 : (byte)255;
                    }
                }
            }
        });
    }

    private unsafe void UpdateImage(byte[] pixels, int width, int height, int sourceStride)
    {
        try
        {
            // Use InteropBitmap with shared memory - MUCH faster than WriteableBitmap!
            if (_interopBitmap == null || _lastBitmapWidth != width || _lastBitmapHeight != height)
            {
                // Clean up old resources
                if (_sharedMemoryMap != IntPtr.Zero)
                {
                    UnmapViewOfFile(_sharedMemoryMap);
                    _sharedMemoryMap = IntPtr.Zero;
                }
                if (_sharedMemorySection != IntPtr.Zero)
                {
                    CloseHandle(_sharedMemorySection);
                    _sharedMemorySection = IntPtr.Zero;
                }

                // Create shared memory section
                uint byteCount = (uint)(width * height * 4);
                _sharedMemorySection = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, byteCount, null);
                _sharedMemoryMap = MapViewOfFile(_sharedMemorySection, FILE_MAP_ALL_ACCESS, 0, 0, byteCount);

                // Create InteropBitmap from shared memory
                _interopBitmap = Imaging.CreateBitmapSourceFromMemorySection(
                    _sharedMemorySection,
                    width,
                    height,
                    PixelFormats.Bgra32,
                    width * 4,
                    0) as InteropBitmap;

                _lastBitmapWidth = width;
                _lastBitmapHeight = height;
                OverlayImage.Source = _interopBitmap;
            }

            // Copy pixels directly to shared memory (no Lock/Unlock overhead!)
            fixed (byte* srcPtr = pixels)
            {
                byte* dstPtr = (byte*)_sharedMemoryMap;
                int rowBytes = width * 4;

                // Fast memory copy
                for (int y = 0; y < height; y++)
                {
                    byte* src = srcPtr + (y * sourceStride);
                    byte* dst = dstPtr + (y * rowBytes);
                    Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                }
            }

            // Tell WPF the bitmap has been updated (very fast call)
            _interopBitmap?.Invalidate();
        }
        catch
        {
            // Silently ignore errors to minimize overhead
        }
    }

    private static ID3D11Texture2D GetDXGISurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var guid = typeof(ID3D11Texture2D).GUID;
        var ptr = access.GetInterface(ref guid);
        return new ID3D11Texture2D(ptr);
    }

    private static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
    {
        // Use the native pointer from Vortice's IDXGIDevice
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr d3dDevicePtr);

        if (hr != 0)
        {
            throw new Exception($"Failed to create IDirect3DDevice. HRESULT: 0x{hr:X8}");
        }

        // Marshal the IInspectable pointer to IDirect3DDevice
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(d3dDevicePtr);

        // Release the extra reference from the out parameter
        Marshal.Release(d3dDevicePtr);

        return device;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private void OverlayWindow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        _session?.Dispose();
        _framePool?.Dispose();
        _captureItem = null;
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();

        // Clean up shared memory
        if (_sharedMemoryMap != IntPtr.Zero)
        {
            UnmapViewOfFile(_sharedMemoryMap);
            _sharedMemoryMap = IntPtr.Zero;
        }
        if (_sharedMemorySection != IntPtr.Zero)
        {
            CloseHandle(_sharedMemorySection);
            _sharedMemorySection = IntPtr.Zero;
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }
}

static class CaptureHelper
{
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        try
        {
            // Verify window is valid
            if (!IsWindow(hwnd)) return null;

            // Use the IGraphicsCaptureItemInterop COM interface
            var factoryTypeName = "Windows.Graphics.Capture.GraphicsCaptureItem";
            var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

            // Create HSTRING for the factory name
            var hrString = WindowsCreateString(factoryTypeName, factoryTypeName.Length, out var hstringFactory);
            if (hrString != 0) return null;

            // Get the activation factory as IInspectable
            var hr = RoGetActivationFactory(hstringFactory, ref interopGuid, out var interopPtr);
            WindowsDeleteString(hstringFactory);

            if (hr != 0 || interopPtr == IntPtr.Zero) return null;

            try
            {
                var interop = Marshal.GetObjectForIUnknown(interopPtr) as IGraphicsCaptureItemInterop;
                if (interop == null) return null;

                var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var itemPtr = interop.CreateForWindow(hwnd, itemGuid);

                if (itemPtr == IntPtr.Zero) return null;

                var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                return item;
            }
            finally
            {
                if (interopPtr != IntPtr.Zero)
                {
                    Marshal.Release(interopPtr);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] Guid iid);
    }
}
