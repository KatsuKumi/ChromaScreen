using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Numerics;
using Veldrid;
using Veldrid.SPIRV;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ChromaScreen;

/// <summary>
/// High-performance overlay window using Veldrid for GPU-accelerated rendering.
/// Eliminates WPF transparency bottleneck by using native DirectX rendering.
/// </summary>
public class VeldridOverlayWindow : IDisposable
{
    private readonly IntPtr _targetWindowHandle;
    private readonly int _chromaKeyThreshold;
    private readonly int _cropTop;
    private readonly int _cropLeft;
    private readonly int _cropRight;
    private readonly int _cropBottom;
    private readonly int _targetFrameTime;

    // Native window
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private bool _isRunning;

    // Veldrid resources
    private GraphicsDevice? _graphicsDevice;
    private CommandList? _commandList;

    // Fullscreen quad rendering
    private DeviceBuffer? _vertexBuffer;
    private DeviceBuffer? _indexBuffer;
    private Pipeline? _pipeline;
    private Shader? _vertexShader;
    private Shader? _fragmentShader;

    // Texture resources
    private Texture? _sourceTexture;
    private TextureView? _sourceTextureView;
    private ResourceSet? _resourceSet;
    private ResourceLayout? _resourceLayout;
    private Sampler? _sampler;

    // Windows Graphics Capture
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDirect3DDevice? _device;
    private SizeInt32 _lastSize;
    private readonly object _lock = new object();
    private long _lastProcessedFrameTime;

    // Performance tracking
    private readonly Stopwatch _performanceTimer = new Stopwatch();
    private long _frameCount = 0;
    private long _totalRenderTime = 0;
    private long _lastFpsReport = 0;

    // Win32 window styles for transparent overlay
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int GWL_EXSTYLE = -20;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProc? _wndProcDelegate;

    public VeldridOverlayWindow(IntPtr targetWindowHandle, int chromaKeyThreshold, int updateRate,
        int cropTop = 0, int cropLeft = 0, int cropRight = 0, int cropBottom = 0)
    {
        _targetWindowHandle = targetWindowHandle;
        _chromaKeyThreshold = chromaKeyThreshold;
        _cropTop = cropTop;
        _cropLeft = cropLeft;
        _cropRight = cropRight;
        _cropBottom = cropBottom;
        _targetFrameTime = updateRate;
        _lastProcessedFrameTime = 0;
    }

    public void Show()
    {
        Debug.WriteLine("VeldridOverlayWindow.Show: Starting...");

        // Create overlay window
        CreateOverlayWindow();
        Debug.WriteLine("VeldridOverlayWindow.Show: Overlay window created");

        // Initialize Veldrid
        InitializeVeldrid();
        Debug.WriteLine("VeldridOverlayWindow.Show: Veldrid initialized");

        // Start capture
        StartCapture();
        Debug.WriteLine("VeldridOverlayWindow.Show: Capture started");

        // Run message loop
        _isRunning = true;
        Debug.WriteLine("VeldridOverlayWindow.Show: Starting message loop");
        RunMessageLoop();
    }

    private void CreateOverlayWindow()
    {
        Debug.WriteLine("CreateOverlayWindow: Starting...");
        _hInstance = GetModuleHandle(null!);
        Debug.WriteLine($"CreateOverlayWindow: Module handle = {_hInstance}");

        _wndProcDelegate = WndProcCallback;

        WNDCLASSEX wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = _hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "VeldridOverlayClass",
            hIconSm = IntPtr.Zero
        };

        Debug.WriteLine($"CreateOverlayWindow: Registering window class '{wndClass.lpszClassName}'...");
        Debug.WriteLine($"CreateOverlayWindow: WndProc pointer = {wndClass.lpfnWndProc}");
        Debug.WriteLine($"CreateOverlayWindow: hInstance = {wndClass.hInstance}");

