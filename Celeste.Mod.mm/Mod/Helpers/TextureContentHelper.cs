// Log when last resort gc allocations happen to not lock the threads for too long
#define TRACE_GC_ALLOCS
// Log the memory usage of the managed and unmanaged pools
#define POOL_USAGE_LOGGING

using Celeste.Mod.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#nullable enable

namespace Celeste.Mod.Helpers;

// TODO: Pooling
public abstract class TextureLoader : IDisposable {
    
    public abstract void AsyncLoad(CancellationToken token);

    public abstract Texture2D SyncLoad();

    public virtual void Dispose() {
    }

    public interface IPreLoader {
        // TODO Merge this two because being separate is useless
        public bool CouldPreload { get; }
        public Point GetPreloadedSize();

        public TextureLoader CreateLoader();
    }
}

public abstract class FNAStreamTextureLoader : TextureLoader {
    private readonly Stream? _stream;
    private int preW;
    private int preH;
    private readonly bool preMul;
    private IntPtr dataPtr;
    private long unmanagedClaimed;

    // Taking in a stream provider is not really necessary here, yet it helps encapsulation and there's no performance impact either
    protected FNAStreamTextureLoader(Func<Stream> streamProvider, bool preMultiplied, int width = -1, int height = -1) {
        _stream = streamProvider();
        preW = width;
        preH = height;
        preMul = preMultiplied;
        unmanagedClaimed = 0;
        dataPtr = IntPtr.Zero;
    }

    public override void AsyncLoad(CancellationToken token) {
        int w = preW;
        int h = preH;
        // This code will ultimately use stb_image to decode whatever is in stream
        // we cannot control its allocation, so estimate it based on the image size
        // and some arbitrary inflation coefficient
        const double inflationCoef = 1.2;
        if (w > 0 && h > 0) {
            unmanagedClaimed = (long) ((double) preW * preH * 4 * inflationCoef);
            // ClaimUnmanaged gets us the amount that we managed to claim
            unmanagedClaimed = TextureContentHelper.MemoryManager.ClaimUnmanaged(unmanagedClaimed
#if TRACE_GC_ALLOCS
            , $"Stream texture from path {
                _stream switch { // Not all streams will have paths, but the vast majority do, so this is good enough for tracing
                    FileStream fs => fs.Name,
                    SynchronizedZipEntryStream szes => szes.entry.FullName,
                    _ => "Unknown path"
                }
            }"
#endif
            );
        }
        // If we don't know the size beforehand VirtualTexture is in charge of not multithreading loads
        // Assume Texture.SetData supports Ptr since we are using FNA
        if (preMul)
            ContentExtensions.LoadTextureRaw(Celeste.Instance.GraphicsDevice, _stream, out w, out h, out dataPtr);
        else
            ContentExtensions.LoadTextureLazyPremultiply(Celeste.Instance.GraphicsDevice, _stream, out w, out h, out dataPtr);
        preW = w;
        preH = h;
        token.ThrowIfCancellationRequested();
    }
    
    public override Texture2D SyncLoad() {
        Texture2D tex = new(Celeste.Instance.GraphicsDevice, preW, preH);
        tex.SetData(dataPtr);
        return tex;
    }
    
    public override void Dispose() {
        ContentExtensions.UnloadTextureRaw(dataPtr);
        if (unmanagedClaimed != 0)
            TextureContentHelper.MemoryManager.ReturnUnmanaged(unmanagedClaimed);
        _stream?.Dispose();
    }
}

public sealed class PNGTextureLoader : FNAStreamTextureLoader {
    private PNGTextureLoader(Func<Stream> streamProvider) : base(streamProvider, false /* pngs are never premultiplied */) {
    }
    
    public class PNGPreLoader : IPreLoader {
        public bool CouldPreload => preW != -1 && preH != -1;
        private readonly Func<Stream> _streamProvider;
        private readonly int preW = -1;
        private readonly int preH = -1;

