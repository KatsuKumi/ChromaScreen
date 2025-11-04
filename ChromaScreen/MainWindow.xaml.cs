using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ChromaScreen;

public partial class MainWindow : Window
{
    private OverlayWindow? _overlayWindow;
    private Thread? _veldridThread;
    private VeldridOverlayWindow? _veldridOverlay;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Clean up Veldrid overlay if running
        _veldridOverlay?.Dispose();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowListBox.SelectedItem is WindowInfo selectedWindow)
        {
            if (_overlayWindow != null)
            {
                _overlayWindow.Close();
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

            _overlayWindow = new OverlayWindow(
                selectedWindow.Handle,
                (int)ThresholdSlider.Value,
                (int)UpdateRateSlider.Value,
                cropTop,
                cropLeft,
                cropRight,
                cropBottom
            );
            _overlayWindow.Show();

            System.Windows.MessageBox.Show("WPF Overlay started! Close the overlay window to stop.", "ChromaScreen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show("Please select a window first.", "ChromaScreen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
