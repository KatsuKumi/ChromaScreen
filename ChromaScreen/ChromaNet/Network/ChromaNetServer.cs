using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using ChromaNet.Capture;
using ChromaNet.Compression;
using ChromaNet.Core;
using K4os.Compression.LZ4;
using ENet;
using MemoryPack;

namespace ChromaNet.Network;

/// <summary>
/// ChromaNet server - captures desktop and streams to clients
/// Uses Desktop Duplication API for ultra-fast capture with built-in dirty rectangles
/// Migrated from LiteNetLib to ENet-CSharp for unreliable fragmentation support
/// </summary>
public class ChromaNetServer : IDisposable
{
    private Host _server;
    private readonly List<Peer> _connectedPeers = new();
    private readonly DesktopDuplicator _duplicator;
    private readonly ChromaMode _chromaMode;
    private readonly byte _chromaThreshold;
    private uint _frameId = 0;
    private bool _running;
    private Thread? _captureThread;
    private bool _disposed;
    private bool _isENetInitialized;

    // Performance tracking
    private readonly Stopwatch _perfTimer = Stopwatch.StartNew();
    private long _frameCount = 0;
    private long _reportFrameCount = 0;
    private long _totalBytes = 0;
    private long _lastReport = 0;
    private long _updateCount = 0;
    private long _unreliablePackets = 0;
    private long _reliablePackets = 0;

    public event Action<string>? OnStatusUpdate;

    /// <summary>
    /// Create a ChromaNet server
    /// </summary>
    /// <param name="port">Port to listen on</param>
    /// <param name="chromaMode">Chroma key mode</param>
    /// <param name="chromaThreshold">Chroma threshold (0-255)</param>
    /// <param name="monitorIndex">Monitor index to capture (0 = primary monitor)</param>
    public ChromaNetServer(int port, ChromaMode chromaMode = ChromaMode.None, byte chromaThreshold = 80, int monitorIndex = 0)
    {
        _chromaMode = chromaMode;
        _chromaThreshold = chromaThreshold;

        // Create desktop duplicator for specified monitor
        _duplicator = new DesktopDuplicator(monitorIndex);

        // Create LiteNetLib server
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            UpdateTime = 15, // 15ms update interval for ~60 FPS
            UnconnectedMessagesEnabled = true
        };

        _netManager.Start(port);

        OptimizeSocketBuffers();

        OnStatusUpdate?.Invoke($"Server started on port {port}");
        Console.WriteLine($"[ChromaNetServer] Listening on port {port}");
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