        // We use stream providers because we open the stream multiple times
        public PNGPreLoader(Func<Stream> streamProvider, string path, bool noPreload = false) {
            _streamProvider = streamProvider;
            if (!noPreload) {
                bool preload = PreloadSizeFromPNG(streamProvider(), path, out int width, out int height);
                if (preload) {
                    preW = width;
                    preH = height;
                }
            }
        }
        
        public Point GetPreloadedSize() {
            return new Point(preW, preH);
        }
        
        public TextureLoader CreateLoader() {
            return new PNGTextureLoader(_streamProvider);
        }
        
        private static bool PreloadSizeFromPNG(Stream stream, string path, out int width, out int height) {
            using BinaryReader reader = new(stream);
            width = 0;
            height = 0;
            ulong magic = reader.ReadUInt64();
            if (magic != 0x0A1A0A0D474E5089U) {
                Logger.Error("vtex", $"Failed preloading PNG: Expected magic to be 0x0A1A0A0D474E5089, got 0x{magic.ToString("X16")} - {path}");
                return false;
            }
            uint length = reader.ReadUInt32();
            if (length != 0x0D000000U) {
                Logger.Error("vtex", $"Failed preloading PNG: Expected first chunk length to be 0x0D000000, got 0x{length.ToString("X8")} - {path}");
                return false;
            }
            uint chunk = reader.ReadUInt32();
            if (chunk != 0x52444849U) {
                Logger.Error("vtex", $"Failed preloading PNG: Expected IHDR marker 0x52444849, got 0x{chunk.ToString("X8")} - {path}");
                return false;
            }
            width = SwapEndian(reader.ReadInt32());
            height = SwapEndian(reader.ReadInt32());
            return true;
        }
    
        private static int SwapEndian(int data) {
            return
                ((data & 0xFF) << 24) |
                (((data >> 8) & 0xFF) << 16) |
                (((data >> 16) & 0xFF) << 8) |
                ((data >> 24) & 0xFF);
        }
    }
}

public sealed class FallbackTextureLoader : FNAStreamTextureLoader {
    public FallbackTextureLoader(Func<Stream> streamProvider, bool preMul) : base(streamProvider, preMul, -1, -1) {
    }

    public class FallbackPreLoader(Func<Stream> streamProvider, bool preMul) : IPreLoader {
        public bool CouldPreload => false;

        public Point GetPreloadedSize() {
            throw new InvalidOperationException();
        }
        public TextureLoader CreateLoader() {
            return new FallbackTextureLoader(streamProvider, preMul);
        }
    }
}

public sealed class DataTextureLoader : TextureLoader {
    [ThreadStatic]
    private static byte[]? bytes;
    private const int bytesSize = 512 * 1024; // 524288
    private const int bytesCheckSize = 512 * 1024 - 32; // 524256
    private readonly Stream _stream;
    private int w;
    private int h;
    private bool hasAlpha;
    private Memory<byte> mem;
    private TextureContentHelper.SpanPoolPool<byte>.SegmentIdentifier? segment;

    private DataTextureLoader(Func<Stream> streamProvider) {
        _stream = streamProvider();
        w = 0;
        h = 0;
        mem = Memory<byte>.Empty;
        segment = null;
    }

