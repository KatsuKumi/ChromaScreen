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

        _duplicator = new DesktopDuplicator(monitorIndex);

        if (!_isENetInitialized)
        {
            Library.Initialize();
            _isENetInitialized = true;
            Console.WriteLine("[ChromaNetServer] ENet library initialized");
        }

        Address address = new Address();
        address.Port = (ushort)port;
        _server = new Host();
        _server.Create(address, 32, 1);

        OnStatusUpdate?.Invoke($"Server started on port {port}");
        Console.WriteLine($"[ChromaNetServer] ENet server listening on port {port}");
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
                while (_server.Service(0, out Event netEvent) > 0)
                {
                    HandleNetworkEvent(netEvent);
                }

                if (_connectedPeers.Count == 0)
                {
                    Thread.Sleep(16);
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

    private void HandleNetworkEvent(Event netEvent)
    {
        switch (netEvent.Type)
        {
            case EventType.Connect:
                Console.WriteLine($"[ChromaNetServer] Client connected: {netEvent.Peer.IP}:{netEvent.Peer.Port} (ID: {netEvent.Peer.ID})");
                _connectedPeers.Add(netEvent.Peer);
                OnStatusUpdate?.Invoke($"Client connected: {netEvent.Peer.IP}");

                SendFullScreenToClient(netEvent.Peer);
                break;

            case EventType.Disconnect:
                Console.WriteLine($"[ChromaNetServer] Client disconnected: {netEvent.Peer.IP}");
                _connectedPeers.Remove(netEvent.Peer);
                OnStatusUpdate?.Invoke($"Client disconnected: {netEvent.Peer.IP}");
                break;

            case EventType.Timeout:
                Console.WriteLine($"[ChromaNetServer] Client timeout: {netEvent.Peer.IP}");
                _connectedPeers.Remove(netEvent.Peer);
                OnStatusUpdate?.Invoke($"Client timeout: {netEvent.Peer.IP}");
                break;

            case EventType.Receive:
                netEvent.Packet.Dispose();
                break;

            case EventType.None:
                break;
        }
    }

    private void SendFullScreenToClient(Peer peer)
    {
        try
        {
            var capture = _duplicator.CaptureFrame(0);
            if (capture == null || !capture.Success)
            {
                Console.WriteLine("[ChromaNetServer] Warning: Could not capture full screen for new client");
                return;
            }

            var fullScreenRegion = new Rectangle(0, 0, _duplicator.Width, _duplicator.Height);
            var regions = new List<Rectangle> { fullScreenRegion };

            var compressedRegions = CompressRegions(regions, capture.FrameData!);

            var framePacket = new FramePacket(
                _frameId,
                (ushort)_duplicator.Width,
                (ushort)_duplicator.Height,
                _chromaMode,
                _chromaThreshold);

            foreach (var region in compressedRegions)
            {
                framePacket.Regions.Add(region);
            }

            byte[] data = MemoryPackSerializer.Serialize(framePacket);

            Packet enetPacket = default;
            enetPacket.Create(data, PacketFlags.Reliable);

            peer.Send(0, ref enetPacket);

            Console.WriteLine($"[ChromaNetServer] Sent full screen to new client ({data.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChromaNetServer] Error sending full screen: {ex.Message}");
        }
    }

    private List<Rectangle> DetectMajorChangeAndAdjustRegions(List<Rectangle> dirtyRegions)
    {
        if (dirtyRegions.Count == 0)
            return dirtyRegions;

        long totalDirtyPixels = 0;
        foreach (var rect in dirtyRegions)
        {
            totalDirtyPixels += (long)rect.Width * rect.Height;
        }

        long screenPixels = (long)_duplicator.Width * _duplicator.Height;
        double coveragePercent = (totalDirtyPixels * 100.0) / screenPixels;

        const double MAJOR_CHANGE_THRESHOLD = 75.0;

        if (coveragePercent >= MAJOR_CHANGE_THRESHOLD || dirtyRegions.Count > 100)
        {
            return new List<Rectangle> { new Rectangle(0, 0, _duplicator.Width, _duplicator.Height) };
        }

        return dirtyRegions;
    }

    /// <summary>
    /// Process captured frame and send to all clients
    /// </summary>
    private void ProcessAndSendFrame(DesktopDuplicator.CaptureResult capture)
    {
        uint frameId = _frameId++;

        var frameTimer = Stopwatch.StartNew();

        var regions = DetectMajorChangeAndAdjustRegions(capture.DirtyRegions);

        var splitStopwatch = Stopwatch.StartNew();
        var splitRegions = SplitLargeRegions(regions);
        splitStopwatch.Stop();

        var compressStopwatch = Stopwatch.StartNew();
        var compressedRegions = CompressRegions(splitRegions, capture.FrameData!);
        compressStopwatch.Stop();

        var sendStopwatch = Stopwatch.StartNew();
        SendRegionsBatched(compressedRegions, frameId);
        sendStopwatch.Stop();

        frameTimer.Stop();

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

        return compressedRegions;
    }

    private void SendRegionsBatched(List<DirtyRegion> compressedRegions, uint frameId)
    {
        var framePacket = new FramePacket(
            frameId,
            (ushort)_duplicator.Width,
            (ushort)_duplicator.Height,
            _chromaMode,
            _chromaThreshold);

        foreach (var region in compressedRegions)
        {
            framePacket.Regions.Add(region);
        }

        byte[] data = MemoryPackSerializer.Serialize(framePacket);

        Packet enetPacket = default;
        enetPacket.Create(data, PacketFlags.UnreliableFragmented);

        _unreliablePackets++;

        foreach (var peer in _connectedPeers)
        {
            peer.Send(0, ref enetPacket);
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
                          $"Dirty: {dirtyRegionCount} | Clients: {_connectedPeers.Count}";
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

    public void Dispose()
    {
        if (_disposed) return;

        Console.WriteLine($"[ChromaNetServer] Disconnecting {_connectedPeers.Count} clients...");
        foreach (var peer in _connectedPeers.ToList())
        {
            peer.Disconnect(0);
        }

        Stop();

        _server.Flush();
        _server.Dispose();

        if (_isENetInitialized)
        {
            Library.Deinitialize();
            _isENetInitialized = false;
            Console.WriteLine("[ChromaNetServer] ENet library deinitialized");
        }

        _duplicator?.Dispose();
        _disposed = true;
        Console.WriteLine("[ChromaNetServer] Server disposed");
    }
}
