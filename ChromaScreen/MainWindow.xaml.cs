using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ChromaScreen;

public partial class MainWindow : Window
{
    private OverlayWindow? _overlayWindow;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
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
                cropTop = 36;
            if (!int.TryParse(CropLeftTextBox.Text, out int cropLeft) || cropLeft < 0)
                cropLeft = 1;
            if (!int.TryParse(CropRightTextBox.Text, out int cropRight) || cropRight < 0)
                cropRight = 1;
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

            MessageBox.Show("Overlay started! Close the overlay window to stop.", "ChromaScreen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Please select a window first.", "ChromaScreen", MessageBoxButton.OK, MessageBoxImage.Warning);
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