    // TODO: Use the token
    public override void AsyncLoad(CancellationToken token) {
        // Vanilla has got a static readonly byte[] bytes of fixed length - currently 524288
        // Luckily we can read more chunks on demand.
        byte[] read = bytes ??= new byte[bytesSize];
        _ = _stream.Read(read, 0, bytesSize);

        // Read the width, height and alpha mode
        w = BitConverter.ToInt32(read, 0);
        h = BitConverter.ToInt32(read, 4);
        hasAlpha = read[8] == 1;
        int size = w * h * 4;
        bool hasSegment;
        TextureContentHelper.SpanPoolPool<byte>.SegmentIdentifier seg;
        {
            hasSegment = TextureContentHelper.MemoryManager.GetChunkOrGcAlloc(size, out seg, out byte[] gcArray
#if TRACE_GC_ALLOCS
                        , $"Path texture {_stream switch {
                            FileStream fs => fs.Name,
                            _ => "Unknown path"
                        }}"
#endif
            );
            mem = hasSegment ? seg.SegId.Memory : gcArray;
        }

        Span<byte> buffer = mem.Span;
        if (hasAlpha) {
            AsyncLoadInner<HasAlpha>(_stream, bytes, buffer);
        } else {
            AsyncLoadInner<NoAlpha>(_stream, bytes, buffer);
        }
        if (hasSegment)
            segment = seg;
    }
    
    public override Texture2D SyncLoad() {
        Texture2D tex = new(Celeste.Instance.GraphicsDevice, w, h);
        unsafe {
            fixed (byte* ptr = mem.Span)
                tex.SetData((IntPtr) ptr);
        }
        return tex;
    }
    
    public override void Dispose() {
        if (segment.HasValue)
            TextureContentHelper.MemoryManager.ReturnChunk(segment.Value);
        segment = null;
        _stream.Dispose();
    }
    
    // Abuse generics in order to get dead code elimination for optimal code on both cases
    // This method simply reads from the `read` array and decodes to the `buffer` span,
    // It also expects `read` to be prefilled with the first part of `stream` and it 
    // will keep reading from `stream` until all data is decoded.
    // Assumptions: read.Length >= bytesCheckSize, stream.Position == read.Length
    // TODO: This has too many range checks
    private static void AsyncLoadInner<T>(Stream stream, byte[] read, Span<byte> to) where T : AlphaMode {
        Span<byte> from = read;
        Span<int> toI = MemoryMarshal.Cast<byte, int>(to);
        int size = to.Length;
        int toIdxB = 0;
        int toIdxI = 0;
        int readIdx = 9; // the first 9 bytes describe width, height and alpha mode (4+4+1), those have been read already
        while (toIdxB < size && toIdxI < toI.Length) { // The second check is unnecessary, it tries to help the jit remove bounds checks
            // Pixel values are run length encoded, this counts the number of pixels in this line
            uint lineSize = from[readIdx];

            bool zeroSplat = false;
            if (typeof(T) == typeof(HasAlpha)) {
                // If there is a nonzero alpha, all 4 bytes are stored, if alpha is zero, a single byte is
                byte a = from[readIdx + 1];
                if (a > 0) {
                    to[toIdxB] = from[readIdx + 4];
                    to[toIdxB + 1] = from[readIdx + 3];
                    to[toIdxB + 2] = from[readIdx + 2];
                    to[toIdxB + 3] = a;
                    readIdx += 1 + 4;
                } else {
                    toI[toIdxI] = 0;
                    readIdx += 1 + 1;
                    zeroSplat = true;
                }
            } else {
                to[toIdxB] = from[readIdx + 3];
                to[toIdxB + 1] = from[readIdx + 2];
                to[toIdxB + 2] = from[readIdx + 1];
                to[toIdxB + 3] = 255;
                readIdx += 4;
            }

            if (lineSize > 1) {
                if (typeof(T) == typeof(HasAlpha) && zeroSplat) {
                    // If alpha was zero, bulk write 0 to the whole line
                    Unsafe.InitBlockUnaligned(ref to[toIdxB + 4], 0, lineSize * 4 - 4);
                } else {
                    // Write via integers for performance
                    int splatValue = toI[toIdxI];
                    for (int jI = toIdxI + 1, end = toIdxI + (int)lineSize; jI < end; jI++)
                        toI[jI] = splatValue;
                }
            }

            // Advance
            toIdxI += (int)lineSize;
            toIdxB = toIdxI * 4;

            // If there is less than 32 bytes left, copy the remaining ones to the beginning and read from the stream again
            if (readIdx > bytesCheckSize) {
                int offset = read.Length - readIdx;
                for (int oB = 0; oB < offset; oB++) {
                    from[oB] = from[readIdx + oB];
                }
                _ = stream.Read(read, offset, read.Length - offset);
                readIdx = 0;
            }
        }
    }

    private interface AlphaMode;

    private struct HasAlpha : AlphaMode {
        // The structs need to be of different sizes in order to force the JIT to compile two different versions
#pragma warning disable CS0169 // Field is never used
        private int _;
#pragma warning restore CS0169 // Field is never used
    }

    private struct NoAlpha : AlphaMode;
    
    public class DataPreLoader : IPreLoader {
        private readonly Func<Stream> _streamProvider;
        private readonly int preW;
        private readonly int preH;
        public bool CouldPreload => true;
        
        // We use stream providers because we open the stream multiple times
        public DataPreLoader(Func<Stream> streamProvider) {
            _streamProvider = streamProvider;
            using Stream stream = _streamProvider();
            Span<byte> read = stackalloc byte[8];
            _ = stream.Read(read);
    
            // Read the width and height
            preW = BitConverter.ToInt32(read[0..]);
            preH = BitConverter.ToInt32(read[4..]);
        }
        
        public Point GetPreloadedSize() => new(preW, preH);
        public TextureLoader CreateLoader() {
            return new DataTextureLoader(_streamProvider);
        }
    }
}

