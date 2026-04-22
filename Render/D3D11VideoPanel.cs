using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using VCDV.Diagnostics;
using D3D11Device    = SharpDX.Direct3D11.Device;
using D3D11Resource  = SharpDX.Direct3D11.Resource;
using MapFlags       = SharpDX.Direct3D11.MapFlags;
using Buffer         = System.Buffer;
using D2DFactory     = SharpDX.Direct2D1.Factory1;
using D2DDevice      = SharpDX.Direct2D1.Device;
using D2DContext     = SharpDX.Direct2D1.DeviceContext;
using D2DBitmap      = SharpDX.Direct2D1.Bitmap1;
using D2DBrush       = SharpDX.Direct2D1.SolidColorBrush;
using D2DPixelFormat = SharpDX.Direct2D1.PixelFormat;
using DWFactory      = SharpDX.DirectWrite.Factory;
using DWTextFormat   = SharpDX.DirectWrite.TextFormat;

namespace VCDV.Render;

// HwndHost-backed D3D11 renderer with a DXGI flip-model swap chain.
//
// Why this shape:
//   - Owning a native HWND + swap chain bypasses WPF's compositor entirely.
//   - Present(1, …) blocks to the monitor's VBlank at the actual refresh rate
//     (60 / 144 / 165 / 240 Hz), instead of WPF's internal ~60 Hz tick.
//   - A dedicated render thread drives Present in a loop so pacing is independent
//     of UI-thread stalls (sidebar animations, GC, layout, etc.).
//   - YUV→BGRA conversion still happens on the capture thread for now; the texture
//     upload path here is a simple Dynamic texture with Map(WriteDiscard).
//
// Known v1 limitation — "airspace":
//   WPF elements that overlap this HWND (cog, overlays, sidebar) will be visually
//   hidden by the HWND. MainWindow hides this panel while the sidebar is open.
public sealed class D3D11VideoPanel : HwndHost
{
    // ── Win32 interop ────────────────────────────────────────────────────────

    private const int  WS_CHILD         = 0x40000000;
    private const int  WS_VISIBLE       = 0x10000000;
    private const int  WS_CLIPCHILDREN  = 0x02000000;
    private const int  WS_CLIPSIBLINGS  = 0x04000000;
    private const int  WM_SIZE          = 0x0005;
    private const int  WM_MOUSEMOVE     = 0x0200;
    private const int  WM_LBUTTONDOWN   = 0x0201;
    private const int  WM_LBUTTONDBLCLK = 0x0203;
    private const int  WM_MOUSELEAVE    = 0x02A3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int    cbSize;
        public int    dwFlags;
        public IntPtr hwndTrack;
        public int    dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    private const int TME_LEAVE = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint   style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Keep the delegate rooted so its native function pointer stays valid.
    private static readonly WndProcDelegate s_wndProc  = (h, m, w, l) => DefWindowProcW(h, m, w, l);
    private static readonly string          s_className = "VCDV_D3D11Host_" + Guid.NewGuid().ToString("N");
    private static bool                     s_classRegistered;
    private static readonly object          s_classLock = new();

    // ── D3D11 state ──────────────────────────────────────────────────────────

    private IntPtr              _hwnd;
    private D3D11Device?        _device;
    private DeviceContext?      _context;
    private SwapChain1?         _swap;
    private RenderTargetView?   _rtv;

    private Texture2D?          _videoTex;
    private ShaderResourceView? _srv;
    private SamplerState?       _sampler;
    private VertexShader?       _vs;
    private PixelShader?        _ps;

    // D2D / DWrite overlay state
    private D2DFactory?   _d2dFactory;
    private D2DDevice?    _d2dDevice;
    private D2DContext?   _d2dContext;
    private D2DBitmap?    _d2dTarget;
    private DWFactory?    _dwFactory;
    private DWTextFormat? _tfFps;       // 13pt Consolas
    private DWTextFormat? _tfRender;    // 11pt Consolas
    private DWTextFormat? _tfCog;       // Segoe Fluent / MDL2 settings glyph
    private DWTextFormat? _tfNoSigTitle;
    private DWTextFormat? _tfNoSigBody;
    private D2DBrush?     _brushPanelBg;
    private D2DBrush?     _brushFpsRed;
    private D2DBrush?     _brushFpsGray;
    private D2DBrush?     _brushRenderInfo;
    private D2DBrush?     _brushCogBg;
    private D2DBrush?     _brushCogBgHover;
    private D2DBrush?     _brushCogFg;
    private D2DBrush?     _brushNoSigTitle;
    private D2DBrush?     _brushNoSigBody;