                Console.WriteLine($"[ChromaNetServer] Socket buffers optimized: Send={socket.SendBufferSize}, Recv={socket.ReceiveBufferSize}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChromaNetServer] Warning: Could not optimize socket buffers: {ex.Message}");
        }
    }

    /// <summary>
    /// Start capturing and streaming
    /// </summary>
    public void Start()
    {
        if (_running)
            return;

        _running = true;

        // Start capture thread
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ChromaNetCapture",
            Priority = ThreadPriority.AboveNormal // Higher priority for smoother capture
        };
        _captureThread.Start();

        OnStatusUpdate?.Invoke("Capture started");
    }

    /// <summary>
    /// Capture and streaming loop
    /// </summary>
    private void CaptureLoop()
    {
        Console.WriteLine("[ChromaNetServer] Capture loop started");

        const double targetFps = 60.0;
        const double targetFrameTimeMs = 1000.0 / targetFps;
        var frameTimer = Stopwatch.StartNew();
        long lastFrameTime = 0;

        while (_running)
        {
            try
            {
                // Poll network events
                _netManager.PollEvents();

                // Only capture if we have connected clients
                if (_netManager.ConnectedPeersCount == 0)
                {
                    Thread.Sleep(16); // ~60 FPS
                    lastFrameTime = frameTimer.ElapsedMilliseconds;
                    continue;
                }

                // Calculate time since last frame
                long currentTime = frameTimer.ElapsedMilliseconds;
                double deltaTime = currentTime - lastFrameTime;

                // Maintain target frame rate
                if (deltaTime < targetFrameTimeMs)
                {
                    int sleepTime = (int)(targetFrameTimeMs - deltaTime);
                    if (sleepTime > 1)
                        Thread.Sleep(sleepTime - 1); // Sleep slightly less to account for timing precision
                    continue;
                }

                lastFrameTime = currentTime;

                // Capture frame with dirty rectangles (16ms timeout to block until changes occur)
                var capture = _duplicator.CaptureFrame(16);

                _updateCount++; // Count every capture attempt

                if (capture == null || !capture.Success)
                {
                    continue;
                }

                // Skip if no changes
                if (capture.NoChanges || capture.DirtyRegions.Count == 0)
                {
                    _frameId++;
                    continue;
                }

                // Build and send frame packet
                ProcessAndSendFrame(capture);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChromaNetServer] Capture error: {ex.Message}");
                OnStatusUpdate?.Invoke($"Error: {ex.Message}");
                Thread.Sleep(100);
            }
        }

        Console.WriteLine("[ChromaNetServer] Capture loop stopped");
    }

    /// <summary>
    /// Process captured frame and send to all clients
    /// </summary>
    private void ProcessAndSendFrame(DesktopDuplicator.CaptureResult capture)
    {
        uint frameId = _frameId++;

        var frameTimer = Stopwatch.StartNew();

        var splitStopwatch = Stopwatch.StartNew();
        var splitRegions = SplitLargeRegions(capture.DirtyRegions);
        splitStopwatch.Stop();

        var compressStopwatch = Stopwatch.StartNew();
        var compressedRegions = CompressRegions(splitRegions, capture.FrameData!);
        compressStopwatch.Stop();

        var sendStopwatch = Stopwatch.StartNew();
        SendRegionsBatched(compressedRegions, frameId);
        sendStopwatch.Stop();

        frameTimer.Stop();

        if (frameTimer.ElapsedMilliseconds > 50)
        {
            Console.WriteLine($"[ChromaNetServer] SLOW FRAME: Total={frameTimer.ElapsedMilliseconds}ms " +
                            $"(Split={splitStopwatch.ElapsedMilliseconds}ms, " +
                            $"Compress={compressStopwatch.ElapsedMilliseconds}ms, " +
                            $"Send={sendStopwatch.ElapsedMilliseconds}ms) " +
                            $"Regions={splitRegions.Count}");
        }

        long currentTime = _perfTimer.ElapsedMilliseconds;
        if (currentTime - _lastReport >= 1000)
        {
            ReportStats(splitRegions.Count);
        }
    }

    private List<Rectangle> SplitLargeRegions(List<Rectangle> dirtyRegions)
    {
        const int MAX_TILE_SIZE = 512;
        const int SPLIT_THRESHOLD_KB = 100;
        const float MAX_ASPECT_RATIO = 4.0f;
        const int MIN_DIMENSION_FOR_SPLIT = 256;

        var splitRegions = new List<Rectangle>();
        int splitCount = 0;
        int totalTilesAfter = 0;
        int shapeSplitCount = 0;

        foreach (var dirtyRect in dirtyRegions)
        {
            int regionSizeKB = (dirtyRect.Width * dirtyRect.Height * 4) / 1024;
            float aspectRatio = Math.Max(
                (float)dirtyRect.Width / dirtyRect.Height,
                (float)dirtyRect.Height / dirtyRect.Width
            );

            bool shouldSplitBySize = regionSizeKB > SPLIT_THRESHOLD_KB;
            bool shouldSplitByShape = aspectRatio > MAX_ASPECT_RATIO &&
                                       Math.Max(dirtyRect.Width, dirtyRect.Height) > MIN_DIMENSION_FOR_SPLIT;

            if (shouldSplitBySize || shouldSplitByShape)
            {
                if (shouldSplitByShape && !shouldSplitBySize)
                    shapeSplitCount++;

                splitCount++;
                int tilesCreated = 0;

                for (int y = 0; y < dirtyRect.Height; y += MAX_TILE_SIZE)
                {
                    for (int x = 0; x < dirtyRect.Width; x += MAX_TILE_SIZE)
                    {
                        int tileWidth = Math.Min(MAX_TILE_SIZE, dirtyRect.Width - x);
                        int tileHeight = Math.Min(MAX_TILE_SIZE, dirtyRect.Height - y);

                        splitRegions.Add(new Rectangle(
                            dirtyRect.X + x,
                            dirtyRect.Y + y,
                            tileWidth,
                            tileHeight));
                        tilesCreated++;
                    }
                }

                totalTilesAfter += tilesCreated;
            }
            else
            {
                splitRegions.Add(dirtyRect);
                totalTilesAfter++;
            }
        }

        if (shapeSplitCount > 0)
        {
            Console.WriteLine($"[ChromaNetServer] Split {shapeSplitCount} pathological regions " +
                             $"(aspect ratio >{MAX_ASPECT_RATIO}:1) into tiles");
        }

        return splitRegions;
    }

    private List<DirtyRegion> CompressRegions(List<Rectangle> regions, byte[] frameData)
    {
        const int COMPRESSION_THRESHOLD_KB = 64;
        var compressedRegions = new List<DirtyRegion>(regions.Count);
        var extractTimer = Stopwatch.StartNew();
        long totalExtractMs = 0;
        long totalCompressMs = 0;
        int compressedCount = 0;
        long originalBytes = 0;
        long compressedBytes = 0;

        foreach (var region in regions)
        {
            var regionTimer = Stopwatch.StartNew();
            byte[] regionPixels = ExtractRegion(frameData, region, _duplicator.Width);
            regionTimer.Stop();
            totalExtractMs += regionTimer.ElapsedMilliseconds;

            int regionSizeKB = regionPixels.Length / 1024;
            bool shouldCompress = regionSizeKB > COMPRESSION_THRESHOLD_KB;
            byte[] finalData;
            bool isCompressed;

            if (shouldCompress)
            {
                var compressTimer = Stopwatch.StartNew();
                int maxCompressedSize = LZ4Codec.MaximumOutputSize(regionPixels.Length);
                byte[] compressedData = new byte[maxCompressedSize];

                int compressedSize = LZ4Codec.Encode(
                    regionPixels, 0, regionPixels.Length,
                    compressedData, 0, maxCompressedSize,
                    LZ4Level.L00_FAST);

                compressTimer.Stop();
                totalCompressMs += compressTimer.ElapsedMilliseconds;

                if (compressedSize > 0 && compressedSize < regionPixels.Length)
                {
                    finalData = new byte[compressedSize];
                    Array.Copy(compressedData, finalData, compressedSize);
                    isCompressed = true;
                    compressedCount++;
                    originalBytes += regionPixels.Length;
                    compressedBytes += compressedSize;
                }
                else
                {
                    finalData = regionPixels;
                    isCompressed = false;
                }
            }
            else
            {
                finalData = regionPixels;
                isCompressed = false;
            }

            compressedRegions.Add(new DirtyRegion(
                (ushort)region.X, (ushort)region.Y,
                (ushort)region.Width, (ushort)region.Height,
                finalData, regionPixels.Length, isCompressed));
        }

        extractTimer.Stop();
        if (extractTimer.ElapsedMilliseconds > 20 && regions.Count > 0)
        {
            double avgPerRegion = (double)totalExtractMs / regions.Count;
            int firstRegionSize = (regions[0].Width * regions[0].Height * 4) / 1024;
            double compressionRatio = compressedCount > 0 ?
                (double)compressedBytes / originalBytes : 1.0;

            Console.WriteLine($"[ChromaNetServer] Frame Processing: " +
                            $"Extract={totalExtractMs}ms, Compress={totalCompressMs}ms, " +
                            $"Regions={regions.Count}, Compressed={compressedCount}/{regions.Count}, " +
                            $"Ratio={compressionRatio:P0}");
        }

        return compressedRegions;
    }

    private void SendRegionsBatched(List<DirtyRegion> compressedRegions, uint frameId)
    {
        var packet = new FramePacket(
            frameId,
            (ushort)_duplicator.Width,
            (ushort)_duplicator.Height,
            _chromaMode,
            _chromaThreshold);

        foreach (var region in compressedRegions)
        {
            packet.Regions.Add(region);
        }

        byte[] data = MemoryPackSerializer.Serialize(packet);
        DeliveryMethod method = DeliveryMethod.Unreliable;

        if (method == DeliveryMethod.Unreliable)
            _unreliablePackets++;
        else
            _reliablePackets++;

        foreach (var peer in _netManager.ConnectedPeerList)
        {
            peer.Send(data, method);
        }

        _frameCount++;
        _reportFrameCount++;
        _totalBytes += data.Length;
    }

    /// <summary>
    /// Extract a rectangular region from frame data
    /// </summary>
    private unsafe byte[] ExtractRegion(byte[] frameData, Rectangle rect, int frameWidth)
    {
        int regionSize = rect.Width * rect.Height * 4; // BGRA
        byte[] region = new byte[regionSize];

        int srcStride = frameWidth * 4;
        int dstStride = rect.Width * 4;

        fixed (byte* srcPtr = frameData)
        fixed (byte* dstPtr = region)
        {
            byte* src = srcPtr + ((rect.Y * frameWidth + rect.X) * 4);
            byte* dst = dstPtr;

            for (int y = 0; y < rect.Height; y++)
            {
                Unsafe.CopyBlockUnaligned(dst, src, (uint)dstStride);
                src += srcStride;
                dst += dstStride;
            }
        }

        return region;
    }

    /// <summary>
    /// Report performance statistics
    /// </summary>
    private void ReportStats(int dirtyRegionCount)
    {
        long currentTime = _perfTimer.ElapsedMilliseconds;

        if (_lastReport > 0)
        {
            double elapsed = (currentTime - _lastReport) / 1000.0;
            double updateRate = _updateCount / elapsed;
            double sendFps = _reportFrameCount / elapsed;
            double mbps = (_totalBytes * 8.0 / 1_000_000.0) / elapsed;
            double kbPerFrame = _reportFrameCount > 0 ? (_totalBytes / 1024.0) / _reportFrameCount : 0;

            long totalPackets = _unreliablePackets + _reliablePackets;
            double reliablePercent = totalPackets > 0 ? (_reliablePackets * 100.0 / totalPackets) : 0;

            string stats = $"Updates: {updateRate:F1}/s | Sent: {sendFps:F1} FPS | BW: {mbps:F2} Mbps ({kbPerFrame:F1} KB/frame) | " +
                          $"Dirty: {dirtyRegionCount} | Clients: {_netManager.ConnectedPeersCount}";
            string delivery = $"Delivery: Unreliable={_unreliablePackets}, Reliable={_reliablePackets} ({reliablePercent:F0}%)";

            OnStatusUpdate?.Invoke(stats);
            Console.WriteLine($"[ChromaNetServer] {stats}");
            Console.WriteLine($"[ChromaNetServer] {delivery}");

            _totalBytes = 0;
            _reportFrameCount = 0;
            _updateCount = 0;
            _unreliablePackets = 0;
            _reliablePackets = 0;
        }

        _lastReport = currentTime;
    }

    /// <summary>
    /// Stop capturing and streaming
    /// </summary>
    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
    }

    // INetEventListener implementation
    public void OnPeerConnected(NetPeer peer)
    {
        Console.WriteLine($"[ChromaNetServer] Client connected: {peer.Address}:{peer.Port}");
        OnStatusUpdate?.Invoke($"Client connected: {peer.Address}");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        Console.WriteLine($"[ChromaNetServer] Client disconnected: {peer.Address}");
        OnStatusUpdate?.Invoke($"Client disconnected: {peer.Address}");
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
    {
        Console.WriteLine($"[ChromaNetServer] Network error: {socketError}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // Server doesn't expect to receive data
    }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Accept all connection requests
        request.Accept();
        Console.WriteLine($"[ChromaNetServer] Accepting connection from {request.RemoteEndPoint}");
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Disconnect all clients gracefully before shutting down
        if (_netManager != null)
        {
            Console.WriteLine($"[ChromaNetServer] Disconnecting {_netManager.ConnectedPeersCount} clients...");
            foreach (var peer in _netManager.ConnectedPeerList.ToList())
            {
                peer.Disconnect();
            }
        }

        Stop();
        _netManager?.Stop();
        _duplicator?.Dispose();

        _disposed = true;
        Console.WriteLine("[ChromaNetServer] Server disposed");
    }
}