public sealed class XnbTextureLoader : TextureLoader {
    private readonly string? _path;

    private XnbTextureLoader(string path) {
        _path = path;
    }
    
    public override void AsyncLoad(CancellationToken token) {
    }
    
    public override Texture2D SyncLoad() {
        return Engine.Instance.Content.Load<Texture2D>(_path!.Replace(".xnb", ""));
    }

    public class XnbPreLoader(string path) : IPreLoader {
        public bool CouldPreload => false; // Never accelerated

        public Point GetPreloadedSize() {
            throw new InvalidOperationException();
        }
        public TextureLoader CreateLoader() {
            return new XnbTextureLoader(path);
        }
    }
}

public sealed class SizeDefinedTextureLoader : TextureLoader {
    private readonly int _width;
    private readonly int _height;
    private readonly Color _color;
    private TextureContentHelper.SpanPoolPool<byte>.SegmentIdentifier? _segment;
    private Memory<byte> _data;
    private SizeDefinedTextureLoader(int width, int height, Color color) {
        _width = width;
        _height = height;
        _color = color;
        _segment = null;
        _data = Memory<byte>.Empty;
    }

    // TODO: Use the token
    public override void AsyncLoad(CancellationToken token) {
        // Layout order for Color is unknown, but since it's guaranteed to be consistent everywhere this will work
        bool hasSegment = TextureContentHelper.MemoryManager.GetChunkOrGcAlloc(_width * _height * Unsafe.SizeOf<Color>(),
            out TextureContentHelper.SpanPoolPool<byte>.SegmentIdentifier seg, out byte[] gcArray
    #if TRACE_GC_ALLOCS
                , $"Sized texture {_width}x{_height}"
    #endif
        );
        if (hasSegment)
            _segment = seg;
        _data = hasSegment ? seg.SegId.Memory : gcArray;
        Span<Color> colorData = MemoryMarshal.Cast<byte, Color>(_data.Span);
        colorData.Fill(_color);
        token.ThrowIfCancellationRequested();
    }
    
    public override Texture2D SyncLoad() {
        Texture2D tex = new(Engine.Instance.GraphicsDevice, _width, _height);
        unsafe {
            fixed (byte* ptr = _data.Span) {
                tex.SetData((IntPtr) ptr);
            }
        }
        return tex;
    }
    
    public override void Dispose() {
        if (_segment != null)
            TextureContentHelper.MemoryManager.ReturnChunk(_segment.Value);
        _segment = null;
    }

    public class SizeDefinedPreLoader(int width, int height, Color color) : IPreLoader {
        public bool CouldPreload => true;
        
        public Point GetPreloadedSize() {
            return new Point(width, height);
        }
        
        public TextureLoader CreateLoader() {
            return new SizeDefinedTextureLoader(width, height, color);
        }
    }
}

public static class TextureContentHelper {
    private const int atlasSize = 4096 * 4096 * 4;
    // The maximum texture size in bytes that is allowed, currently this 512 mb which is still unreasonably
    // high, not all loads check its own size because not all of them have a known size before it occurs
    private const int maxCheckedTextureSize = atlasSize * 8;
    internal static readonly FTLMemoryManager MemoryManager;
    
    static TextureContentHelper() {
        const int initialMemUsage = atlasSize * 4; // hardcoded for now, should be plenty to start and a good default
        MemoryManager = new FTLMemoryManager(initialMemUsage);
    }