    // SVG cog (loaded from Assets\cog.svg, fallback to MDL2 glyph if null).
    private SharpDX.Direct2D1.SvgDocument? _cogSvg;

    private int _videoW, _videoH;
    private int _bbW = 1, _bbH = 1;
    private int _pendingResizeW, _pendingResizeH;

    // ── Overlay state (set from UI thread, read from render thread) ──────────
    private readonly object _overlayLock = new();
    private string _fpsText = "";
    private bool   _fpsIsAlert;
    private bool   _showFps;
    private string _renderInfoText = "";
    private bool   _showRenderInfo;
    private float  _cogOpacity;          // 0..1, animated from UI thread
    private bool   _cogHover;            // set from render thread based on WM_MOUSEMOVE
    private int    _mouseX = -1, _mouseY = -1;
    private bool   _mouseTracking;

    // No-signal message overlay (shown when no device selected / no frames yet)
    private bool   _noSignalShow;
    private string _noSignalTitle = "";
    private string _noSignalBody  = "";

    public void SetFpsOverlay(bool show, string text, bool alert)
    {
        lock (_overlayLock) { _showFps = show; _fpsText = text ?? ""; _fpsIsAlert = alert; }
    }

    public void SetRenderInfoOverlay(bool show, string text)
    {
        lock (_overlayLock) { _showRenderInfo = show; _renderInfoText = text ?? ""; }
    }

    public void SetCogOpacity(float opacity)
    {
        lock (_overlayLock) { _cogOpacity = Math.Clamp(opacity, 0f, 1f); }
    }

    // When show=true, the video is not drawn (so stale/garbage texture data
    // can't leak through) and a centered message replaces it.
    public void SetNoSignal(bool show, string title, string body)
    {
        lock (_overlayLock)
        {
            _noSignalShow = show;
            _noSignalTitle = title ?? "";
            _noSignalBody  = body  ?? "";
        }
    }

    // ── Events (raised from message pump / UI thread) ────────────────────────
    public event Action?      CogClicked;
    public event Action?      VideoDoubleClicked;
    public event Action?      PointerMoved;
    public event Action?      PointerLeft;

    // ── Frame pipeline ───────────────────────────────────────────────────────

    private readonly object _lock = new();
    private byte[]?         _pendingPixels;
    private int             _pendingW, _pendingH, _pendingBytes;
    private long            _pendingReadyTicks;
    private bool            _havePending;

    private Thread?         _renderThread;
    private volatile bool   _running;

    private int _renderFrameCount;
    public  int ConsumeRenderedFrameCount() => Interlocked.Exchange(ref _renderFrameCount, 0);

    // ── HwndHost lifecycle ───────────────────────────────────────────────────

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();

        var hInstance = Marshal.GetHINSTANCE(typeof(D3D11VideoPanel).Module);
        _hwnd = CreateWindowExW(
            0, s_className, null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Fatal("CreateWindowExW failed, err={Err}", err);
            throw new InvalidOperationException($"CreateWindowExW failed ({err})");
        }

        try
        {
            InitializeD3D();
            StartRenderLoop();
            Log.Information("D3D11 swap-chain host initialised (hwnd=0x{Hwnd:X})", _hwnd.ToInt64());
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "D3D11 initialisation failed");
            throw;
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        StopRenderLoop();
        DisposeD3D();
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private static void EnsureClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;

            var cls = new WNDCLASS
            {
                // CS_OWNDC | CS_DBLCLKS — CS_DBLCLKS is required for WM_LBUTTONDBLCLK
                // to be delivered; without it the double-click is silently dropped.
                style         = 0x0020 | 0x0008,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance     = Marshal.GetHINSTANCE(typeof(D3D11VideoPanel).Module),
                hbrBackground = IntPtr.Zero,
                lpszClassName = s_className,
            };

