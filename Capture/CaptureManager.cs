using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using VCDV.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace VCDV.Capture;

// Pixel buffer rented from ArrayPool — caller must Dispose() after use.
// Avoids per-frame LOH allocation (8+ MB at 1080p) that causes periodic GC stutter.
// Always contains BGRA8 pixels — conversion happens on the capture thread, not the UI thread.
public sealed class CaptureFrame : IDisposable
{
    private static readonly System.Buffers.ArrayPool<byte> Pool =
        System.Buffers.ArrayPool<byte>.Shared;

    public int    Width      { get; }
    public int    Height     { get; }
    public byte[] Pixels     { get; }
    public int    ByteCount  { get; }
    public long   ArrivedMs  { get; } = Environment.TickCount64;
    public long   ReadyTicks { get; set; } // Stopwatch ticks when conversion finished

    private bool _disposed;

    public static CaptureFrame RentBgra8(int width, int height)
    {
        int count = width * height * 4;
        return new CaptureFrame(width, height, Pool.Rent(count), count);
    }

    private CaptureFrame(int width, int height, byte[] pixels, int byteCount)
    {
        Width     = width;
        Height    = height;
        Pixels    = pixels;
        ByteCount = byteCount;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pool.Return(Pixels);
    }
}

public record CaptureStats(float Fps, int Width, int Height, string Subtype, string Mode);

public class CaptureManager : IDisposable
{
    public event Action<CaptureFrame>? FrameArrived;
    public event Action<CaptureStats>? StatsUpdated;
    public event Action<string>?       ErrorOccurred;

    private MediaCapture?     _mediaCapture;
    private MediaFrameReader? _frameReader;
    private CancellationTokenSource _cts = new();

    private int      _frameCount;
    private DateTime _lastStats = DateTime.UtcNow;
    private int      _captureWidth;
    private int      _captureHeight;
    private string   _captureSubtype = "—";
    private string   _captureMode    = "—";
    private bool     _useYuy2;
    private bool     _useNv12;

    public bool IsRunning { get; private set; }

    // ── Start ─────────────────────────────────────────────────────────────────

    public async Task StartAsync(string deviceName, int width, int height, int fps)
    {
        Stop();
        _cts = new CancellationTokenSource();

        Log.Information("starting capture: device='{Device}' {W}x{H} @ {Fps}fps",
            deviceName, width, height, fps);

        try
        {
            var group = await FindGroupAsync(deviceName);
            if (group is null)
            {
                var msg = $"video device '{deviceName}' not found";
                Log.Warning("{Msg}", msg);
                ErrorOccurred?.Invoke(msg);
                return;
            }

            var source = await OpenSourceAsync(group, width, height, fps);
            if (source is null)
            {
                var msg = $"no suitable format found on '{deviceName}'";
                Log.Warning("{Msg}", msg);
                ErrorOccurred?.Invoke(msg);
                return;
            }

            // Prefer YUY2 (device native 4:2:2) — requesting any other subtype causes
            // WinRT to transcode, e.g. YUY2→NV12 (4:2:2→4:2:0) loses chroma resolution.
            bool started = await TryStartReaderAsync(source, MediaEncodingSubtypes.Yuy2, useYuy2: true,  useNv12: false);
            if (!started)
                started = await TryStartReaderAsync(source, MediaEncodingSubtypes.Nv12, useYuy2: false, useNv12: true);
            if (!started)
                started = await TryStartReaderAsync(source, MediaEncodingSubtypes.Bgra8, useYuy2: false, useNv12: false);

            if (!started)
            {
                var msg = "frame reader failed to start on YUY2, NV12, or BGRA8";
                Log.Warning("{Msg}", msg);
                ErrorOccurred?.Invoke(msg);
                return;
            }

            IsRunning = true;
            Log.Information("capture started: {W}x{H} @ {Fps}fps  mode={Mode}",
                _captureWidth, _captureHeight, fps, _captureMode);
        }
        catch (Exception ex)
        {
            var msg = $"capture error: {ex.Message}";
            Log.Error("{Msg}", msg);
            ErrorOccurred?.Invoke(msg);
        }
    }

