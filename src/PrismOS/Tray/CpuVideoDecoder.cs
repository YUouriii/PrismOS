using System.Runtime.InteropServices;
using PrismOS.Common;
using Vortice.MediaFoundation;

namespace PrismOS.Tray;

/// <summary>CPU-decoded, looping video for the settings panel preview.
///
/// Decoded entirely through the software Media Foundation path: MF is started, the source
/// reader is told to bypass DXVA (SourceReaderAttributeKeys.DisableDxva), and frames arrive as
/// NV12 system memory which this class converts and downscales itself. No D3D device, no DirectComposition visuals, nothing associates the
/// process with the GPU — which is the only reliable way to keep the NVIDIA App overlay from
/// flagging the preview.
///
/// The decode runs on a background thread. Frames are copied into one reusable byte buffer, so a
/// looping preview allocates nothing per frame; the panel keeps a single bitmap and asks for the
/// pixels on paint. (The previous version handed out a fresh 854×480 bitmap per decoded frame —
/// ~1.6 MB, over the large-object threshold, thirty times a second — which is what made opening
/// the wallpaper page balloon the working set.) The UI thread never sees a Media Foundation call.
///
/// Scaling is deliberately *not* left to the reader's video processor: asking it for RGB32 at the
/// preview's size makes Media Foundation keep a full-resolution surface pool (and the processor's
/// own device) alive for as long as the reader lives — a couple of hundred megabytes on a 1080p
/// or 4K file. Taking the decoder's native NV12 and shrinking it here costs a few milliseconds a
/// frame and holds nothing bigger than one decoded frame.</summary>
public sealed class CpuVideoDecoder : IDisposable
{
    /// <summary>MFVideoFormat_NV12 (FourCC 'NV12'), the software decoders' native output.</summary>
    private static readonly Guid Nv12Subtype = new(0x3231564E, 0x0000, 0x0010,
        0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    private readonly string _path;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _paused;
    private readonly Lock _gate = new();

    /// <summary>The latest decoded frame, BGRX, rows tightly packed. Written by the decode thread
    /// and copied out under <see cref="_frameGate"/>; only that buffer is shared.</summary>
    private readonly Lock _frameGate = new();
    private byte[]? _pixels;
    private long _nextCopy;
    private int _width = -1, _height = -1;
    /// <summary>True when the reader hands out native-size NV12 instead of RGB32 at the preview
    /// size (the normal case; only a decoder that refuses NV12 falls back to the processor).</summary>
    private bool _nv12;
    private int _srcWidth, _srcHeight;
    /// <summary>Source column/row ranges per output pixel, computed once per size. A 3×3 box per
    /// pixel at 640×360 is what keeps the shrink from aliasing.</summary>
    private int[]? _cols, _rows;
    private bool _started;

    /// <summary>Preview frames per second. A 30 fps source is decoded in full but the pixels are
    /// copied at this rate: the panel is 444 px wide, so anything above this is invisible work.</summary>
    private const int PreviewFps = 24;

    public event Action? FrameReady;

    /// <summary>Set when the file cannot be opened or decoded at all — the UI shows the message
    /// instead of a black hole in the panel.</summary>
    public string? Error { get; private set; }

    public CpuVideoDecoder(string path)
    {
        _path = path;
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "CpuVideoPreview" };
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _thread.Start();
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>The decoded size, valid once the first frame lands.</summary>
    public int Width
    {
        get { lock (_frameGate) return _pixels is null ? 0 : _width; }
    }

    public int Height
    {
        get { lock (_frameGate) return _pixels is null ? 0 : _height; }
    }

    /// <summary>The file's own resolution, known as soon as the reader is open — the gallery
    /// library shows it before any frame has been decoded.</summary>
    public int SourceWidth => _srcWidth;

    public int SourceHeight => _srcHeight;

    /// <summary>Copies the latest frame into <paramref name="destination"/>, which must be the
    /// decoder's size and <c>Format32bppRgb</c>. False before the first frame (or after an error),
    /// in which case the caller draws its placeholder.</summary>
    public bool TryCopyFrame(Bitmap destination)
    {
        lock (_frameGate)
        {
            if (_pixels is null || destination.Width != _width || destination.Height != _height)
                return false;

            var rect = new Rectangle(0, 0, _width, _height);
            var bits = destination.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                unsafe
                {
                    fixed (byte* src = _pixels)
                    {
                        byte* dst = (byte*)bits.Scan0.ToPointer();
                        int rowBytes = _width * 4;
                        if (bits.Stride == rowBytes)
                        {
                            Buffer.MemoryCopy(src, dst, (long)rowBytes * _height, (long)rowBytes * _height);
                        }
                        else
                        {
                            for (int y = 0; y < _height; y++)
                                Buffer.MemoryCopy(src + (long)y * rowBytes, dst + (long)y * bits.Stride, rowBytes, rowBytes);
                        }
                    }
                }
            }
            finally
            {
                destination.UnlockBits(bits);
            }
            return true;
        }
    }

