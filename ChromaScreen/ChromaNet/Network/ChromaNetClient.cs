using System.Diagnostics;
using System.Net;
using ChromaNet.Compression;
using ChromaNet.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using MemoryPack;

namespace ChromaNet.Network;

/// <summary>
/// ChromaNet client - receives and decompresses frames from server
/// </summary>
public class ChromaNetClient : IDisposable, INetEventListener
{
    private readonly NetManager _netManager;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private NetPeer? _serverPeer;
    private byte[]? _compositeBuffer;
    private int _screenWidth;
    private int _screenHeight;
    private bool _running;
    private bool _disposed;

    // Performance tracking
    private readonly Stopwatch _perfTimer = Stopwatch.StartNew();
    private long _frameCount = 0;
    private long _reportFrameCount = 0;
    private long _totalBytes = 0;
    private long _lastReport = 0;

    // Callback interval tracking for FPS bottleneck diagnosis
    private long _lastCallbackTime = 0;
    private readonly Stopwatch _callbackIntervalTimer = Stopwatch.StartNew();

    public event Action<string>? OnStatusUpdate;
    public event Action<byte[], int, int, ChromaMode, byte>? OnFrameReceived;

    /// <summary>
    /// Get current screen dimensions
    /// </summary>
    public (int Width, int Height) ScreenSize => (_screenWidth, _screenHeight);

    /// <summary>
    /// Create a ChromaNet client
    /// </summary>
    /// <param name="serverAddress">Server IP address</param>
    /// <param name="serverPort">Server port</param>
    public ChromaNetClient(string serverAddress, int serverPort)
    {
        _serverAddress = serverAddress;
        _serverPort = serverPort;

        // Create LiteNetLib client
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            UpdateTime = 15 // 15ms update interval
        };

        _netManager.Start();

        OptimizeSocketBuffers();

