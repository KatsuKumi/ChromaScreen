# ChromaScreen

ChromaScreen is a Windows application that captures a window's content, applies chroma keying (black transparency), and displays it as an always-on-top overlay.

## Features

- **Window Selection**: Select any visible window to capture
- **Client Area Only**: Captures only window content (excludes titlebar, borders, overlapping windows)
- **Chroma Key Processing**: Automatically makes black pixels transparent
- **Always-on-Top Overlay**: The processed window appears as an overlay that stays on top of all windows
- **Draggable Overlay**: Click and drag the overlay window to position it anywhere (e.g., over a game as a HUD)
- **Resizable Overlay**: Resize the overlay to any size you want - the captured content scales automatically
- **Independent Positioning**: The overlay doesn't follow the source window - you control where it displays
- **Adjustable Settings**:
  - **Chroma Key Threshold**: Control how dark a pixel needs to be to become transparent (0-100)
  - **Update Rate**: Control how frequently the capture refreshes (16-500ms)

## How to Use

1. **Launch ChromaScreen**: Run the application
2. **Select a Window**: From the window list, select the window you want to capture (source window)
3. **Adjust Settings** (optional):
   - Set the chroma key threshold (higher = more aggressive black removal)
   - Set the update rate (lower = smoother but more CPU intensive)
4. **Click "Start Overlay"**: The overlay window will appear, showing the captured content with black areas transparent
5. **Position the Overlay**: Click and drag the overlay window to position it wherever you want (e.g., over a game)
6. **Resize the Overlay**: Drag the edges or corners to resize the overlay to your preferred size
7. **Close the Overlay**: Simply close the overlay window when you're done

### Example Use Case

1. Open a monitoring app or tool with a black background
2. Start ChromaScreen and select that window
3. Drag the overlay onto your game window
4. The monitoring info appears as a transparent HUD over your game!

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
- **Window Capture**: Native Windows API using PrintWindow (PW_CLIENTONLY flag)
- **DWM Integration**: Custom DWM Thumbnail API wrapper for hardware-accelerated rendering
- **Chroma Key**: Real-time pixel processing with adjustable threshold
- **Performance**:
  - Hardware-accelerated window capture
  - Unsafe code for fast pixel manipulation
  - Direct memory access for chroma key processing

## Requirements

- Windows OS
- .NET 9.0 Runtime
- DirectX 11 compatible graphics card

## Known Limitations

- **Multi-Monitor Support**: The tool captures from the primary display's coordinate space. If you move the source window to a secondary monitor, the capture may fail with coordinate errors. For best results, keep the source window on your primary monitor, or restart the overlay after moving windows between monitors.

## Use Cases

- **Gaming HUD**: Display monitoring tools (FPS counters, system stats, chat) as transparent overlays on games
- **Live Streaming**: Add transparent elements over your stream content
- **Presentations**: Overlay notes or information on top of presentation software
- **Multi-tasking**: Keep important information visible over full-screen applications
- Any scenario requiring real-time chroma keying and overlay positioning