            if (RegisterClassW(ref cls) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                // 1410 = ERROR_CLASS_ALREADY_EXISTS — fine, we ignore
                if (err != 1410)
                    throw new InvalidOperationException($"RegisterClassW failed ({err})");
            }
            s_classRegistered = true;
        }
    }

    // ── D3D11 init ───────────────────────────────────────────────────────────

    private void InitializeD3D()
    {
        var flags = DeviceCreationFlags.BgraSupport;

        _device  = new D3D11Device(DriverType.Hardware, flags,
                        FeatureLevel.Level_11_0,
                        FeatureLevel.Level_10_1,
                        FeatureLevel.Level_10_0);
        _context = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device2>();
        using var adapter    = dxgiDevice.Adapter;
        using var factory    = adapter.GetParent<Factory2>();

        var desc = new SwapChainDescription1
        {
            Width             = 1,
            Height            = 1,
            Format            = Format.B8G8R8A8_UNorm,
            Stereo            = false,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = Usage.RenderTargetOutput,
            BufferCount       = 2,
            Scaling           = Scaling.Stretch,
            SwapEffect        = SwapEffect.FlipDiscard,
            AlphaMode         = AlphaMode.Ignore,
            Flags             = SwapChainFlags.None,
        };

        _swap = new SwapChain1(factory, _device, _hwnd, ref desc);
        // Disable DXGI's own alt-enter fullscreen; we manage window state ourselves.
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);

        CreateD2DResources();
        CreateBackBufferViews();
        CreateShadersAndSampler();
    }

    private void CreateD2DResources()
    {
        _d2dFactory = new D2DFactory(SharpDX.Direct2D1.FactoryType.SingleThreaded);
        using var dxgiDevice = _device!.QueryInterface<SharpDX.DXGI.Device>();
        _d2dDevice  = new D2DDevice(_d2dFactory, dxgiDevice);
        _d2dContext = new D2DContext(_d2dDevice, SharpDX.Direct2D1.DeviceContextOptions.None);

        _dwFactory = new DWFactory();
        _tfFps    = new DWTextFormat(_dwFactory, "Consolas",          20f);
        _tfRender = new DWTextFormat(_dwFactory, "Consolas",          16f);
        // Segoe Fluent Icons (Win11) / Segoe MDL2 Assets (Win10) — both have
        // glyph E713 "Settings", a cleaner cog than U+2699 from Segoe UI Symbol.
        _tfCog    = new DWTextFormat(_dwFactory, "Segoe Fluent Icons", 22f)
        {
            TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
            ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center,
        };
        _tfNoSigTitle = new DWTextFormat(_dwFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.SemiBold,
                                         SharpDX.DirectWrite.FontStyle.Normal, 32f)
        {
            TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
            ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Near,
        };
        _tfNoSigBody = new DWTextFormat(_dwFactory, "Segoe UI", 14f)
        {
            TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center,
            ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Near,
        };

        _brushPanelBg    = new D2DBrush(_d2dContext, new RawColor4(0f, 0f, 0f, 0.667f));
        _brushFpsRed     = new D2DBrush(_d2dContext, new RawColor4(0xFF / 255f, 0x6E / 255f, 0x6E / 255f, 1f)); // #FF6E6E
        _brushFpsGray    = new D2DBrush(_d2dContext, new RawColor4(0xAA / 255f, 0xAA / 255f, 0xAA / 255f, 1f)); // #AAAAAA
        _brushRenderInfo = new D2DBrush(_d2dContext, new RawColor4(0xAA / 255f, 0xAA / 255f, 0xAA / 255f, 1f)); // #AAAAAA
        _brushCogBg      = new D2DBrush(_d2dContext, new RawColor4(0f, 0f, 0f, 0.533f));
        _brushCogBgHover = new D2DBrush(_d2dContext, new RawColor4(0f, 0f, 0f, 0.8f));
        _brushCogFg      = new D2DBrush(_d2dContext, new RawColor4(1f, 1f, 1f, 1f));                             // #FFFFFF
        _brushNoSigTitle = new D2DBrush(_d2dContext, new RawColor4(1f, 1f, 1f, 1f));                             // #FFFFFF
        _brushNoSigBody  = new D2DBrush(_d2dContext, new RawColor4(0xAA / 255f, 0xAA / 255f, 0xAA / 255f, 1f));  // #AAAAAA

        TryLoadCogSvg();
    }

    [DllImport("shlwapi.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHCreateMemStream(IntPtr pInit, uint cbInit);

    private void TryLoadCogSvg()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "cog.svg");
            if (!System.IO.File.Exists(path))
            {
                Log.Warning("cog.svg not found at {Path}", path);
                return;
            }

            byte[] bytes = System.IO.File.ReadAllBytes(path);

            // SHCreateMemStream returns an IStream* on a copy of the bytes.
            IntPtr pStream;
            unsafe
            {
                fixed (byte* p = bytes)
                    pStream = SHCreateMemStream((IntPtr)p, (uint)bytes.Length);
            }
            if (pStream == IntPtr.Zero)
            {
                Log.Warning("SHCreateMemStream returned null");
                return;
            }

            using var comStream = new SharpDX.Win32.ComStream(pStream);
            // ID2D1DeviceContext5 is required for SVG. Present on Windows 10+.
            using var ctx5 = _d2dContext!.QueryInterface<SharpDX.Direct2D1.DeviceContext5>();
            _cogSvg = ctx5.CreateSvgDocument(comStream, new Size2F(79f, 79f));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "failed to load cog.svg — falling back to MDL2 glyph");
            _cogSvg = null;
        }
    }

    private void CreateBackBufferViews()
    {
        _rtv?.Dispose();
        _d2dTarget?.Dispose();
        _d2dTarget = null;

        using var bb = D3D11Resource.FromSwapChain<Texture2D>(_swap!, 0);
        _rtv = new RenderTargetView(_device, bb);
        _bbW = bb.Description.Width;
        _bbH = bb.Description.Height;

        // Wrap same back buffer as a D2D target so overlays composite in-place.
        if (_d2dContext is not null)
        {
            using var surf = _swap!.GetBackBuffer<Surface>(0);
            var bmpProps = new SharpDX.Direct2D1.BitmapProperties1(
                new D2DPixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                96f, 96f,
                SharpDX.Direct2D1.BitmapOptions.Target | SharpDX.Direct2D1.BitmapOptions.CannotDraw);
            _d2dTarget = new D2DBitmap(_d2dContext, surf, bmpProps);
        }
    }

    private const string ShaderSrc = @"
