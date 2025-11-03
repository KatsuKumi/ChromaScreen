# ChromaScreen

ChromaScreen is a Windows application that captures a window's content, applies chroma keying (brightness-based transparency), and displays it as an always-on-top overlay.

## Features

- **Window Selection**: Select any visible window to capture
- **Full Window Capture**: Captures the entire window content using Windows Graphics Capture API
- **Chroma Key Processing**: Makes dark pixels transparent based on brightness threshold
- **Custom Crop Margins**: Trim unwanted edges (top, left, right, bottom) from the captured window
- **Always-on-Top Overlay**: The processed window appears as an overlay that stays on top of all windows
- **Draggable Overlay**: Click and drag the overlay window to position it anywhere (e.g., over a game as a HUD)
- **Resizable Overlay**: Resize the overlay to any size you want - the captured content scales automatically
- **Independent Positioning**: The overlay doesn't follow the source window - you control where it displays
- **Adjustable Settings**:
  - **Chroma Key Threshold**: Control how dark a pixel needs to be to become transparent (0-100, based on RGB brightness average)
  - **Update Rate**: Control how frequently the capture refreshes (16-500ms)
  - **Crop Margins**: Customize crop values for each edge (top, left, right, bottom) in pixels

## How to Use

1. **Launch ChromaScreen**: Run the application
2. **Select a Window**: From the window list, select the window you want to capture (source window)
3. **Adjust Settings** (optional):
   - Set the chroma key threshold (higher = more aggressive transparency for dark pixels)
   - Set the update rate (lower = smoother but more CPU intensive)
   - Adjust crop margins to trim unwanted edges from the capture (top, left, right, bottom in pixels)
4. **Click "Start Overlay"**: The overlay window will appear, showing the captured content with dark areas transparent
5. **Position the Overlay**: Click and drag the overlay window to position it wherever you want (e.g., over a game)
6. **Resize the Overlay**: Drag the edges or corners to resize the overlay to your preferred size
7. **Close the Overlay**: Simply close the overlay window when you're done

### Example Use Case

1. Open a monitoring app or tool with a dark/black background
2. Start ChromaScreen and select that window
3. Adjust the chroma key threshold to make the dark background transparent
4. Adjust crop margins if needed to trim window edges/borders
5. Drag the overlay onto your game window
6. The monitoring info appears as a transparent HUD over your game!

## Building

```bash
dotnet build
```

## Running

```bash
dotnet run
```

Or run the compiled executable from:
```
bin\Debug\net9.0-windows\ChromaScreen.exe
```

## Technical Details

- **Framework**: .NET 9.0 with WPF
- **Window Capture**: Windows Graphics Capture API (Windows.Graphics.Capture)
- **Graphics Backend**:
  - Direct3D 11 (Vortice.Direct3D11 and Vortice.DXGI libraries)
  - Hardware-accelerated frame capture and processing
  - Staging textures for CPU-side pixel access
- **Chroma Key Algorithm**: Brightness-based transparency using RGB average ((R+G+B)/3)
- **Image Processing**:
  - Custom crop region extraction from captured frames
  - Real-time chroma key application on CPU
  - WriteableBitmap for WPF rendering

## Requirements

- **Windows 10** version 1809 (October 2018 Update, build 17763) or later
- **Windows 11** (fully supported)
- **.NET 9.0 Runtime**
- **DirectX 11** compatible graphics card
- **Windows Graphics Capture API** support (available on Windows 10 1809+)

## Known Limitations

- **Brightness-Based Transparency**: The chroma key uses brightness threshold (RGB average), not true color keying. This means it removes dark colors regardless of hue. It works best with content that has dark backgrounds and bright foreground elements.
- **CPU Processing**: Chroma key processing happens on the CPU rather than GPU, which may impact performance on very high resolution captures or low update rates.
- **Windows Graphics Capture API Restrictions**: Some protected content windows (DRM, certain games with anti-cheat) may not be capturable due to OS-level restrictions.

## Use Cases

- **Gaming HUD**: Display monitoring tools (FPS counters, system stats, chat) as transparent overlays on games
- **Live Streaming**: Add transparent elements over your stream content
- **Presentations**: Overlay notes or information on top of presentation software
- **Multi-tasking**: Keep important information visible over full-screen applications
- Any scenario requiring real-time chroma keying and overlay positioning