    public static bool TryEnableFTL() {
        /* Vanilla calls GFX.Load and MTN.Load in LoadContent on non-Stadia platforms.
         * Sadly we can't load them in GameLoader.LoadThread as mods rely on them in LoadContent.
         *
         * Loading in a new thread with texture -> GPU ops on the main thread helps barely.
         * Spawning a new thread just to wait for it to end doesn't make much sense,
         * BUT delaying the slow texture load ops to happen lazy-async gets the game window to appear sooner.
         *
         * Note that on XNA, this dies both with and without threaded GL due to OOM exceptions.
         * -ade
         */
        if (patch_VirtualTexture.FtlToggle) return true;
        if (CoreModule.Settings.FastTextureLoading ?? Environment.ProcessorCount >= 4) {
            long limit = (long) (CoreModule.Settings.FastTextureLoadingMaxMB * 1024f * 1024f);

            if (limit <= 0) {
                limit = (long) (Everest.SystemMemoryMB * 0.2f * 1024f * 1024f);
                // Assume that even in the worst case with 4 GB system RAM, 512 MB (= 12.5% = 1/8) are still available for texture loads.
                if (limit <= (512L * 1024L * 1024L))
                    limit = (512L * 1024L * 1024L);
            }
            // ... and even if the user forcibly lowered it below 128 MB, fall back to 128 MB as even the vanilla gameplay atlas is 64MB.
            if (limit <= (128L * 1024L * 1024L))
                limit = (128L * 1024L * 1024L);

            Logger.Info("LoadContent", $"Enabling FTL with {limit} bytes");
            patch_VirtualTexture.FtlToggle = true;
            MemoryManager.SetAllocSize(limit);
            return true;
        }
        return false;
    }
    

    /// <summary>
    /// Helper class to manage memory allocations and limit those.
    /// This class is thread-safe.
    /// </summary>
    /// <param name="initialMemUsage">Initial reserved memory usage.</param>
    internal class FTLMemoryManager(long initialMemUsage) {
        private const double SplitPercent = 1 / 4D;
        private readonly long initialMemUsage = initialMemUsage;
        private const int MainThreadTimeout = 500;
        private const int OtherThreadTimeout = -1;
        private const int PoolSize = atlasSize;
        // Managed
        private readonly ResourceWaiter waitingForSpace = new(MainThreadTimeout, OtherThreadTimeout);
        private readonly SpanPoolPool<byte> spanPool = new(PoolSize, (long)(initialMemUsage*SplitPercent));
        
        // Unmanaged
        private long unmanagedMemoryUsage;
        private readonly object unmanagedLock = new();
        private readonly ResourceWaiter unmanagedWaitingForSpace = new(MainThreadTimeout, OtherThreadTimeout);

        private long _currMemUsage = initialMemUsage;
        // Reads and writes to this are slightly racy, that's why it's marked as volatile, but adding sync to this would be too much effort given
        // that it's value ever rarely changes
        public long CurrMemUsage { get => Volatile.Read(ref _currMemUsage); private set => Volatile.Write(ref _currMemUsage, value); }
        
