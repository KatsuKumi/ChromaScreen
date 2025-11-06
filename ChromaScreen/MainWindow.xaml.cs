using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using ChromaNet.Core;
using ChromaNet.Network;

namespace ChromaScreen;

public partial class MainWindow : Window
{
    private Thread? _veldridThread;
    private VeldridOverlayWindow? _veldridOverlay;

    // ChromaNet fields
    private ChromaNetServer? _chromaServer;
    private ChromaNetClient? _chromaClient;
    private VeldridOverlayWindow? _clientOverlay;
    private Thread? _clientOverlayThread;
    private CancellationTokenSource? _clientCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Clean up local Veldrid overlay if running
        if (_veldridOverlay != null)
        {
            _veldridOverlay.Close(); // Send WM_CLOSE to gracefully exit message loop
            _veldridThread?.Join(2000); // Wait up to 2 seconds for thread to finish
            _veldridOverlay.Dispose();
            _veldridOverlay = null;
        }

        // Clean up ChromaNet client overlay if running
        CloseClientOverlay();

        // Clean up ChromaNet resources
        _chromaServer?.Dispose();
        _chromaClient?.Dispose();
        _clientCts?.Cancel();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
        RefreshMonitorList();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e)
    {
        RefreshMonitorList();
    }

    private void RefreshMonitorList()
    {
        var monitors = new List<MonitorInfo>();
        var screens = WinForms.Screen.AllScreens;

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            string displayName = $"Monitor {i}: {screen.Bounds.Width}x{screen.Bounds.Height}";
            if (screen.Primary)
                displayName += " (Primary)";

            monitors.Add(new MonitorInfo
            {
                Index = i,
                DisplayName = displayName,
                Bounds = screen.Bounds,
                IsPrimary = screen.Primary
            });
        }

        MonitorListBox.ItemsSource = monitors;
        MonitorListBox.SelectedIndex = 0; // Select primary monitor by default
    }

    private void StartVeldridButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowListBox.SelectedItem is WindowInfo selectedWindow)
        {
            // Clean up existing Veldrid overlay
            if (_veldridOverlay != null)
            {
                _veldridOverlay.Dispose();
                _veldridOverlay = null;
            }

            // Parse crop margins with validation
            if (!int.TryParse(CropTopTextBox.Text, out int cropTop) || cropTop < 0)
                cropTop = 0;
            if (!int.TryParse(CropLeftTextBox.Text, out int cropLeft) || cropLeft < 0)
                cropLeft = 0;
            if (!int.TryParse(CropRightTextBox.Text, out int cropRight) || cropRight < 0)
                cropRight = 0;
            if (!int.TryParse(CropBottomTextBox.Text, out int cropBottom) || cropBottom < 0)
                cropBottom = 0;

            // Create Veldrid overlay
            _veldridOverlay = new VeldridOverlayWindow(
                selectedWindow.Handle,
                (int)ThresholdSlider.Value,
                (int)UpdateRateSlider.Value,
                cropTop,
                cropLeft,
                cropRight,
                cropBottom
            );

            // Run in separate thread (Veldrid requires its own message loop)
            _veldridThread = new Thread(() =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("Starting Veldrid overlay...");
                    _veldridOverlay.Show(); // Blocks until window closed
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Veldrid error: {ex}");
                    Dispatcher.Invoke(() =>
                    {
                        System.Windows.MessageBox.Show(
                            $"Veldrid overlay error: {ex.Message}\n\n" +
                            $"Exception Type: {ex.GetType().Name}\n\n" +
                            "This might be due to:\n" +
                            "- Graphics drivers need updating\n" +
                            "- DirectX 11 not available\n" +
                            "- Shader compilation failed\n" +
                            "- Window creation failed\n\n" +
                            $"Full error:\n{ex}",
                            "Veldrid Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
            })
            {
                IsBackground = false,
                Name = "VeldridOverlayThread"
            };
            _veldridThread.SetApartmentState(System.Threading.ApartmentState.STA);
            _veldridThread.Start();

            System.Windows.MessageBox.Show(
                "Veldrid overlay started! (Hardware-Accelerated)\n\n" +
                "This should be 3-4x faster than WPF overlay.\n" +
                "Close the overlay window to stop.\n\n" +
                "Check Debug Output in Visual Studio for FPS stats.",
                "ChromaScreen - Veldrid",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show("Please select a window first.", "ChromaScreen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshWindowList()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, lParam) =>
        {
            if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
            {
                var title = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);

                var titleStr = title.ToString();
                if (!string.IsNullOrWhiteSpace(titleStr))
                {
                    windows.Add(new WindowInfo
                    {
                        Handle = hWnd,
                        Title = titleStr
                    });
                }
            }
            return true;
        }, IntPtr.Zero);

        WindowListBox.ItemsSource = windows;
    }

    #region ChromaNet Event Handlers

    private void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Parse chroma mode
            var chromaModeStr = ((ComboBoxItem)ChromaModeSelector.SelectedItem).Tag.ToString()!;
            var chromaMode = (ChromaMode)Enum.Parse(typeof(ChromaMode), chromaModeStr);

            // Parse port
            int port = int.Parse(ServerPortTextBox.Text);

            // Get all local IPs for display
            var localIps = GetAllLocalIPAddresses();
            string localIpsDisplay = string.Join(", ", localIps);

            // Get selected monitor
            if (MonitorListBox.SelectedItem is not MonitorInfo selectedMonitor)
            {
                System.Windows.MessageBox.Show("Please select a monitor first.", "ChromaNet", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int monitorIndex = selectedMonitor.Index;

            // Create server - captures desktop/monitor, not a specific window
            _chromaServer = new ChromaNetServer(
                port,
                chromaMode,
                (byte)ThresholdSlider.Value,
                monitorIndex
            );

            // Subscribe to status updates
            _chromaServer.OnStatusUpdate += (status) =>
            {
                Dispatcher.Invoke(() => ServerStatusText.Text = status);
            };

            // Start capturing (captures entire desktop)
            _chromaServer.Start();

            ServerStatusText.Text = $"✅ Server listening on port {port} | IPs: {localIpsDisplay}";
            StartServerButton.IsEnabled = false;

            System.Windows.MessageBox.Show(
                $"ChromaNet Server Started!\n\n" +
                $"Capturing: {selectedMonitor.DisplayName}\n" +
                $"Resolution: {selectedMonitor.Bounds.Width}x{selectedMonitor.Bounds.Height}\n" +
                $"Available Server IPs: {localIpsDisplay}\n" +
                $"Server Port: {port}\n" +
                $"Chroma Mode: {chromaMode}\n\n" +
                $"Waiting for client to connect...\n" +
                $"Client should use one of the IPs above with port {port}",
                "Server Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to start server:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ServerStatusText.Text = $"❌ Error: {ex.Message}";
        }
    }

    private void ConnectClientButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string serverIp = ServerIpTextBox.Text;
            int serverPort = int.Parse(ClientPortTextBox.Text);

            // Create cancellation token
            _clientCts = new CancellationTokenSource();

            // Create client (uses ephemeral port automatically)
            _chromaClient = new ChromaNetClient(serverIp, serverPort);

            // Subscribe to status updates
            _chromaClient.OnStatusUpdate += (status) =>
            {
                Dispatcher.Invoke(() => ClientStatusText.Text = status);
            };

            // Subscribe to frame received events
            _chromaClient.OnFrameReceived += OnClientFrameReceived;

            // Subscribe to status updates to detect disconnection
            _chromaClient.OnStatusUpdate += (status) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ClientStatusText.Text = status;

                    // Check if disconnected - close overlay
                    if (status.Contains("Disconnected") || status.Contains("disconnect"))
                    {
                        Console.WriteLine("[MainWindow] Server disconnected, closing overlay...");
                        CloseClientOverlay();
                        ConnectClientButton.IsEnabled = true;
                    }
                });
            };

            ClientStatusText.Text = $"🔄 Connecting to {serverIp}:{serverPort}...";
            ConnectClientButton.IsEnabled = false;

            // Connect to server (non-blocking, starts background network loop)
            _chromaClient.Connect();

            ClientStatusText.Text = $"✅ Connected to {serverIp}:{serverPort}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Connection failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ClientStatusText.Text = $"❌ Connection failed: {ex.Message}";
            ConnectClientButton.IsEnabled = true;
        }
    }

    private void OnClientFrameReceived(byte[] frameData, int width, int height, ChromaMode chromaMode, byte threshold)
    {
        // First frame? Create overlay window
        if (_clientOverlay == null)
        {
            var overlayReady = new ManualResetEventSlim(false);

            _clientOverlayThread = new Thread(() =>
            {
                try
                {
                    Console.WriteLine("[ClientOverlay] Creating overlay window...");
                    _clientOverlay = new VeldridOverlayWindow(
                        IntPtr.Zero, // No specific window target
                        threshold,
                        16, // 60 FPS target
                        0, 0, 0, 0, // No cropping
                        true // isNetworkClient = true
                    );

                    Console.WriteLine("[ClientOverlay] Overlay created, calling Show()...");

                    // Signal that overlay is ready BEFORE Show() blocks in message loop
                    overlayReady.Set();

                    // This will create the window and run the message loop (blocks)
                    _clientOverlay.Show();

                    Console.WriteLine("[ClientOverlay] Message loop ended");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ClientOverlay] Error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Client overlay error: {ex}");
                }
            })
            {
                IsBackground = false,
                Name = "ChromaNetClientOverlay"
            };
            _clientOverlayThread.SetApartmentState(System.Threading.ApartmentState.STA);
            _clientOverlayThread.Start();

            // Wait for overlay to be created (with timeout)
            if (!overlayReady.Wait(2000))
            {
                Console.WriteLine("[ClientOverlay] WARNING: Overlay creation timed out!");
            }
            else
            {
                Console.WriteLine("[ClientOverlay] Overlay ready!");
                // Give Veldrid a bit more time to initialize
                Thread.Sleep(200);
            }
        }

        // Render the frame (if overlay is ready)
        if (_clientOverlay != null)
        {
            try
            {
                _clientOverlay.RenderFromBuffer(frameData, width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClientOverlay] Render error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error rendering frame: {ex}");
            }
        }
    }

    private string GetLocalIPAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private List<string> GetAllLocalIPAddresses()
    {
        var ips = new List<string>();
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    ips.Add(ip.ToString());
                }
            }
        }
        catch { }

        if (ips.Count == 0)
        {
            ips.Add("127.0.0.1");
        }

        return ips;
    }

    /// <summary>
    /// Helper method to close client overlay and clean up thread
    /// </summary>
    private void CloseClientOverlay()
    {
        if (_clientOverlay != null)
        {
            Console.WriteLine("[MainWindow] CloseClientOverlay: Closing overlay...");
            _clientOverlay.Close(); // Send WM_CLOSE
            _clientOverlayThread?.Join(2000); // Wait for thread to exit
            _clientOverlay.Dispose();
            _clientOverlay = null;
            _clientOverlayThread = null;
            Console.WriteLine("[MainWindow] CloseClientOverlay: Overlay closed");
        }
    }

    #endregion

    #region Windows API

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    #endregion
}

public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class MonitorInfo
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public System.Drawing.Rectangle Bounds { get; set; }
    public bool IsPrimary { get; set; }
}