    private void DecodeLoop()
    {
        try
        {
            MediaFactory.MFStartup(useLightVersion: false);
            using var reader = OpenReader(_path);
            if (reader is null) return; // Error already set

            while (!_stop.IsCancellationRequested)
            {
                if (_paused) { Thread.Sleep(50); continue; }

                var sample = reader.ReadSample((int)SourceReaderIndex.FirstVideoStream,
                    SourceReaderControlFlag.None, out _, out var flags, out _);

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    // Loop forever: go back to the start and keep going.
                    reader.SetCurrentPosition(0);
                    continue;
                }
                if (sample is null) continue;

                long durationHns = sample.SampleDuration;
                using (sample)
                {
                    // Only copy at the preview rate: decoding continues at the file's rate so the
                    // loop timing stays right, but the panel never needs every frame.
                    long now = Environment.TickCount64;
                    if (now >= _nextCopy)
                    {
                        _nextCopy = now + 1000 / PreviewFps;
                        if (CopySample(sample)) FrameReady?.Invoke();
                    }
                }

                int waitMs = (int)(durationHns / 10_000); // 100-ns units → ms
                if (waitMs > 0) Thread.Sleep(Math.Min(waitMs, 250));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn($"CPU video preview failed for {_path}: {ex.Message}");
            FrameReady?.Invoke();
        }
    }

    /// <summary>Opens the file for software decode. First choice is the decoder's own NV12 at the
    /// file's size (see the class note); a file whose decoder refuses NV12 falls back to the
    /// video processor, which does the scale instead. Returns null with <see cref="Error"/> set
    /// for anything unsupported.</summary>
    private IMFSourceReader? OpenReader(string path)
    {
        if (TryOpen(path, scaled: false) is { } plain) return plain;
        if (Error is not null) return null;
        Log.Info($"CPU preview: {Path.GetFileName(path)} needs the video processor for scaling");
        return TryOpen(path, scaled: true);
    }

    private IMFSourceReader? TryOpen(string path, bool scaled)
    {
        using var attributes = MediaFactory.MFCreateAttributes(4);
        attributes.Set(SourceReaderAttributeKeys.DisableDxva, true);
        // Only the advanced processor scales; it is also the expensive one, so it is the fallback.
        if (scaled) attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);

        var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        try
        {
            using var native = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0);
            ulong packedSize = native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            uint srcWidth = (uint)(packedSize >> 32);
            uint srcHeight = (uint)(packedSize & 0xFFFFFFFF);
            if (srcWidth < 2 || srcHeight < 2) throw new InvalidOperationException("unknown frame size");