        ushort classAtom = RegisterClassEx(ref wndClass);
        Debug.WriteLine($"CreateOverlayWindow: RegisterClassEx returned atom = {classAtom}");

        if (classAtom == 0)
        {
            uint error = GetLastError();
            Debug.WriteLine($"RegisterClassEx failed with error: {error}");

            // Error 1410 means class already registered, which is OK
            if (error == 1410)
            {
                Debug.WriteLine("CreateOverlayWindow: Class already registered (1410), continuing...");
            }
            else
            {
                throw new InvalidOperationException($"Failed to register window class. Win32 Error: {error}");
            }
        }
        else
        {
            Debug.WriteLine($"CreateOverlayWindow: Window class registered successfully with atom {classAtom}");
        }

        // Get screen dimensions
        int screenWidth = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
        int screenHeight = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height;

        // Create transparent, topmost, click-through window
        // WS_EX_LAYERED: Required for transparency and click-through to work
        // WS_EX_TRANSPARENT: Makes window transparent to mouse events (only works with WS_EX_LAYERED)
        // WS_EX_NOACTIVATE: Prevents window from capturing focus/input
        // WS_EX_TOOLWINDOW: Hides from taskbar
        // WS_EX_TOPMOST: Always on top
        _hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "VeldridOverlayClass",
            "ChromaScreen Overlay",
            WS_POPUP,
            0, 0,
            screenWidth, screenHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            _hInstance,
            IntPtr.Zero
        );

        if (_hwnd == IntPtr.Zero)
        {
            uint error = GetLastError();
            Debug.WriteLine($"CreateWindowEx failed with error: {error}");
            throw new InvalidOperationException($"Failed to create overlay window. Win32 Error: {error}");
        }

        Debug.WriteLine($"Veldrid overlay window created successfully: HWND={_hwnd}");

        // Check current extended style
        int currentStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        Debug.WriteLine($"Current extended style: 0x{currentStyle:X}");

        // Set layered window attributes for proper transparency and clickthrough
        // Alpha = 255 (fully opaque), LWA_ALPHA flag enables alpha blending
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        Debug.WriteLine("Layered window attributes set");

        // Enable DWM transparency for DirectX rendering
        MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        Debug.WriteLine("DWM transparency enabled");

        ShowWindow(_hwnd, 5); // SW_SHOW
        UpdateWindow(_hwnd);

        Debug.WriteLine("Veldrid overlay window shown");
    }

    private void InitializeVeldrid()
    {
        Debug.WriteLine("InitializeVeldrid: Starting...");
        try
        {
            // Increase process priority
            using (var process = Process.GetCurrentProcess())
            {
                try { process.PriorityClass = ProcessPriorityClass.High; }
                catch { try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { } }
            }

            // Create swapchain source from our HWND
            SwapchainSource swapchainSource = SwapchainSource.CreateWin32(_hwnd, _hInstance);

            // Configure graphics device options
            GraphicsDeviceOptions options = new GraphicsDeviceOptions
            {
                PreferStandardClipSpaceYDirection = true,
                PreferDepthRangeZeroToOne = true,
                SyncToVerticalBlank = false, // Disable VSync for lower latency
                SwapchainDepthFormat = null, // No depth buffer needed
                SwapchainSrgbFormat = false,
                Debug = false
            };

            // Get screen dimensions
            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
            int screenHeight = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height;

            // Create swapchain description
            SwapchainDescription swapchainDescription = new SwapchainDescription
            {
                Source = swapchainSource,
                Width = (uint)screenWidth,
                Height = (uint)screenHeight,
                ColorSrgb = false,
                DepthFormat = null,
                SyncToVerticalBlank = false
            };

            // Create DirectX11 graphics device with swapchain
            Debug.WriteLine("InitializeVeldrid: Creating D3D11 graphics device...");
            _graphicsDevice = GraphicsDevice.CreateD3D11(options, swapchainDescription);
            Debug.WriteLine($"InitializeVeldrid: Graphics device created successfully");

            _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
            Debug.WriteLine("InitializeVeldrid: Command list created");

            // Create rendering resources
            CreateRenderingResources();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize Veldrid: {ex.Message}", ex);
        }
    }

    private void CreateRenderingResources()
    {
        if (_graphicsDevice == null) return;

        // Create fullscreen quad vertex buffer
        VertexPositionTexture[] quadVertices = new VertexPositionTexture[]
        {
            new VertexPositionTexture(new Vector2(-1, -1), new Vector2(0, 1)),
            new VertexPositionTexture(new Vector2(1, -1), new Vector2(1, 1)),
            new VertexPositionTexture(new Vector2(1, 1), new Vector2(1, 0)),
            new VertexPositionTexture(new Vector2(-1, 1), new Vector2(0, 0))
        };

        _vertexBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(
            new Veldrid.BufferDescription((uint)(VertexPositionTexture.SizeInBytes * quadVertices.Length), BufferUsage.VertexBuffer));
        _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, quadVertices);

        // Create index buffer
        ushort[] quadIndices = new ushort[] { 0, 1, 2, 0, 2, 3 };
        _indexBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(
            new Veldrid.BufferDescription(sizeof(ushort) * (uint)quadIndices.Length, BufferUsage.IndexBuffer));
        _graphicsDevice.UpdateBuffer(_indexBuffer, 0, quadIndices);

        // Create shaders
        CreateShaders();

        // Create sampler
        _sampler = _graphicsDevice.ResourceFactory.CreateSampler(new Veldrid.SamplerDescription
        {
            AddressModeU = SamplerAddressMode.Clamp,
            AddressModeV = SamplerAddressMode.Clamp,
            AddressModeW = SamplerAddressMode.Clamp,
            Filter = SamplerFilter.MinLinear_MagLinear_MipLinear,
            LodBias = 0,
            MinimumLod = 0,
            MaximumLod = uint.MaxValue,
            MaximumAnisotropy = 0,
        });
    }

    private void CreateShaders()
    {
        Debug.WriteLine("CreateShaders: Starting...");
        if (_graphicsDevice == null) return;

        // Vertex shader - simple passthrough for fullscreen quad
        string vertexShaderCode = @"
#version 450

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoords;

layout(location = 0) out vec2 fsin_TexCoords;

void main()
{
    gl_Position = vec4(Position, 0, 1);
    fsin_TexCoords = TexCoords;
}";

        // Fragment shader - sample texture and apply chroma key
        string fragmentShaderCode = @"
#version 450

layout(location = 0) in vec2 fsin_TexCoords;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform ChromaKeyParams
{
    float Threshold;
};

void main()
{
    vec4 color = texture(sampler2D(SourceTexture, SourceSampler), fsin_TexCoords);

    // Calculate brightness
    float brightness = (color.r + color.g + color.b) / 3.0;

    // Apply chroma key threshold
    float alpha = brightness <= Threshold ? 0.0 : 1.0;

    fsout_Color = vec4(color.rgb, alpha);
}";

        ShaderDescription vertexShaderDesc = new ShaderDescription(
            ShaderStages.Vertex,
            System.Text.Encoding.UTF8.GetBytes(vertexShaderCode),
            "main");

        ShaderDescription fragmentShaderDesc = new ShaderDescription(
            ShaderStages.Fragment,
            System.Text.Encoding.UTF8.GetBytes(fragmentShaderCode),
            "main");

        Debug.WriteLine("CreateShaders: Compiling shaders from SPIRV...");
        var shaders = _graphicsDevice.ResourceFactory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
        _vertexShader = shaders[0];
        _fragmentShader = shaders[1];
        Debug.WriteLine("CreateShaders: Shaders compiled successfully");

        // Create resource layout
        _resourceLayout = _graphicsDevice.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("ChromaKeyParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)
        ));
    }

    private void StartCapture()
    {
        // Create D3D11 device for capture
        var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            Array.Empty<FeatureLevel>(),
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
        // Always consume frames from pool
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        // Frame rate throttling
        long currentTime = Environment.TickCount64;
        long timeSinceLastFrame = currentTime - _lastProcessedFrameTime;

        if (_lastProcessedFrameTime > 0 && timeSinceLastFrame < _targetFrameTime)
        {
            return;
        }

        // Non-blocking lock
        bool lockTaken = false;
        try
        {
            Monitor.TryEnter(_lock, ref lockTaken);
            if (!lockTaken) return;

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
                return;
            }

            // Process and render frame
            ProcessAndRenderFrame(frame);
        }
        catch
        {
            // Silently ignore errors
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_lock);
            }
        }
    }

    private void ProcessAndRenderFrame(Direct3D11CaptureFrame frame)
    {
        if (_graphicsDevice == null || _commandList == null) return;

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

            if (croppedWidth <= 0 || croppedHeight <= 0) return;

            // Create or recreate Veldrid texture if size changed
            if (_sourceTexture == null ||
                _sourceTexture.Width != croppedWidth ||
                _sourceTexture.Height != croppedHeight)
            {
                _sourceTexture?.Dispose();
                _sourceTextureView?.Dispose();
                _resourceSet?.Dispose();

                _sourceTexture = _graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
                    (uint)croppedWidth,
                    (uint)croppedHeight,
                    1, 1, 1,
                    PixelFormat.B8_G8_R8_A8_UNorm,
                    TextureUsage.Sampled,
                    TextureType.Texture2D));

                _sourceTextureView = _graphicsDevice.ResourceFactory.CreateTextureView(_sourceTexture);

                // Create uniform buffer for chroma key threshold
                // Note: DirectX requires uniform buffers to be at least 16 bytes (a multiple of 16)
                float normalizedThreshold = _chromaKeyThreshold / 255.0f;
                DeviceBuffer chromaKeyBuffer = _graphicsDevice.ResourceFactory.CreateBuffer(
                    new Veldrid.BufferDescription(16, BufferUsage.UniformBuffer)); // 16 bytes minimum
                _graphicsDevice.UpdateBuffer(chromaKeyBuffer, 0, normalizedThreshold);

                _resourceSet = _graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    _resourceLayout!,
                    _sourceTextureView,
                    _sampler!,
                    chromaKeyBuffer
                ));

                // Recreate pipeline with correct framebuffer
                CreatePipeline();
            }

            // Copy cropped region from D3D11 texture to staging texture
            using var croppedStagingTexture = CreateStagingTexture(croppedWidth, croppedHeight);

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

            // Map the staging texture and copy to Veldrid texture
            var mapped = _d3dContext.Map(croppedStagingTexture, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    _graphicsDevice.UpdateTexture(
                        _sourceTexture,
                        (IntPtr)mapped.DataPointer,
                        (uint)(croppedWidth * croppedHeight * 4),
                        0, 0, 0,
                        (uint)croppedWidth,
                        (uint)croppedHeight,
                        1,
                        0, 0);
                }
            }
            finally
            {
                _d3dContext.Unmap(croppedStagingTexture, 0);
            }

            // Render the frame
            RenderFrame();

            // Update performance metrics
            _frameCount++;
            _totalRenderTime += _performanceTimer.ElapsedTicks;

            if (_frameCount % 60 == 0)
            {
                long currentTime = Environment.TickCount64;
                if (_lastFpsReport > 0)
                {
                    double elapsed = (currentTime - _lastFpsReport) / 1000.0;
                    double fps = 60.0 / elapsed;
                    double avgRenderTime = (_totalRenderTime / (double)_frameCount) / (Stopwatch.Frequency / 1000.0);

                    Debug.WriteLine($"Veldrid FPS: {fps:F1} | Render: {avgRenderTime:F2}ms | Resolution: {croppedWidth}x{croppedHeight}");
                }
                _lastFpsReport = currentTime;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error processing frame: {ex.Message}");
        }
    }

    private void CreatePipeline()
    {
        if (_graphicsDevice == null || _vertexShader == null || _fragmentShader == null) return;

        _pipeline?.Dispose();

        VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
            new VertexElementDescription("TexCoords", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

        GraphicsPipelineDescription pipelineDescription = new GraphicsPipelineDescription
        {
            BlendState = BlendStateDescription.SingleAlphaBlend,
            DepthStencilState = DepthStencilStateDescription.Disabled,
            RasterizerState = new RasterizerStateDescription(
                cullMode: FaceCullMode.None,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.Clockwise,
                depthClipEnabled: false,
                scissorTestEnabled: false),
            PrimitiveTopology = Veldrid.PrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _resourceLayout! },
            ShaderSet = new ShaderSetDescription(
                vertexLayouts: new[] { vertexLayout },
                shaders: new[] { _vertexShader, _fragmentShader }),
            Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription
        };

        _pipeline = _graphicsDevice.ResourceFactory.CreateGraphicsPipeline(pipelineDescription);
    }

    private void RenderFrame()
    {
        if (_graphicsDevice == null || _commandList == null || _pipeline == null || _resourceSet == null) return;

        _commandList.Begin();

        _commandList.SetFramebuffer(_graphicsDevice.SwapchainFramebuffer);
        _commandList.ClearColorTarget(0, RgbaFloat.Clear);

        _commandList.SetPipeline(_pipeline);
        _commandList.SetVertexBuffer(0, _vertexBuffer);
        _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
        _commandList.SetGraphicsResourceSet(0, _resourceSet);

        _commandList.DrawIndexed(
            indexCount: 6,
            instanceCount: 1,
            indexStart: 0,
            vertexOffset: 0,
            instanceStart: 0);

        _commandList.End();

        _graphicsDevice.SubmitCommands(_commandList);
        _graphicsDevice.SwapBuffers();
    }

    private static ID3D11Texture2D GetDXGISurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var guid = typeof(ID3D11Texture2D).GUID;
        var ptr = access.GetInterface(ref guid);
        return new ID3D11Texture2D(ptr);
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

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    private static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr d3dDevicePtr);
        if (hr != 0)
        {
            throw new Exception($"Failed to create IDirect3DDevice. HRESULT: 0x{hr:X8}");
        }

        var device = MarshalInterface<IDirect3DDevice>.FromAbi(d3dDevicePtr);
        Marshal.Release(d3dDevicePtr);
        return device;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private void RunMessageLoop()
    {
        while (_isRunning && GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_DESTROY = 0x0002;
        const uint WM_CLOSE = 0x0010;
        const uint WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        switch (msg)
        {
            case WM_NCHITTEST:
                // Return HTTRANSPARENT to make the window click-through
                Debug.WriteLine("WM_NCHITTEST received - returning HTTRANSPARENT");
                return new IntPtr(HTTRANSPARENT);

            case WM_CLOSE:
            case WM_DESTROY:
                _isRunning = false;
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _isRunning = false;

        // Dispose capture resources
        _session?.Dispose();
        _framePool?.Dispose();
        _d3dContext?.Dispose();
        _d3dDevice?.Dispose();

        // Dispose Veldrid resources
        _resourceSet?.Dispose();
        _resourceLayout?.Dispose();
        _sampler?.Dispose();
        _sourceTextureView?.Dispose();
        _sourceTexture?.Dispose();
        _pipeline?.Dispose();
        _fragmentShader?.Dispose();
        _vertexShader?.Dispose();
        _indexBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _commandList?.Dispose();
        _graphicsDevice?.Dispose();

        // Destroy window
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VertexPositionTexture
    {
        public const uint SizeInBytes = 16;
        public Vector2 Position;
        public Vector2 TexCoords;

        public VertexPositionTexture(Vector2 position, Vector2 texCoords)
        {
            Position = position;
            TexCoords = texCoords;
        }
    }
}
