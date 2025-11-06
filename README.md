# ChromaScreen

High-performance Windows screen capture and streaming application with GPU-accelerated transparent overlay and real-time network streaming capabilities.

## Features

### Local Overlay Mode
- **Hardware-Accelerated Rendering**: GPU-based transparent overlay using Veldrid and DirectX 11
- **Chroma Key Support**: Real-time green screen filtering with adjustable threshold
- **Window Capture**: Capture any visible window with Windows Graphics Capture API
- **Click-Through Overlay**: Transparent, topmost overlay that doesn't interfere with your workflow
- **Crop Controls**: Adjust top/left/right/bottom margins to exclude unwanted areas
- **High Performance**: Sustained 60 FPS with minimal CPU usage

### Network Streaming Mode
- **Ultra-Low Latency**: Real-time screen streaming with <50ms end-to-end latency
- **Intelligent Delta Compression**: Only sends changed screen regions using Desktop Duplication API
- **High Efficiency**: 8-15 Mbps for typical desktop usage, 60-120 Mbps for high-motion content
- **LZ4 Compression**: Ultra-fast compression for large regions
- **Unreliable UDP**: ENet-CSharp with unreliable fragmentation for zero head-of-line blocking
- **Auto-Recovery**: Automatic full-screen refresh on major scene changes (Alt+Tab, maximize) at 75% screen coverage threshold
- **Multi-Client**: Server can stream to multiple clients simultaneously

## System Requirements

### Minimum Requirements
- **OS**: Windows 10 version 1809 (Build 17763) or later
- **GPU**: DirectX 11 compatible GPU (Feature Level 11.0+)
- **RAM**: 4 GB
- **.NET**: .NET 9.0 Runtime

### Recommended
- **OS**: Windows 11
- **GPU**: Modern GPU with updated drivers (NVIDIA, AMD, Intel)
- **RAM**: 8 GB or more
- **Network**: Gigabit Ethernet for network streaming

## Installation

### From Release
1. Download the latest release from GitHub
2. Extract the archive to your preferred location
3. Run `ChromaScreen.exe`

### Building from Source

```powershell
# Clone repository
git clone https://github.com/yourusername/ChromaScreen.git
cd ChromaScreen

# Build with .NET 9
dotnet build ChromaScreen.sln -c Release

# Run
dotnet run --project ChromaScreen/ChromaScreen.csproj
```

## Usage

### Local Overlay Mode

Perfect for capturing a specific window and displaying it as a transparent overlay on your screen.

1. **Select Window**:
   - Click "Refresh Windows" to populate the window list
   - Select the target window from the dropdown

2. **Configure Capture** (Optional):
   - Adjust **Crop Margins** (Top/Left/Right/Bottom) to exclude borders
   - Adjust **Chroma Threshold** slider for green screen filtering (0-255)
   - Adjust **Update Rate** slider for target FPS (16-100ms)

3. **Start Overlay**:
   - Click "Start Veldrid Overlay"
   - A transparent, topmost overlay window appears
   - The overlay follows the target window automatically

4. **Stop Overlay**:
   - Close the overlay window to stop

### Network Streaming Mode

Stream your entire monitor to other computers on your network in real-time.

#### Server Setup (Computer sharing screen)

1. **Select Monitor**:
   - Click "Refresh Monitors" to list available displays
   - Select the monitor to capture

2. **Configure Streaming**:
   - Choose **Chroma Mode**: None, GreenScreen, or Custom
   - Set **Server Port** (default: 9050)
   - Ensure port is open in Windows Firewall

3. **Start Server**:
   - Click "Start Server"
   - Note the displayed **Server IP addresses**
   - Share IP and port with clients

#### Client Setup (Computer viewing stream)

1. **Connect to Server**:
   - Enter **Server IP** address
   - Enter **Server Port** (default: 9050)
   - Click "Connect"

2. **View Stream**:
   - Overlay window appears automatically
   - Full screen is sent on initial connection
   - Real-time updates follow automatically
   - Major scene changes (≥75% coverage) trigger full screen refresh

3. **Disconnect**:
   - Overlay closes automatically when server disconnects
   - Or restart client application

## Performance

### Local Overlay
- **1920x1080 @ 60 FPS**: Sustained, GPU-accelerated
- **Render Time**: ~10ms average per frame
- **CPU Usage**: Minimal (5-10%)

### Network Streaming
- **Resolution**: Up to 4K supported
- **Frame Rate**: 40-60 FPS typical
- **Latency**: 30-50ms total (capture to display)
- **Ping**: 8-15ms on local network
- **Bandwidth**:
  - Idle Desktop: 8-15 Mbps
  - Active Desktop: 30-60 Mbps
  - High Motion (video/gaming): 60-120 Mbps

### Optimization Tips
- **Update GPU Drivers**: Ensures best compatibility and performance
- **Reduce Capture Resolution**: Use crop margins to exclude unnecessary areas
- **Wired Network**: Use Gigabit Ethernet for streaming (avoid WiFi)
- **Close Unnecessary Apps**: Free up GPU and CPU resources

## Known Issues and Limitations