            // 640×360 at most: the panel is 444 px wide at 100 % DPI, so anything larger is wasted.
            double scale = Math.Min(1.0, Math.Min(640.0 / srcWidth, 360.0 / srcHeight));
            _width = Math.Max(2, (int)Math.Round(srcWidth * scale) & ~1);
            _height = Math.Max(2, (int)Math.Round(srcHeight * scale) & ~1);
            _srcWidth = (int)srcWidth;
            _srcHeight = (int)srcHeight;

            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            if (scaled)
            {
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                outType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)_width, (uint)_height));
                outType.Set(MediaTypeAttributeKeys.DefaultStride, (ulong)(_width * 4));
            }
            else
            {
                outType.Set(MediaTypeAttributeKeys.Subtype, Nv12Subtype);
                outType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize(srcWidth, srcHeight));
                outType.Set(MediaTypeAttributeKeys.DefaultStride, srcWidth);
            }
            outType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(30, 1));
            reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, outType);

            if (!scaled) BuildScaleTables();
            _nv12 = !scaled;
            // Don't assume stream 1 is audio — a 4K file can lead with a data or subtitle track and
            // have no audio at all, and unselecting a non-existent stream fails with
            // MF_E_INVALIDSTREAMNUMBER. Walk every stream instead and disable anything not video.
            DisableNonVideoStreams(reader);
            return reader;
        }
        catch (Exception ex)
        {
            reader.Dispose();
            if (scaled)
            {
                Error = ex.Message;
                Log.Warn($"CPU video preview failed for {path}: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>Per output column/row, the half-open source range it averages. 1920×1080 → 640×360
    /// is an exact 3×3 box; other sizes round, which is fine at thumbnail scale.</summary>
    private void BuildScaleTables()
    {
        _cols = BuildRanges(_width, _srcWidth);
        _rows = BuildRanges(_height, _srcHeight);
    }

    private static int[] BuildRanges(int output, int source)
    {
        // Two ints per pixel: start and end.
        var ranges = new int[output * 2];
        for (int i = 0; i < output; i++)
        {
            ranges[i * 2] = (int)((long)i * source / output);
            ranges[i * 2 + 1] = Math.Max(ranges[i * 2] + 1, (int)((long)(i + 1) * source / output));
        }
        return ranges;
    }

    /// <summary>Walks the stream list and unselects everything that is not video, so files with a
    /// leading data track, multiple audio tracks or no audio at all keep only the video.</summary>
    private static void DisableNonVideoStreams(IMFSourceReader reader)
    {
        for (int i = 0; i < 16; i++)
        {
            IMFMediaType? type = null;
            try
            {
                type = reader.GetNativeMediaType(i, 0);
            }
            catch
            {
                break;   // MF_E_INVALIDSTREAMNUMBER: no more streams
            }
            using (type)
            {
                var major = type.MajorType;
                if (major != MediaTypeGuids.Video) reader.SetStreamSelection(i, false);
            }
        }
    }

    /// <summary>Copies the latest sample into the shared BGRX buffer at the preview size.</summary>
    private bool CopySample(IMFSample sample)
    {
        if (_width <= 0 || _height <= 0) return false;

        using var buffer = sample.GetBufferByIndex(0);
        if (buffer is null) return false;

        // A decoder's frame is a 2D surface and only Lock2D knows its real row pitch; assuming
        // tight rows shears the picture, which is exactly what the first version did. The surface
        // is also padded vertically (a 1080-line frame lives in 1088 rows), so the chroma plane
        // is not where the frame height says it is — Lock2DSize tells us the true size.
        IMF2DBuffer2? surface2 = null;
        try { surface2 = buffer.QueryInterfaceOrNull<IMF2DBuffer2>(); }
        catch { /* not a 2D buffer: fall through to the contiguous path */ }

        if (surface2 is not null)
        {
            try
            {
                surface2.Lock2DSize(Buffer2DLockFlags.Read, out IntPtr scan0, out int pitch, out _, out int byteLength);
                try
                {
                    if (pitch <= 0) return false;
                    int planeHeight = _srcHeight;
                    if (_nv12 && byteLength > 0)
                    {
                        // Y + interleaved UV: total = pitch × height × 1.5.
                        int derived = (int)((long)byteLength * 2 / (3L * pitch));
                        if (derived >= _srcHeight) planeHeight = derived;
                    }
                    return _nv12 ? CopyNv12(scan0, pitch, planeHeight) : CopyRgb32(scan0, pitch);
                }
                finally { surface2.Unlock2D(); }
            }
            catch (Exception ex)
            {
                Log.Warn($"CPU video surface could not be locked: {ex.Message}");
                return false;
            }
            finally { surface2.Dispose(); }
        }

        IMF2DBuffer? surface = null;
        try { surface = buffer.QueryInterfaceOrNull<IMF2DBuffer>(); }
        catch { }
        if (surface is not null)
        {
            try
            {
                surface.Lock2D(out IntPtr scan0, out int pitch);
                try
                {
                    if (pitch <= 0) return false;
                    return _nv12 ? CopyNv12(scan0, pitch, _srcHeight) : CopyRgb32(scan0, pitch);
                }
                finally { surface.Unlock2D(); }
            }
            catch (Exception ex)
            {
                Log.Warn($"CPU video surface could not be locked: {ex.Message}");
                return false;
            }
            finally { surface.Dispose(); }
        }

        IntPtr data = IntPtr.Zero;
        try
        {
            buffer.Lock(out data, out _, out _);
        }
        catch (Exception ex)
        {
            Log.Warn($"CPU video sample could not be locked: {ex.Message}");
            return false;
        }

        try
        {
            return _nv12 ? CopyNv12(data, _srcWidth, _srcHeight) : CopyRgb32(data, _width * 4);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>RGB32 at the preview size (the video-processor fallback): a straight copy. RGB32 in
    /// memory is B,G,R,X — the layout GDI+ calls Format32bppRgb.</summary>
    private bool CopyRgb32(IntPtr data, int stride)
    {
        int width = _width, height = _height;
        int rowBytes = width * 4;
        if (stride < rowBytes) return false;

        lock (_frameGate)
        {
            _pixels ??= new byte[rowBytes * height];
            unsafe
            {
                fixed (byte* dstBase = _pixels)
                {
                    byte* src = (byte*)data.ToPointer();
                    for (int y = 0; y < height; y++)
                        Buffer.MemoryCopy(src + (long)y * stride, dstBase + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }
        }
        return true;
    }

    /// <summary>The normal path: native-size NV12, box-filtered down to the preview size. Luma and
    /// chroma are each averaged over the block they cover, so the shrink neither aliases nor
    /// shears the colour plane.</summary>
    private unsafe bool CopyNv12(IntPtr data, int stride, int planeHeight)
    {
        int sw = _srcWidth, sh = _srcHeight, ow = _width, oh = _height;
        if (_cols is null || _rows is null) return false;
        if (stride < sw || planeHeight < sh) return false;

        // NV12: a full-size Y plane, then the interleaved U/V plane at half height. The chroma
        // plane begins a whole pitch-sized run of rows after the luma plane — of the *surface's*
        // height, which is padded up (1080 lines live in 1088 rows).
        byte* src = (byte*)data.ToPointer();
        byte* uv = src + (long)stride * planeHeight;
        int cw = sw >> 1, ch = sh >> 1;
        int[] cols = _cols, rows = _rows;

        lock (_frameGate)
        {
            int rowBytes = ow * 4;
            _pixels ??= new byte[rowBytes * oh];
            fixed (byte* dstBase = _pixels)
            {
                for (int y = 0; y < oh; y++)
                {
                    int y0 = rows[y * 2], y1 = rows[y * 2 + 1];
                    // The chroma rows covered by this block: one byte pair per 2×2 luma block.
                    int cy0 = y0 >> 1;
                    int cy1 = Math.Min(ch, Math.Max(cy0 + 1, (y1 + 1) >> 1));
                    byte* dst = dstBase + (long)y * rowBytes;
                    for (int x = 0; x < ow; x++)
                    {
                        int x0 = cols[x * 2], x1 = cols[x * 2 + 1];
                        // Mean of the block's luma.
                        int sum = 0;
                        for (int sy = y0; sy < y1; sy++)
                        {
                            byte* luma = src + (long)sy * stride;
                            for (int sx = x0; sx < x1; sx++) sum += luma[sx];
                        }
                        int yValue = sum / ((x1 - x0) * (y1 - y0));

                        // Mean of the block's chroma. A pixel column c lives in chroma column
                        // c / 2, and the byte pair for that column starts at 2 × the column —
                        // using the pixel column here stretched the colour plane and clamped it
                        // at the middle of the frame, which read as slightly wrong hues.
                        int cx0 = x0 >> 1;
                        int cx1 = Math.Min(cw, Math.Max(cx0 + 1, (x1 + 1) >> 1));
                        int uSum = 0, vSum = 0;
                        for (int cy = cy0; cy < cy1; cy++)
                        {
                            byte* chroma = uv + (long)cy * stride;
                            for (int cx = cx0; cx < cx1; cx++)
                            {
                                uSum += chroma[cx * 2];
                                vSum += chroma[cx * 2 + 1];
                            }
                        }
                        int samples = (cx1 - cx0) * (cy1 - cy0);
                        int u = uSum / samples - 128;
                        int v = vSum / samples - 128;

                        // BT.709 studio-swing YUV → RGB: the file is tagged BT.709 primaries and
                        // matrix with 16–235 levels, and HD content is 709 in practice anyway.
                        int c = yValue - 16;
                        if (c < 0) c = 0;
                        int r = (298 * c + 459 * v + 128) >> 8;
                        int g = (298 * c - 55 * u - 136 * v + 128) >> 8;
                        int b = (298 * c + 541 * u + 128) >> 8;
                        dst[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        dst[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        dst[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        dst[3] = 255;
                        dst += 4;
                    }
                }
            }
        }
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _thread.Join(1500); } catch { /* best effort */ }
        lock (_frameGate) _pixels = null;
        _stop.Dispose();
    }
}
