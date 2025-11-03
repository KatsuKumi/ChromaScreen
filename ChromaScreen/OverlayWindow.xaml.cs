using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
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

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDirect3DDevice? _device;
    private SizeInt32 _lastSize;
    private readonly object _lock = new object();

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

        Loaded += OverlayWindow_Loaded;
        Closed += OverlayWindow_Closed;
        MouseLeftButtonDown += OverlayWindow_MouseLeftButtonDown;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
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

        System.Diagnostics.Debug.WriteLine($"Capture item created: {_captureItem.Size.Width}x{_captureItem.Size.Height}");

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

        System.Diagnostics.Debug.WriteLine("Capture started successfully");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_lock)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null) return;

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
                }

                // Process frame
                ProcessFrame(frame);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Frame error: {ex.Message}");
            }
        }
    }

    private void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        try
        {
            // Get the Direct3D11 surface
            var surface = GetDXGISurface(frame.Surface);

            // Copy texture data
            using var stagingTexture = CreateStagingTexture(surface);
            _d3dContext!.CopyResource(stagingTexture, surface);

            // Map the staging texture to CPU memory
            var mapped = _d3dContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                var desc = stagingTexture.Description;
                var fullWidth = (int)desc.Width;
                var fullHeight = (int)desc.Height;

                // Calculate cropped dimensions
                var croppedWidth = fullWidth - _cropLeft - _cropRight;
                var croppedHeight = fullHeight - _cropTop - _cropBottom;

                // Validate crop margins
                if (croppedWidth <= 0 || croppedHeight <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("Invalid crop margins - result would be empty");
                    return;
                }

                // Copy pixel data
                var pixelData = new byte[mapped.RowPitch * fullHeight];
                Marshal.Copy(mapped.DataPointer, pixelData, 0, pixelData.Length);

                // Create cropped buffer
                var croppedData = new byte[croppedWidth * croppedHeight * 4];

                // Copy cropped region row by row
                for (int y = 0; y < croppedHeight; y++)
                {
                    int sourceOffset = (y + _cropTop) * mapped.RowPitch + (_cropLeft * 4);
                    int destOffset = y * croppedWidth * 4;
                    Array.Copy(pixelData, sourceOffset, croppedData, destOffset, croppedWidth * 4);
                }

                // Apply chroma key to cropped data
                ApplyChromaKey(croppedData, croppedWidth, croppedHeight, croppedWidth * 4);

                // Update UI with cropped image
                Dispatcher.Invoke(() => UpdateImage(croppedData, croppedWidth, croppedHeight, croppedWidth * 4));
            }
            finally
            {
                _d3dContext.Unmap(stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ProcessFrame error: {ex.Message}");
        }
    }

    private ID3D11Texture2D CreateStagingTexture(ID3D11Texture2D sourceTexture)
    {
        var desc = sourceTexture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;

        return _d3dDevice!.CreateTexture2D(desc);
    }

    private void ApplyChromaKey(byte[] pixels, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 4;

                byte b = pixels[index];
                byte g = pixels[index + 1];
                byte r = pixels[index + 2];

                int brightness = (r + g + b) / 3;

                if (brightness <= _chromaKeyThreshold)
                {
                    pixels[index + 3] = 0; // Transparent
                }
                else
                {
                    pixels[index + 3] = 255; // Opaque
                }
            }
        }
    }

    private void UpdateImage(byte[] pixels, int width, int height, int sourceStride)
    {
        try
        {
            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            wb.Lock();
            try
            {
                // Copy row by row, accounting for DirectX texture stride (padding)
                for (int y = 0; y < height; y++)
                {
                    // Source offset uses DirectX stride (with padding)
                    // Destination offset uses WriteableBitmap stride
                    Marshal.Copy(pixels, y * sourceStride,
                        wb.BackBuffer + y * wb.BackBufferStride,
                        width * 4);
                }
                wb.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                wb.Unlock();
            }

            OverlayImage.Source = wb;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateImage error: {ex.Message}");
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

        System.Diagnostics.Debug.WriteLine("Capture stopped");
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
            if (!IsWindow(hwnd))
            {
                System.Diagnostics.Debug.WriteLine($"Invalid window handle: {hwnd}");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"Creating capture item for window: {hwnd}");

            // Use the IGraphicsCaptureItemInterop COM interface
            var factoryTypeName = "Windows.Graphics.Capture.GraphicsCaptureItem";
            var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

            // Create HSTRING for the factory name
            var hrString = WindowsCreateString(factoryTypeName, factoryTypeName.Length, out var hstringFactory);
            if (hrString != 0)
            {
                System.Diagnostics.Debug.WriteLine($"WindowsCreateString failed: {hrString:X}");
                return null;
            }

            // Get the activation factory as IInspectable
            var hr = RoGetActivationFactory(hstringFactory, ref interopGuid, out var interopPtr);
            WindowsDeleteString(hstringFactory);

            if (hr != 0 || interopPtr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"RoGetActivationFactory failed: {hr:X}");
                return null;
            }

            try
            {
                var interop = Marshal.GetObjectForIUnknown(interopPtr) as IGraphicsCaptureItemInterop;
                if (interop == null)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to cast to IGraphicsCaptureItemInterop");
                    return null;
                }

                var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem GUID
                var itemPtr = interop.CreateForWindow(hwnd, itemGuid);

                if (itemPtr == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("CreateForWindow returned null");
                    return null;
                }

                var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                System.Diagnostics.Debug.WriteLine($"Successfully created capture item: {item?.Size.Width}x{item?.Size.Height}");

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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CreateItemForWindow failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
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
