using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;
using VCDV.Audio;
using VCDV.Capture;
using VCDV.Devices;
using VCDV.Diagnostics;
using VCDV.Models;

namespace VCDV;

public partial class MainWindow : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private Settings               _settings = Settings.Load();
    private readonly AudioPassthrough _audio = new();
    private CaptureManager?        _capture;

    private List<string>           _videoDevices = [];
    private List<AudioDeviceInfo>  _audioInputs  = [];
    private List<AudioDeviceInfo>  _audioOutputs = [];

    private CaptureStats?          _latestStats;
    private string?                _statusMessage;

    private bool _sidebarOpen;
    private bool _isFullscreen;

    // Set while ApplySettingsToUi is programmatically updating controls, so
    // the SelectionChanged/ValueChanged handlers skip live-apply. Starts
    // true so that SelectionChanged events fired during InitializeComponent
    // (e.g. ComboBoxItem IsSelected="True") don't hit uninitialized fields.
    // The first ApplySettingsToUi (from OnLoaded) resets it to false.
    private bool _suppressLiveApply = true;

    // Debounce live-apply of custom FPS slider so dragging doesn't thrash
    // the capture restart.
    private readonly DispatcherTimer _customFpsDebounce = new()
        { Interval = TimeSpan.FromMilliseconds(400) };

    // Animated cog opacity (drives VideoDisplay.SetCogOpacity via DP callback).
    public static readonly DependencyProperty CogOpacityProperty =
        DependencyProperty.Register(nameof(CogOpacity), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) =>
                ((MainWindow)d).VideoDisplay.SetCogOpacity((float)(double)e.NewValue)));
    public double CogOpacity { get => (double)GetValue(CogOpacityProperty); set => SetValue(CogOpacityProperty, value); }

    // Render stats
    private int      _droppedFrameCount;
    private int      _renderTickCount;         // CompositionTarget.Rendering fires (post-dedup) per second
    private float    _renderFps;
    private int      _renderTicksPerSec;       // last-second snapshot for overlay
    private int      _droppedPerSec;           // last-second snapshot for overlay
    private int      _lastLagMs;
    private DateTime _lastOverlayTick = DateTime.UtcNow;
    private TimeSpan _lastRenderTime  = TimeSpan.FromTicks(-1); // dedup guard
    private long     _lastTickTicks;           // Stopwatch ticks of previous render fire

    // Timer that hides the cog button after the mouse stops moving
    private readonly DispatcherTimer _cogTimer = new()
        { Interval = TimeSpan.FromSeconds(2.5) };

    private const double SidebarWidth   = 440;
    private const double SidebarMarginX = 12;   // gap from right edge of window
    private const double SidebarMarginY = 12;   // gap from top/bottom

    // ── Init ──────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            _cogTimer.Tick += (_, _) => { _cogTimer.Stop(); HideCogButton(); };
            _customFpsDebounce.Tick += (_, _) =>
            {
                _customFpsDebounce.Stop();
                if (_sidebarOpen) LiveApplyFromUi();
            };

            // Window drag leaves the popup stuck at its old screen position
            // in windowed mode (the Popup HWND doesn't follow). Close it.
            LocationChanged += (_, _) =>
            {
                if (_sidebarOpen) CloseSidebar(animate: false);
            };

            // Events from the D3D11 swap-chain panel (overlay interaction)
            VideoDisplay.CogClicked         += OnVideoCogClicked;
            VideoDisplay.VideoDoubleClicked += OnVideoDoubleClicked;
            VideoDisplay.PointerMoved       += OnVideoPointerMoved;
            VideoDisplay.PointerLeft        += OnVideoPointerLeft;

            Loaded += OnLoaded;
            CompositionTarget.Rendering += OnCompositionRender;

            // Popup is a top-level HWND with no owner; it floats over all apps
            // by default. Hide it when VCDV loses focus, restore when regained.
            Deactivated += (_, _) =>
            {
                if (_sidebarOpen) SidebarPopup.IsOpen = false;
            };
            Activated += (_, _) =>
            {
                if (_sidebarOpen && WindowState != WindowState.Minimized)
                {
                    UpdateSidebarPosition();
                    SidebarPopup.IsOpen = true;
                }
            };
            // Clicking the taskbar icon to minimize doesn't always fire
            // Deactivated, and the Popup HWND doesn't ride the window's
            // minimize animation — it shrinks to the center of the screen
            // instead. Hide it explicitly on minimize.
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    if (_sidebarOpen) SidebarPopup.IsOpen = false;
                }
                else if (_sidebarOpen && IsActive)
                {
                    UpdateSidebarPosition();
                    SidebarPopup.IsOpen = true;
                }
            };
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "MainWindow constructor failed");
            Log.CloseAndFlush();
            throw;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
        Log.Information("main window loaded");

        await RefreshDevicesAsync();
        PopulateSettingsPanel();
        ApplySettingsToUi(_settings);
        UpdateOverlayVisibility();

        bool deviceMissing = string.IsNullOrWhiteSpace(_settings.VideoDevice) ||
                             !_videoDevices.Contains(_settings.VideoDevice);

        if (deviceMissing)
        {
            _statusMessage = "No device selected";
            OpenSidebar();
        }
        else
        {
            StartCaptureAndAudio(_settings);
        }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "OnLoaded failed");
        }
    }

    // ── Render loop ───────────────────────────────────────────────────────────

    // Used purely for once-per-second stats snapshots now; the DXGI swap chain
    // drives its own VBlank-paced render loop inside D3D11VideoPanel.
    private void OnCompositionRender(object? sender, EventArgs e)
    {
        var renderTime = ((RenderingEventArgs)e).RenderingTime;
        if (renderTime == _lastRenderTime) return;
        _lastRenderTime = renderTime;

        long tickNow = Stopwatch.GetTimestamp();
        if (_lastTickTicks != 0)
            PerfStats.TickInterval.Record(tickNow - _lastTickTicks);
        _lastTickTicks = tickNow;
        _renderTickCount++;

        var now = DateTime.UtcNow;
        if ((now - _lastOverlayTick).TotalSeconds < 1.0) return;

        double elapsed     = (now - _lastOverlayTick).TotalSeconds;
        _renderFps         = VideoDisplay.ConsumeRenderedFrameCount() / (float)elapsed;
        _renderTicksPerSec = _renderTickCount;
        _droppedPerSec     = _droppedFrameCount;
        _droppedFrameCount = 0;
        _renderTickCount   = 0;
        _lastOverlayTick   = now;

        UpdateFpsOverlay();
        UpdateRenderInfoOverlay();
        UpdateNoSignalOverlay();
        LogPerfSnapshot();
    }

    // Persistent record of per-second percentiles for post-run analysis.
    private void LogPerfSnapshot()
    {
        var cb = PerfStats.CaptureCallback.Snapshot();
        var pl = PerfStats.PickupLatency.Snapshot();
        var pr = PerfStats.PresentLatency.Snapshot();
        var ti = PerfStats.TickInterval.Snapshot();

        Log.Information(
            "perf rnd={Rnd:F1} ticks={Ticks} drops={Drops} " +
            "cb(p50={CbP50:F2}/p95={CbP95:F2}/max={CbMax:F2}) " +
            "pickup(p50={PlP50:F2}/p95={PlP95:F2}/max={PlMax:F2}) " +
            "present(p50={PrP50:F2}/p95={PrP95:F2}/max={PrMax:F2}) " +
            "interval(p50={TiP50:F2}/p95={TiP95:F2}/max={TiMax:F2}) ms",
            _renderFps, _renderTicksPerSec, _droppedPerSec,
            cb.p50, cb.p95, cb.max,
            pl.p50, pl.p95, pl.max,
            pr.p50, pr.p95, pr.max,
            ti.p50, ti.p95, ti.max);
    }

    // ── Capture callbacks ─────────────────────────────────────────────────────

    private void OnFrameArrived(CaptureFrame frame)
    {
        // Hand the frame straight to the swap-chain panel. SubmitFrame copies into
        // an internal buffer so we can immediately return the pooled array.
        using (frame)
        {
            unsafe
            {
                fixed (byte* src = frame.Pixels)
                    VideoDisplay.SubmitFrame(src, frame.Width, frame.Height, frame.ByteCount, frame.ReadyTicks);
            }
            _lastLagMs = (int)(Environment.TickCount64 - frame.ArrivedMs);
        }
    }

    private void OnStatsUpdated(CaptureStats stats)
    {
        _latestStats   = stats;
        _statusMessage = null;
    }

    private void OnCaptureError(string message)
    {
        Log.Warning("capture error: {Message}", message);
        _statusMessage = message;
    }

    // ── Overlays ──────────────────────────────────────────────────────────────

    private void UpdateFpsOverlay()
    {
        if (!_settings.ShowOverlay)
        {
            VideoDisplay.SetFpsOverlay(show: false, text: "", alert: false);
            return;
        }

        // The centered "No Signal" overlay covers the no-device / no-frames
        // / error cases, so the top-left FPS chip only shows real stats.
        if (_latestStats is null)
        {
            VideoDisplay.SetFpsOverlay(show: false, text: "", alert: false);
            return;
        }

        var text = $"{_latestStats.Width}×{_latestStats.Height} | {_latestStats.Fps:F1} FPS";
        VideoDisplay.SetFpsOverlay(show: true, text: text, alert: true);
    }

    private void UpdateNoSignalOverlay()
    {
        // Show "No Signal" centered whenever we don't currently have a valid
        // video feed: no device selected, or no frames received yet. This also
        // tells the panel to skip drawing the video texture (prevents stale
        // texture garbage from leaking through across restarts).
        bool noDevice = string.IsNullOrWhiteSpace(_settings.VideoDevice);
        bool noFrames = _latestStats is null;
        bool show     = noDevice || noFrames;

        string title = "No Signal";
        string body  = "Check that your device is connected, and selected in the device list";
        VideoDisplay.SetNoSignal(show, title, body);
    }

    private void UpdateRenderInfoOverlay()
    {
        if (!_settings.ShowRenderInfo)
        {
            VideoDisplay.SetRenderInfoOverlay(show: false, text: "");
            return;
        }

        if (_latestStats is null)
        {
            VideoDisplay.SetRenderInfoOverlay(show: true, text: "No capture active");
            return;
        }

        var cb = PerfStats.CaptureCallback.Snapshot();
        var pl = PerfStats.PickupLatency.Snapshot();
        var pr = PerfStats.PresentLatency.Snapshot();
        var ti = PerfStats.TickInterval.Snapshot();

        var text =
            $"Source    {_latestStats.Subtype,-8} {_latestStats.Width}×{_latestStats.Height}\n" +
            $"Mode      {_latestStats.Mode}\n" +
            $"Cap FPS   {_latestStats.Fps,5:F1}   Rnd FPS {_renderFps,5:F1}   Ticks {_renderTicksPerSec,3}\n" +
            $"Dropped   {_droppedPerSec,4}/s   Lag     {_lastLagMs,4}ms\n" +
            $"Callback  p50 {cb.p50,5:F2}  p95 {cb.p95,5:F2}  max {cb.max,5:F2} ms\n" +
            $"Pickup    p50 {pl.p50,5:F2}  p95 {pl.p95,5:F2}  max {pl.max,5:F2} ms\n" +
            $"Present   p50 {pr.p50,5:F2}  p95 {pr.p95,5:F2}  max {pr.max,5:F2} ms\n" +
            $"Interval  p50 {ti.p50,5:F2}  p95 {ti.p95,5:F2}  max {ti.max,5:F2} ms";

        VideoDisplay.SetRenderInfoOverlay(show: true, text: text);
    }

    // ── Start / stop ──────────────────────────────────────────────────────────

    private void StartCaptureAndAudio(Settings s)
    {
        StopAll();

        _statusMessage = "Connecting...";
        _latestStats   = null;

        if (!string.IsNullOrWhiteSpace(s.VideoDevice))
        {
            _capture = new CaptureManager();
            _capture.FrameArrived  += OnFrameArrived;
            _capture.StatsUpdated  += OnStatsUpdated;
            _capture.ErrorOccurred += OnCaptureError;

            var (w, h) = s.ResolutionSize;
            _ = _capture.StartAsync(s.VideoDevice, w, h, s.TargetFps);
        }
        else
        {
            _statusMessage = "No device selected";
        }

        _audio.Start(s.VideoDevice, s.AudioInput, s.AudioOutput,
                     s.Volume, _audioInputs, _audioOutputs);

        UpdateNoSignalOverlay();
    }

    private void StopAll()
    {
        if (_capture is not null)
        {
            _capture.FrameArrived  -= OnFrameArrived;
            _capture.StatsUpdated  -= OnStatsUpdated;
            _capture.ErrorOccurred -= OnCaptureError;
            _capture.Dispose();
            _capture = null;
        }
        _audio.Stop();
    }

    // ── Device refresh ────────────────────────────────────────────────────────

    private async Task RefreshDevicesAsync()
    {
        _videoDevices = await DeviceEnumerator.EnumerateVideoDevicesAsync();
        _audioInputs  = DeviceEnumerator.EnumerateAudioInputs();
        _audioOutputs = DeviceEnumerator.EnumerateAudioOutputs();
    }

    // ── Settings panel ────────────────────────────────────────────────────────

    private void PopulateSettingsPanel()
    {
        // Clearing items fires SelectionChanged with index -1; guard against
        // that triggering LiveApplyFromUi (which would blank VideoDevice).
        bool prevSuppress = _suppressLiveApply;
        _suppressLiveApply = true;
        try
        {
            CbVideoDevice.Items.Clear();
            CbVideoDevice.Items.Add("(none)");
            foreach (var d in _videoDevices)
                CbVideoDevice.Items.Add(d);

            CbAudioInput.Items.Clear();
            CbAudioInput.Items.Add("Auto-detect");
            foreach (var d in _audioInputs)
                CbAudioInput.Items.Add(d.Name);

            CbAudioOutput.Items.Clear();
            CbAudioOutput.Items.Add("Default");
            foreach (var d in _audioOutputs)
                CbAudioOutput.Items.Add(d.Name);
        }
        finally
        {
            _suppressLiveApply = prevSuppress;
        }
    }

    private void ApplySettingsToUi(Settings s)
    {
        _suppressLiveApply = true;
        try
        {
        var vIdx = _videoDevices.IndexOf(s.VideoDevice);
        CbVideoDevice.SelectedIndex = vIdx >= 0 ? vIdx + 1 : 0;

        CbResolution.SelectedIndex = s.Resolution switch
        {
            "720p"  => 0,
            "1080p" => 1,
            "1440p" => 2,
            "4K"    => 3,
            _       => 1,
        };

        CbFps.SelectedIndex = s.FpsMode switch
        {
            "30"     => 0,
            "60"     => 1,
            "120"    => 2,
            "custom" => 3,
            _        => 1,
        };
        SlCustomFps.Value = s.CustomFps;

        CbAudioInput.SelectedIndex  = s.AudioInput  >= 0 ? s.AudioInput  + 1 : 0;
        CbAudioOutput.SelectedIndex = s.AudioOutput >= 0 ? s.AudioOutput + 1 : 0;

        SlVolume.Value            = s.Volume * 100;
        ChkOverlay.IsChecked      = s.ShowOverlay;
        ChkRenderInfo.IsChecked   = s.ShowRenderInfo;

        UpdateFullscreenButton();
        UpdateCustomFpsVisibility();
        UpdateHighFpsWarning();
        }
        finally
        {
            _suppressLiveApply = false;
        }
    }

    // Apply any UI changes to live settings + running capture/audio.
    // Called on every SelectionChanged/ValueChanged handler.
    private void LiveApplyFromUi()
    {
        if (_suppressLiveApply) return;

        var newSettings = ReadDraftFromUi();

        bool captureChanged = newSettings.VideoDevice != _settings.VideoDevice ||
                              newSettings.Resolution  != _settings.Resolution  ||
                              newSettings.FpsMode     != _settings.FpsMode     ||
                              newSettings.CustomFps   != _settings.CustomFps   ||
                              newSettings.AudioInput  != _settings.AudioInput  ||
                              newSettings.AudioOutput != _settings.AudioOutput;
        bool volumeChanged = Math.Abs(newSettings.Volume - _settings.Volume) > 0.001;

        _settings = newSettings;
        _settings.Save();
        UpdateOverlayVisibility();

        if (captureChanged)
            StartCaptureAndAudio(_settings);
        else if (volumeChanged)
            _audio.SetVolume(_settings.Volume);
    }

    private Settings ReadDraftFromUi()
    {
        return new Settings
        {
            VideoDevice = CbVideoDevice.SelectedIndex > 0
                ? _videoDevices[CbVideoDevice.SelectedIndex - 1]
                : "",

            Resolution = CbResolution.SelectedIndex switch
            {
                0 => "720p",
                1 => "1080p",
                2 => "1440p",
                3 => "4K",
                _ => "1080p",
            },

            FpsMode = CbFps.SelectedIndex switch
            {
                0 => "30",
                1 => "60",
                2 => "120",
                3 => "custom",
                _ => "60",
            },
            CustomFps   = (int)SlCustomFps.Value,
            AudioInput  = CbAudioInput.SelectedIndex  > 0 ? CbAudioInput.SelectedIndex  - 1 : -1,
            AudioOutput = CbAudioOutput.SelectedIndex > 0 ? CbAudioOutput.SelectedIndex - 1 : -1,
            Volume         = SlVolume.Value / 100.0,
            ShowOverlay    = ChkOverlay.IsChecked    == true,
            ShowRenderInfo = ChkRenderInfo.IsChecked == true,
        };
    }

    // ── Sidebar open / close ──────────────────────────────────────────────────

    // Right edge of the video area in DIPs, in screen coordinates.
    // Works in windowed, maximized, and borderless-fullscreen modes across DPI.
    private (double rightEdgeDip, double topDip) ComputeSidebarAnchorDip()
    {
        // PointToScreen returns device pixels; convert to DIPs via CompositionTarget.
        var src = PresentationSource.FromVisual(RootGrid);
        var rightDev = RootGrid.PointToScreen(new Point(RootGrid.ActualWidth, 0));
        if (src?.CompositionTarget is { } ct)
        {
            var m = ct.TransformFromDevice;
            var rightDip = m.Transform(rightDev);
            return (rightDip.X, rightDip.Y);
        }
        return (rightDev.X, rightDev.Y);
    }

    private void UpdateSidebarPosition()
    {
        var (rightEdge, top) = ComputeSidebarAnchorDip();
        SidebarPopup.VerticalOffset   = top + SidebarMarginY;
        SidebarPopup.HorizontalOffset = rightEdge - SidebarWidth - SidebarMarginX;
        SidebarPanel.Height           = Math.Max(0, RootGrid.ActualHeight - 2 * SidebarMarginY);
    }

    private void OpenSidebar()
    {
        if (_sidebarOpen) return;
        _sidebarOpen = true;

        UpdateSidebarPosition();
        SidebarPopup.IsOpen = true;

        // Defer applying settings until after the popup has laid out its
        // ComboBoxes — otherwise ComboBox selection can appear reset because
        // items aren't measured yet.
        Dispatcher.BeginInvoke(new Action(() => ApplySettingsToUi(_settings)),
                               DispatcherPriority.ContextIdle);

        SidebarPanel.Opacity = 0;
        var anim = new DoubleAnimation(0, 1,
            new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SidebarPanel.BeginAnimation(OpacityProperty, anim);

        SidebarPanel.Focus();
        Keyboard.Focus(SidebarPanel);

        // Hide cog while the sidebar is open (otherwise it peeks out from
        // behind the sidebar's rounded corner).
        _cogTimer.Stop();
        HideCogButton();
    }

    private void CloseSidebar(bool animate = true)
    {
        if (!_sidebarOpen) return;
        _sidebarOpen = false;

        // Flush any pending debounced slider change before closing.
        if (_customFpsDebounce.IsEnabled)
        {
            _customFpsDebounce.Stop();
            LiveApplyFromUi();
        }

        if (!animate)
        {
            SidebarPanel.BeginAnimation(OpacityProperty, null);
            SidebarPanel.Opacity = 1;
            SidebarPopup.IsOpen  = false;
            _cogTimer.Stop();
            _cogTimer.Start();
            return;
        }

        var anim = new DoubleAnimation(1, 0,
            new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            // Only close the popup if the user hasn't reopened meanwhile.
            if (!_sidebarOpen)
            {
                SidebarPopup.IsOpen = false;
                SidebarPanel.Opacity = 1; // reset for next open
            }
        };
        SidebarPanel.BeginAnimation(OpacityProperty, anim);

        // Resume cog auto-hide
        _cogTimer.Stop();
        _cogTimer.Start();
    }

    // ── Cog button ────────────────────────────────────────────────────────────

    private void ShowCogButton()
    {
        var anim = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(150)));
        BeginAnimation(CogOpacityProperty, anim);
    }

    private void HideCogButton()
    {
        var anim = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(400)));
        BeginAnimation(CogOpacityProperty, anim);
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        WindowStyle   = _isFullscreen ? WindowStyle.None            : WindowStyle.SingleBorderWindow;
        WindowState   = _isFullscreen ? WindowState.Maximized       : WindowState.Normal;
        UpdateFullscreenButton();
    }

    private void UpdateFullscreenButton() =>
        BtnFullscreen.Content = _isFullscreen ? "Exit Fullscreen" : "Enter Fullscreen";

    private void UpdateOverlayVisibility()
    {
        // Push an initial state in case the stats snapshot hasn't fired yet.
        if (!_settings.ShowOverlay)
            VideoDisplay.SetFpsOverlay(show: false, text: "", alert: false);
        if (!_settings.ShowRenderInfo)
            VideoDisplay.SetRenderInfoOverlay(show: false, text: "");
        UpdateNoSignalOverlay();
    }

    private void UpdateCustomFpsVisibility()
    {
        if (CustomFpsRow is null || CbFps is null) return;
        CustomFpsRow.Visibility = CbFps.SelectedIndex == 3
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateHighFpsWarning();
    }

    private void UpdateHighFpsWarning()
    {
        if (TbHighFpsWarning is null || SlCustomFps is null) return;
        int fps = CbFps.SelectedIndex switch
        {
            0 => 30,  1 => 60,  2 => 120,
            3 => (int)SlCustomFps.Value,
            _ => 60,
        };
        TbHighFpsWarning.Visibility = fps > 60 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_sidebarOpen) CloseSidebar();
                else              OpenSidebar();
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_sidebarOpen) UpdateSidebarPosition();
    }

    private void SidebarPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSidebar();
            e.Handled = true;
        }
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_sidebarOpen) return;
        ShowCogButton();
        _cogTimer.Stop();
        _cogTimer.Start();
    }

    private void RootGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_sidebarOpen)
        {
            _cogTimer.Stop();
            HideCogButton();
        }
    }

    private void OnVideoCogClicked()
    {
        if (!CheckAccess()) { Dispatcher.BeginInvoke(OnVideoCogClicked); return; }
        if (_sidebarOpen) CloseSidebar();
        else              OpenSidebar();
    }

    private void OnVideoDoubleClicked()
    {
        if (!CheckAccess()) { Dispatcher.BeginInvoke(OnVideoDoubleClicked); return; }
        if (!_sidebarOpen) ToggleFullscreen();
    }

    private void OnVideoPointerMoved()
    {
        if (!CheckAccess()) { Dispatcher.BeginInvoke(OnVideoPointerMoved); return; }
        if (_sidebarOpen) return;
        ShowCogButton();
        _cogTimer.Stop();
        _cogTimer.Start();
    }

    private void OnVideoPointerLeft()
    {
        if (!CheckAccess()) { Dispatcher.BeginInvoke(OnVideoPointerLeft); return; }
        if (!_sidebarOpen)
        {
            _cogTimer.Stop();
            HideCogButton();
        }
    }

    private void BtnCloseSidebar_Click(object sender, RoutedEventArgs e) =>
        CloseSidebar();

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) =>
        ToggleFullscreen();

    private void BtnExit_Click(object sender, RoutedEventArgs e) => Close();

    private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        BtnRefreshDevices.IsEnabled = false;
        BtnRefreshDevices.Content   = "↺";
        await RefreshDevicesAsync();
        PopulateSettingsPanel();
        ApplySettingsToUi(_settings);

        // Re-initialize the currently-selected device (video feed flashes off
        // and on). If the saved device is no longer enumerated, this will
        // surface the "No Signal" overlay instead.
        StartCaptureAndAudio(_settings);

        BtnRefreshDevices.IsEnabled = true;
        BtnRefreshDevices.Content   = "↻";
    }

    private void CbFps_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateCustomFpsVisibility();
        LiveApplyFromUi();
    }

    private void SlCustomFps_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (TbCustomFpsValue is null) return;
        TbCustomFpsValue.Text = ((int)e.NewValue).ToString();
        UpdateHighFpsWarning();
        // Debounce: only restart capture once the user stops dragging.
        if (_suppressLiveApply) return;
        _customFpsDebounce.Stop();
        _customFpsDebounce.Start();
    }

    private void SlVolume_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (TbVolumeValue is null) return;
        TbVolumeValue.Text = $"{(int)e.NewValue}%";
        LiveApplyFromUi();
    }

    private void CbVideoDevice_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        LiveApplyFromUi();

    private void CbResolution_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        LiveApplyFromUi();

    private void CbAudioInput_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        LiveApplyFromUi();

    private void CbAudioOutput_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        LiveApplyFromUi();

    private void ChkOverlay_CheckedChanged(object sender, RoutedEventArgs e) =>
        LiveApplyFromUi();

    private void ChkRenderInfo_CheckedChanged(object sender, RoutedEventArgs e) =>
        LiveApplyFromUi();

    // ── Dark title bar (DWM) ──────────────────────────────────────────────────

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR          = 35;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                                  ref useDark, sizeof(int));

            // COLORREF = 0x00BBGGRR; #202020 is r=g=b=0x20.
            int caption = 0x00202020;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR,
                                  ref caption, sizeof(int));
        }
        catch (Exception ex)
        {
            // Pre-Windows 11 or DWM unavailable — title bar stays default.
            Log.Debug(ex, "dark title bar not applied");
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRender;
        _cogTimer.Stop();
        StopAll();
        _audio.Dispose();
        _settings.Save();
        Log.Information("window closed");
    }
}