        Console.WriteLine($"[ChromaNetClient] Created client for {serverAddress}:{serverPort}");
    }

    private void OptimizeSocketBuffers()
    {
        try
        {
            var socketField = _netManager.GetType().GetField("_socket",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (socketField?.GetValue(_netManager) is System.Net.Sockets.Socket socket)
            {
                socket.SendBufferSize = 1024 * 512;
                socket.ReceiveBufferSize = 1024 * 512;

                Console.WriteLine($"[ChromaNetClient] Socket buffers optimized: Send={socket.SendBufferSize}, Recv={socket.ReceiveBufferSize}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChromaNetClient] Warning: Could not optimize socket buffers: {ex.Message}");
        }
    }

    /// <summary>
    /// Connect to server and start receiving
    /// </summary>
    public void Connect()
    {
        if (_running)
            return;

        _running = true;

        // Connect to server
        _serverPeer = _netManager.Connect(_serverAddress, _serverPort, "ChromaNet");

        OnStatusUpdate?.Invoke($"Connecting to {_serverAddress}:{_serverPort}...");
        Console.WriteLine($"[ChromaNetClient] Connecting to server...");

        // Start network poll loop
        Task.Run(NetworkLoop);
    }

    /// <summary>
    /// Network polling loop
    /// </summary>
    private async Task NetworkLoop()
    {
        while (_running)
        {
            try
            {
                _netManager.PollEvents();
                await Task.Delay(1); // ~1000 FPS poll rate
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChromaNetClient] Network error: {ex.Message}");
                OnStatusUpdate?.Invoke($"Error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Disconnect from server
    /// </summary>
    public void Disconnect()
    {
        _running = false;
        _serverPeer?.Disconnect();
    }

    /// <summary>
    /// Process received frame packet
    /// </summary>
    private void ProcessFramePacket(byte[] data)
    {
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            var deserializeStopwatch = Stopwatch.StartNew();
            var packet = MemoryPackSerializer.Deserialize<FramePacket>(data);
            deserializeStopwatch.Stop();

            if (packet == null)
                return;

            if (_compositeBuffer == null ||
                _screenWidth != packet.ScreenWidth ||
                _screenHeight != packet.ScreenHeight)
            {
                _screenWidth = packet.ScreenWidth;
                _screenHeight = packet.ScreenHeight;
                _compositeBuffer = new byte[_screenWidth * _screenHeight * 4];

                Array.Fill<byte>(_compositeBuffer, 0);

                OnStatusUpdate?.Invoke($"Initialized: {_screenWidth}x{_screenHeight}, Mode: {packet.ChromaMode}");
            }

            var decompressStopwatch = Stopwatch.StartNew();

            var decompressedRegions = new (DirtyRegion region, byte[] pixels)[packet.Regions.Count];

            for (int i = 0; i < packet.Regions.Count; i++)
            {
                var region = packet.Regions[i];
                byte[] pixels = region.IsCompressed
                    ? LZ4Compressor.Decompress(region.CompressedPixels, region.UncompressedSize)
                    : region.CompressedPixels;
                decompressedRegions[i] = (region, pixels);
            }

            decompressStopwatch.Stop();
            double totalDecompressMs = decompressStopwatch.Elapsed.TotalMilliseconds;

            var blitStopwatch = Stopwatch.StartNew();
            foreach (var (region, pixels) in decompressedRegions)
            {
                BlitRegion(_compositeBuffer, region, pixels);
            }
            blitStopwatch.Stop();
            double totalBlitMs = blitStopwatch.Elapsed.TotalMilliseconds;

            long currentCallbackTime = _callbackIntervalTimer.ElapsedMilliseconds;
            long intervalMs = currentCallbackTime - _lastCallbackTime;
            _lastCallbackTime = currentCallbackTime;

            if (intervalMs > 100)
            {
                Console.WriteLine($"[ChromaNetClient] WARNING: Long callback interval: {intervalMs}ms");
            }

            var callbackStopwatch = Stopwatch.StartNew();
            OnFrameReceived?.Invoke(
                _compositeBuffer,
                _screenWidth,
                _screenHeight,
                packet.ChromaMode,
                packet.ChromaThreshold);
            callbackStopwatch.Stop();

            totalStopwatch.Stop();

            _frameCount++;
            _reportFrameCount++;
            _totalBytes += data.Length;

            long currentTime = _perfTimer.ElapsedMilliseconds;
            if (currentTime - _lastReport >= 1000)
            {
                ReportStats(packet.Regions.Count, deserializeStopwatch.Elapsed.TotalMilliseconds,
                    totalDecompressMs, totalBlitMs,
                    callbackStopwatch.Elapsed.TotalMilliseconds, totalStopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChromaNetClient] Process error: {ex.Message}");
            OnStatusUpdate?.Invoke($"Process error: {ex.Message}");
        }
    }

    private unsafe void BlitRegion(byte[] buffer, DirtyRegion region, byte[] pixels)
    {
        int srcStride = region.Width * 4;
        int dstStride = _screenWidth * 4;

        fixed (byte* srcPtr = pixels)
        fixed (byte* dstPtr = buffer)
        {
            byte* src = srcPtr;
            byte* dst = dstPtr + ((region.Y * _screenWidth + region.X) * 4);

            for (int y = 0; y < region.Height; y++)
            {
                Buffer.MemoryCopy(src, dst, srcStride, srcStride);
                src += srcStride;
                dst += dstStride;
            }
        }
    }

    private void ReportStats(int regionCount, double deserializeMs, double decompressMs,
        double blitMs, double callbackMs, double totalMs)
    {
        long currentTime = _perfTimer.ElapsedMilliseconds;

        if (_lastReport > 0)
        {
            double elapsed = (currentTime - _lastReport) / 1000.0;
            double fps = _reportFrameCount / elapsed;
            double mbps = (_totalBytes * 8.0 / 1_000_000.0) / elapsed;
            int latency = _serverPeer?.Ping ?? 0;

            string stats = $"FPS: {fps:F1} | BW: {mbps:F2} Mbps | Ping: {latency}ms | Regions: {regionCount}";
            string timing = $"Timing(ms): Deserialize={deserializeMs:F2} | Decompress={decompressMs:F2} | " +
                           $"Blit={blitMs:F2} | Callback={callbackMs:F2} | Total={totalMs:F2}";

            OnStatusUpdate?.Invoke(stats);
            Console.WriteLine($"[ChromaNetClient] {stats}");
            Console.WriteLine($"[ChromaNetClient] {timing}");

            _totalBytes = 0;
            _reportFrameCount = 0;
        }

        _lastReport = currentTime;
    }

    /// <summary>
    /// Get current composite frame buffer
    /// </summary>
    public byte[]? GetCurrentFrame()
    {
        return _compositeBuffer;
    }

    // INetEventListener implementation
    public void OnPeerConnected(NetPeer peer)
    {
        Console.WriteLine($"[ChromaNetClient] Connected to server");
        OnStatusUpdate?.Invoke("Connected to server");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine($"[ChromaNetClient] Disconnected: {disconnectInfo.Reason}");
        OnStatusUpdate?.Invoke($"Disconnected: {disconnectInfo.Reason}");
    }

    public void OnNetworkError(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Console.WriteLine($"[ChromaNetClient] Network error: {socketError}");
        OnStatusUpdate?.Invoke($"Network error: {socketError}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // Received frame data - OPTIMIZED: Removed Console.WriteLine for performance
        byte[] data = reader.GetRemainingBytes();
        ProcessFramePacket(data);
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Client doesn't accept connections
    }

    public void Dispose()
    {
        if (_disposed) return;

        Disconnect();
        _netManager?.Stop();

        _disposed = true;
    }
}