        public bool GetChunkOrGcAlloc(int chunkSize, out SpanPoolPool<byte>.SegmentIdentifier seg, out byte[] gcArray
#if TRACE_GC_ALLOCS
        , string source
#endif
        ) {
            if (chunkSize > PoolSize) { // It's not going to fit
                if (chunkSize > maxCheckedTextureSize) { // Just no
                    throw new InvalidOperationException($"Tried to obtain a chunk that is too large ({chunkSize})" 
#if TRACE_GC_ALLOCS
                                                        + $" for texture {source}:"
#endif
                    );
                }
                
                // Just gc alloc it
                Logger.Warn(nameof(TextureContentHelper), $"Chunk size was too big for the pool size ({chunkSize} > {PoolSize})!");
#if TRACE_GC_ALLOCS
                Logger.Warn(nameof(TextureContentHelper), $"For texture {source}:");
                Logger.Warn(nameof(TextureContentHelper), new StackTrace().ToString());
#endif
                gcArray =  new byte[chunkSize];
                seg = default;
                return false;
            }
            
            while (true) {
                bool hasSegment = spanPool.TryRent(chunkSize, out seg);
                if (hasSegment) {
                    gcArray = [];
                    return true;
                }

                if (!waitingForSpace.Wait()) // On timeout just exit and gc alloc
                    break;
            }
        
            bool isMainThread = MainThreadHelper.IsMainThread;
            Logger.Warn(nameof(TextureContentHelper), $"Allocating {chunkSize} bytes in the gc because " +
                                                                    $"{(isMainThread ? "the main-thread" : "a worker thread")} was " +
                                                                    $"blocked for more than {(isMainThread ? MainThreadTimeout : OtherThreadTimeout)}ms");
#if TRACE_GC_ALLOCS
            Logger.Warn(nameof(TextureContentHelper), $"For texture {source}:");
            Logger.Warn(nameof(TextureContentHelper), new StackTrace().ToString());
#endif
            gcArray = new byte[chunkSize];
            seg = default;
            return false;
        }

        public void ReturnChunk(SpanPoolPool<byte>.SegmentIdentifier seg) {
            spanPool.Return(seg);
            waitingForSpace.Pulse();
        }
        
#if POOL_USAGE_LOGGING
        private DateTime lastLog;
#endif
        public long ClaimUnmanaged(long amount
#if TRACE_GC_ALLOCS
        , string source
#endif
        ) {
            if (amount > maxCheckedTextureSize) { // Just no
                throw new InvalidOperationException($"Tried to obtain a chunk that is too large ({amount})" 
#if TRACE_GC_ALLOCS
                                                    + $" for texture {source}:"
#endif
                );
            }
            if (amount > CurrMemUsage * (1 - SplitPercent)) {
                // Just roll with it, it is not going to fit
                Logger.Warn(nameof(TextureContentHelper), $"Chunk size was too big for the unmanaged budget ({amount} > {CurrMemUsage * (1 - SplitPercent)})!");
#if TRACE_GC_ALLOCS
                Logger.Warn(nameof(TextureContentHelper), $"For texture {source}:");
                Logger.Warn(nameof(TextureContentHelper), new StackTrace().ToString());
#endif
                return 0;
            }
            while (true) {
                lock (unmanagedLock) {
                    if (unmanagedMemoryUsage + amount <= CurrMemUsage * (1 - SplitPercent)) {
                        unmanagedMemoryUsage += amount;
#if POOL_USAGE_LOGGING
                        LogStatus("++");
#endif
                        return amount;
                    }
                }
                if (!unmanagedWaitingForSpace.Wait()) { // On timeout just allocate without budget
                    bool isMainThread = MainThreadHelper.IsMainThread;
                    Logger.Warn(nameof(TextureContentHelper), $"Allocating {amount} bytes over the unmanaged budget because" +
                                                              $"{(isMainThread ? "the main-thread" : "a worker thread")} was " +
                                                              $"blocked for more than {(isMainThread ? MainThreadTimeout : OtherThreadTimeout)}ms");
#if TRACE_GC_ALLOCS
                    Logger.Warn(nameof(TextureContentHelper), $"For texture {source}:");
                    Logger.Warn(nameof(TextureContentHelper), new StackTrace().ToString());
#endif
                    break;
                }
            }
            return 0;
        }

        public void ReturnUnmanaged(long amount) {
            lock (unmanagedLock) {
                unmanagedMemoryUsage -= amount;
#if POOL_USAGE_LOGGING
                LogStatus("--");
#endif
                Debug.Assert(unmanagedMemoryUsage >= 0);
            }
            unmanagedWaitingForSpace.Pulse();
        }

#if POOL_USAGE_LOGGING
        private void LogStatus(string postfix) {
            if (DateTime.Now - lastLog > TimeSpan.FromMilliseconds(100)) {
                long cap = (long)(CurrMemUsage * (1 - SplitPercent));
                Logger.Info(nameof(TextureContentHelper), $"UnmanagedMemory: {{used: {unmanagedMemoryUsage} ({(double)unmanagedMemoryUsage/cap:P}), cap: {cap}}} {postfix}");
                lastLog = DateTime.Now;
            }
        }
#endif

