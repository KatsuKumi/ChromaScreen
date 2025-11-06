using System.Diagnostics;
using System.Net;
using ChromaNet.Compression;
using ChromaNet.Core;
using ENet;
using MemoryPack;

namespace ChromaNet.Network;

/// <summary>
/// ChromaNet client - receives and decompresses frames from server
/// Migrated from LiteNetLib to ENet-CSharp for unreliable fragmentation support
/// </summary>
public class ChromaNetClient : IDisposable
{
    private Host _client;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private Peer _serverPeer;
    private bool _isConnected;
    private byte[]? _compositeBuffer;
    private int _screenWidth;
    private int _screenHeight;
    private bool _running;
    private bool _disposed;
    private bool _isENetInitialized;

    // Performance tracking
    private readonly Stopwatch _perfTimer = Stopwatch.StartNew();
    private long _frameCount = 0;
    private long _reportFrameCount = 0;
    private long _totalBytes = 0;
    private long _lastReport = 0;

    public event Action<string>? OnStatusUpdate;
    public event Action<byte[], int, int, ChromaMode, byte>? OnFrameReceived;
    public event Action? OnConnectionLost;

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

        if (!_isENetInitialized)
        {
            Library.Initialize();
            _isENetInitialized = true;
            Console.WriteLine("[ChromaNetClient] ENet library initialized");
        }

        _client = new Host();
        _client.Create();

        Console.WriteLine($"[ChromaNetClient] Created client for {serverAddress}:{serverPort}");
    }

    /// <summary>
    /// Connect to server and start receiving
    /// </summary>
    public void Connect()
    {
        if (_running)
            return;

        _running = true;

        Address address = new Address();
        address.SetHost(_serverAddress);
        address.Port = (ushort)_serverPort;

        _serverPeer = _client.Connect(address, 1);

        OnStatusUpdate?.Invoke($"Connecting to {_serverAddress}:{_serverPort}...");
        Console.WriteLine($"[ChromaNetClient] Connecting to server...");

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
                while (_client.Service(0, out Event netEvent) > 0)
                {
                    HandleNetworkEvent(netEvent);
                }

                await Task.Delay(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChromaNetClient] Network error: {ex.Message}");
                OnStatusUpdate?.Invoke($"Error: {ex.Message}");
            }
        }
    }

    private void HandleNetworkEvent(Event netEvent)
    {
        switch (netEvent.Type)
        {
            case EventType.Connect:
                Console.WriteLine("[ChromaNetClient] Connected to server!");
                _isConnected = true;
                OnStatusUpdate?.Invoke("Connected to server");
                break;

            case EventType.Disconnect:
                Console.WriteLine("[ChromaNetClient] Disconnected from server");
                _isConnected = false;
                OnStatusUpdate?.Invoke("Disconnected from server");
                OnConnectionLost?.Invoke();
                break;

            case EventType.Timeout:
                Console.WriteLine("[ChromaNetClient] Connection timeout");
                _isConnected = false;
                OnStatusUpdate?.Invoke("Connection timeout");
                OnConnectionLost?.Invoke();
                break;

            case EventType.Receive:
                byte[] data = new byte[netEvent.Packet.Length];
                netEvent.Packet.CopyTo(data);
                netEvent.Packet.Dispose();

                ProcessFramePacket(data);
                break;

            case EventType.None:
                break;
        }
    }

    /// <summary>
    /// Disconnect from server
    /// </summary>
    public void Disconnect()
    {
        _running = false;
        if (_isConnected)
        {
            _serverPeer.Disconnect(0);
        }
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
            int latency = _isConnected ? (int)_serverPeer.RoundTripTime : 0;

            string stats = $"FPS: {fps:F1} | BW: {mbps:F2} Mbps | Ping: {latency}ms | Regions: {regionCount}";

            OnStatusUpdate?.Invoke(stats);
            Console.WriteLine($"[ChromaNetClient] {stats}");

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

    public void Dispose()
    {
        if (_disposed) return;

        Disconnect();

        _client.Flush();
        _client.Dispose();

        if (_isENetInitialized)
        {
            Library.Deinitialize();
            _isENetInitialized = false;
            Console.WriteLine("[ChromaNetClient] ENet library deinitialized");
        }

        _disposed = true;
    }
}