### General
- **Elevated Windows**: Cannot capture windows running with administrator privileges from non-elevated process
- **Fullscreen Exclusive**: Some games using exclusive fullscreen mode cannot be captured
- **RDP/Remote Desktop**: Desktop Duplication API doesn't work over Remote Desktop sessions

### Network Streaming
- **Firewall**: UDP port must be open on server (default: 9050)
- **No Authentication**: Designed for trusted networks only - no encryption or access control
- **No Audio**: Currently video-only (audio streaming planned for future release)

### Troubleshooting

**Overlay shows black screen:**
- Window may be minimized or occluded
- Try selecting a different window
- Refresh window list and retry

**"Shader compilation failed":**
- Update GPU drivers
- Verify DirectX 11 Feature Level 11.0+ support

**Network connection fails:**
- Check Windows Firewall (allow UDP port)
- Verify server IP address is correct
- Ensure server is started before connecting client
- Test with localhost (127.0.0.1) on same machine first

**Poor streaming performance:**
- Check network connection quality (use wired Gigabit Ethernet)
- Update GPU drivers on both server and client
- Close bandwidth-intensive applications

**Client shows artifacts after Alt+Tab:**
- This should be fixed automatically (75% coverage threshold triggers full screen)
- If persisting, report as issue with console logs

## Technology Stack

- **.NET 9**: Modern C# with Windows platform integration
- **WPF**: Main application UI
- **Veldrid**: Cross-platform GPU rendering abstraction (4.9.0)
- **DirectX 11**: Windows Graphics Capture API and Desktop Duplication API
  - **Vortice.Direct3D11** (2.3.0)
  - **Vortice.DXGI** (2.3.0)
- **ENet-CSharp** (2.4.8): Reliable UDP networking with unreliable fragmentation
- **MemoryPack** (1.21.3): Zero-allocation serialization
- **K4os.Compression.LZ4** (1.3.8): Ultra-fast compression

## Architecture

### Local Mode Pipeline
```
Windows Graphics Capture API
    ↓
GraphicsCaptureItem (Window Handle)
    ↓
Direct3D11CaptureFramePool
    ↓
Veldrid Texture Upload
    ↓
GPU Shader (Chroma Key)
    ↓
DirectX 11 Swapchain (Transparent Overlay)
```

### Network Streaming Pipeline

**Server:**
```
Desktop Duplication API (Monitor Capture)
    ↓
Dirty Rectangle Detection (Changed Regions Only)
    ↓
Major Change Detection (≥75% coverage → Full Screen)
    ↓
Parallel LZ4 Compression (Multi-Core)
    ↓
MemoryPack Serialization
    ↓
ENet UnreliableFragmented Delivery
```

**Client:**
```
ENet UDP Socket
    ↓
MemoryPack Deserialization
    ↓
LZ4 Decompression (Parallel)
    ↓
Frame Reconstruction (Merge Dirty Regions)
    ↓
Veldrid Texture Upload
    ↓
GPU Rendering (Transparent Overlay)
```

## Changelog

### Recent Improvements
- **Migration to ENet-CSharp**: Replaced LiteNetLib with ENet for unreliable fragmentation support
  - Eliminated multi-second delays during high-motion capture
  - 100% unreliable delivery for zero head-of-line blocking
  - Automatic packet fragmentation without reliability overhead
- **Major Change Detection**: Automatic full-screen refresh when ≥75% of screen changes
  - Fixes artifacts on Alt+Tab, window maximize/minimize
  - Prevents stale regions during major scene transitions
- **Reduced Logging**: Minimized console output to avoid performance bottlenecks
  - Removed per-frame debug logs
  - Kept connection events and per-second statistics
- **Full Screen on Connect**: New clients receive complete initial frame
- **Auto-Close on Disconnect**: Client overlay automatically closes when server disconnects

## Roadmap

### Planned Features
- H.264 hardware encoding for reduced bandwidth
- Authentication and encryption for network streaming
- Audio capture and streaming
- Recording to file (MP4, WebM)
- Web-based client (WebRTC)
- OBS plugin integration
- Configurable chroma key color

### Performance Improvements
- Vulkan backend option
- GPU-based chroma key processing
- Adaptive bitrate based on network conditions
- Client-side frame interpolation

## License

[Specify license here - e.g., MIT, GPL, Apache 2.0]

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

For development documentation and architecture details, see `CLAUDE.md`.

## Contact

- **GitHub Issues**: [Project Issues Page]
- **Documentation**: See `CLAUDE.md` for development environment setup

## Acknowledgments

Built with high-performance libraries:
- [Veldrid](https://github.com/mellinoe/veldrid) - GPU rendering abstraction
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) - DirectX bindings
- [ENet-CSharp](https://github.com/SoftwareGuy/ENet-CSharp) - UDP networking with fragmentation
- [MemoryPack](https://github.com/Cysharp/MemoryPack) - Zero-allocation serialization
- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) - Ultra-fast LZ4 compression

---

**Disclaimer**: ChromaScreen is a tool for screen capture and streaming. Ensure you have permission to capture and stream content. Performance varies by hardware - DirectX 11 compatible GPU required. Not suitable for capturing protected/DRM content.
