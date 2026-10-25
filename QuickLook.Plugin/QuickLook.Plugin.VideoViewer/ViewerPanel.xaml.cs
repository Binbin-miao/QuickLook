// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.MediaInfo;
using QuickLook.MediaInfo.Core;
using QuickLook.Plugin.VideoViewer.AudioTrack;
using QuickLook.Plugin.VideoViewer.Engine;
using QuickLook.Plugin.VideoViewer.LyricTrack;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtfUnknown;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

public partial class ViewerPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly ContextObject _context;
    private BitmapSource _coverArt;
    private DispatcherTimer _lyricTimer;
    private LrcLine[] _lyricLines;
    private MidiPlayer _midiPlayer;

    private bool _hasVideo;
    private bool _isVideoFile;
    private bool _autoFocusPending;
    private bool _activatedSubscribed;
    private bool _isPlaying;
    private bool _wasPlaying;
    private bool _shouldLoop;
    private bool _useHardwareAcceleration;
    private double _playbackRate = 1.0;

    // --- RTX VSR（超分辨率）状态 ---
    private bool _vsrEnabled;
    private int _vsrCap = 2160;      // 输出高度上限：1080 / 1440 / 2160
    private int _vsrQuality = 3;
    private int _vsrAlgo;          // 0 = 驱动 VideoProcessorBlt（默认）, 1 = Spline36
    private static bool? _nvidiaGpu;

    // 1x 已从菜单移除：超分输出现在至少铺满显示区域，1x 实际等于「关闭」，
    // 而「关闭」在质量列里已经有了，留着只会让人困惑。
    /// <summary>超分输出高度的三档上限。1K=1080 / 2K=1440 / 4K=2160。</summary>
    private static readonly int[] VsrCaps = { 1080, 1440, 2160 };

    // --- 新渲染内核（D3D11 + RTX VSR） ---
    private IVideoEngine _engine;
    private D3DImage _d3dImage;
    private bool _engineActive;
    private int _framePending;
    private int _framePendingAt;
    private int _dirtyCount;        // 成功 AddDirtyRect 的次数，用来区分「引擎没出帧」和「UI 没重绘」
    private string _dirtyError;
    private volatile bool _frontAvailable = true;   // 只能在 UI 线程读，缓存出来给看门狗用
    private long _mediaPosition;
    private long _mediaDuration;
    private bool _scrubbing;
    private bool _disposed;
    private bool _suppressSeek;        // 来自播放器的回写，不要当成 seek
    private int _seekGuardUntil;       // seek 之后这段时间内忽略播放器的位置回写

    // 面板还没进可视树时拿不到 HWND，先记下来等 Loaded 再起引擎
    private DispatcherTimer _resizeTimer;
    /// <summary>目标尺寸还没定下来（面板未布局 / 重建失败），等下次尺寸变化重试。</summary>
    private bool _targetPending;
    private int _targetRetry;
    private bool _resizeQueued;
    private string _currentPath;
    private bool _startPending;
    private string _pendingPath;
    private double _pendingRotation;
    /// <summary>切到原播放器后要落到的位置（秒）；&lt; 0 表示从头播。在 MediaOpened 里消费。</summary>
    private double _legacySeekSeconds = -1;
    /// <summary>待补的目标位置（ticks，&lt; 0 表示没有）。</summary>
    private long _legacySeekTarget = -1;
    /// <summary>什么时候才允许去读/seek 播放器：图刚开好时碰它会崩。</summary>
    private int _legacyReadyAt;
    private int _legacySeekDueAt;
    private int _pumpAt;

    /// <summary>切过来时本来是暂停的，等图开好再暂停（开图前 Pause 会被忽略）。</summary>
    private bool _legacyStartPaused;

    /// <summary>Playback speed steps offered in the side menu (0.5x … 3.0x).</summary>
    private static readonly double[] SpeedSteps = { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0 };

    public ViewerPanel(ContextObject context)
    {
        InitializeComponent();
        LoadAndInsertGlassLayer();

        // 引擎日志统一走本文件末尾那个 EngineLog（以前引擎反过来直接调它，
        // 编译子进程宿主时是死结，现在改成引擎暴露 LogSink）
        VideoEngine.LogSink = EngineLog;

        // 子进程模式默认开。万一手上有机器跑不起来，把配置项 IsolatedEngine 设成 false
        // 就退回进程内引擎——两条路走的是同一份引擎源码。
        VideoEngineProxy.Enabled = SettingHelper.Get("IsolatedEngine", true, "QuickLook.Plugin.VideoViewer");

        // 空闲回收的计数：面板活着的时候不许杀引擎宿主
        EnterPanel();

        // apply global theme
        Resources.MergedDictionaries[0].MergedDictionaries.Clear();

        _context = context;

        mediaElement.MediaUriPlayer.LAVFilterDirectory =
            IntPtr.Size == 8 ? @"LAVFilters-x64\" : @"LAVFilters-x86\";

        //ShowViedoControlContainer(null, null);
        viewerPanel.PreviewMouseMove += ShowViedoControlContainer;

        mediaElement.MediaUriPlayer.PlayerStateChanged += PlayerStateChanged;
        mediaElement.MediaOpened += MediaOpened;
        mediaElement.MediaEnded += MediaEnded;
        mediaElement.MediaFailed += MediaFailed;

        ShouldLoop = SettingHelper.Get("ShouldLoop", false, "QuickLook.Plugin.VideoViewer");

        // 这个开关现在决定「用哪条播放路径」，不再只是 LAV 的一个标志位：
        //   H/W → 新引擎（D3D11 + DXVA），可开 VSR
        //   S/W → 原播放器（WPFMediaKit + LAV 软解），一个 D3D 对象都不建
        // 默认给 H/W。注意这里**不做老配置迁移**：配置里已经写着 false 的，
        // 说明用户（或旧默认值）选了软解，就按软解解释。用户换硬件、或者
        // 想用软解省电，都靠这个档位自己切，我们不去覆盖。
        UseHardwareAcceleration = SettingHelper.Get("UseHardwareAcceleration", true, "QuickLook.Plugin.VideoViewer");

        // Apply persisted HW/SW mode to the underlying player if supported.
        HardwareAccelerationModeChanged(UseHardwareAcceleration);

        string translationFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");
        buttonPlayPause.ToolTip = TranslationHelper.Get("BTN_PlayPause", translationFile, failsafe: "Play/Pause");
        buttonLoop.ToolTip = TranslationHelper.Get("BTN_Loop", translationFile, failsafe: "Loop");
        buttonHardwareAcceleration.ToolTip = TranslationHelper.Get("BTN_HardwareAcceleration", translationFile, failsafe: "Hardware/Software Decoding");
        buttonMute.ToolTip = TranslationHelper.Get("BTN_Volume", translationFile, failsafe: "Volume");
        buttonTime.ToolTip = TranslationHelper.Get("BTN_Time", translationFile, failsafe: "Time Elapsed/Remaining");

        buttonPlayPause.Click += TogglePlayPause;
        buttonLoop.Click += ToggleShouldLoop;
        buttonHardwareAcceleration.Click += ToggleHardwareAcceleration;
        buttonSpeed.Click += ToggleSpeedMenu;
        buttonVsr.Click += ToggleVsrMenu;

        _playbackRate = SettingHelper.Get("PlaybackRate", 1.0, "QuickLook.Plugin.VideoViewer");
        if (_playbackRate <= 0) _playbackRate = 1.0;
        BuildSpeedMenu();

        _vsrEnabled = SettingHelper.Get("VsrEnabled", false, "QuickLook.Plugin.VideoViewer");
        // 老配置里是 VsrScale（倍率），语义已经变了，直接当没有：一律从 4K 起。
        _vsrCap = SettingHelper.Get("VsrCap", 2160, "QuickLook.Plugin.VideoViewer");
        _vsrQuality = SettingHelper.Get("VsrQuality", 3, "QuickLook.Plugin.VideoViewer");
        if (_vsrCap != 1080 && _vsrCap != 1440 && _vsrCap != 2160) _vsrCap = 2160;
        if (_vsrQuality < 1 || _vsrQuality > 4) _vsrQuality = 3;
        _vsrAlgo = SettingHelper.Get("VsrAlgo", 0, "QuickLook.Plugin.VideoViewer");
        if (_vsrAlgo < 0 || _vsrAlgo > 2) _vsrAlgo = 0;
        BuildVsrMenu();

        // 引擎一旦判定这台机器用不了 VSR（老 N 卡 / 驱动过旧 / 缺组件），
        // 就把按钮置灰并把原因写进 ToolTip——不然用户只会觉得「点了没反应」。
        VideoEngine.VsrUnavailable += OnVsrUnavailable;
        buttonTime.Click += (_, _) => buttonTime.Tag = (string)buttonTime.Tag == "Time" ? "Length" : "Time";
        buttonMute.Click += (_, _) => volumeSliderLayer.Visibility = Visibility.Visible;
        volumeSliderLayer.MouseDown += (_, _) => volumeSliderLayer.Visibility = Visibility.Collapsed;

        sliderProgress.PreviewMouseDown += (_, e) =>
        {
            _scrubbing = true;

            if (_engineActive)
            {
                _wasPlaying = !_engine.IsPaused;
                _engine.IsPaused = true;
            }
            else
            {
                _wasPlaying = mediaElement.IsPlaying;
                mediaElement.Pause();
            }
        };
        sliderProgress.PreviewMouseUp += (_, _) =>
        {
            _scrubbing = false;

            if (_engineActive)
            {
                if (_wasPlaying) _engine.IsPaused = false;
                IsPlaying = !_engine.IsPaused;
            }
            else if (_wasPlaying)
            {
                mediaElement.Play();
            }
        };

        PreviewMouseWheel += (_, e) => ChangeVolume(e.Delta / 120d * 0.04d);

        // 走 WPFMediaKit 的路径没有帧回调，靠定时器把进度泵到 UI。
        // 这里**不能用 DispatcherTimer**：实测它在 QuickLook 宿主和测试宿主里都不 tick
        // （S/W 模式下进度条会永远停在 0:00）。改成后台 Timer + BeginInvoke，
        // UI 线程只要在泵消息就一定会处理到。
        _legacyReadyAt = Environment.TickCount + 800;

        // 进度泵挂在 CompositionTarget.Rendering 上。
        // 这里踩过两个坑：DispatcherTimer 在 QuickLook 宿主里根本不 tick；
        // System.Threading.Timer 在测试宿主里也不触发。渲染事件由合成器驱动，
        // 只要窗口在画就一定会来，是唯一可靠的心跳。
        CompositionTarget.Rendering += OnCompositionTick;

        // 窗口缩放后重新按显示尺寸建表面（去抖，避免拖动过程中反复重建）
        // 优先级必须是 Normal：DispatcherTimer 默认是 Background，而帧回调一直
        // 排在 Render 优先级上，Background 的定时器会被长期饿死、根本不触发。
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer?.Stop();
            ApplyTargetSize();
        };

        void OnHostSizeChanged()
        {
            if (_disposed || !_engineActive) return;

            // 直接算，不经过 DispatcherTimer：实测这个宿主里 DispatcherTimer
            // （Background / Normal 都一样）根本不触发，靠它做去抖会永远等不到。
            // ApplyTargetSize 内部对「尺寸没变」会立刻返回，所以重复调用很便宜。
            ApplyTargetSize();

            // 拖拽窗口时尺寸会连续变化，排一次收尾把最终尺寸落定。
            if (_resizeQueued) return;
            _resizeQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _resizeQueued = false;
                if (!_disposed && _engineActive) ApplyTargetSize();
            }));
        }

        videoSurfaceHost.SizeChanged += (_, _) => OnHostSizeChanged();

        // 面板自身尺寸变化也要触发重建：宿主切换 busy 层时先变的是面板尺寸，
        // videoSurfaceHost 可能还没被测量。
        SizeChanged += (_, _) => OnHostSizeChanged();

        Focusable = true;
        PreviewKeyDown += ViewerPanel_PreviewKeyDown;
        Loaded += ViewerPanel_Loaded;
        IsVisibleChanged += ViewerPanel_IsVisibleChanged;
    }

    /// <summary>渲染帧回调：把进度泵限流到 10Hz。</summary>
    private void OnCompositionTick(object sender, EventArgs e)
    {
        if (_disposed) return;

        int now = Environment.TickCount;
        if (now - _pumpAt < 100) return;
        _pumpAt = now;

        PumpLegacyPosition();
    }

    /// <summary>
    /// 把原播放器的位置泵到 UI（限流到 10Hz，由渲染帧驱动）。
    /// 不在引擎模式下工作——那时候进度由 OnEngineFrame 推。
    /// </summary>
    private void PumpLegacyPosition()
    {
        if (_disposed) return;

        // 引擎在跑的时候，进度由 OnEngineFrame 推；这里只负责补上
        // 「第一次打开时面板还没布局、拿不到显示尺寸」的那一次重试。
        if (_engineActive)
        {
            if (_targetPending) ApplyTargetSize();
            return;
        }

        if (mediaElement == null) return;

        // 图刚开好/刚 seek 完的这一小段时间里，读 MediaPosition 会让
        // WPFMediaKit 在 DirectShow 的过渡状态上直接访问冲突（进程消失）。
        if (Environment.TickCount < _legacyReadyAt) return;

        if (_legacySeekTarget >= 0 && Environment.TickCount >= _legacySeekDueAt)
        {
            ApplyLegacySeekIfPending();
            return;
        }

        long duration = mediaElement.MediaDuration;
        if (duration > 0 && MediaDuration != duration)
            MediaDuration = duration;

        if (_scrubbing || Environment.TickCount < _seekGuardUntil) return;

        _suppressSeek = true;
        try
        {
            MediaPosition = mediaElement.MediaPosition;
        }
        finally
        {
            _suppressSeek = false;
        }
    }

    private partial void LoadAndInsertGlassLayer();

    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (value == _hasVideo) return;
            _hasVideo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VsrButtonVisibility));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (value == _isPlaying) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldLoop
    {
        get => _shouldLoop;
        private set
        {
            if (value == _shouldLoop) return;
            _shouldLoop = value;
            OnPropertyChanged();
        }
    }

    public bool UseHardwareAcceleration
    {
        get => _useHardwareAcceleration;
        private set
        {
            if (value == _useHardwareAcceleration) return;
            _useHardwareAcceleration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VsrButtonVisibility));
        }
    }

    public BitmapSource CoverArt
    {
        get => _coverArt;
        private set
        {
            if (ReferenceEquals(value, _coverArt)) return;
            if (value == null) return;
            _coverArt = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Text shown on the speed button, e.g. "1.0x".</summary>
    public string SpeedLabel => _playbackRate.ToString("0.0#", CultureInfo.InvariantCulture) + "x";

    /// <summary>Text shown on the VSR button.</summary>
    public string VsrLabel =>
        VideoEngine.VsrUnsupported
            ? "VSR ✕"
            : _vsrEnabled
                ? "VSR " + CapLabel(_vsrCap)
                : "VSR";

    /// <summary>
    /// VSR 只在 H/W 模式下存在：S/W 走的是原播放器，根本没有超分链路。
    /// 另外还要求引擎真的起来了（回退到 WPFMediaKit 时按钮点了也没用），
    /// 以及这块卡能跑（用渲染设备上的适配器厂商判断，不是注册表里有没有 N 卡）。
    /// </summary>
    public Visibility VsrButtonVisibility =>
        _hasVideo && _engineActive && UseHardwareAcceleration && SupportsVsr()
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>确认不支持之后按钮留着但点不动，用户能看到原因（ToolTip）。</summary>
    public bool VsrButtonEnabled => !VideoEngine.VsrUnsupported;

    public string VsrButtonToolTip =>
        VideoEngine.VsrUnsupported
            ? (VideoEngine.VsrUnsupportedReason ?? "VSR 不可用")
            : "RTX Video Super Resolution";

    /// <summary>
    /// 有没有 N 卡。设备信息拿得到就以设备为准，拿不到才退回注册表探测。
    /// </summary>
    private bool SupportsVsr()
    {
        if (VideoEngine.VsrUnsupported) return false;

        var engine = _engine;
        if (engine != null && engine.AdapterVendor != 0)
            return engine.IsNvidiaAdapter;

        return HasNvidiaGpu();
    }

    /// <summary>
    /// 引擎判定 VSR 不可用时的回调。可能在渲染/UI 线程上，统一转回 UI 线程。
    /// </summary>
    private void OnVsrUnavailable(string reason)
    {
        if (_disposed) return;

        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(() => OnVsrUnavailable(reason))); }
            catch { }
            return;
        }

        EngineLog("VSR unavailable: " + reason);

        // 顺手把持久化的开关关掉：否则下次打开视频，按钮上还写着「VSR 2x」，
        // 看起来像开着、其实一直是普通缩放。
        if (_vsrEnabled)
        {
            _vsrEnabled = false;
            try { SettingHelper.Set("VsrEnabled", false, "QuickLook.Plugin.VideoViewer"); }
            catch { }
        }

        OnPropertyChanged(nameof(VsrLabel));
        OnPropertyChanged(nameof(VsrButtonVisibility));
        OnPropertyChanged(nameof(VsrButtonEnabled));
        OnPropertyChanged(nameof(VsrButtonToolTip));

        if (vsrMenuLayer != null) vsrMenuLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>播放进度（ticks）。进度条双向绑定到这里，拖动即 seek。</summary>
    public long MediaPosition
    {
        get => _mediaPosition;
        set
        {
            if (_mediaPosition == value) return;
            _mediaPosition = value;
            OnPropertyChanged();

            if (_suppressSeek) return;

            // 用户拖动 / 键盘 seek：立刻下发，并让播放器的位置回写在短时间内闭嘴，
            // 否则滑块会被旧位置拉回去，看起来就是"拖不动"
            _seekGuardUntil = Environment.TickCount + 500;

            if (_engineActive)
                _engine?.Seek(TimeSpan.FromTicks(value));
            else if (mediaElement != null)
                mediaElement.MediaPosition = value;
        }
    }

    /// <summary>总时长（ticks）。</summary>
    public long MediaDuration
    {
        get => _mediaDuration;
        private set
        {
            if (_mediaDuration == value) return;
            _mediaDuration = value;
            OnPropertyChanged();
        }
    }

    // ================================================================ 新渲染内核

    private static readonly string EngineLogPath =
        Path.Combine(Path.GetTempPath(), "quicklook-vsr.log");

    /// <summary>引擎诊断日志。没有调试器时也能定位问题（%TEMP%\quicklook-vsr.log）。</summary>
    internal static void EngineLog(string message)
    {
        try
        {
            var info = new FileInfo(EngineLogPath);
            if (info.Exists && info.Length > 256 * 1024)
                File.Delete(EngineLogPath);

            File.AppendAllText(EngineLogPath,
                $"{DateTime.Now:MM-dd HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
            // 日志失败不影响播放
        }
    }

    /// <summary>
    /// 尝试用 D3D11 引擎接管视频预览。成功返回 true，失败则回退到 WPFMediaKit。
    /// </summary>
    private bool TryStartEngine(string path, double rotation)
    {
        IVideoEngine engine = null;
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            var wnd = Window.GetWindow(this);
            if (wnd != null) hwnd = new WindowInteropHelper(wnd).EnsureHandle();

            // 进程内还是子进程，只在这里分叉。两边的配置项完全一样，
            // 因为 VideoEngineProxy 就是照 VideoEngine 的接口做的。
            engine = VideoEngineProxy.Enabled ? (IVideoEngine)new VideoEngineProxy() : new VideoEngine();
            engine.VsrEnabled = _vsrEnabled;
            engine.VsrCapHeight = _vsrCap;
            engine.VsrQuality = _vsrQuality;
            engine.DownscaleAlgo = _vsrAlgo;
            engine.Rate = _playbackRate;
            engine.Loop = ShouldLoop;
            engine.Volume = (float)Math.Max(0d, Math.Min(1d, LinearVolume));
            engine.DiagSink = EngineLog;
            engine.DiagExtra = () =>
                $"dirty={_dirtyCount} uiPos={_mediaPosition / 1e7:0.00}s " +
                $"front={(_frontAvailable ? "ok" : "no")} " +
                $"pending={_framePending} target={_targetPending} " +
                $"err={(_dirtyError ?? "-")}";


            if (!engine.Open(path, hwnd))
            {
                EngineLog($"open FAILED  {Path.GetFileName(path)}  {engine.LastError}");
                engine.Dispose();
                return false;
            }

            // H/W 模式的承诺是「硬解 + 可超分」。这台机器跑不了 VSR 就整条退回
            // 原播放器（按开关走 LAV 硬解），而不是留在这里给一个用不了的按钮。
            // 必须在 Start() 之前探测：那时渲染线程还没起来，探测不需要闸门。
            if (!engine.ProbeVsr())
            {
                EngineLog($"VSR 不可用，H/W 退回原播放器：{engine.LastError}");
                engine.Dispose();
                return false;
            }

            engine.FrameRendered += OnEngineFrame;
            _engine = engine;
            _engineActive = true;
            MarkPreviewStarted();

            AttachSurface(engine);

            if (Math.Abs(rotation) > 0.1d)
                videoRotation.Angle = rotation;

            ApplyTargetSize();

            MediaDuration = (long)engine.Duration.Ticks;

            // 让 WPFMediaKit 彻底退场，避免它自己起一个图去解码/出声
            mediaElement.Visibility = Visibility.Collapsed;

            HasVideo = true;
            OnPropertyChanged(nameof(VsrButtonVisibility));

            engine.Start();
            IsPlaying = true;
            _context.IsBusy = false;

            // 关键顺序：IsBusy=false 之前，本面板根本还没被放进可视树，
            // 这时候 ActualWidth 一定是 0，上面那次 ApplyTargetSize 必然拿不到尺寸。
            // 宿主把面板挂上之后必须再算一次，否则共享表面会一直停在默认（源）尺寸，
            // 超分结果再被 WPF 拉伸一次，看起来就是「没效果」。
            ApplyTargetSize();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyTargetSize));

            // 画面已经能出了，这时候再补 VSR。
            // NGX 初始化实测 400ms（VSR 开 1050ms vs 关 620ms），以前它挡在首帧前面，
            // 就是「按空格要等一秒」的来源。现在挪到画面出来之后的空闲档位，
            // 用户先看到画面，超分在几百毫秒内无缝追上来。
            if (engine.VsrEnabled)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (_disposed || !ReferenceEquals(_engine, engine)) return;
                    try
                    {
                        var t = System.Diagnostics.Stopwatch.StartNew();
                        bool ok = engine.ActivateVsr();
                        EngineLog($"VSR 后台激活 {(ok ? "ok" : "失败")} {t.ElapsedMilliseconds}ms  " +
                                  $"out={engine.OutputWidth}x{engine.OutputHeight}  " +
                                  $"err={(string.IsNullOrEmpty(engine.LastError) ? "-" : engine.LastError)}");
                    }
                    catch (Exception e)
                    {
                        EngineLog("VSR 后台激活异常 " + e.Message);
                    }
                }));
            }

            EngineLog($"open OK  {Path.GetFileName(path)}  " +
                      $"{engine.SourceWidth}x{engine.SourceHeight} → {engine.OutputWidth}x{engine.OutputHeight}  " +
                      $"VSR={(engine.VsrActive ? "on" : "off")}  audio={(engine.AudioAvailable ? "on" : "off")}  " +
                      $"mtp={(engine.MultithreadProtected ? "on" : "off")}  " +
                      $"err={(string.IsNullOrEmpty(engine.LastError) ? "-" : engine.LastError)}");
            return true;
        }
        catch (Exception ex)
        {
            EngineLog("EXCEPTION " + ex);
            try { engine?.Dispose(); } catch { }
            _engine = null;
            _engineActive = false;
            return false;
        }
    }

    /// <summary>把引擎的 D3D9Ex 共享表面挂到 D3DImage 上。</summary>
    private void AttachSurface(IVideoEngine engine)
    {
        IntPtr ptr = engine.SurfacePointer;
        if (ptr == IntPtr.Zero) return;

        if (_d3dImage == null)
        {
            _d3dImage = new D3DImage();
            videoSurface.Source = _d3dImage;
        }

        _d3dImage.Lock();
        try
        {
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, ptr);

            // 脏矩形必须按共享表面的真实尺寸来，不是 VSR 输出尺寸——
            // 两者在“显示尺寸 < 超分尺寸”时不同，传大了会直接抛异常导致黑屏
            int w = engine.SurfaceWidth;
            int h = engine.SurfaceHeight;
            if (w > 0 && h > 0)
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            _d3dImage.Unlock();
        }

        videoSurfaceHost.Visibility = Visibility.Visible;
    }

    /// <summary>把 D3DImage 从当前表面上解绑（表面即将被重建时用）。</summary>
    private void DetachSurface()
    {
        if (_d3dImage == null) return;

        try
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            _d3dImage.Unlock();
        }
        catch (Exception e) { Debug.WriteLine(e); }
    }

    /// <summary>
    /// 按视频宽高比把画面铺满面板，算出设备像素尺寸，交给引擎做最终缩放。
    /// 这样共享表面和屏幕像素一一对应，降采样算法才真正起作用。
    /// </summary>
    private void ApplyTargetSize()
    {
        var engine = _engine;
        if (!_engineActive || engine == null) return;

        // 刚进可视树时面板还没布局，ActualWidth 是 0。这时候不能就此放弃——
        // 置位后由渲染帧回调每 100ms 重试，直到拿到真实尺寸为止。
        // 优先用面板自身尺寸：videoSurfaceHost 是从 Collapsed 切过来的，
        // 在布局跑完之前它的 ActualWidth 一直是 0，而面板尺寸是可靠的。
        double availW = ActualWidth;
        double availH = ActualHeight;
        if (availW < 16 || availH < 16)
        {
            availW = videoSurfaceHost.ActualWidth;
            availH = videoSurfaceHost.ActualHeight;
        }
        if (availW < 16 || availH < 16)
        {
            availW = videoSurface.ActualWidth;
            availH = videoSurface.ActualHeight;
        }

        // 最后兜底：宿主窗口本身。某些情况下（宿主还没把本面板放进可视树）
        // 上面三个全是 0，但窗口早就有尺寸了。没有这一层，共享表面会一直
        // 停在默认尺寸，超分结果再被 WPF 拉伸一次，看起来就是「没有效果」。
        var hostWindow = Window.GetWindow(this);
        if (availW < 16 || availH < 16)
        {
            if (hostWindow != null && hostWindow.ActualWidth > 16 && hostWindow.ActualHeight > 16)
            {
                availW = hostWindow.ActualWidth;
                availH = hostWindow.ActualHeight;
            }
        }

        if (availW < 16 || availH < 16)
        {
            if (_targetRetry++ < 20)
                EngineLog($"target 等待布局#{_targetRetry}: panel {ActualWidth:0}x{ActualHeight:0} " +
                          $"host {videoSurfaceHost.ActualWidth:0}x{videoSurfaceHost.ActualHeight:0} " +
                          $"surface {videoSurface.ActualWidth:0}x{videoSurface.ActualHeight:0} " +
                          $"window {hostWindow?.ActualWidth ?? -1:0}x{hostWindow?.ActualHeight ?? -1:0} " +
                          $"hostVis={videoSurfaceHost.Visibility}");
            _targetPending = true;
            return;
        }

        int srcW = engine.SourceWidth;
        int srcH = engine.SourceHeight;
        if (srcW < 2 || srcH < 2) { _targetPending = true; return; }

        var dpi = VisualTreeHelper.GetDpi(this);
        double pxW = availW * dpi.DpiScaleX;
        double pxH = availH * dpi.DpiScaleY;

        double aspect = (double)srcW / srcH;
        double w = pxW;
        double h = w / aspect;
        if (h > pxH)
        {
            h = pxH;
            w = h * aspect;
        }

        int targetW = Math.Max(2, (int)Math.Round(w));
        int targetH = Math.Max(2, (int)Math.Round(h));

        // 尺寸没变就别重挂——重挂会丢掉一帧，而且 100ms 重试会变成持续抖动
        if (targetW == engine.SurfaceWidth && targetH == engine.SurfaceHeight)
        {
            _targetPending = false;
            return;
        }

        try
        {
            DetachSurface();
            engine.SetTargetSize(targetW, targetH);

            // SetTargetSize 只换表面，不重算倍率。窗口从小拖到大以后，显示需要的
            // 倍数已经变了（打开时往往正好 1:1，那时 VSR 按设计是不开的），
            // 这里补一次重算，否则「打开再最大化」永远看不到超分。
            ApplyVsrToEngine();

            AttachSurface(engine);
        }
        catch (Exception e)
        {
            // 重建表面失败不能让整个预览崩掉，保持上一帧画面即可
            EngineLog($"target 重建失败 {targetW}x{targetH}: {e.Message}");
            _targetPending = true;
            return;
        }

        _targetPending = false;
        EngineLog($"target {targetW}x{targetH}  (panel {pxW:0}x{pxH:0}px, dpi {dpi.DpiScaleX:0.##})");
    }

    /// <summary>解码线程每渲染完一帧回调（非 UI 线程）。</summary>
    private void OnEngineFrame()
    {
        var engine = _engine;
        if (engine == null) return;

        // 同一时刻只排队一次重绘，UI 忙的时候自动丢帧而不是堆积。
        // 但门闩必须有超时：一旦 BeginInvoke 丢了（宿主正在关闭、调度器忙），
        // 这个标志会永久停在 1，画面就再也不更新了。
        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            if (Environment.TickCount - _framePendingAt < 2000) return;
            Interlocked.Exchange(ref _framePending, 0);     // 卡住了，强行放开
            Interlocked.Exchange(ref _framePending, 1);
        }
        _framePendingAt = Environment.TickCount;

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Interlocked.Exchange(ref _framePending, 0);

                var img = _d3dImage;
                if (_disposed || img == null || !_engineActive) return;

                try
                {
                    _frontAvailable = img.IsFrontBufferAvailable;
                    img.Lock();
                    img.AddDirtyRect(new Int32Rect(0, 0, img.PixelWidth, img.PixelHeight));
                    img.Unlock();
                    _dirtyCount++;
                }
                catch (Exception e)
                {
                    _dirtyError = e.GetType().Name + ": " + e.Message;
                    Debug.WriteLine(e);
                }

                if (!_scrubbing)
                {
                    // 必须压住 seek 回写！MediaPosition 的 setter 在 _suppressSeek=false
                    // 时会调 engine.Seek()，而这里是「引擎报告自己播到哪了」。
                    // 不压住就等于每渲染一帧就 seek 回当前位置一次：解码器被反复 flush、
                    // 预卷帧被全部丢弃，画面永远停在原地，进度条也几乎不动。
                    _suppressSeek = true;
                    try
                    {
                        MediaPosition = (long)engine.Position.Ticks;
                    }
                    finally
                    {
                        _suppressSeek = false;
                    }
                }
            }));
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref _framePending, 0);
            _dirtyError = "BeginInvoke: " + e.Message;
        }
    }

    private void StopEngine()
    {
        EngineLog("stop engine");
        var engine = _engine;
        _engine = null;
        _engineActive = false;

        if (engine != null)
            engine.FrameRendered -= OnEngineFrame;

        // 顺序很重要：先把 D3DImage 从共享表面上摘下来，再释放引擎。
        // 反过来的话，WPF 还拿着一个已经被 Dispose 的 IDirect3DSurface9，
        // 驱动侧的引用计数永远清不干净——每预览一次就漏一份显卡内存。
        if (_d3dImage != null)
        {
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                _d3dImage.Unlock();
            }
            catch (Exception e) { Debug.WriteLine(e); }

            videoSurface.Source = null;
            _d3dImage = null;
        }

        if (engine != null)
        {
            try { engine.Dispose(); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        videoSurfaceHost.Visibility = Visibility.Collapsed;
        if (mediaElement != null) mediaElement.Visibility = Visibility.Visible;

        ScheduleRecycleIfNeeded();
    }

    // ============================================================ 空闲回收
    //
    // 显卡栈现在住在子进程（QuickLook.VideoEngineHost.exe）里，所以「回收」就是
    // 「把子进程关掉」——主程序不重启、托盘不闪、也不会再被游戏++当成「程序结束」。
    //
    // 时机：最后一个预览面板关掉、并且 30 秒内没有新的预览。
    // 看片过程中永远不会触发；期间又开了新片就重新计时。

    private static int _recycleToken;       // 每次重新计时 +1，旧的一次性计时器自然作废
    private static int _livePanels;         // 当前活着的预览面板数
    private static bool _gpuDirty;          // 这个进程里跑过 H/W 引擎吗

    /// <summary>关掉预览后等这么久才回收：给「连按空格翻片」留出空档。</summary>
    private const int RecycleIdleDelayMs = 30_000;

    private static void MarkPreviewStarted() => Volatile.Write(ref _gpuDirty, true);

    private static void EnterPanel()
    {
        // 新面板一进来就把正在排队的回收计时作废
        Interlocked.Increment(ref _recycleToken);
        Interlocked.Increment(ref _livePanels);
    }

    /// <summary>面板销毁时调用：最后一个面板走了才开始计时。</summary>
    private static void LeavePanel()
    {
        if (Interlocked.Decrement(ref _livePanels) <= 0)
            ScheduleRecycleIfNeeded();
    }

    private static void ScheduleRecycleIfNeeded()
    {
        // 这个进程压根没建过引擎，没什么好回收的
        if (!Volatile.Read(ref _gpuDirty)) return;

        // 进程内模式（IsolatedEngine=false）没有可关的东西，设备就住在主进程里
        if (!VideoEngineProxy.Enabled) return;

        // 上一次的计时不一定已经落地（连按空格翻片时，前一次排队的计时还在路上）。
        // 只认最后一次：token 对不上就直接作废，否则「最后一部片子关掉」这一次
        // 会因为前面那次计时抢先跑完而被吃掉。
        int token = Interlocked.Increment(ref _recycleToken);

        Task.Delay(RecycleIdleDelayMs).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref _recycleToken, 0, token) != token) return;
            if (Volatile.Read(ref _livePanels) > 0) return;
            if (!VideoEngineProxy.HostAlive) return;

            EngineLog($"recycle: 预览已关闭 {RecycleIdleDelayMs / 1000}s，关掉引擎宿主进程归还显卡资源");
            VideoEngineProxy.ShutdownHost();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        EngineLog("panel dispose");
        _disposed = true;

        // 静态事件不取消订阅的话，面板会被一直挂在委托链上（内存泄漏），
        // 而且关掉预览之后还可能收到回调。
        try { VideoEngine.VsrUnavailable -= OnVsrUnavailable; } catch { }

        var wnd = Window.GetWindow(this);
        if (wnd != null)
            wnd.Activated -= OnHostWindowActivated;

        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        SettingHelper.Set("VolumeDouble", LinearVolume, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("ShouldLoop", ShouldLoop, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        try
        {
            StopEngine();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        try
        {
            mediaElement?.Close();

            Task.Run(() =>
            {
                mediaElement?.MediaUriPlayer.Dispose();
                mediaElement = null;
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        try { CompositionTarget.Rendering -= OnCompositionTick; } catch { }

        _resizeTimer?.Stop();
        _resizeTimer = null;

        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;
        _midiPlayer?.Dispose();
        _midiPlayer = null;

        // 最后一个面板走了才开始算「闲置」：这时候才允许回收
        LeavePanel();
    }

    // ---------------------------------------------------------------- speed menu

    private void BuildSpeedMenu()
    {
        speedMenuItems.Children.Clear();

        foreach (double rate in SpeedSteps)
        {
            var item = new Button
            {
                Content = rate.ToString("0.0#", CultureInfo.InvariantCulture) + "x",
                Tag = rate,
                Height = 28,
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 13,
                Foreground = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
            };

            // 圆角、无边框、悬停变亮 —— 与截图里的竖排菜单一致
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            item.Template = template;

            item.MouseEnter += (s, _) => { if (s is Button b && !IsCurrentRate((double)b.Tag)) b.Background = HoverBrush; };
            item.MouseLeave += (s, _) => { if (s is Button b && !IsCurrentRate((double)b.Tag)) b.Background = Brushes.Transparent; };
            item.Click += (s, _) => ApplyPlaybackRate((double)((Button)s).Tag);

            speedMenuItems.Children.Add(item);
        }

        RefreshSpeedMenuHighlight();
    }

    private bool IsCurrentRate(double rate) => Math.Abs(rate - _playbackRate) < 0.001;

    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));

    private void RefreshSpeedMenuHighlight()
    {
        if (speedMenuItems == null) return;

        foreach (object child in speedMenuItems.Children)
        {
            if (child is Button item && item.Tag is double rate)
                item.Background = IsCurrentRate(rate) ? SelectedBrush : Brushes.Transparent;
        }
    }

    private void ApplyPlaybackRate(double rate)
    {
        _playbackRate = rate;

        if (_engineActive)
        {
            _engine.Rate = rate;
        }
        else
        {
            try
            {
                mediaElement.SpeedRatio = rate;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        OnPropertyChanged(nameof(SpeedLabel));
        RefreshSpeedMenuHighlight();
        speedMenuLayer.Visibility = Visibility.Collapsed;

        SettingHelper.Set("PlaybackRate", rate, "QuickLook.Plugin.VideoViewer");
    }

    /// <summary>把弹出菜单放到触发按钮正上方，并夹在面板范围内。</summary>
    private void PositionPopup(FrameworkElement popup, FrameworkElement anchor)
    {
        if (popup == null || anchor == null) return;

        popup.Visibility = Visibility.Visible;
        popup.UpdateLayout();

        double pw = popup.ActualWidth;
        double ph = popup.ActualHeight;
        if (pw <= 0 || ph <= 0) return;

        Point origin = anchor.TranslatePoint(new Point(0, 0), this);
        double x = origin.X + anchor.ActualWidth / 2 - pw / 2;
        double y = origin.Y - ph - 8;

        x = Math.Max(4, Math.Min(x, ActualWidth - pw - 4));
        y = Math.Max(4, y);

        popup.HorizontalAlignment = HorizontalAlignment.Left;
        popup.VerticalAlignment = VerticalAlignment.Top;
        popup.Margin = new Thickness(x, y, 0, 0);
    }

    private void ToggleSpeedMenu(object sender, RoutedEventArgs e)
    {
        if (speedMenuLayer.Visibility == Visibility.Visible)
        {
            speedMenuLayer.Visibility = Visibility.Collapsed;
            ShowViedoControlContainer(null, null);
            return;
        }

        vsrMenuLayer.Visibility = Visibility.Collapsed;
        RefreshSpeedMenuHighlight();
        speedMenuLayer.Visibility = Visibility.Visible;
        PositionPopup(speedPopup, buttonSpeed);
        ShowViedoControlContainer(null, null);
    }

    private void SpeedMenuLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        speedMenuLayer.Visibility = Visibility.Collapsed;
        ShowViedoControlContainer(null, null);
    }

    // ------------------------------------------------------------- VSR menu

    /// <summary>
    /// NVIDIA adapter detection via the display class registry key.
    /// Cheap (no WMI), cached for the process lifetime.
    /// </summary>
    private static bool HasNvidiaGpu()
    {
        if (_nvidiaGpu.HasValue) return _nvidiaGpu.Value;

        bool found = false;
        try
        {
            const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classKey))
            {
                if (root != null)
                {
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (var dev = root.OpenSubKey(sub))
                        {
                            string provider = dev?.GetValue("ProviderName") as string;
                            if (provider != null &&
                                provider.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        _nvidiaGpu = found;
        return found;
    }

    private void BuildVsrMenu()
    {
        vsrQualityItems.Children.Clear();
        vsrScaleItems.Children.Clear();

        // 左列：质量（含「关闭」）
        vsrQualityItems.Children.Add(CreateMenuHeader("质量"));
        string[] qualityNames = { "关闭", "低", "中", "高", "极高" };
        for (int i = 0; i < qualityNames.Length; i++)
        {
            var item = CreateMenuButton(qualityNames[i]);
            item.Tag = i;                       // 0 = 关闭
            item.Click += (s, _) => ApplyVsrQuality((int)((Button)s).Tag);
            vsrQualityItems.Children.Add(item);
        }

        // 中列：超分输出的分辨率上限
        vsrScaleItems.Children.Add(CreateMenuHeader("超分辨率"));
        foreach (int cap in VsrCaps)
        {
            var item = CreateMenuButton(CapLabel(cap));
            item.Tag = cap;
            item.Click += (s, _) => ApplyVsrCap((int)((Button)s).Tag);
            vsrScaleItems.Children.Add(item);
        }

        // 右列：降采样算法
        vsrAlgoItems.Children.Add(CreateMenuHeader("压缩算法"));
        string[] algoNames = { "驱动", "Spline36", "Jinc3" };
        for (int i = 0; i < algoNames.Length; i++)
        {
            var item = CreateMenuButton(algoNames[i]);
            item.Tag = i;
            item.Click += (s, _) => ApplyVsrAlgo((int)((Button)s).Tag);
            vsrAlgoItems.Children.Add(item);
        }

        RefreshVsrMenuHighlight();
    }

    /// <summary>1080 → "1K"，1440 → "2K"，2160 → "4K"。</summary>
    private static string CapLabel(int cap) => cap >= 2160 ? "4K" : cap >= 1440 ? "2K" : "1K";

    private TextBlock CreateMenuHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        };
    }

    private Button CreateMenuButton(string text)
    {
        var item = new Button
        {
            Content = text,
            Height = 28,
            Margin = new Thickness(0, 1, 0, 1),
            FontSize = 13,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty,
            new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        item.Template = template;

        item.MouseEnter += (s, _) => ((Button)s).Background = HoverBrush;
        item.MouseLeave += (s, _) => RefreshVsrMenuHighlight();

        return item;
    }

    private void RefreshVsrMenuHighlight()
    {
        if (vsrQualityItems != null)
        {
            foreach (object child in vsrQualityItems.Children)
            {
                if (child is Button item && item.Tag is int quality)
                {
                    bool selected = quality == 0 ? !_vsrEnabled : _vsrEnabled && quality == _vsrQuality;
                    item.Background = selected ? SelectedBrush : Brushes.Transparent;
                }
            }
        }

        if (vsrScaleItems != null)
        {
            foreach (object child in vsrScaleItems.Children)
            {
                if (child is Button item && item.Tag is int cap)
                {
                    bool selected = _vsrEnabled && cap == _vsrCap;
                    item.Background = selected ? SelectedBrush : Brushes.Transparent;
                }
            }
        }

        if (vsrAlgoItems != null)
        {
            foreach (object child in vsrAlgoItems.Children)
            {
                if (child is Button item && item.Tag is int algo)
                    item.Background = algo == _vsrAlgo ? SelectedBrush : Brushes.Transparent;
            }
        }
    }

    /// <summary>0 = 关闭，1-4 = 低/中/高/极高。</summary>
    private void ApplyVsrQuality(int quality)
    {
        bool wasEnabled = _vsrEnabled;
        int previousCap = _vsrCap;

        if (quality <= 0)
        {
            _vsrEnabled = false;
        }
        else
        {
            _vsrEnabled = true;
            _vsrQuality = quality;
        }

        OnPropertyChanged(nameof(VsrLabel));
        RefreshVsrMenuHighlight();
        vsrMenuLayer.Visibility = Visibility.Collapsed;

        SettingHelper.Set("VsrEnabled", _vsrEnabled, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("VsrQuality", _vsrQuality, "QuickLook.Plugin.VideoViewer");

        // 只是换质量档、上限和开关都没变 → 直接改参数，不重建管线（重建会卡半秒）
        if (_engineActive && _engine != null && _vsrEnabled && wasEnabled && _engine.VsrActive &&
            previousCap == _vsrCap)
        {
            _engine.SetQuality(_vsrQuality);
            EngineLog($"quality={_vsrQuality} (live, 不重建)");
            return;
        }

        ApplyVsrToEngine();
    }

    private void ApplyVsrCap(int cap)
    {
        _vsrCap = cap;
        _vsrEnabled = true;     // 选倍率即视为开启

        OnPropertyChanged(nameof(VsrLabel));
        RefreshVsrMenuHighlight();
        vsrMenuLayer.Visibility = Visibility.Collapsed;

        SettingHelper.Set("VsrEnabled", _vsrEnabled, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("VsrCap", _vsrCap, "QuickLook.Plugin.VideoViewer");

        ApplyVsrToEngine();
    }

    /// <summary>
    /// 把 VSR 设置推到正在跑的引擎上。
    /// <para>
    /// 分两步：先 <see cref="IVideoEngine.Reconfigure"/>（换档位）；
    /// 只有当这个引擎里 NGX 还没初始化过时，才补一次 <see cref="IVideoEngine.ActivateVsr"/>。
    /// 全程不重建引擎、不重挂 D3DImage —— 所以不黑屏、不 seek、不左右跳。
    /// </para>
    /// </summary>
    private void ApplyVsrToEngine()
    {
        var engine = _engine;
        if (!_engineActive || engine == null)
            return;

        try
        {
            // 第一步：参数更新。共享表面尺寸不变，这里一个 D3D 调用都没有。
            engine.Reconfigure(_vsrCap, _vsrQuality, _vsrEnabled);

            // 第二步：这个引擎上还没初始化过 NGX，才真正初始化一次。
            // 初始化会把渲染线程停住几百毫秒（音频同步挂住），但不会丢帧、不会跳。
            if (_vsrEnabled && !engine.VsrReady)
            {
                var sw = Stopwatch.StartNew();
                bool ok = engine.ActivateVsr();
                EngineLog($"activate VSR {sw.ElapsedMilliseconds}ms ok={ok} " +
                          $"VSR={(engine.VsrActive ? "on" : "off")} err={engine.LastError}");
            }

            EngineLog($"apply  cap={CapLabel(_vsrCap)} quality={_vsrQuality} enabled={_vsrEnabled}  " +
                      $"→ {engine.OutputWidth}x{engine.OutputHeight}  VSR={(engine.VsrActive ? "on" : "off")}  " +
                      $"surface={engine.SurfaceWidth}x{engine.SurfaceHeight}");
        }
        catch (Exception e)
        {
            EngineLog("apply EXCEPTION " + e.Message);
        }
    }

    private void ApplyVsrAlgo(int algo)
    {
        _vsrAlgo = algo;

        if (_engine != null)
            _engine.DownscaleAlgo = algo;

        EngineLog($"algo={algo} (0=driver 1=spline36 2=jinc3)");
        RefreshVsrMenuHighlight();
        vsrMenuLayer.Visibility = Visibility.Collapsed;

        SettingHelper.Set("VsrAlgo", _vsrAlgo, "QuickLook.Plugin.VideoViewer");
    }

    private void ToggleVsrMenu(object sender, RoutedEventArgs e)
    {
        if (vsrMenuLayer.Visibility == Visibility.Visible)
        {
            vsrMenuLayer.Visibility = Visibility.Collapsed;
            ShowViedoControlContainer(null, null);
            return;
        }

        speedMenuLayer.Visibility = Visibility.Collapsed;
        RefreshVsrMenuHighlight();
        vsrMenuLayer.Visibility = Visibility.Visible;
        PositionPopup(vsrPopup, buttonVsr);
        ShowViedoControlContainer(null, null);
    }

    private void VsrMenuLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        vsrMenuLayer.Visibility = Visibility.Collapsed;
        ShowViedoControlContainer(null, null);
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {        // Clicking the video area also gives it keyboard focus, so arrow-key
        // shortcuts keep working after switching back from another window.
        Focus();

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var wnd = Window.GetWindow(this);
            // Do not allow dragging when the window is borderless (e.g. fullscreen)
            // or maximized (DragMove throws InvalidOperationException when maximized).
            if (wnd?.WindowStyle == WindowStyle.None || wnd?.WindowState == WindowState.Maximized)
                return;

            try
            {
                wnd?.DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore exception if mouse state changed unexpectedly during drag
            }
        }
    }

    private void ViewerPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;

        if (_startPending)
        {
            _startPending = false;
            string path = _pendingPath;
            _pendingPath = null;
            StartPlayback(path, _pendingRotation);
        }

        TryAutoFocus();
    }

    private void ViewerPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The panel stays hidden while the host shows its busy/loading overlay;
        // once it becomes visible the preview is ready and focus can be taken.
        if (e.NewValue is true)
        {
            TryAutoFocus();

            // 宿主把 busy 层收起来、面板真正可见之后，才第一次拿到布局尺寸。
            // 这里再补一次，确保共享表面按真实显示尺寸建立。
            if (_engineActive) ApplyTargetSize();
        }
    }

    private void OnHostWindowActivated(object sender, EventArgs e)
    {
        if (sender is Window wnd)
            wnd.Activated -= OnHostWindowActivated;
        _activatedSubscribed = false;

        TryAutoFocus();
    }

    /// <summary>
    /// Claim keyboard focus for video previews once all three conditions hold:
    /// the panel is visible (host busy overlay gone), the panel is enabled and
    /// the host window is active. Focusing earlier is silently ignored by WPF
    /// (invisible element) or overridden by the activation pipeline.
    /// </summary>
    private void TryAutoFocus()
    {
        if (!_autoFocusPending)
            return;

        if (!IsVisible || !IsEnabled)
            return;

        var wnd = Window.GetWindow(this);
        if (wnd == null)
            return;

        if (!wnd.IsActive)
        {
            if (!_activatedSubscribed)
            {
                _activatedSubscribed = true;
                wnd.Activated += OnHostWindowActivated;
            }
            return;
        }

        _autoFocusPending = false;

        Focus();
        // Re-assert after the input pipeline settles.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible && IsEnabled)
                Keyboard.Focus(this);
        }), DispatcherPriority.Input);
    }

    private void ViewerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Do not interfere when a modifier key is held, keeping system shortcuts intact.
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Left:
                Seek(-TimeSpan.FromSeconds(5).Ticks);
                ShowViedoControlContainer(null, null);
                e.Handled = true;
                break;

            case Key.Right:
                Seek(TimeSpan.FromSeconds(5).Ticks);
                ShowViedoControlContainer(null, null);
                e.Handled = true;
                break;

            case Key.Up:
                ChangeVolume(0.05d);
                ShowViedoControlContainer(null, null);
                e.Handled = true;
                break;

            case Key.Down:
                ChangeVolume(-0.05d);
                ShowViedoControlContainer(null, null);
                e.Handled = true;
                break;
        }
    }

    private void Seek(long deltaTicks)
    {
        if (_engineActive)
        {
            long engineTarget = _engine.Position.Ticks + deltaTicks;
            if (MediaDuration > 0)
                engineTarget = Math.Min(MediaDuration, engineTarget);
            _engine.Seek(TimeSpan.FromTicks(Math.Max(0, engineTarget)));
            return;
        }

        if (mediaElement == null || mediaElement.MediaDuration <= 0)
            return;

        var target = Math.Max(0, Math.Min(mediaElement.MediaDuration, mediaElement.MediaPosition + deltaTicks));
        mediaElement.MediaPosition = target;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void MediaOpened(object o, RoutedEventArgs args)
    {
        if (mediaElement == null)
            return;

        EngineLog($"media opened  pos={mediaElement.MediaPosition / 1e7:0.00}s " +
                  $"dur={mediaElement.MediaDuration / 1e7:0.00}s seekPending={_legacySeekSeconds:0.00}");

        HasVideo = mediaElement.HasVideo;

        // 从新引擎切过来时要把播放位置接上，否则一切换就回到片头。
        // MediaOpened 这一刻图刚建好，直接写 MediaPosition 会被播放器忽略，
        // 而每 100ms 连着重试会把 DirectShow 图 seek 崩（实测进程直接消失）——
        // 所以只安排**一次**补位，等图真的跑起来（600ms）再写。
        if (_legacySeekSeconds >= 0)
        {
            _legacySeekTarget = (long)(_legacySeekSeconds * 1e7);
            _legacySeekSeconds = -1;
            _seekGuardUntil = Environment.TickCount + 4000;
            _legacyReadyAt = Environment.TickCount + 600;
            _legacySeekDueAt = Environment.TickCount + 1200;

            // 注意：MediaPosition 的 setter 自己会下发 seek！这里只是想更新进度条，
            // 必须压住它——在 MediaOpened 这一刻 seek 会打在图还没跑起来的时候。
            _suppressSeek = true;
            try { MediaPosition = _legacySeekTarget; }
            finally { _suppressSeek = false; }
        }

        // Re-apply the chosen speed: a new graph starts at 1.0x.
        if (Math.Abs(_playbackRate - 1.0) > 0.001)
            ApplyPlaybackRate(_playbackRate);
        else
            RefreshSpeedMenuHighlight();

        // 切换路径时本来是暂停的，图开好之后再停住
        if (_legacyStartPaused)
        {
            _legacyStartPaused = false;
            try { mediaElement.Pause(); } catch { }
            IsPlaying = false;
        }

        _context.IsBusy = false;
    }

    private void MediaFailed(object sender, MediaFailedEventArgs e)
    {
        ((MediaUriElement)sender).Dispatcher.BeginInvoke(new Action(() =>
        {
            _context.ViewerContent = new TextBlock()
            {
                Text = e.Exception.ToString(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _context.IsBusy = false;
        }));
    }

    private void MediaEnded(object sender, RoutedEventArgs e)
    {
        if (mediaElement == null)
            return;

        mediaElement.MediaPosition = 0L;
        if (ShouldLoop)
        {
            IsPlaying = true;

            mediaElement.Play();
        }
        else
        {
            IsPlaying = false;

            mediaElement.Pause();
        }
    }

    private void ShowViedoControlContainer(object sender, MouseEventArgs e)
    {
        var show = (Storyboard)videoControlContainer.FindResource("ShowControlStoryboard");
        if (videoControlContainer.Opacity == 0d || videoControlContainer.Opacity == 1d)
            show.Begin();
    }

    private void AutoHideViedoControlContainer(object sender, EventArgs e)
    {
        if (!HasVideo)
            return;

        if (videoControlContainer.IsMouseOver)
            return;

        // 弹出菜单开着的时候，不要把控制栏和菜单一起收掉
        if (speedMenuLayer.Visibility == Visibility.Visible ||
            vsrMenuLayer.Visibility == Visibility.Visible)
            return;

        var hide = (Storyboard)videoControlContainer.FindResource("HideControlStoryboard");

        hide.Begin();
    }

    private void PlayerStateChanged(PlayerState oldState, PlayerState newState)
    {
        switch (newState)
        {
            case PlayerState.Playing:
                IsPlaying = true;
                break;

            case PlayerState.Paused:
            case PlayerState.Stopped:
            case PlayerState.Closed:
                IsPlaying = false;
                break;
        }
    }

    /// <summary>
    /// 从新引擎切到原播放器后，把位置补到切换前的那一刻。
    /// 只做一次：连续 seek 会让 DirectShow 在开图过程中反复重定位，实测会崩。
    /// </summary>
    private void ApplyLegacySeekIfPending()
    {
        if (_disposed || _legacySeekTarget < 0) return;

        long target = _legacySeekTarget;
        _legacySeekTarget = -1;
        _seekGuardUntil = Environment.TickCount + 800;
        _legacyReadyAt = Environment.TickCount + 600;

        try
        {
            if (mediaElement.MediaDuration <= 0) return;

            mediaElement.MediaPosition = target;
            EngineLog($"legacy seek target={target / 1e7:0.00}s " +
                      $"actual={mediaElement.MediaPosition / 1e7:0.00}s");
        }
        catch (Exception e) { Debug.WriteLine(e.Message); }
        finally
        {
            // 同步进度条，但别再触发一次 seek（setter 自己会下发）
            _suppressSeek = true;
            try { MediaPosition = target; }
            finally { _suppressSeek = false; }
        }
    }

    private void UpdateMeta(string path, MediaInfoNative info)
    {
        if (HasVideo)
            return;

        try
        {
            if (info == null)
                throw new NullReferenceException();

            var title = info.Get(StreamKind.General, 0, "Title");
            var artist = info.Get(StreamKind.General, 0, "Performer");
            var album = info.Get(StreamKind.General, 0, "Album");

            metaTitle.Text = !string.IsNullOrWhiteSpace(title) ? title : Path.GetFileName(path);
            metaArtists.Text = artist;
            metaAlbum.Text = album;

            // Extract cover art
            var coverData = info.Get(StreamKind.General, 0, "Cover_Data");
            var coverBytes = CoverDataExtractor.Extract(coverData);
            CoverArt = CoverDataExtractor.Extract(coverBytes);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            metaTitle.Text = Path.GetFileName(path);
            metaArtists.Text = metaAlbum.Text = string.Empty;
        }

        metaArtists.Visibility = string.IsNullOrEmpty(metaArtists.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        metaAlbum.Visibility = string.IsNullOrEmpty(metaAlbum.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var lyricPath = Path.ChangeExtension(path, ".lrc");

        // Stop previous timer if any.
        _resizeTimer?.Stop();
        _resizeTimer = null;

        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;

        if (File.Exists(lyricPath))
        {
            var buffer = File.ReadAllBytes(lyricPath);
            var encoding = CharsetDetector.DetectFromBytes(buffer).Detected?.Encoding ?? Encoding.Default;

            _lyricLines = [.. LrcHelper.ParseText(encoding.GetString(buffer))];
        }
        else
        {
            // Use embedded lyrics from MediaInfo if present.
            // Common tag: General/Lyrics (may contain LRC formatted content).
            var embeddedLyrics = info?.Get(StreamKind.General, 0, "Lyrics");

            // Only check whether the tag of lyrics is present by MediaInfo
            if (!string.IsNullOrWhiteSpace(embeddedLyrics))
            {
                var file = TagLib.File.Create(path);
                embeddedLyrics = file.Tag.Lyrics;

                // Check whether the tag of lyrics is present by TagLib#
                if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                {
                    _lyricLines = [.. LrcHelper.ParseText(embeddedLyrics)];
                }
            }
        }

        if (_lyricLines != null && _lyricLines.Length != 0)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _lyricTimer.Tick += (sender, e) =>
            {
                if (_lyricLines != null && _lyricLines.Length != 0)
                {
                    var lyric = LrcHelper.GetNearestLrc(_lyricLines, new TimeSpan(mediaElement.MediaPosition));
                    metaLyric.Text = lyric?.LrcText?.Trim();
                }
                else
                {
                    metaLyric.Text = null;
                    metaLyric.Visibility = Visibility.Collapsed;
                }
            };
            _lyricTimer.Start();

            metaLyric.Visibility = Visibility.Visible;
        }
        else
        {
            metaLyric.Visibility = Visibility.Collapsed;
        }
    }

    public double LinearVolume
    {
        get => mediaElement?.Volume ?? 1d;
        set
        {
            if (mediaElement != null)
                mediaElement.Volume = value;

            if (_engine != null)
                _engine.Volume = (float)Math.Max(0d, Math.Min(1d, value));

            OnPropertyChanged();
        }
    }

    private void ChangeVolume(double delta)
    {
        if (mediaElement == null)
            return;

        LinearVolume = Math.Max(0d, Math.Min(1d, LinearVolume + delta));
    }

    private void TogglePlayPause(object sender, EventArgs e)
    {
        if (_engineActive)
        {
            _engine.IsPaused = !_engine.IsPaused;
            IsPlaying = !_engine.IsPaused;
            return;
        }

        if (mediaElement == null)
            return;

        if (mediaElement.IsPlaying)
            mediaElement.Pause();
        else
            mediaElement.Play();
    }

    private void ToggleShouldLoop(object sender, EventArgs e)
    {
        ShouldLoop = !ShouldLoop;

        if (_engine != null)
            _engine.Loop = ShouldLoop;
    }

    private void ToggleHardwareAcceleration(object sender, EventArgs e)
    {
        UseHardwareAcceleration = !UseHardwareAcceleration;
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        // 没有视频在播（或还没开流）时只记标志位，下次开流生效
        if (!_hasVideo || string.IsNullOrEmpty(_currentPath) || !_isVideoFile)
        {
            HardwareAccelerationModeChanged(UseHardwareAcceleration);
            return;
        }

        SwitchPlaybackPath();
    }

    /// <summary>
    /// 运行中切换 S/W·H/W：停掉当前路径，用另一条从同一个位置接着播。
    /// 位置和播放状态必须带过去——切一下开关就回到片头是不能接受的。
    /// </summary>
    private void SwitchPlaybackPath()
    {
        string path = _currentPath;
        if (string.IsNullOrEmpty(path) || _disposed) return;

        bool fromEngine = _engineActive && _engine != null;
        double posSec = fromEngine
            ? _engine.Position.TotalSeconds
            : (_legacySeekTarget >= 0 ? _legacySeekTarget / 1e7 : mediaElement.MediaPosition / 1e7);
        bool wasPlaying = IsPlaying;

        // 位置已经取走，别再让泵补它
        _legacySeekTarget = -1;
        _seekGuardUntil = 0;
        _legacyReadyAt = Environment.TickCount + 1200;

        if (fromEngine)
        {
            StopEngine();
        }
        else
        {
            try { mediaElement.Pause(); } catch { }
            try { mediaElement.Close(); } catch { }
        }

        EngineLog($"switch path hw={UseHardwareAcceleration} " +
                  $"from={(fromEngine ? "engine" : "legacy")} " +
                  $"pos={posSec:0.00}s play={wasPlaying}");

        if (UseHardwareAcceleration && TryStartEngine(path, videoRotation.Angle))
        {
            _engine.Seek(TimeSpan.FromSeconds(posSec));
            _engine.IsPaused = !wasPlaying;
            IsPlaying = wasPlaying;
            return;
        }

        // 走到这里说明 H/W 起不来（或本来就是 S/W）——用原播放器接上。
        // 注意 UseHardwareAcceleration 仍是它自己的值：true 时 LAV 走硬解，
        // 这正是「H/W 但跑不了 VSR」的语义。
        StartLegacyPlayback(path, videoRotation.Angle, posSec, wasPlaying);
    }

    private void HardwareAccelerationModeChanged(bool enable)
    {
        try
        {
            var player = mediaElement?.MediaUriPlayer;
            if (player == null) return;

            if (mediaElement.Source == null)
            {
                // No source loaded yet – just store the flag for the next Open
                player.Dispatcher.BeginInvoke(() =>
                    player.EnableLAVHardwareAcceleration = enable);
                return;
            }

            // Dispatch to the player's own MTA thread.
            // ApplyHardwareAcceleration will call OpenSource() there, which
            // rebuilds the full graph (incl. EVR/VMR9 allocator) so that
            // NewAllocatorSurface fires and the WPF back buffer is refreshed.
            // Position + play state are restored inside ApplyHardwareAcceleration
            // via a MediaOpened callback.
            player.Dispatcher.BeginInvoke(() =>
                player.ApplyHardwareAcceleration(enable));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void LoadAndPlay(string path, MediaInfoNative info)
    {
        // Remember whether this preview contains a video stream; only video
        // previews take keyboard focus automatically (audio previews keep the
        // default behaviour so arrow keys still navigate files in Explorer).
        _isVideoFile = !string.IsNullOrWhiteSpace(info?.Get(StreamKind.Video, 0, "Format"));
        if (_isVideoFile)
            _autoFocusPending = true;

        // Detect whether it is other playback formats
        if (!HasVideo)
        {
            string audioCodec = info?.Get(StreamKind.Audio, 0, "Format");

            if (audioCodec?.Equals("MIDI", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                _midiPlayer = new MidiPlayer(this, _context);
                _midiPlayer.LoadAndPlay(path);
                return; // Midi player will handle the playback at all
            }
        }

        UpdateMeta(path, info);

        // detect rotation
        _ = double.TryParse(info?.Get(StreamKind.Video, 0, "Rotation"), out var rotation);
        // Correct rotation: on some machine the value "90" becomes "90000" by some reason
        if (rotation > 360d)
            rotation /= 1e3;

        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        LinearVolume = Math.Max(0d, Math.Min(1d, SettingHelper.Get("VolumeDouble", 1d, "QuickLook.Plugin.VideoViewer")));

        _currentPath = path;

        // 引擎需要宿主窗口的 HWND（D3D9Ex 共享表面用），面板还没进可视树就先挂起
        var hostWindow = Window.GetWindow(this);
        if (_isVideoFile && (hostWindow == null || new WindowInteropHelper(hostWindow).Handle == IntPtr.Zero))
        {
            _startPending = true;
            _pendingPath = path;
            _pendingRotation = rotation;
            return;
        }

        StartPlayback(path, rotation);
    }

    /// <summary>
    /// 两条播放路径，由 S/W·H/W 开关决定：
    /// <list type="bullet">
    /// <item>H/W → 新引擎（D3D11 + DXVA），开流时顺便探测 VSR 能力；</item>
    /// <item>S/W → 原播放器（WPFMediaKit + LAV 软解），一个 D3D 对象都不建。</item>
    /// </list>
    /// 引擎起不来、或者这台机器跑不了 VSR，都退回原播放器——
    /// 后者仍按开关决定 LAV 走硬解还是软解。
    /// </summary>
    private void StartPlayback(string path, double rotation)
    {
        if (_disposed) return;

        if (_isVideoFile && UseHardwareAcceleration && TryStartEngine(path, rotation))
            return;

        StartLegacyPlayback(path, rotation, -1);
    }

    /// <summary>
    /// 原播放器路径。<paramref name="seekSeconds"/> 小于 0 表示从头播。
    /// 软/硬解由开关决定：H/W 走 LAV 硬解，S/W 走软解。
    /// </summary>
    private void StartLegacyPlayback(string path, double rotation, double seekSeconds, bool play = true)
    {
        if (_disposed) return;

        if (Math.Abs(rotation) > 0.1d)
            mediaElement.LayoutTransform = new RotateTransform(rotation, 0.5d, 0.5d);

        // 换路径时旧图还开着的话，LAV 的开关不会生效——必须先关掉再设标志。
        try { mediaElement.Close(); } catch { }

        // Source 必须清空：HardwareAccelerationModeChanged 看到 Source != null
        // 会走 ApplyHardwareAcceleration，那是「关掉再重开当前源」，
        // 会跟我们马上要设的新源打架——实测表现是刚 seek 好的位置被重置回 0。
        mediaElement.Source = null;

        // Source 为 null 时这个调用只是把标志位排进播放器线程的队列，
        // 排在后面的 OpenSource 之前，顺序有保证。
        HardwareAccelerationModeChanged(UseHardwareAcceleration);

        _legacySeekSeconds = seekSeconds;
        _legacyStartPaused = !play;

        mediaElement.Source = new Uri(path);
        mediaElement.Play();
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