        public void SetAllocSize(long limit) {
            long prevMemUsage = CurrMemUsage;
            CurrMemUsage = limit == -1 ? initialMemUsage : limit;
            spanPool.CurrMemUsage = (long) (CurrMemUsage * SplitPercent);
            if (prevMemUsage < CurrMemUsage) {
                // Make everyone waiting recheck
                waitingForSpace.Pulse();
                unmanagedWaitingForSpace.Pulse();
            }
        }

        private class ResourceWaiter(int MainThreadTimeout, int OtherThreadTimeout) {
            private readonly ManualResetEventSlim mre = new();

            public bool Wait() {
                bool isMainThread = MainThreadHelper.IsMainThread;
                // TODO: this is sort of ugly, all threads should attempt to claim memory but we have no guarantee of that
                // What about our own impl of a better version?
                if (mre.Wait(isMainThread ? MainThreadTimeout : OtherThreadTimeout)) {
                    mre.Reset();
                    return true;
                }
                return false;
            }

            public void Pulse() {
                mre.Set();
            }
        }
    }

    /// <summary>
    /// Pool of SpanPools, used to dynamically create and destroy span pools to adjust to the currently allowed memory usage.
    /// This class is thread-safe
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class SpanPoolPool<T> where T : unmanaged {
        private readonly object @lock = new();
        private readonly int size;
        private long currMemUsage;
        private readonly Dictionary<int, SpanPool<T>> pools = [];
        private int poolId;
        private long releasePending;

        public long CurrMemUsage {
            // ReSharper disable once InconsistentlySynchronizedField
            get => Volatile.Read(ref currMemUsage);
            set {
                lock (@lock) {
                    currMemUsage = value;
                    CheckAlloc();
                }
            }
        }

        public SpanPoolPool(int poolSize, long initialMemUsage) {
            size = poolSize;
            currMemUsage = initialMemUsage;
            CheckAlloc();
        }

        // Must be called from a lock
        private void CheckAlloc() {
            releasePending = 0;
            long minPoolCount = currMemUsage / size + (currMemUsage % size != 0 ? 1 : 0);
            if (pools.Count < minPoolCount) {
                for (int i = pools.Count; i < minPoolCount; i++) {
                    pools.Add(poolId++, new SpanPool<T>(size));
                }
            } else if (pools.Count > minPoolCount) {
                int count = pools.Count;
                List<int> deallocating = [];
                foreach ((int id, SpanPool<T> pool) in pools) {
                    if (!pool.IsEmpty()) continue;
                    deallocating.Add(id);
                    count--;
                    if (count == minPoolCount) break;
                }
                foreach (int id in deallocating) {
                    pools[id].Dispose();
                    pools.Remove(id);
                }
                releasePending = count - minPoolCount;
            }
        }

        public bool TryRent(int chunkSize, out SegmentIdentifier segment) {
            lock (@lock) {
                for (int i = 0; i < 2; i++) {
                    // First try non-empty pools, then try empty ones
                    foreach ((int id, SpanPool<T> pool) in pools) {
                        if (pool.IsEmpty() == (i == 0)) continue;
                        bool hasSegment = pool.TryRent(chunkSize, out SpanPool<T>.SegmentIdentifier seg);
                        if (!hasSegment) continue;
#if POOL_USAGE_LOGGING
                        LogUsage("++");
#endif
                        segment = new SegmentIdentifier(seg, id);
                        return true;
                    }
                }
                
                // There's no space anywhere
                segment = default;
                return false;
            }
        }

        public void Return(SegmentIdentifier segment) {
            lock (@lock) {
                // Just return it to the correct pool
                if (!pools.TryGetValue(segment.PoolId, out SpanPool<T>? pool)) {
                    throw new ArgumentException($"Unknown pool with id {segment.PoolId}", nameof(segment));
                }
                pool.Return(segment.SegId);
                // But if we are looking to deallocate more, do so now
                if (pool.IsEmpty() && releasePending > 0) {
                    pool.Dispose();
                    pools.Remove(segment.PoolId);
                    releasePending--;
                }
                
#if POOL_USAGE_LOGGING
                LogUsage("--");
#endif
            }
        }
#if POOL_USAGE_LOGGING
        private DateTime lastLog;
        private void LogUsage(string postfix) {
            if (DateTime.Now - lastLog > TimeSpan.FromMilliseconds(100)) {
                Logger.Info(nameof(TextureContentHelper), $"ManagedMemory: {{cap: {currMemUsage}, releasePending: {releasePending}}} {postfix}");
                foreach ((int pId, SpanPool<T> pool) in pools) {
                    Logger.Info(nameof(TextureContentHelper), $"  {pId}: {pool.StatusString()}");
                }
                lastLog = DateTime.Now;
            }
        }
#endif

        public readonly struct SegmentIdentifier(SpanPool<T>.SegmentIdentifier segmentIdentifier, int poolId) {
            public readonly SpanPool<T>.SegmentIdentifier SegId = segmentIdentifier;
            public readonly int PoolId = poolId;
        }
    }