Texture2D    tex  : register(t0);
SamplerState samp : register(s0);

struct V2P { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

V2P VSMain(uint vid : SV_VertexID)
{
    // Fullscreen triangle — single draw call, no vertex buffer needed.
    float2 p = float2((vid << 1) & 2, vid & 2);
    V2P o;
    o.uv  = p;
    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 PSMain(V2P i) : SV_Target
{
    return tex.Sample(samp, i.uv);
}
";

    private void CreateShadersAndSampler()
    {
        using var vsBlob = ShaderBytecode.Compile(ShaderSrc, "VSMain", "vs_4_0", ShaderFlags.OptimizationLevel3);
        using var psBlob = ShaderBytecode.Compile(ShaderSrc, "PSMain", "ps_4_0", ShaderFlags.OptimizationLevel3);
        if (vsBlob.HasErrors || psBlob.HasErrors)
            throw new InvalidOperationException("shader compile error: "
                + vsBlob.Message + " / " + psBlob.Message);

        _vs = new VertexShader(_device, vsBlob);
        _ps = new PixelShader(_device, psBlob);

        _sampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter             = Filter.MinMagMipLinear,
            AddressU           = TextureAddressMode.Clamp,
            AddressV           = TextureAddressMode.Clamp,
            AddressW           = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MinimumLod         = 0,
            MaximumLod         = float.MaxValue,
        });
    }

    private void EnsureVideoTexture(int w, int h)
    {
        if (_videoTex is not null && _videoW == w && _videoH == h) return;

        _srv?.Dispose();
        _videoTex?.Dispose();

        _videoTex = new Texture2D(_device, new Texture2DDescription
        {
            Width             = w,
            Height            = h,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Dynamic,
            BindFlags         = BindFlags.ShaderResource,
            CpuAccessFlags    = CpuAccessFlags.Write,
        });
        _srv    = new ShaderResourceView(_device, _videoTex);
        _videoW = w;
        _videoH = h;
    }

    // ── Frame submission (called from capture thread) ────────────────────────

    // Copy frame bytes into an internal buffer so the capture thread's pooled
    // array is released immediately; the render thread consumes at its own cadence.
    public unsafe void SubmitFrame(byte* pixels, int w, int h, int byteCount, long readyTicks)
    {
        lock (_lock)
        {
            if (_pendingPixels is null || _pendingPixels.Length < byteCount)
                _pendingPixels = new byte[byteCount];
            fixed (byte* dst = _pendingPixels)
                Buffer.MemoryCopy(pixels, dst, byteCount, byteCount);
            _pendingW          = w;
            _pendingH          = h;
            _pendingBytes      = byteCount;
            _pendingReadyTicks = readyTicks;
            _havePending       = true;
        }
    }

    // ── Render thread ────────────────────────────────────────────────────────

    private void StartRenderLoop()
    {
        _running = true;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name         = "VCDV.D3D11Render",
        };
        _renderThread.Start();
    }

    private void StopRenderLoop()
    {
        _running = false;
        _renderThread?.Join(1000);
        _renderThread = null;
    }

    private void RenderLoop()
    {
        while (_running)
        {
            try
            {
                // ── 1. Pick up newest frame (if any) ─────────────────────────
                byte[]? pixels = null;
                int w = 0, h = 0, bytes = 0;
                long ready = 0;
                lock (_lock)
                {
                    if (_havePending)
                    {
                        pixels       = _pendingPixels;
                        w            = _pendingW;
                        h            = _pendingH;
                        bytes        = _pendingBytes;
                        ready        = _pendingReadyTicks;
                        _havePending = false;
                    }
                }

                // ── 2. Handle WM_SIZE ────────────────────────────────────────
                MaybeResizeBackBuffer();

                // ── 3. Upload new texture data ───────────────────────────────
                if (pixels is not null && _device is not null && _context is not null)
                {
                    EnsureVideoTexture(w, h);
                    long t0  = Stopwatch.GetTimestamp();
                    var  box = _context.MapSubresource(_videoTex, 0, MapMode.WriteDiscard, MapFlags.None);
                    unsafe
                    {
                        fixed (byte* src = pixels)
                        {
                            byte* dst = (byte*)box.DataPointer;
                            int rowBytes = w * 4;
                            if (box.RowPitch == rowBytes)
                            {
                                Buffer.MemoryCopy(src, dst, bytes, bytes);
                            }
                            else
                            {
                                for (int r = 0; r < h; r++)
                                    Buffer.MemoryCopy(src + r * rowBytes,
                                                      dst + r * box.RowPitch,
                                                      rowBytes, rowBytes);
                            }
                        }
                    }
                    _context.UnmapSubresource(_videoTex, 0);
                    PerfStats.PickupLatency.Record(t0 - ready);
                }

                // ── 4. Draw + Present (blocks to VBlank) ─────────────────────
                if (_rtv is not null && _context is not null && _swap is not null)
                {
                    // Clear the full back buffer first — this is the letterbox padding.
                    _context.OutputMerger.SetRenderTargets(_rtv);
                    _context.ClearRenderTargetView(_rtv, new RawColor4(0f, 0f, 0f, 1f)); // #000000

                    // Snapshot no-signal state so we can gate the video draw.
                    bool noSignal;
                    lock (_overlayLock) noSignal = _noSignalShow;

                    bool drawVideo = !noSignal && _srv is not null && _videoW > 0 && _videoH > 0;
                    if (drawVideo)
                    {

                    // Compute aspect-preserving viewport: pillar-box when window is
                    // wider than source, letter-box when taller. Fullscreen-triangle
                    // clip-space coords map to whatever viewport we set, so changing
                    // the viewport is sufficient — no geometry or shader work needed.
                    float targetAspect = _videoW > 0 && _videoH > 0
                        ? (float)_videoW / _videoH
                        : (float)_bbW   / Math.Max(1, _bbH);
                    float bbAspect = (float)_bbW / Math.Max(1, _bbH);

                    float vpX, vpY, vpW, vpH;
                    if (bbAspect > targetAspect)
                    {
                        // Window wider than source → pillar-box (bars on sides)
                        vpH = _bbH;
                        vpW = _bbH * targetAspect;
                        vpX = (_bbW - vpW) * 0.5f;
                        vpY = 0;
                    }
                    else
                    {
                        // Window taller than source → letter-box (bars top/bottom)
                        vpW = _bbW;
                        vpH = _bbW / targetAspect;
                        vpX = 0;
                        vpY = (_bbH - vpH) * 0.5f;
                    }

                    _context.Rasterizer.SetViewport(new RawViewportF
                    {
                        X = vpX, Y = vpY,
                        Width = vpW, Height = vpH,
                        MinDepth = 0, MaxDepth = 1,
                    });

                    _context.InputAssembler.InputLayout = null;
                    _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    _context.VertexShader.Set(_vs);
                    _context.PixelShader.Set(_ps);
                    _context.PixelShader.SetShaderResource(0, _srv);
                    _context.PixelShader.SetSampler(0, _sampler);
                    _context.Draw(3, 0);
                    }

                    // Overlays (text + cog + no-signal message) drawn via D2D.
                    DrawOverlays();

                    long p0 = Stopwatch.GetTimestamp();
                    _swap.Present(1, PresentFlags.None);
                    PerfStats.PresentLatency.Record(Stopwatch.GetTimestamp() - p0);

                    Interlocked.Increment(ref _renderFrameCount);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "render-loop iteration failed");
                Thread.Sleep(5);
            }
        }
    }

    // ── Overlay drawing (runs on render thread) ──────────────────────────────

    // Positioned just below the system title bar's close (X) button.
    private const float CogSize   = 40f;
    private const float CogMargin = 10f;   // gap from window right edge
    private const float CogTop    = 8f;    // gap from top of video area
    private const float OvMargin  = 14f;

    private RawRectangleF CogRect() =>
        new(_bbW - CogMargin - CogSize, CogTop,
            _bbW - CogMargin,            CogTop + CogSize);

    private void DrawOverlays()
    {
        if (_d2dContext is null || _d2dTarget is null) return;

        // Snapshot shared overlay state
        bool   showFps, showRender, fpsAlert, showNoSig;
        string fpsText, renderText, noSigTitle, noSigBody;
        float  cogOpacity;
        bool   cogHover;
        lock (_overlayLock)
        {
            showFps     = _showFps;
            fpsText     = _fpsText;
            fpsAlert    = _fpsIsAlert;
            showRender  = _showRenderInfo;
            renderText  = _renderInfoText;
            cogOpacity  = _cogOpacity;
            cogHover    = _cogHover;
            showNoSig   = _noSignalShow;
            noSigTitle  = _noSignalTitle;
            noSigBody   = _noSignalBody;
        }

        _d2dContext.Target = _d2dTarget;
        _d2dContext.BeginDraw();
        _d2dContext.Transform = new SharpDX.Mathematics.Interop.RawMatrix3x2(1, 0, 0, 1, 0, 0);
        try
        {
            float y = OvMargin;

            if (showFps && fpsText.Length > 0)
            {
                y = DrawTextPanel(fpsText, _tfFps!,
                    fpsAlert ? _brushFpsRed! : _brushFpsGray!,
                    OvMargin, y,
                    padX: 10f, padY: 5f, corner: 6f);
                y += 6f; // gap
            }

            if (showRender && renderText.Length > 0)
            {
                y = DrawTextPanel(renderText, _tfRender!,
                    _brushRenderInfo!,
                    OvMargin, y,
                    padX: 10f, padY: 7f, corner: 6f);
            }

            if (showNoSig)
                DrawNoSignal(noSigTitle, noSigBody);

            if (cogOpacity > 0.01f)
                DrawCog(cogOpacity, cogHover);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "overlay draw failed");
        }
        finally
        {
            // EndDraw may return HR; swallow recoverable errors.
            try { _d2dContext.EndDraw(); } catch { /* ignored */ }
        }
    }

    // Measures, draws the dark rounded panel, then the text inside.
    // Returns the y-coordinate just below the panel (for stacking).
    private float DrawTextPanel(string text, DWTextFormat format, D2DBrush textBrush,
                                 float x, float y, float padX, float padY, float corner)
    {
        // Measure via a transient TextLayout
        using var layout = new SharpDX.DirectWrite.TextLayout(_dwFactory, text, format, 800f, 400f);
        var metrics = layout.Metrics;
        float w = metrics.Width  + padX * 2;
        float h = metrics.Height + padY * 2;

        var rect = new SharpDX.Direct2D1.RoundedRectangle
        {
            Rect     = new RawRectangleF(x, y, x + w, y + h),
            RadiusX  = corner,
            RadiusY  = corner,
        };
        _d2dContext!.FillRoundedRectangle(rect, _brushPanelBg);

        _d2dContext.DrawTextLayout(new RawVector2(x + padX, y + padY), layout, textBrush);
        return y + h;
    }

    private void DrawNoSignal(string title, string body)
    {
        if (_d2dContext is null || _tfNoSigTitle is null || _tfNoSigBody is null
            || _brushNoSigTitle is null || _brushNoSigBody is null) return;

        // Vertical block ~80px high; center it around the back-buffer midpoint.
        const float titleH = 44f;
        const float gap    = 8f;
        const float bodyH  = 40f;
        float total        = titleH + gap + bodyH;
        float y0           = (_bbH - total) * 0.5f;

        var titleRect = new RawRectangleF(0, y0, _bbW, y0 + titleH);
        var bodyRect  = new RawRectangleF(0, y0 + titleH + gap, _bbW, y0 + titleH + gap + bodyH);

        if (!string.IsNullOrEmpty(title))
            _d2dContext.DrawText(title, _tfNoSigTitle, titleRect, _brushNoSigTitle);
        if (!string.IsNullOrEmpty(body))
            _d2dContext.DrawText(body, _tfNoSigBody, bodyRect, _brushNoSigBody);
    }

    private void DrawCog(float opacity, bool hover)
    {
        var rect = CogRect();
        var bgBrush = hover ? _brushCogBgHover! : _brushCogBg!;
        var prevBgOpacity = bgBrush.Opacity;
        var prevFgOpacity = _brushCogFg!.Opacity;
        bgBrush.Opacity        = prevBgOpacity * opacity;
        _brushCogFg.Opacity    = prevFgOpacity * opacity;

        var rr = new SharpDX.Direct2D1.RoundedRectangle
        {
            Rect    = rect,
            RadiusX = 8f,
            RadiusY = 8f,
        };
        _d2dContext!.FillRoundedRectangle(rr, bgBrush);

        if (_cogSvg is not null)
        {
            // Scale 79x79 SVG into the button with padding, center it.
            // ~20px inner size (was 28px) — about 30% smaller glyph.
            float iconSize = CogSize - 20f;
            float scale    = iconSize / 79f;
            float iconX    = rect.Left + (CogSize - iconSize) * 0.5f;
            float iconY    = rect.Top  + (CogSize - iconSize) * 0.5f;

            var prevXform = _d2dContext.Transform;
            _d2dContext.Transform = new RawMatrix3x2(scale, 0, 0, scale, iconX, iconY);

            // Use a layer for opacity since SVG has its own fills/strokes.
            var layerParams = new SharpDX.Direct2D1.LayerParameters1
            {
                ContentBounds     = new RawRectangleF(float.NegativeInfinity, float.NegativeInfinity,
                                                       float.PositiveInfinity, float.PositiveInfinity),
                MaskTransform     = new RawMatrix3x2(1, 0, 0, 1, 0, 0),
                Opacity           = opacity,
                MaskAntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive,
                LayerOptions      = SharpDX.Direct2D1.LayerOptions1.None,
            };
            _d2dContext.PushLayer(ref layerParams, null);
            try
            {
                using var ctx5 = _d2dContext.QueryInterface<SharpDX.Direct2D1.DeviceContext5>();
                ctx5.DrawSvgDocument(_cogSvg);
            }
            finally
            {
                _d2dContext.PopLayer();
                _d2dContext.Transform = prevXform;
            }
        }
        else
        {
            // E713 = "Settings" in Segoe Fluent Icons / Segoe MDL2 Assets.
            _d2dContext.DrawText("\uE713", _tfCog, rect, _brushCogFg);
        }

        bgBrush.Opacity     = prevBgOpacity;
        _brushCogFg.Opacity = prevFgOpacity;
    }

    private void MaybeResizeBackBuffer()
    {
        int rw = Interlocked.Exchange(ref _pendingResizeW, 0);
        int rh = Interlocked.Exchange(ref _pendingResizeH, 0);
        if (rw <= 0 || rh <= 0) return;
        if (rw == _bbW && rh == _bbH) return;
        if (_swap is null) return;

        _rtv?.Dispose();
        _rtv = null;
        // D2D target bitmap references the back buffer — must be released before resize.
        if (_d2dContext is not null) _d2dContext.Target = null;
        _d2dTarget?.Dispose();
        _d2dTarget = null;

        _swap.ResizeBuffers(2, rw, rh, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateBackBufferViews();
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SIZE:
            {
                long l = lParam.ToInt64();
                int w = (int)(l        & 0xFFFF);
                int h = (int)((l >> 16) & 0xFFFF);
                Interlocked.Exchange(ref _pendingResizeW, w);
                Interlocked.Exchange(ref _pendingResizeH, h);
                break;
            }
            case WM_MOUSEMOVE:
            {
                short x = (short)(lParam.ToInt64() & 0xFFFF);
                short y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                OnMouseMove(x, y);
                break;
            }
            case WM_MOUSELEAVE:
            {
                lock (_overlayLock) { _cogHover = false; }
                _mouseTracking = false;
                PointerLeft?.Invoke();
                break;
            }
            case WM_LBUTTONDOWN:
            {
                short x = (short)(lParam.ToInt64() & 0xFFFF);
                short y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                OnLeftButtonDown(x, y);
                break;
            }
            case WM_LBUTTONDBLCLK:
            {
                short x = (short)(lParam.ToInt64() & 0xFFFF);
                short y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (!HitTestCog(x, y))
                    VideoDoubleClicked?.Invoke();
                break;
            }
        }
        handled = false;
        return IntPtr.Zero;
    }

    private void OnMouseMove(int x, int y)
    {
        if (!_mouseTracking)
        {
            var t = new TRACKMOUSEEVENT
            {
                cbSize      = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags     = TME_LEAVE,
                hwndTrack   = _hwnd,
                dwHoverTime = 0,
            };
            TrackMouseEvent(ref t);
            _mouseTracking = true;
        }

        bool hit = HitTestCog(x, y);
        lock (_overlayLock)
        {
            _mouseX = x; _mouseY = y;
            _cogHover = hit;
        }
        PointerMoved?.Invoke();
    }

    private void OnLeftButtonDown(int x, int y)
    {
        if (!HitTestCog(x, y)) return;
        float op;
        lock (_overlayLock) op = _cogOpacity;
        if (op > 0.05f) CogClicked?.Invoke();
    }

    private bool HitTestCog(int x, int y)
    {
        var r = CogRect();
        return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
    }

    private void DisposeD3D()
    {
        try
        {
            _cogSvg?.Dispose();
            _brushNoSigBody?.Dispose();
            _brushNoSigTitle?.Dispose();
            _brushCogFg?.Dispose();
            _brushCogBgHover?.Dispose();
            _brushCogBg?.Dispose();
            _brushRenderInfo?.Dispose();
            _brushFpsGray?.Dispose();
            _brushFpsRed?.Dispose();
            _brushPanelBg?.Dispose();
            _tfNoSigBody?.Dispose();
            _tfNoSigTitle?.Dispose();
            _tfCog?.Dispose();
            _tfRender?.Dispose();
            _tfFps?.Dispose();
            _dwFactory?.Dispose();
            _d2dTarget?.Dispose();
            _d2dContext?.Dispose();
            _d2dDevice?.Dispose();
            _d2dFactory?.Dispose();

            _srv?.Dispose();
            _videoTex?.Dispose();
            _sampler?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();
            _rtv?.Dispose();
            _swap?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "D3D11 dispose error");
        }
    }
}