    private async Task<bool> TryStartReaderAsync(MediaFrameSource source, string subtype, bool useYuy2, bool useNv12)
    {
        try
        {
            var reader = await _mediaCapture!.CreateFrameReaderAsync(source, subtype);
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.Dispose();
                Log.Debug("frame reader {Sub} start status: {Status}", subtype, status);
                return false;
            }
            _frameReader               = reader;
            _frameReader.FrameArrived += OnFrameArrived;
            _useYuy2     = useYuy2;
            _useNv12     = useNv12;
            _captureMode = useYuy2 ? "YUY2→BGRA8(BT.709)" :
                           useNv12 ? "NV12→BGRA8(BT.709)" : "BGRA8";
            Log.Information("frame reader started: subtype={Sub} mode={Mode}", subtype, _captureMode);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("frame reader {Sub} failed: {Error}", subtype, ex.Message);
            return false;
        }
    }

    // ── Frame handler ─────────────────────────────────────────────────────────

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long t0 = Stopwatch.GetTimestamp();

        using var frameRef = sender.TryAcquireLatestFrame();
        if (frameRef?.VideoMediaFrame is not { } videoFrame) return;

        SoftwareBitmap? converted = null;
        CaptureFrame?   output    = null;
        try
        {
            var bitmap = videoFrame.SoftwareBitmap;
            if (bitmap is null) return;

            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;

            output = CaptureFrame.RentBgra8(w, h);

            if (_useYuy2 && bitmap.BitmapPixelFormat == BitmapPixelFormat.Yuy2)
            {
                // Convert on this thread-pool thread so the UI thread only ever receives
                // ready-to-blit BGRA8 pixels and never spends render-budget on conversion.
                ConvertYuy2ToBgra8(bitmap, output.Pixels, w, h);
            }
            else if (_useNv12 && bitmap.BitmapPixelFormat == BitmapPixelFormat.Nv12)
            {
                ConvertNv12ToBgra8(bitmap, output.Pixels, w, h);
            }
            else
            {
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                {
                    converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    bitmap    = converted;
                }
                CopyBitmapPixels(bitmap, output.Pixels);
            }

            long t1 = Stopwatch.GetTimestamp();
            output.ReadyTicks = t1;
            PerfStats.CaptureCallback.Record(t1 - t0);

            FrameArrived?.Invoke(output);
            output = null; // ownership transferred — don't dispose

            _frameCount++;
            var now = DateTime.UtcNow;
            if ((now - _lastStats).TotalSeconds >= 1.0)
            {
                var fps = _frameCount / (float)(now - _lastStats).TotalSeconds;
                StatsUpdated?.Invoke(new CaptureStats(fps, w, h, _captureSubtype, _captureMode));
                _frameCount = 0;
                _lastStats  = now;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("frame processing error: {Error}", ex.Message);
        }
        finally
        {
            output?.Dispose();
            converted?.Dispose();
        }
    }

    // ── Device / format selection ─────────────────────────────────────────────

    private async Task<MediaFrameSourceGroup?> FindGroupAsync(string deviceName)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups.FirstOrDefault(g =>
            string.Equals(g.DisplayName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<MediaFrameSource?> OpenSourceAsync(
        MediaFrameSourceGroup group, int width, int height, int fps)
    {
        var fallbacks = new[] { (width, height, fps), (width, height, fps / 2), (1920, 1080, 60), (1280, 720, 30) };

        foreach (var (w, h, f) in fallbacks.Distinct())
        {
            var source = await TryOpenAsync(group, w, h, f);
            if (source is not null)
            {
                if (w != width || h != height || f != fps)
                    Log.Warning("capture negotiated fallback: {W}x{H} {F}fps (requested {RW}x{RH} {RF}fps)",
                        w, h, f, width, height, fps);
                _captureWidth  = w;
                _captureHeight = h;
                return source;
            }
        }

        return null;
    }

    private async Task<MediaFrameSource?> TryOpenAsync(
        MediaFrameSourceGroup group, int width, int height, int fps)
    {
        _mediaCapture?.Dispose();
        _mediaCapture = new MediaCapture();

        var settings = new MediaCaptureInitializationSettings
        {
            SourceGroup          = group,
            SharingMode          = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference     = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        };

        try   { await _mediaCapture.InitializeAsync(settings); }
        catch (Exception ex)
        {
            Log.Debug("MediaCapture init failed for '{Group}': {Error}", group.DisplayName, ex.Message);
            return null;
        }

        var source = _mediaCapture.FrameSources.Values
            .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);

        if (source is null) return null;

        var format = source.SupportedFormats
            .Where(f =>
                f.VideoFormat.Width  == (uint)width  &&
                f.VideoFormat.Height == (uint)height &&
                Math.Abs(f.FrameRate.Numerator / (double)f.FrameRate.Denominator - fps) < 2)
            .OrderByDescending(f => f.FrameRate.Numerator / (double)f.FrameRate.Denominator)
            .FirstOrDefault();

        if (format is null) return null;

        try
        {
            await source.SetFormatAsync(format);
            _captureSubtype = format.Subtype ?? "—";
            Log.Information("capture format set: {W}x{H} @ {Fps}fps subtype={Sub}",
                width, height, fps, _captureSubtype);
            return source;
        }
        catch (Exception ex)
        {
            Log.Debug("SetFormatAsync failed: {Error}", ex.Message);
            return null;
        }
    }

    // ── Pixel conversion ──────────────────────────────────────────────────────

    // YUY2 (YUYV 4:2:2) → BGRA8 using BT.709 limited-range coefficients.
    // Runs on the WinRT thread-pool callback — keeps the UI thread free for rendering.
    // Packed layout: Y0 U Y1 V repeating (4 bytes per 2 horizontal pixels).
    // 4:2:2 preserves full horizontal chroma — no downsampling vs the device's native output.
    private static unsafe void ConvertYuy2ToBgra8(SoftwareBitmap bitmap, byte[] dst, int w, int h)
    {
        using var bmpBuffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var desc = bmpBuffer.GetPlaneDescription(0);

        using var reference = bmpBuffer.CreateReference();
        nint native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, ref iid, out nint byteAccess));
        try
        {
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)
                ((void***)byteAccess)[0][3];
            byte* src;
            uint  cap;
            Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &src, &cap));

            byte* srcPlane  = src + desc.StartIndex;
            int   srcStride = desc.Stride;

            fixed (byte* dstPtr = dst)
            {
                for (int row = 0; row < h; row++)
                {
                    byte* srcRow = srcPlane + row * srcStride;
                    byte* outRow = dstPtr   + row * (w * 4);

                    for (int col = 0; col < w; col += 2)
                    {
                        int Y0 = srcRow[col * 2];
                        int U  = srcRow[col * 2 + 1];
                        int Y1 = srcRow[col * 2 + 2];
                        int V  = srcRow[col * 2 + 3];

                        int cb = U - 128;
                        int cr = V - 128;

                        int y0 = 298 * (Y0 - 16) + 128;
                        int r0 = (y0 + 459 * cr) >> 8;
                        int g0 = (y0 -  55 * cb - 136 * cr) >> 8;
                        int b0 = (y0 + 541 * cb) >> 8;
                        outRow[col * 4]     = (byte)((uint)b0 > 255u ? (b0 >> 31 & 255) ^ 255 : b0);
                        outRow[col * 4 + 1] = (byte)((uint)g0 > 255u ? (g0 >> 31 & 255) ^ 255 : g0);
                        outRow[col * 4 + 2] = (byte)((uint)r0 > 255u ? (r0 >> 31 & 255) ^ 255 : r0);
                        outRow[col * 4 + 3] = 255;

                        int y1 = 298 * (Y1 - 16) + 128;
                        int r1 = (y1 + 459 * cr) >> 8;
                        int g1 = (y1 -  55 * cb - 136 * cr) >> 8;
                        int b1 = (y1 + 541 * cb) >> 8;
                        outRow[(col + 1) * 4]     = (byte)((uint)b1 > 255u ? (b1 >> 31 & 255) ^ 255 : b1);
                        outRow[(col + 1) * 4 + 1] = (byte)((uint)g1 > 255u ? (g1 >> 31 & 255) ^ 255 : g1);
                        outRow[(col + 1) * 4 + 2] = (byte)((uint)r1 > 255u ? (r1 >> 31 & 255) ^ 255 : r1);
                        outRow[(col + 1) * 4 + 3] = 255;
                    }
                }
            }
        }
        finally { Marshal.Release(byteAccess); }
    }

    // NV12 → BGRA8 using ITU-R BT.709 limited-range coefficients (integer arithmetic).
    // This matches TackleCast's GPU shader math without requiring a WinRT SoftwareBitmap
    // conversion, eliminating the main per-frame stall on the capture thread.
    //
    // NV12 layout: Y plane (W×H bytes, stride-padded) followed by interleaved UV plane
    // (W×(H/2) bytes, one UV pair per 2×2 luma block).
    //
    // BT.709 limited range:
    //   y  = 298 * (Y - 16) + 128
    //   R  = (y + 459 * Cr) >> 8          where Cr = V - 128
    //   G  = (y -  55 * Cb - 136 * Cr) >> 8   where Cb = U - 128
    //   B  = (y + 541 * Cb) >> 8
    private static unsafe void ConvertNv12ToBgra8(SoftwareBitmap bitmap, byte[] dst, int w, int h)
    {
        using var bmpBuffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);

        var yDesc  = bmpBuffer.GetPlaneDescription(0);
        var uvDesc = bmpBuffer.GetPlaneDescription(1);

        using var reference = bmpBuffer.CreateReference();
        nint native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        var iid = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, ref iid, out nint byteAccess));
        try
        {
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)
                ((void***)byteAccess)[0][3];
            byte* src;
            uint  cap;
            Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &src, &cap));

            byte* yPlane  = src + yDesc.StartIndex;
            byte* uvPlane = src + uvDesc.StartIndex;
            int   yStride = yDesc.Stride;
            int   uvStride = uvDesc.Stride;

            fixed (byte* dstPtr = dst)
            {
                for (int row = 0; row < h; row++)
                {
                    byte* yRow   = yPlane  + row       * yStride;
                    byte* uvRow  = uvPlane + (row >> 1) * uvStride;
                    byte* outRow = dstPtr  + row        * (w * 4);

                    for (int col = 0; col < w; col++)
                    {
                        int Y  = yRow[col];
                        int U  = uvRow[col & ~1];
                        int V  = uvRow[(col & ~1) + 1];

                        int y  = 298 * (Y - 16) + 128;
                        int cb = U - 128;
                        int cr = V - 128;

                        int r = (y + 459 * cr) >> 8;
                        int g = (y -  55 * cb - 136 * cr) >> 8;
                        int b = (y + 541 * cb) >> 8;

                        outRow[col * 4]     = (byte)((uint)b > 255u ? (b >> 31 & 255) ^ 255 : b);
                        outRow[col * 4 + 1] = (byte)((uint)g > 255u ? (g >> 31 & 255) ^ 255 : g);
                        outRow[col * 4 + 2] = (byte)((uint)r > 255u ? (r >> 31 & 255) ^ 255 : r);
                        outRow[col * 4 + 3] = 255;
                    }
                }
            }
        }
        finally { Marshal.Release(byteAccess); }
    }

    // vtable layout: [0] QueryInterface  [1] AddRef  [2] Release  [3] GetBuffer
    private static unsafe void CopyBitmapPixels(SoftwareBitmap bitmap, byte[] destination)
    {
        using var buffer    = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var iid     = new Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
        nint native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, ref iid, out nint byteAccess));
        try
        {
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)
                ((void***)byteAccess)[0][3];
            byte* src;
            uint  cap;
            Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &src, &cap));
            int len = Math.Min((int)cap, destination.Length);
            fixed (byte* dst = destination)
                Buffer.MemoryCopy(src, dst, len, len);
        }
        finally { Marshal.Release(byteAccess); }
    }

    // ── Stop / Dispose ────────────────────────────────────────────────────────

    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();

        if (_frameReader is not null)
        {
            _frameReader.FrameArrived -= OnFrameArrived;
            _frameReader.StopAsync().AsTask().Wait(500);
            _frameReader.Dispose();
            _frameReader = null;
        }

        _mediaCapture?.Dispose();
        _mediaCapture = null;
    }

    public void Dispose() => Stop();
}