    /// <summary>
    /// This class holds large arrays from which then hands segments of it as <see cref="Memory{T}"/> (for later to be used as a <see cref="Span{T}"/>).
    /// This class is not thread safe.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class SpanPool<T> : IDisposable where T : unmanaged {
        private readonly int size;

        private readonly Memory<T> array;
        private readonly List<(int start, int end)> usedSegments = new();
        
        public SpanPool(int itemCount) {
            size = itemCount;
            T[] arrayHolder1 = new T[itemCount];
            array = arrayHolder1.AsMemory();
        }

        public bool TryRent(int chunkSize, out SegmentIdentifier seg) {
            (int start, int end)? freeSegment = NextFreeSegmentAndReserve(chunkSize);
            if (!freeSegment.HasValue) { // No space
                seg = default;
                return false;
            }
            
            // We have a spot
            Memory<T> memory = array[freeSegment.Value.start..freeSegment.Value.end];
            seg = new SegmentIdentifier(memory, freeSegment.Value.start, freeSegment.Value.end);
            return true;
        }

        public void Return(SegmentIdentifier seg) {
            // Find the matching segment and remove it
            for (int i = 0; i < usedSegments.Count; i++) {
                if (seg.Start == usedSegments[i].start) {
                    // Some extra verification to prevent corruption
                    if (seg.End != usedSegments[i].end) {
                        throw new ArgumentException("Invalid segment!");
                    }
                    usedSegments.RemoveAt(i);
                    break;
                }
            }
        }
        
        public bool IsEmpty() => usedSegments.Count == 0;
        
        public void Dispose() {
            if (!IsEmpty()) {
                throw new Exception("Attempted to deallocate with segments potentially in use!");
            }
        }

        // Should always be called in a lock
        private (int, int)? NextFreeSegmentAndReserve(int minSize) {
            int prevIdx = 0;
            for (int i = 0; i < usedSegments.Count; i++) {
                int currIdx = usedSegments[i].Item1;
                if (currIdx - prevIdx >= minSize) { // Found a spot
                    (int, int) newSegment = (prevIdx, prevIdx+minSize);
                    usedSegments.Insert(i, newSegment);
                    return newSegment;
                }
                prevIdx = usedSegments[i].Item2;
            }
            // No in-between segments, check remaining space
            if (size - prevIdx >= minSize) {
                (int, int) newSegment = (prevIdx, prevIdx + minSize);
                usedSegments.Add(newSegment);
                return newSegment;
            }
            // No space
            return null;
        }

#if POOL_USAGE_LOGGING
        internal string StatusString() {
            long used = usedSegments.Select(s => s.end - s.start).Sum();
            return $"{{Segments: {usedSegments.Count}, Used: {used} ({(double) used / size:P})}}";
        }
#endif

        public readonly struct SegmentIdentifier(Memory<T> memory, int start, int end) {
            public readonly Memory<T> Memory = memory;
            public readonly int Start = start;
            public readonly int End = end;
        }
    }
}
