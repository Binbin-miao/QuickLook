// VideoEngine —— QuickLook 视频预览的新渲染内核
//
// 链路：
//   MF(DXVA) 解码 → NV12(D3D11) → 视频处理器转 BGRA → [NGX VSR 超分] → [降采样] → D3D9Ex 共享表面 → WPF D3DImage
//   音频走独立的 SourceReader → PCM → [WSOLA 变速不变调] → XAudio2
//
// 设计取舍：
//   * 音视频用两个 SourceReader：互不阻塞，音频不会因为视频慢而爆队列
//   * 音频时钟是主时钟（SamplesPlayed），视频按它节流；音频不可用时退回系统时钟
//   * 解码在独立线程，UI 线程只做 D3DImage.AddDirtyRect
//   * VSR 不可用时自动退化为纯 VpBlt（不影响播放）
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SharpDX.MediaFoundation;
using D3D9 = SharpDX.Direct3D9;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal static class VsrNative
{
    private const string Dll = "VsrBridge.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    private static bool _prepared;
    private static bool _usable;

    /// <summary>
    /// 进程级闩锁：一旦确认这台机器用不了 VSR（非 N 卡 / 老 N 卡 / 缺组件），
    /// 后续所有引擎、所有视频都不再尝试。重启进程自然清空——
    /// 用户升了驱动再打开就能重试。
    /// </summary>
    public static bool Unavailable { get; set; }

    /// <summary>组件缺失的具体原因，用于日志和按钮提示。</summary>
    public static string PrepareError { get; private set; }

    /// <summary>
    /// NGX 内部按裸文件名加载 nvngx_vsr.dll，插件目录不在默认搜索路径里。
    /// 这里先用完整路径把它载进进程，之后 NGX 再 LoadLibrary 就命中已加载模块。
    /// <para>
    /// 返回值只代表「桥接层和 NGX 运行库都在」，不代表这台机器支持 VSR——
    /// 那要等 VsrInit 里 NGX 自己回答。加载失败不再静默吞掉：
    /// 缺文件时必须让上层知道原因，否则用户只会看到「点了没反应」。
    /// </para>
    /// </summary>
    public static bool Prepare()
    {
        if (_prepared) return _usable;
        _prepared = true;

        try
        {
            string dir = System.IO.Path.GetDirectoryName(typeof(VsrNative).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                PrepareError = "无法定位插件目录";
                return false;
            }

            // 桥接层缺失 = P/Invoke 会抛 DllNotFoundException，必须先拦住
            string bridge = System.IO.Path.Combine(dir, "VsrBridge.dll");
            if (!System.IO.File.Exists(bridge))
            {
                PrepareError = "缺少 VsrBridge.dll";
                return false;
            }

            string ngx = System.IO.Path.Combine(dir, "nvngx_vsr.dll");
            if (!System.IO.File.Exists(ngx))
            {
                // 不直接判定失败：NGX 也可能从系统目录找到一份。
                // 但记下原因，VsrInit 失败时它就是最可能的解释。
                PrepareError = "缺少 nvngx_vsr.dll";
            }
            else if (LoadLibraryW(ngx) == IntPtr.Zero)
            {
                PrepareError = "nvngx_vsr.dll 加载失败 (err=" + Marshal.GetLastWin32Error() + ")";
            }

            _usable = true;
            return true;
        }
        catch (Exception e)
        {
            PrepareError = e.Message;
            Debug.WriteLine(e);
            return false;
        }
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VsrInit(IntPtr device);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VsrIsReady();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VsrEvaluate(IntPtr ctx, IntPtr input, int inW, int inH,
                                         IntPtr output, int outW, int outH, int quality);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr VsrLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void VsrShutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VsrAdapterVendor(IntPtr device);

    // ── 色彩空间 v1（ID3D11VideoContext1）────────────────────────────────────
    // SharpDX 没有 v1 的绑定，所以这三个都走 C++ 桥。
    // 参数是语义化的整数（0=BT601/1=BT709/2=BT2020，0=G22/1=PQ/2=HLG），
    // 由 C++ 侧映射成 DXGI_COLOR_SPACE_TYPE——避免 C# 传错枚举值。
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VpHasColorSpace1(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VpSetStreamColorSpace1(IntPtr context, IntPtr vp, int streamIndex,
        int rgb, int fullRange, int matrix, int transfer);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int VpSetOutputColorSpace1(IntPtr context, IntPtr vp,
        int rgb, int fullRange, int matrix, int transfer);

    // 桥接层返回的是宽字符：源码是 UTF-8 编译，中文若走 char* 再按 ANSI 解码就是乱码。
    public static string LastError => Marshal.PtrToStringUni(VsrLastError()) ?? string.Empty;
}

public sealed class VideoEngine : IVideoEngine
{
    // ---------- 配置 ----------
    /// <summary>
    /// 输出高度的上限（1K=1080 / 2K=1440 / 4K=2160）。
    /// 用户选的是「超分结果最多到这个分辨率」，不是倍率——倍率由引擎按源的
    /// 高度和显示尺寸自己算（见 <see cref="EffectiveVsrScale"/>）。
    /// </summary>
    public int VsrCapHeight { get; set; } = 2160;
    public int VsrQuality { get; set; } = 3;        // 1-4
    public bool VsrEnabled { get; set; } = true;
    public int DownscaleAlgo { get; set; }           // 0 = 驱动 VpBlt, 1 = Spline36
    public bool Loop { get; set; } = true;

    /// <summary>
    /// true = 只产出「共享句柄」，不建 D3D9Ex 设备（子进程模式）。
    /// <para>
    /// 子进程里建 D3D9Ex 共享表面会在**第二张**上吊死：同样的代码在主进程里好好的，
    /// 只有在「DXVA 正在解码 + 进程里没有 WPF」时必现（实测卡 30s 以上不返回）。
    /// 改用 D3D11 的共享纹理就没这问题，父进程照样能用 D3D9Ex 打开它。
    /// </para>
    /// </summary>
    public bool SharedOutputOnly { get; set; }

    // ---------- 状态 ----------
    public TimeSpan Duration { get; private set; }
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }
    public bool VsrActive { get; private set; }
    /// <summary>设备是否已打开 D3D11 多线程保护（MF 解码线程与渲染线程共用设备的前提）。</summary>
    public bool MultithreadProtected { get; private set; }
    public bool AudioAvailable => _audio != null && _audio.IsReady;
    public string LastError { get; private set; }

    /// <summary>每渲染完一帧触发（在解码线程上）。UI 侧应 Dispatcher 回主线程再 AddDirtyRect。</summary>
    public event Action FrameRendered;

    /// <summary>
    /// 引擎日志出口。插件里接到面板的文件日志；子进程宿主接到自己的日志文件。
    /// 以前直接调 ViewerPanel.EngineLog，等于引擎反过来依赖面板，
    /// 子进程宿主就编译不过（探针工程当年就是被这一行卡住的）。
    /// </summary>
    public static Action<string> LogSink { get; set; }

    internal static void Log(string message)
    {
        try { LogSink?.Invoke(message); } catch { }
    }

    /// <summary>给代理用：子进程判定 VSR 不可用时，把进程级闩锁打上并通知面板。</summary>
    internal static void RaiseVsrUnavailable(string reason)
    {
        VsrUnsupported = true;
        VsrUnsupportedReason = reason;
        VsrUnavailable?.Invoke(reason);
    }

    // 下面这五个都是**借来的**：设备由 GpuContext 全进程共用，引擎只是借用。
    // 绝不能在这里 Dispose —— 一是别的引擎（关掉一个预览、立刻开下一个）正在用，
    // 二是给过 MF 硬解的设备本来也放不掉（见 GpuContext 顶部注释）。
    private D3D11Device _device;
    private DeviceContext _ctx;
    private VideoDevice _videoDevice;
    private VideoContext _videoContext;
    private D3D9.Direct3DEx _d3d9;
    private D3D9.DeviceEx _device9;
    private IntPtr _sharedHandle;
    private D3D9.Texture _texture9;
    private D3D9.Surface _surface9;
    private D3D11Texture2D _sharedTex;

    private SourceReader _reader;
    /// <summary>借来的：由 GpuContext 持有，不要 Dispose。</summary>
    private DXGIDeviceManager _dxgiManager;
    private int _srcW, _srcH;      // 真实画面尺寸（可能比解码纹理小）
    private int _texW, _texH;      // 解码纹理尺寸（高度按 16 对齐填充过）

    private D3D11Texture2D _bgraTex;
    private D3D11Texture2D _vsrOutTex;
    /// <summary>双层 VSR 的中间结果。和 _vsrOutTex 一样按 4K 上限一次性分配。</summary>
    private D3D11Texture2D _vsrMidTex;
    private ShaderResourceView _bgraSrv;
    private ShaderResourceView _vsrSrv;
    private int _bgraW, _bgraH;

    /// <summary>
    /// 实验性：把一次超分拆成两级（倍率开平方，保证乘积等于总倍率）。
    /// 默认关闭——理论上级联比单级差，靠 QL_VSR2=1 打开做对比测试。
    /// </summary>
    public bool VsrTwoStage { get; set; } =
        Environment.GetEnvironmentVariable("QL_VSR2") == "1";

    /// <summary>复用的解码缓冲（每帧覆盖写），尺寸/格式变了才重建。</summary>
    private D3D11Texture2D _decodeTex;
    private int _decodeW, _decodeH;
    private Format _decodeFmt;

    // 最终缩放：VSR 输出 → 显示尺寸
    private D3D11Texture2D _outTex;
    private VideoProcessorOutputView _vpOutFinal;
    private Scaler _scaler;

    // ── HDR ────────────────────────────────────────────────────────────────
    /// <summary>把 PQ/HLG 压到 SDR。只有 HDR 源才创建。</summary>
    private ToneMapper _toneMap;
    /// <summary>本片是否要跑 tone map。</summary>
    private bool _hdrToneMap;
    /// <summary>传给 tone map 着色器的传递函数：1=PQ，2=HLG。</summary>
    private int _toneMapTransfer = 1;
    private string _toneMapError;

    /// <summary>HDR 峰值亮度（nits）。HDR10 主流是 1000，先按这个压。</summary>
    private const float HdrPeakNits = 1000f;
    /// <summary>SDR 参考白（nits）。ITU-R BT.2408 建议 203。</summary>
    private const float HdrSdrWhiteNits = 203f;

    /// <summary>诊断用：把 SDR 内容强行当 PQ 跑一遍 tone map，验证管线通不通。</summary>
    private static bool ForceHdrToneMap =>
        Environment.GetEnvironmentVariable("QL_FORCE_HDR") == "1";
    private int _targetW, _targetH;
    private int _vsrMaxW, _vsrMaxH;     // VSR 输出纹理的固定尺寸（一次性按 4K 分配）
    private int _enumOutW, _enumOutH;   // 枚举器声明的最大输出尺寸
    private bool _ngxReady;

    private VideoProcessorEnumerator _vpEnum;
    private VideoProcessor _vp;
    private VideoProcessorInputView _vpIn;
    private VideoProcessorOutputView _vpOut;
    private int _vpW, _vpH;

    // ── 色彩空间 ────────────────────────────────────────────────────────────
    // NV12 是「YUV + 有限的取值区间」：电视范围（studio，16-235）或全范围（0-255）。
    // 这件事必须显式告诉视频处理器，否则驱动自己猜。实测 NVIDIA 在「未指定」时按
    // 全范围直通，于是 16-235 的片子整幅画面卡在 16..234：黑是 6% 的灰、白差一口气，
    // 观感就是「偏灰、黑色不黑」。原播放器（LAV 软解）会自己展开，所以两条路径对不上。
    //
    // 规则（没有元数据时按行业惯例）：
    //   * 矩阵：流里写了就照写；没写则 高度 ≥ 720 用 BT.709、否则 BT.601
    //   * 范围：流里写了就照写；没写一律按 studio（16-235）
    //   * 输出一律 RGB full（0-255），展开过的数据不能再被第二趟 Blt 压回去
    private int _mtYuvMatrix = -1;       // MF_MT_YUV_MATRIX，-1 = 流里没写
    private int _mtNominalRange = -1;    // MF_MT_VIDEO_NOMINAL_RANGE，-1 = 流里没写
    private int _mtTransfer = -1;        // MF_MT_TRANSFER_FUNCTION：15=PQ，16=HLG
    private int _mtPrimaries = -1;       // MF_MT_VIDEO_PRIMARIES：9=BT.2020
    private bool _csFullRange;           // 源是不是 0-255
    /// <summary>矩阵：0 = BT.601，1 = BT.709，2 = BT.2020-NCL。</summary>
    private int _csMatrix = 1;
    /// <summary>传递函数：0 = gamma 2.2（SDR），1 = PQ（HDR10），2 = HLG。</summary>
    private int _csTransfer;
    /// <summary>源是 BT.2020 原色（HDR 片基本都是）。</summary>
    private bool _csBt2020;

    /// <summary>走 v1（DXGI_COLOR_SPACE_TYPE）还是回退 v0（DXVA2_ExtendedFormat）。</summary>
    private bool _useColorSpace1;
    private bool _colorSpace1Probed;
    /// <summary>v1 调用失败后本次会话不再尝试，避免每帧抛/每帧失败。</summary>
    private bool _colorSpace1Broken;

    /// <summary>一旦拒绝色彩空间声明就永久关掉，不能每帧抛异常。</summary>
    private bool _colorSpaceUsable = true;
    private string _colorSpaceNote = "未判定";

    /// <summary>诊断用：当前判定到的色彩空间、走的哪代接口、声明是否成功。</summary>
    public string ColorSpaceInfo =>
        $"{_colorSpaceNote} 声明={(_colorSpaceUsable ? "ok" : "失败")} " +
        $"接口={(_useColorSpace1 && !_colorSpace1Broken ? "v1" : "v0")} " +
        $"hdr={(_hdrToneMap ? "on" : "off")} tm={(_toneMap != null ? "ready" : "null")} " +
        $"force={ForceHdrToneMap} tmErr={_toneMapError ?? "-"}";

    /// <summary>渲染与管线重建互斥：重建时不能有帧正在用旧纹理。</summary>
    private readonly object _renderLock = new();

    /// <summary>
    /// 渲染闸门。默认开着（true）。首次初始化 NGX 时 UI 线程会把它关上，
    /// 等渲染线程彻底离开 D3D 之后再初始化——两者同时碰设备必挂死。
    /// </summary>
    private readonly ManualResetEventSlim _renderGate = new(true);

    private Thread _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _paused;
    private double _rate = 1.0;
    private long _seekTicks = -1;
    /// <summary>上次 seek 的时刻（TickCount）：用来区分「用户还在拖」和「已经停手」。</summary>
    private int _lastSeekAt;
    /// <summary>停手之后还要追到的精确位置；-1 表示没有待办。</summary>
    private long _precisePending = -1;

    /// <summary>
    /// seek 之后解码器会从「前一个关键帧」开始吐帧，长 GOP 的片可能早十几秒。
    /// 这些帧必须丢弃而不是渲染，否则画面会闪一下从关键帧快进到目标位置。
    /// </summary>
    private long _dropBeforeHns = -1;
    private int _diagDropped;
    private long _videoPosHns;
    private readonly Stopwatch _sw = new();

    // 节流用的基准
    private long _wallOriginHns = -1;   // 墙钟基准 = 开始播放（或 seek 后）的第一帧时间戳
    private long _acLast = -1;          // 上一次读到的音频时钟，用来发现「时钟停住」
    private int _acSameSince;

    // ---------- 音频 ----------
    private SourceReader _aReader;
    private AudioOutput _audio;
    private TimeStretcher _stretch;
    private Thread _aThread;
    private volatile bool _aRunning;
    private readonly ManualResetEventSlim _aWake = new(false);
    private long _aSeekHns = -1;
    private volatile bool _aAwaitFirst = true;
    private volatile bool _audioOk;
    private volatile bool _useAudioClock;
    /// <summary>临时挂住音频线程（首次激活 VSR 时用，避免视频停住而音频继续跑）。</summary>
    private volatile bool _aHold;
    private int _aChannels = 2;
    private int _aBits = 16;
    private byte[] _aBytes = new byte[65536];
    private int _aBytesLen;
    private byte[] _aOut = new byte[65536];
    private short[] _aShort = new short[32768];
    private float[] _aFloat = new float[32768];
    private float[] _aFloatOut;
    private float _volume = 0.8f;
    private bool _muted;

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    // ---------- 诊断（探针用，正常路径零开销） ----------
    private volatile int _diagStage;
    private int _diagNull, _diagErr, _diagNoDxgi;
    private int _frameCount;
    private int _lastOutW, _lastOutH;   // 上一帧真正用的 VSR 输出尺寸（诊断用）

    /// <summary>诊断输出口。UI 侧接上 EngineLog，出问题才有据可查。</summary>
    public Action<string> DiagSink { get; set; }

    /// <summary>UI 侧的呈现状态（重绘次数、D3DImage 状态），拼进看门狗日志。</summary>
    public Func<string> DiagExtra { get; set; }

    private volatile bool _wdRunning;
    private Thread _wdThread;
    private string _lastLoggedError;

    /// <summary>
    /// 看门狗：一旦连续两秒没有新帧，或者 LastError 变了，就把现场写进日志。
    /// 渲染线程里的异常被 PlaybackLoop 吞掉了，没有这个就完全看不见。
    /// </summary>
    private void WatchdogLoop()
    {
        int last = 0;
        int idle = 0;
        int beats = 0;

        while (_wdRunning)
        {
            Thread.Sleep(1000);
            if (!_wdRunning) break;

            int now = Volatile.Read(ref _frameCount);
            string err = LastError;

            if (!string.IsNullOrEmpty(err) && err != _lastLoggedError)
            {
                _lastLoggedError = err;
                DiagSink?.Invoke("[error] " + err);
            }

            // 前 20 秒每秒打一次心跳：帧数 + 位置 + UI 重绘次数。
            // 一眼就能分清「引擎没出帧」和「引擎出了帧但 UI 没重绘」。
            if (beats < 20 && now > 0)
            {
                beats++;
                string extra = null;
                try { extra = DiagExtra?.Invoke(); } catch { }
                DiagSink?.Invoke($"[beat] frames={now} pos={Position.TotalSeconds:0.00}s " +
                                 $"vpos={_videoPosHns / 1e7:0.00}s aclock={(_useAudioClock ? 1 : 0)} " +
                                 $"vsr={(VsrActive ? "on" : "off")} out={_lastOutW}x{_lastOutH} " +
                                 $"algo={DownscaleAlgo} {extra}");
            }

            if (now == last && !_paused)
            {
                idle++;
                if (idle == 2) DiagSink?.Invoke("[stall] " + Diag);
            }
            else
            {
                if (idle >= 2) DiagSink?.Invoke($"[resume] frames={now}");
                idle = 0;
            }

            last = now;
        }
    }

    private static string StageName(int s)
    {
        switch (s)
        {
            case 1: return "step-entry";
            case 2: return "seek-apply";
            case 3: return "decode";
            case 30: return "decode-fail";
            case 31: return "decode-fail-parked";
            case 4: return "before-waitframe";
            case 40: return "dropping-preroll";
            case 5: return "enter-render";
            case 51: return "nv12->bgra(VpBlt)";   // 卡在这里 = 色彩转换那次 Blt
            case 52: return "ngx-evaluate";        // 卡在这里 = VSR 求值
            case 53: return "final-scale";         // 卡在这里 = 缩放（驱动或着色器）
            case 54: return "copy-to-shared";      // 卡在这里 = 拷到共享表面
            case 55: return "flush";               // 卡在这里 = 提交 GPU
            case 56: return "frame-done";
            default: return "?" + s;
        }
    }

    public string Diag
    {
        get
        {
            bool lockFree = Monitor.TryEnter(_renderLock, 0);
            if (lockFree) Monitor.Exit(_renderLock);
            return $"alive={_thread?.IsAlive ?? false} at={StageName(_diagStage)} " +
                   $"seek={_seekTicks} paused={_paused} aclock={_useAudioClock} " +
                   $"gate={_renderGate.IsSet} hold={_aHold} lockFree={lockFree} " +
                   $"frames={_frameCount} src={_srcW}x{_srcH} tex={_texW}x{_texH} " +
                   $"target={_targetW}x{_targetH} vsr={(VsrActive ? "on" : "off")} " +
                   $"out={OutputWidth}x{OutputHeight} " +
                   $"vpos={_videoPosHns / 1e7:0.00} null={_diagNull} err={_diagErr} nodxgi={_diagNoDxgi} " +
                   $"dropped={_diagDropped} dropBefore={Interlocked.Read(ref _dropBeforeHns) / 1e7:0.00} " +
                   $"e={LastError}";
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0f, Math.Min(1f, value));
            if (_audio != null) _audio.Volume = _volume;
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_audio != null) _audio.Muted = _muted;
        }
    }

    // ================================================================ open

    public bool Open(string path, IntPtr hwnd)
    {
        try
        {
            // 设备全进程共用。每预览新建一个的话，给过 MF 硬解之后驱动就不回收了，
            // 实测每关一次预览漏 55 个线程 + 250MB 显存 + 350MB 内存。
            if (!GpuContext.Acquire())
            {
                LastError = "显卡设备不可用: " + GpuContext.LastError;
                return false;
            }

            _device = GpuContext.Device;
            _ctx = GpuContext.Context;
            _videoDevice = GpuContext.VideoDevice;
            _videoContext = GpuContext.VideoContext;
            _dxgiManager = GpuContext.DxgiManager;

            // 问出「我们实际渲染用的这块卡」是哪家的。注意是设备上的适配器，
            // 不是系统里装了哪几块卡——双显卡笔记本上核显建了设备就不该进 NGX。
            try
            {
                using (var dxgiDev = _device.QueryInterfaceOrNull<SharpDX.DXGI.Device>())
                {
                    var adapter = dxgiDev?.Adapter;
                    if (adapter != null)
                    {
                        AdapterVendor = (uint)adapter.Description.VendorId;
                        AdapterName = adapter.Description.Description?.Trim() ?? string.Empty;
                    }
                }
            }
            catch (Exception e) { Debug.WriteLine("query adapter: " + e.Message); }

            // 这个设备同时被三方使用：我们的渲染线程、MF 的 DXVA 解码工作队列、
            // 以及 DXGIDeviceManager。D3D11 立即上下文不是线程安全的，
            // 不打开多线程保护时两边并发提交会导致驱动内部死锁——
            // 实测表现就是 seek 之后 _ctx.Flush() 永久阻塞（画面卡死）。
            // （GpuContext 建设备时已经开过；这里重开一次是幂等的，只为了拿它做诊断。）
            try
            {
                using (var mt = _device.QueryInterfaceOrNull<Multithread>())
                {
                    mt?.SetMultithreadProtected(true);
                    MultithreadProtected = mt != null;
                }
            }
            catch (Exception e) { Debug.WriteLine("SetMultithreadProtected: " + e.Message); }

            Log("open: 设备就绪，编译缩放着色器");
            _scaler = new Scaler(_device, _ctx);
            if (!_scaler.Prepare())
            {
                LastError = "缩放着色器编译失败: " + _scaler.LastError;
                Debug.WriteLine(LastError);
                _scaler.Dispose();
                _scaler = null;
            }

            if (SharedOutputOnly)
            {
                Log("open: 跳过 D3D9Ex 设备（子进程模式，只产共享句柄）");
            }
            else
            {
                Log("open: 创建 D3D9Ex 输出设备");
                CreateD3D9(hwnd);
            }

            Log("open: 打开 SourceReader");
            var attrs = new MediaAttributes(2);
            attrs.Set(SourceReaderAttributeKeys.D3DManager, _dxgiManager);

            _reader = new SourceReader(path, attrs);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using (var mt = new MediaType())
            {
                mt.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                mt.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, mt);
            }

            Log("open: 协商 NV12 输出");

            // 色彩元数据要在协商之后读：先问原生类型（H.264 的 VUI 写在它上面），
            // 再问协商后的 NV12 类型。很多片源（尤其是网络压制的 mp4）两个都没有。
            ReadColorMetadata();

            // 真实画面尺寸要在这里读：解码之后 MF 会把当前媒体类型换成对齐后的尺寸
            int mediaW = 0, mediaH = 0;
            try
            {
                using (var cur = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
                {
                    long fs = cur.Get(MediaTypeAttributeKeys.FrameSize);
                    mediaW = (int)(fs >> 32);
                    mediaH = (int)(fs & 0xFFFFFFFF);
                }
            }
            catch (Exception e) { Debug.WriteLine(e.Message); }

            if (!TryGetDuration())
                Duration = TimeSpan.Zero;

            // 先解一帧拿到真实尺寸
            Log("open: 解第一帧");
            if (!DecodeNext(out var firstTex, out int sub, out long ts))
            {
                LastError = "无法解码视频帧";
                return false;
            }

            var desc = firstTex.Description;
            _texW = desc.Width;
            _texH = desc.Height;

            // 解码纹理常按 16 对齐填充（例如 478 → 480），用媒体类型里的真实尺寸裁掉填充
            _srcW = _texW;
            _srcH = _texH;
            if (mediaW > 1 && mediaH > 1 && mediaW <= _texW && mediaH <= _texH)
            {
                _srcW = mediaW;
                _srcH = mediaH;
            }

            SourceWidth = _srcW;
            SourceHeight = _srcH;

            Log("open: 建管线");
            SetupPipeline();
            Log("open: 渲染第一帧");
            RenderFrame(firstTex, sub);
            // 不再 Dispose：firstTex 是引擎缓存的那张解码缓冲，下一帧还要用

            // 音频独立打开；失败不影响视频
            Log("open: 打开音频");
            OpenAudio(path);
            _audioOk = _audio != null && _audio.IsReady;
            _useAudioClock = _audioOk;

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            return false;
        }
    }

    /// <summary>
    /// 读源视频的色彩元数据。先看原生类型（编码器写进 VUI 的矩阵/范围会转成
    /// MF_MT_YUV_MATRIX / MF_MT_VIDEO_NOMINAL_RANGE），再看协商后的类型。
    /// 两处都读不到就保持 -1，留给 DecideColorSpace 按惯例判定。
    /// </summary>
    private void ReadColorMetadata()
    {
        _mtYuvMatrix = -1;
        _mtNominalRange = -1;
        _mtTransfer = -1;
        _mtPrimaries = -1;

        // 原生类型优先：它代表「作者写的」，NV12 类型是「我们要的」，后者有时会把
        // 色彩属性留在转换链上、有时直接丢光。
        for (int pass = 0; pass < 2; pass++)
        {
            MediaType mt = null;
            try
            {
                mt = pass == 0
                    ? _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0)
                    : _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);

                if (TryGetInt(mt, MediaTypeAttributeKeys.YuvMatrix, out int matrix))
                    _mtYuvMatrix = matrix;
                if (TryGetInt(mt, MediaTypeAttributeKeys.VideoNominalRange, out int range))
                    _mtNominalRange = range;
                if (TryGetInt(mt, MediaTypeAttributeKeys.TransferFunction, out int transfer))
                    _mtTransfer = transfer;
                if (TryGetInt(mt, MediaTypeAttributeKeys.VideoPrimaries, out int primaries))
                    _mtPrimaries = primaries;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[color] 读元数据(pass={pass})失败: {e.Message}");
            }
            finally
            {
                mt?.Dispose();
            }

            if (_mtYuvMatrix >= 0 && _mtNominalRange >= 0 &&
                _mtTransfer >= 0 && _mtPrimaries >= 0) break;
        }

        Log($"color: 元数据 矩阵={_mtYuvMatrix} 范围={_mtNominalRange} " +
            $"传递={_mtTransfer} 原色={_mtPrimaries}（-1 表示流里没写）");
    }

    /// <summary>读一个整型媒体属性；属性不存在时返回 false（MF 会抛异常，不能当成 0）。</summary>
    private static bool TryGetInt(MediaType mt, MediaAttributeKey<int> key, out int value)
    {
        value = 0;
        if (mt == null) return false;
        try
        {
            value = mt.Get(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 决定 NV12 输入该按哪种色彩空间解释。必须在 _srcH 已知之后调用。
    /// MF 的取值：MFVideoTransferMatrix_BT709=1 / BT601=2 / SMPTE240M=3；
    /// MFNominalRange_Normal=1（0-255，全范围）/ Wide=2（16-235，电视范围）。
    /// </summary>
    private void DecideColorSpace()
    {
        // 传递函数：MF 的 15 = SMPTE 2084(PQ)，16 = HLG（见 mfobjects.h）
        _csTransfer = _mtTransfer == 15 ? 1 : (_mtTransfer == 16 ? 2 : 0);
        // BT.2020 原色：HDR 片基本都是，SDR 片偶尔也有宽色域
        _csBt2020 = _mtPrimaries == 9;

        // 矩阵三态。MF 的取值见 mfobjects.h：1=709，2=601，3=240M，4/5=BT.2020
        switch (_mtYuvMatrix)
        {
            case 2: _csMatrix = 0; break;                 // BT.601
            case 4:
            case 5: _csMatrix = 2; break;                 // BT.2020-10 / 12
            case 1:
            case 3: _csMatrix = 1; break;                 // BT.709 / SMPTE240M
            default:
                // 流里没写：HDR 必然是 BT.2020；SD 按 601；其余按 709
                _csMatrix = _csBt2020 ? 2 : (_srcH < 720 ? 0 : 1);
                break;
        }

        // 没写范围时一律按电视范围：这是压制的绝对主流，也是「黑不黑」的关键。
        _csFullRange = _mtNominalRange == 1;

        string m = _csMatrix == 2 ? "BT.2020" : (_csMatrix == 0 ? "BT.601" : "BT.709");
        string t = _csTransfer == 1 ? "PQ(HDR10)" : (_csTransfer == 2 ? "HLG" : "gamma2.2");
        string r = _csFullRange ? "FULL(0-255)" : "STUDIO(16-235)";

        _colorSpaceNote = $"{_srcW}x{_srcH} {m} {r} {t}";

        Log($"color: 判定 {_colorSpaceNote}" +
            $"{(isHdr ? "  ★ HDR 源" : "")}" +
            $"{(_csFullRange ? "" : "  → 由驱动展开成 0-255")}");
    }

    /// <summary>源是不是 HDR（PQ 或 HLG）。tone mapping 是否介入看这个。</summary>
    private bool isHdr => _csTransfer >= 1;

    /// <summary>
    /// HDR 才准备 tone map，SDR 片这条路一步都不走。
    /// 失败就整体放弃（退回直通）：半开半闭的后果是画面发灰，还找不到原因。
    /// </summary>
    private void SetupToneMap()
    {
        if (!isHdr && !ForceHdrToneMap)
        {
            _hdrToneMap = false;
            return;
        }

        if (_toneMap != null)
        {
            _hdrToneMap = true;
            return;
        }

        var tm = new ToneMapper(_device, _ctx);
        if (!tm.Prepare())
        {
            _toneMapError = tm.LastError ?? "未知";
            Log("hdr: tone map 初始化失败，退回直通 —— " + _toneMapError);
            tm.Dispose();
            _hdrToneMap = false;
            return;
        }

        _toneMap = tm;
        _hdrToneMap = true;
        // 诊断模式：内容其实是 SDR，但按 PQ 解码——用它确认这个 pass 真的在跑。
        _toneMapTransfer = ForceHdrToneMap ? 1 : Math.Max(1, _csTransfer);

        Log($"hdr: tone map 就绪 transfer={(_toneMapTransfer == 1 ? "PQ" : "HLG")} " +
            $"peak={HdrPeakNits:0}nits white={HdrSdrWhiteNits:0}nits");
    }

    /// <summary>
    /// 给视频处理器的第 0 号输入流声明色彩空间。每趟 Blt 之前都要设一次，
    /// 因为同一个处理器会交替吃 NV12（第一趟）和 BGRA（缩放趟）。
    /// <paramref name="rgbInput"/> = true 表示这一趟吃的是已经转好的 RGB。
    /// 返回 false 表示这台机器不吃这一套，调用方照旧跑。
    /// </summary>
    private bool TrySetStreamColorSpace(VideoProcessor vp, bool rgbInput)
    {
        if (vp == null || !_colorSpaceUsable) return false;

        // v1 优先：DXGI_COLOR_SPACE_TYPE 语义明确，而且是唯一能表达 PQ / HLG 的接口。
        // v0 的 DXVA2_ExtendedFormat 传递函数枚举里根本没有这两项。
        if (_useColorSpace1 && !_colorSpace1Broken)
        {
            try
            {
                int rc = VsrNative.VpSetStreamColorSpace1(
                    _ctx.NativePointer, vp.NativePointer, 0,
                    rgbInput ? 1 : 0,
                    rgbInput ? 1 : (_csFullRange ? 1 : 0),
                    rgbInput ? 1 : _csMatrix,
                    rgbInput ? 0 : _csTransfer);

                if (rc == 0) return true;

                // 失败一次就不再试：每帧都失败比老实走 v0 更糟。
                _colorSpace1Broken = true;
                Log($"color: v1 设置输入失败 rc={rc}，本次会话退回 v0");
            }
            catch (Exception e)
            {
                _colorSpace1Broken = true;
                Log("color: v1 输入调用异常，退回 v0 —— " + e.Message);
            }
        }

        try
        {
            _videoContext.VideoProcessorSetStreamColorSpace(vp, 0, BuildColorSpace(rgbInput));
            return true;
        }
        catch (Exception e)
        {
            // 一次失败就永久关掉：每帧抛一次异常比颜色不对更糟。
            _colorSpaceUsable = false;
            LastError = "色彩空间设置失败: " + e.Message;
            Log("color: 设置失败，退回驱动默认行为 —— " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 探测这台机器有没有 ID3D11VideoContext1（Win10 15063+）。
    /// 只查一次：结果存下来，整个会话不再变。
    /// </summary>
    private void ProbeColorSpace1()
    {
        if (_colorSpace1Probed) return;
        _colorSpace1Probed = true;

        try
        {
            _useColorSpace1 = VsrNative.Prepare() &&
                              VsrNative.VpHasColorSpace1(_ctx.NativePointer) == 1;
        }
        catch (Exception e)
        {
            _useColorSpace1 = false;
            Log("color: v1 探测失败 —— " + e.Message);
        }

        Log($"color: 接口 = {(_useColorSpace1 ? "v1 (DXGI_COLOR_SPACE_TYPE)" : "v0 (DXVA2_ExtendedFormat，HDR 不可用)")}");
    }

    /// <summary>
    /// 组一个 DXVA2_ExtendedFormat。位域：
    ///   SampleFormat:8 | ChromaSubsampling:4 | NominalRange:3 | TransferMatrix:3 |
    ///   Lighting:4 | Primaries:5 | TransferFunction:5
    /// 只填真正影响观感的三项（色度采样、取值区间、矩阵），其余留 Unknown 交给驱动。
    /// </summary>
    private VideoProcessorColorSpace BuildColorSpace(bool rgbInput)
    {
        // NominalRange：1 = PC 全范围(0-255)，2 = TV 电视范围(16-235)
        int range = rgbInput ? 1 : (_csFullRange ? 1 : 2);
        // VideoTransferMatrix：1 = BT.709，2 = BT.601；RGB 输入没有矩阵概念。
        // 注意 v0 的枚举只到 SMPTE240M，**没有 BT.2020**——HDR 片在这里只能近似成 709，
        // 这也是「补 HDR 必须先上 v1」的直接原因。
        int matrix = rgbInput ? 0 : (_csMatrix == 0 ? 2 : 1);
        // VideoChromaSubsampling：1 = MPEG2（4:2:0，也就是 NV12）
        int chroma = rgbInput ? 0 : 1;

        int value = (0 << 0)            // SampleFormat = unknown（逐行由 SetStreamFrameFormat 管）
                  | (chroma << 8)
                  | (range << 12)
                  | (matrix << 15)
                  | (0 << 18)           // VideoLighting = unknown
                  | (0 << 22)           // VideoPrimaries = unknown
                  | (0 << 27);          // VideoTransferFunction = unknown

        return MakeColorSpace(value);
    }

    /// <summary>
    /// 这个版本的 SharpDX 没给 VideoProcessorColorSpace 暴露任何字段，但 COM 那边
    /// 要的就是 4 字节的 DXVA2_ExtendedFormat。直接按内存布局造一个，
    /// 既不用 unsafe，也不用猜字段名。
    /// </summary>
    private static VideoProcessorColorSpace MakeColorSpace(int value)
    {
        Type t = typeof(VideoProcessorColorSpace);
        int size = Marshal.SizeOf(t);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            // 整个结构先清零，再把有效位写进前 4 字节
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            if (size >= 4) Marshal.WriteInt32(p, 0, value);
            return (VideoProcessorColorSpace)Marshal.PtrToStructure(p, t);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private bool TryGetDuration()
    {
        try
        {
            long hns = _reader.GetPresentationAttribute(SourceReaderIndex.MediaSource,
                PresentationDescriptionAttributeKeys.Duration);
            if (hns > 0)
            {
                // MF 用 100ns 为单位，而 TimeSpan 的 1 tick 也正好是 100ns，直接对应
                Duration = TimeSpan.FromTicks(hns);
                return true;
            }
        }
        catch (Exception e) { Debug.WriteLine(e.Message); }
        return false;
    }

    // ================================================================ pipeline

    private void CreateD3D9(IntPtr hwnd)
    {
        _d3d9 = new D3D9.Direct3DEx();
        var present = new D3D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = D3D9.SwapEffect.Discard,
            DeviceWindowHandle = hwnd,
            PresentationInterval = D3D9.PresentInterval.Immediate,
            BackBufferFormat = D3D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };

        _device9 = new D3D9.DeviceEx(_d3d9, 0, D3D9.DeviceType.Hardware, hwnd,
            D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve,
            new[] { present });
    }

    /// <summary>输出尺寸 = 源尺寸 × VSR 倍率；共享表面按这个尺寸创建。</summary>
    private void SetupPipeline()
    {
        Log($"setup: 开始 {_srcW}x{_srcH} target={_targetW}x{_targetH}");
        DecideColorSpace();
        SetupToneMap();
        // 重配时先拆掉上一轮的资源
        _vpOut?.Dispose(); _vpOut = null;
        _vp?.Dispose(); _vp = null;
        _vpEnum?.Dispose(); _vpEnum = null;

        // 输出纹理按「4K 上限 + 当前源的宽高比」分配，换挡位不再重建任何东西。
        // 不能只按 3840 定宽：21:9 的源按 4K 高度超分会比 3840 更宽，
        // 那样 outW 会被单独夹住、outH 不会，画面直接变形。
        int capW = (int)Math.Round(_srcW * (2160.0 / Math.Max(1, _srcH)));
        _vsrMaxW = Math.Max(_srcW, Math.Max(3840, capW));
        _vsrMaxH = Math.Max(_srcH, 2160);

        double scale = EffectiveVsrScale();
        OutputWidth = Even(_srcW * scale);
        OutputHeight = Even(_srcH * scale);

        // NV12 → BGRA（源尺寸）
        _bgraW = _srcW;
        _bgraH = _srcH;
        _bgraTex = new D3D11Texture2D(_device, new Texture2DDescription
        {
            Width = _bgraW,
            Height = _bgraH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        _bgraSrv?.Dispose();
        _bgraSrv = new ShaderResourceView(_device, _bgraTex);

        // 枚举器按「输入/输出都会用到的最大尺寸」建，缩放趟才能用同一个处理器
        int maxOutW = Math.Max(_bgraW, Math.Max(OutputWidth, _targetW));
        int maxOutH = Math.Max(_bgraH, Math.Max(OutputHeight, _targetH));

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = _texW,
            InputHeight = _texH,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = maxOutW,
            OutputHeight = maxOutH,
            Usage = VideoUsage.PlaybackNormal,
        };
        Log("setup: 建枚举器");
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _vpEnum);
        Log("setup: 建视频处理器");
        _enumOutW = maxOutW;
        _enumOutH = maxOutH;
        _videoDevice.CreateVideoProcessor(_vpEnum, 0, out _vp);

        // 先探测有没有 v1 接口，再声明颜色。探测结果整个会话有效。
        ProbeColorSpace1();

        // 输出统一声明成 full-range RGB。不声明的话，驱动可能把「已经展开成 0-255」
        // 的数据再按电视范围理解一次，于是第二趟 Blt（VSR 降采样）又把它压回去。
        if (_colorSpaceUsable)
        try
        {
            bool done = false;

            if (_useColorSpace1 && !_colorSpace1Broken)
            {
                // 输出永远是 sRGB full range：tone mapping 之后的数据是标准 SDR。
                try
                {
                    done = VsrNative.VpSetOutputColorSpace1(
                        _ctx.NativePointer, _vp.NativePointer, 1, 1, 1, 0) == 0;
                }
                catch (Exception e)
                {
                    Log("color: v1 输出调用异常 —— " + e.Message);
                }
            }

            if (!done)
                _videoContext.VideoProcessorSetOutputColorSpace(_vp, BuildColorSpace(true));

            Log($"color: 输出 = RGB full (0-255)  接口={(_useColorSpace1 && !_colorSpace1Broken ? "v1" : "v0")}");
        }
        catch (Exception e)
        {
            // 输出侧失败只影响观感，不影响出帧，绝不能因此中断管线。
            Log("color: 输出色彩空间声明失败，沿用驱动默认 —— " + e.Message);
        }
        Log("setup: 建输出视图");
        _vpW = _bgraW;
        _vpH = _bgraH;
        _videoDevice.CreateVideoProcessorOutputView(_bgraTex, _vpEnum,
            new VideoProcessorOutputViewDescription
            {
                Dimension = VpovDimension.Texture2D,
                Texture2D = new Texture2DVpov { MipSlice = 0 },
            }, out _vpOut);

        // 这里**故意不**初始化 VSR。NGX 初始化实测 400ms 左右：
        //   VSR 关：Open + 首帧 = 620ms
        //   VSR 开：Open + 首帧 = 1050ms（多出来的就是它挡在首帧前面）
        // 现在是首帧先用普通路径出画面，UI 层在画面出来之后再调 ActivateVsr()
        // 补上超分。运行中切换 VSR 的机制本来就是好的（闸门 + 渲染锁），
        // 只是以前没人用它来「先出画面」。
        VsrActive = false;

        if (!VsrActive)
        {
            OutputWidth = _srcW;
            OutputHeight = _srcH;
        }

        if (_targetW <= 0 || _targetH <= 0)
        {
            // 默认必须按「源尺寸」而不是「VSR 输出尺寸」！
            // 拿 4K 当默认目标会让 WPF 每帧合成一张 4K 图，直接卡到不动。
            // 真正的显示尺寸由 UI 侧 SetTargetSize 定，这里只是保底。
            _targetW = _srcW;
            _targetH = _srcH;
        }

        Log("setup: 建输出资源");
        CreateOutputResources();
        Log("setup: 完成");
    }

    /// <summary>
    /// 实际生效的超分倍率。用户选的是「输出高度的上限」，这里换算成倍率，
    /// 只夹 NGX 规格的 4x，**不再按显示区域缩水**。
    /// <para>
    /// 以前这里还要再夹一个「不超过显示区域需要的倍数」，理由是「产出比屏幕还多的
    /// 像素只会被缩回去，白烧 GPU」。但那样 1080p 片在 1080p 屏上根本不会超分
    /// （倍率 0.955 &lt; 1），用户选 4K 档等于没反应。
    /// </para>
    /// <para>
    /// 现在按用户选的档位老实超分：1080p → 2160（2x）再降采样回屏幕，等于超采样，
    /// 细节和边缘会比直接缩放更干净。代价是 GPU 开销变成 4 倍左右——这是用户
    /// 明确要的取舍（「强制按档位超分」）。
    /// </para>
    /// </summary>
    private double EffectiveVsrScale()
    {
        if (!VsrEnabled) return 1.0;
        if (VsrCapHeight <= 0 || _srcH <= 0) return 1.0;

        double scale = VsrCapHeight / (double)_srcH;

        // NGX VSR 的规格上限是 4x。超过 4x 它不会更锐利，只会更慢甚至被拒绝。
        if (scale > 4.0) scale = 4.0;

        // 放大量小到只有几个像素时不值得开——1:1 的 evaluate 没有意义，
        // 有些驱动会直接返回失败，表现是「VSR 第一帧就自己关了」。
        if (scale <= 1.001) return 1.0;

        return scale;
    }

    /// <summary>
    /// 双层 VSR 的中间纹理。按 4K 上限一次性分配，之后换倍率不再重建
    /// （和 _vsrOutTex 同一个理由：重建就要碰管线，容易黑屏甚至卡死）。
    /// </summary>
    private bool EnsureVsrMidTex()
    {
        if (_vsrMidTex != null) return true;
        if (_vsrMaxW <= 0 || _vsrMaxH <= 0) return false;

        try
        {
            _vsrMidTex = new D3D11Texture2D(_device, new Texture2DDescription
            {
                Width = _vsrMaxW,
                Height = _vsrMaxH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            });
            Log($"vsr2: 中间纹理 {_vsrMaxW}x{_vsrMaxH} 就绪");
            return true;
        }
        catch (Exception e)
        {
            Log("vsr2: 中间纹理创建失败 —— " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 准备 NGX 和固定尺寸的 VSR 输出纹理。只在第一次开启时做一次，
    /// 之后换倍率/质量都不再触碰这里——这正是避免卡死和黑屏的关键。
    /// </summary>
    private void SetupVsrResources()
    {
        if (_vsrOutTex != null && _ngxReady) { VsrActive = true; return; }

        try
        {
            // 第一道：厂商。AMD / Intel 上根本不该走到 NGX，
            // 桥接层里也有一道同样的判断——这里是让失败来得更快、原因更清楚。
            if (!IsNvidiaAdapter)
            {
                MarkVsrUnsupported(string.Format(
                    "VSR 不可用: 非 NVIDIA 显卡 (vendor=0x{0:X4}{1})",
                    AdapterVendor,
                    string.IsNullOrEmpty(AdapterName) ? "" : " " + AdapterName));
                return;
            }

            // 第二道：组件是否齐全。缺 VsrBridge.dll 时 P/Invoke 会抛
            // DllNotFoundException，虽然外层能接住，但先拦住能给出准确原因。
            if (!VsrNative.Prepare())
            {
                MarkVsrUnsupported("VSR 组件缺失: " + (VsrNative.PrepareError ?? "未知原因"));
                return;
            }

            // 绝不能拿 VsrIsReady() 当「这个引擎就绪」的证据。
            // NGX 是进程级单例，feature 绑在上一个视频的 ID3D11Device 上；
            // 只看全局就绪标志会直接拿旧设备的 feature 去求值新设备的纹理——
            // 实测是访问冲突（进程直接消失）。一律交给 VsrInit 判断：
            // 同一个设备它立刻返回 0，换了设备它先关旧的再重开。
            int rc = VsrNative.VsrInit(_device.NativePointer);
            bool ready = rc == 0 && VsrNative.VsrIsReady() == 1;
            if (!ready)
            {
                // 第三道：NGX 自己的回答（老卡/驱动旧/显存不足）。
                // 这是唯一权威的判断，桥接层已经用 SEH 包住，不会崩。
                // PrepareError 优先：缺 nvngx_vsr.dll 时 NGX 只会含糊地说
                // 「unavailable (needDriver=0)」，真实原因在 Prepare 那一步就已经知道了。
                string detail = VsrNative.LastError;
                if (!string.IsNullOrEmpty(VsrNative.PrepareError))
                    detail = VsrNative.PrepareError + "；" + detail;
                LastError = "VSR 不可用: " + detail;
                MarkVsrUnsupported(LastError);
                return;
            }
            _ngxReady = ready;

            if (_vsrOutTex == null)
            {
                _vsrOutTex = new D3D11Texture2D(_device, new Texture2DDescription
                {
                    Width = _vsrMaxW,
                    Height = _vsrMaxH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                });
                _vsrSrv?.Dispose();
                _vsrSrv = new ShaderResourceView(_device, _vsrOutTex);
            }

            VsrActive = true;
        }
        catch (Exception e)
        {
            LastError = "VSR 初始化失败: " + e.Message;
            VsrActive = false;
        }
    }

    /// <summary>
    /// 记下「这台机器用不了 VSR」，并让 UI 有机会把按钮关掉。
    /// 只在进程内有效：用户升了驱动、重启播放器就会重新试一次。
    /// </summary>
    private void MarkVsrUnsupported(string reason)
    {
        VsrActive = false;
        _ngxReady = false;
        LastError = reason;
        VsrUnsupportedReason = reason;
        VsrUnsupported = true;
        VsrUnavailable?.Invoke(reason);
    }

    /// <summary>VSR 判定不可用时触发（在调用 ActivateVsr 的线程上）。</summary>
    public static event Action<string> VsrUnavailable;

    /// <summary>目标（显示）尺寸的中间纹理 + D3D9Ex 共享表面 + 缩放输出视图。</summary>
    private void CreateOutputResources()
    {
        _outTex?.Dispose(); _outTex = null;
        _sharedTex?.Dispose(); _sharedTex = null;
        _surface9?.Dispose(); _surface9 = null;
        _texture9?.Dispose(); _texture9 = null;
        _vpOutFinal?.Dispose(); _vpOutFinal = null;

        Log($"out: 建 D3D11 输出纹理 {_targetW}x{_targetH}");
        _outTex = new D3D11Texture2D(_device, new Texture2DDescription
        {
            Width = _targetW,
            Height = _targetH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        // 驱动缩放（VideoProcessorBlt）用的输出视图；建不出来就退回着色器。
        // 顺序很关键：先建这个视图，再去建 D3D9Ex 共享表面。
        // 反过来（先建共享表面、再建视频处理器视图）实测会卡死在
        // CreateVideoProcessorOutputView 里——而同一段代码在进程内跑不出问题，
        // 只在「另一个进程正开着上一张共享表面」时必现。
        Log("out: 建 VpBlt 输出视图");
        try
        {
            _videoDevice.CreateVideoProcessorOutputView(_outTex, _vpEnum,
                new VideoProcessorOutputViewDescription
                {
                    Dimension = VpovDimension.Texture2D,
                    Texture2D = new Texture2DVpov { MipSlice = 0 },
                }, out _vpOutFinal);
            Log("out: VpBlt 输出视图 ok");
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            Log("out: VpBlt 输出视图失败 " + e.Message);
            _vpOutFinal = null;
        }

        _sharedHandle = IntPtr.Zero;

        if (SharedOutputOnly)
        {
            // 子进程模式：直接建一张 D3D11 共享纹理，把老式 SharedHandle 交出去。
            // 父进程用 D3D9Ex CreateTexture(handle) 打开的就是同一块显存（跨进程实测通过）。
            Log($"out: 建 D3D11 共享纹理 {_targetW}x{_targetH}");
            _sharedTex = new D3D11Texture2D(_device, new Texture2DDescription
            {
                Width = _targetW,
                Height = _targetH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                OptionFlags = ResourceOptionFlags.Shared,
            });

            using (var res = _sharedTex.QueryInterface<SharpDX.DXGI.Resource>())
            {
                _sharedHandle = res.SharedHandle;
            }

            Log($"out: 共享句柄 0x{_sharedHandle.ToInt64():X}");
            return;
        }

        Log($"out: 建 D3D9Ex 共享表面 {_targetW}x{_targetH}");
        _texture9 = new D3D9.Texture(_device9, _targetW, _targetH, 1,
            D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref _sharedHandle);
        Log("out: 取 surface");
        _surface9 = _texture9.GetSurfaceLevel(0);
        Log("out: D3D11 打开共享资源");
        _sharedTex = _device.OpenSharedResource<D3D11Texture2D>(_sharedHandle);
        Log("out: 共享表面就绪");
    }

    /// <summary>
    /// 设置最终输出（显示）尺寸。返回 true 表示表面换了，调用方要重新挂 D3DImage。
    /// </summary>
    public bool SetTargetSize(int width, int height)
    {
        int w = Math.Max(2, Math.Min(8192, width));
        int h = Math.Max(2, Math.Min(8192, height));
        if (w == _targetW && h == _targetH) return false;

        lock (_renderLock)
        {
            _targetW = w;
            _targetH = h;

            // 输出视图不能大于枚举器声明的输出尺寸，否则驱动会直接崩。
            // 超了就整条管线按新尺寸重建。
            if (w > _enumOutW || h > _enumOutH)
                SetupPipeline();
            else
                CreateOutputResources();
        }
        return true;
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2.0) * 2);

    public IntPtr SurfacePointer => _surface9?.NativePointer ?? IntPtr.Zero;

    /// <summary>
    /// 输出表面的共享句柄。子进程模式下父进程拿它去 D3D9Ex CreateTexture，
    /// 打开的是同一块显存——画面零拷贝，不用走 CPU。
    /// </summary>
    public IntPtr SharedHandle => _sharedHandle;

    /// <summary>
    /// 诊断用：把共享表面抽样读回，返回一个校验和。
    /// 探针用它判断「送到屏幕的那张纹理到底有没有在变」。
    /// </summary>
    public long SharedChecksum()
    {
        var px = ReadSharedGrid(64);
        if (px == null) return -1;

        long sum = 0;
        foreach (byte b in px) sum = sum * 31 + b;
        return sum;
    }

    /// <summary>
    /// 诊断用：共享表面的「边缘能量」（相邻像素的平均亮度差）。
    /// 必须按原生像素算：抽样网格会把细节整个跳过去，测不出 AI 超分的差别。
    /// </summary>
    public double SharedEdgeEnergy()
    {
        if (_sharedTex == null || _targetW < 4 || _targetH < 4) return -1;

        lock (_renderLock)
        {
            D3D11Texture2D stage = null;
            try
            {
                stage = new D3D11Texture2D(_device, new Texture2DDescription
                {
                    Width = _targetW,
                    Height = _targetH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None,
                });

                _ctx.CopyResource(_sharedTex, stage);
                _ctx.Flush();

                var box = _ctx.MapSubresource(stage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    int rows = 96;                       // 均匀取 96 行
                    var line = new byte[_targetW * 4];
                    double sum = 0;
                    long count = 0;

                    for (int r = 0; r < rows; r++)
                    {
                        int y = (int)((long)r * _targetH / rows);
                        IntPtr row = IntPtr.Add(box.DataPointer, y * box.RowPitch);
                        Marshal.Copy(row, line, 0, _targetW * 4);

                        for (int x = 0; x + 1 < _targetW; x++)
                        {
                            int i = x * 4;
                            double l0 = 0.114 * line[i] + 0.587 * line[i + 1] + 0.299 * line[i + 2];
                            double l1 = 0.114 * line[i + 4] + 0.587 * line[i + 5] + 0.299 * line[i + 6];
                            sum += Math.Abs(l1 - l0);
                            count++;
                        }
                    }

                    return count == 0 ? -1 : sum / count;
                }
                finally
                {
                    _ctx.UnmapSubresource(stage, 0);
                }
            }
            catch (Exception e)
            {
                LastError = "读回共享表面失败: " + e.Message;
                return -1;
            }
            finally
            {
                stage?.Dispose();
            }
        }
    }

    /// <summary>
    /// 诊断用：把共享表面整帧导出成 .raw（前 8 字节是宽高，之后是 BGRA）。
    /// 用来做「同一帧 VSR 开/关」的像素级对比图。
    /// </summary>
    public bool DumpShared(string path)
    {
        if (_sharedTex == null || _targetW < 4 || _targetH < 4) return false;

        lock (_renderLock)
        {
            D3D11Texture2D stage = null;
            try
            {
                stage = new D3D11Texture2D(_device, new Texture2DDescription
                {
                    Width = _targetW,
                    Height = _targetH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None,
                });

                _ctx.CopyResource(_sharedTex, stage);
                _ctx.Flush();

                var box = _ctx.MapSubresource(stage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(_targetW);
                        bw.Write(_targetH);
                        var line = new byte[_targetW * 4];
                        for (int y = 0; y < _targetH; y++)
                        {
                            Marshal.Copy(IntPtr.Add(box.DataPointer, y * box.RowPitch), line, 0, line.Length);
                            bw.Write(line);
                        }
                    }
                    return true;
                }
                finally
                {
                    _ctx.UnmapSubresource(stage, 0);
                }
            }
            catch (Exception e)
            {
                LastError = "导出共享表面失败: " + e.Message;
                return false;
            }
            finally
            {
                stage?.Dispose();
            }
        }
    }

    /// <summary>把共享表面按 n×n 网格采样读回（BGRA）。失败返回 null。</summary>
    private byte[] ReadSharedGrid(int n)
    {
        if (_sharedTex == null || _targetW < n || _targetH < n) return null;

        lock (_renderLock)
        {
            D3D11Texture2D stage = null;
            try
            {
                stage = new D3D11Texture2D(_device, new Texture2DDescription
                {
                    Width = _targetW,
                    Height = _targetH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None,
                });

                _ctx.CopyResource(_sharedTex, stage);
                _ctx.Flush();

                var box = _ctx.MapSubresource(stage, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    var px = new byte[n * n * 4];

                    for (int y = 0; y < n; y++)
                    {
                        int sy = Math.Min(_targetH - 1, y * _targetH / n);
                        IntPtr row = IntPtr.Add(box.DataPointer, sy * box.RowPitch);

                        for (int x = 0; x < n; x++)
                        {
                            int sx = Math.Min(_targetW - 1, x * _targetW / n);
                            int o = (y * n + x) * 4;
                            px[o] = Marshal.ReadByte(row, sx * 4);
                            px[o + 1] = Marshal.ReadByte(row, sx * 4 + 1);
                            px[o + 2] = Marshal.ReadByte(row, sx * 4 + 2);
                            px[o + 3] = 255;
                        }
                    }
                    return px;
                }
                finally
                {
                    _ctx.UnmapSubresource(stage, 0);
                }
            }
            catch (Exception e)
            {
                LastError = "读回共享表面失败: " + e.Message;
                return null;
            }
            finally
            {
                stage?.Dispose();
            }
        }
    }

    /// <summary>共享表面的真实尺寸（= 显示尺寸，不是 VSR 输出尺寸）。</summary>
    public int SurfaceWidth => _targetW;
    public int SurfaceHeight => _targetH;

    /// <summary>NGX 是否已经在当前引擎里初始化过（初始化过一次就再也不用重建）。</summary>
    public bool VsrReady => _ngxReady && _vsrOutTex != null;

    /// <summary>渲染设备的适配器厂商号（0x10DE = NVIDIA，0 = 没查到）。</summary>
    public uint AdapterVendor { get; private set; }

    /// <summary>适配器名字，仅用于日志。</summary>
    public string AdapterName { get; private set; } = string.Empty;

    /// <summary>VSR 只跑在 NVIDIA 卡上。别的厂商连碰都不能碰 NGX。</summary>
    public bool IsNvidiaAdapter => AdapterVendor == 0x10DE;

    /// <summary>
    /// 进程级「这台机器用不了 VSR」闩锁。
    /// 一旦 NGX 明确回答不可用（老卡、驱动过旧、缺运行库），就不再重试：
    /// 每次重试都要走一遍 NGX，慢且没有意义。
    /// </summary>
    public static bool VsrUnsupported { get; private set; }

    /// <summary>VSR 不可用的具体原因（给 UI 做提示用）。</summary>
    public static string VsrUnsupportedReason { get; private set; }

    /// <summary>
    /// 运行时改 VSR 参数（菜单里切换质量/上限/开关）。**零 D3D 调用**，瞬时返回。
    /// 如果 NGX 已经在当前引擎里就绪，连开关都是瞬时的；
    /// 只有在还没初始化过时才需要额外调 <see cref="ActivateVsr"/>。
    /// </summary>
    public bool Reconfigure(int capHeight, int quality, bool enabled)
    {
        VsrCapHeight = capHeight;
        VsrQuality = quality;
        VsrEnabled = enabled;

        // 注意必须是 else 而不是 else if：上限不够大时 EffectiveVsrScale()==1.0，
        // 用 else if 会让 VsrActive 保持上一次的 true，于是拿「输入==输出」去
        // 调 NGX，轻则 evaluate 失败、重则整条链路卡住。
        if (!enabled)
            VsrActive = false;
        else
            VsrActive = VsrReady && EffectiveVsrScale() > 1.0;

        double effective = EffectiveVsrScale();
        OutputWidth = Even(_srcW * effective);
        OutputHeight = Even(_srcH * effective);

        _wake.Set();
        return false;
    }

    /// <summary>
    /// 首次在当前引擎上开启 VSR。必须在 UI 线程调用。
    /// <para>
    /// 做法：把渲染闸门关上 → 拿到渲染锁（保证渲染线程已经离开设备）→
    /// 顺手挂住音频线程 → 在这条线程上初始化 NGX → 全部放开。
    /// </para>
    /// <para>
    /// 这样既不用重建引擎（不会黑屏、不会 seek、不会左右跳），
    /// 又不会出现"渲染线程和初始化同时碰设备"导致的挂死。
    /// </para>
    /// </summary>
    public bool ActivateVsr()
    {
        if (!VsrEnabled || EffectiveVsrScale() <= 1.0) return false;

        // 已经确认这台机器用不了，就别再走一遍 NGX 了
        if (VsrUnsupported)
        {
            LastError = VsrUnsupportedReason ?? "VSR 不可用";
            return false;
        }

        if (VsrReady)
        {
            VsrActive = true;
            _wake.Set();
            return true;
        }

        _renderGate.Reset();
        _aHold = true;

        bool locked = false;
        try
        {
            // 等当前这一帧走完。超时只是保险，正常应该几毫秒内拿到。
            locked = Monitor.TryEnter(_renderLock, 4000);
            if (!locked)
            {
                LastError = "VSR 激活超时：渲染线程没有让出设备";
                return false;
            }

            SetupVsrResources();

            if (VsrActive)
            {
                double s = EffectiveVsrScale();
                OutputWidth = Math.Min(_vsrMaxW, Even(_srcW * s));
                OutputHeight = Math.Min(_vsrMaxH, Even(_srcH * s));
                _sw.Restart();
                _useAudioClock = _audioOk;
            }

            return VsrActive;
        }
        catch (Exception e)
        {
            // DllNotFoundException / BadImageFormatException 都会落到这里：
            // 桥接层缺失或位数不对时，不能让它冒到渲染线程上。
            LastError = "VSR 激活失败: " + e.Message;
            MarkVsrUnsupported(LastError);
            return false;
        }
        finally
        {
            if (locked) Monitor.Exit(_renderLock);

            // 音频线程在 _aHold 期间已经自己 Pause 了，放开时它会 Rebaseline + Resume，
            // 所以视频不用追赶，也就不会有"左右跳"。
            _aHold = false;
            _renderGate.Set();
            _wake.Set();
            _aWake.Set();
        }
    }

    /// <summary>只有质量档位变化时不需要重建管线，改个参数就生效。</summary>
    public void SetQuality(int quality)
    {
        VsrQuality = Math.Max(1, Math.Min(4, quality));
    }

    /// <summary>
    /// 开流前的 VSR 能力探测：这台机器到底能不能跑超分。
    /// <para>
    /// 与 <see cref="ActivateVsr"/> 的区别是**不看用户的 VSR 开关**。
    /// H/W 模式的承诺是「硬解 + 可超分」，如果这台机器连 VSR 都没有，
    /// UI 层会据此退回原播放器，所以这里必须能独立回答"能不能"。
    /// </para>
    /// <para>
    /// 必须在 <see cref="Start"/> 之前调用：那时渲染线程还没起来，
    /// 不需要闸门，也不会跟解码线程抢设备。
    /// </para>
    /// </summary>
    public bool ProbeVsr()
    {
        if (_device == null) return false;
        if (VsrUnsupported) return false;

        // 只回答「能不能」，**不初始化 NGX**。
        // 以前这里直接 SetupVsrResources()，等于把 400ms 的 AI 初始化塞进开流路径，
        // 而且它是在 Start() 之前跑的——首帧必须等它做完才可能出现。
        // 现在只问渲染设备上的适配器厂商（纯 CPU 查询，微秒级），
        // 真正的初始化推迟到画面出来以后由 UI 层触发 ActivateVsr()。
        try
        {
            if (!VsrNative.Prepare())          // 只加载 DLL，不碰 NGX
            {
                MarkVsrUnsupported("VSR 组件缺失: " + VsrNative.PrepareError);
                return false;
            }

            int vendor = VsrNative.VsrAdapterVendor(_device.NativePointer);
            if (vendor != 0x10DE)
            {
                // 非 N 卡必须在这里就返回：在别家驱动上调 NGX 是未定义行为。
                MarkVsrUnsupported($"渲染设备不是 NVIDIA（vendor=0x{vendor:X}）");
                return false;
            }
        }
        catch (Exception e)
        {
            MarkVsrUnsupported("VSR 探测失败: " + e.Message);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 这台机器是否「可能」支持 VSR：只看厂商，不看 NGX 有没有真的初始化成功。
    /// UI 用它决定按钮显示与否（非 N 卡直接隐藏），不触发任何 GPU 工作。
    /// </summary>
    public bool VsrLikelyAvailable
    {
        get
        {
            if (_device == null || VsrUnsupported) return false;
            try
            {
                return VsrNative.Prepare() && VsrNative.VsrAdapterVendor(_device.NativePointer) == 0x10DE;
            }
            catch { return false; }
        }
    }

    // ================================================================ playback

    public void Start()
    {
        if (_running) return;
        _running = true;
        _sw.Restart();
        _wallOriginHns = -1;
        _acLast = -1;
        _acSameSince = 0;
        _thread = new Thread(PlaybackLoop) { IsBackground = true, Name = "QL-VideoEngine" };
        _thread.Start();

        if (_audioOk)
        {
            _aRunning = true;
            _aThread = new Thread(AudioLoop) { IsBackground = true, Name = "QL-AudioEngine" };
            _aThread.Start();
        }

        if (_wdThread == null)
        {
            _wdRunning = true;
            _wdThread = new Thread(WatchdogLoop) { IsBackground = true, Name = "QL-VideoWatchdog" };
            _wdThread.Start();
        }
    }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value)
            {
                _wake.Set();
                _sw.Restart();
                _wallOriginHns = -1;
                _acLast = -1;
                _acSameSince = 0;
                _precisePending = -1;   // 恢复播放，不用再补精确 seek
            }
            _aWake.Set();
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            double r = Math.Max(0.1, Math.Min(4.0, value));
            if (Math.Abs(r - _rate) < 1e-6) return;
            _rate = r;
            if (_audio != null) _audio.ChangeRate(r);
            if (_stretch != null) _stretch.Rate = r;
            _aWake.Set();
        }
    }

    /// <summary>播放位置：优先音频时钟（真实播放进度），无音频时用视频时间戳。</summary>
    public TimeSpan Position
    {
        get
        {
            // 暂停时音频线程不推进，音频时钟会停在旧位置。这时候必须只信视频位置：
            // 否则拖动进度条 / 按方向键 seek 完，帧回调把旧的音频位置写回进度条，
            // 表现就是「拖完弹回原处」「按住方向键位置不跟手」。
            if (!_paused && _audioOk && _useAudioClock)
            {
                long a = _audio?.PositionHns() ?? -1;
                if (a >= 0) return TimeSpan.FromTicks(a);
            }
            return TimeSpan.FromTicks(_videoPosHns);
        }
    }

    public void Seek(TimeSpan position)
    {
        long ticks = Math.Max(0, position.Ticks);

        // 忽略「原地 seek」。UI 会把引擎报告的播放位置每帧回写过来，
        // 如果每次都真去 flush 解码器 + 丢弃预卷帧，画面会永远停在原地、
        // 进度条几乎不动——这正是「打开就卡住」的成因。
        // 300ms 以内的 seek 用户感知不到，直接吞掉。
        if (!_paused && _seekTicks < 0 && Math.Abs(ticks - _videoPosHns) < 3_000_000)
            return;

        _seekTicks = ticks;
        _lastSeekAt = Environment.TickCount;   // 渲染线程靠它判断「还在拖」还是「停手了」
        Interlocked.Exchange(ref _aSeekHns, ticks);
        _wake.Set();
        _aWake.Set();
    }

    private void PlaybackLoop()
    {
        while (_running)
        {
            try
            {
                PlaybackStep();
            }
            catch (Exception e)
            {
                // 渲染线程绝不能把异常抛出去，否则整个进程直接崩
                LastError = "渲染失败: " + e.Message;
                Debug.WriteLine(LastError);
                Thread.Sleep(50);
            }
        }
    }

    private void PlaybackStep()
    {
        {
            _diagStage = 1;

            if (_paused)
            {
                // 暂停态的 seek 分两段走，这是「拖得动」和「停得准」的取舍：
                //
                //   拖动中（距上次 seek < 180ms）→ 只解到最近关键帧，一帧就出画面。
                //       用户手指还在动，画面必须跟得上；精确位置等他停手再说。
                //   停手后（超过 180ms 没新 seek）→ 从当前位置继续解，追到精确时间。
                //
                // 以前只有前半段（永远停在关键帧，实测差 6 秒），改成全程精确又会在
                // 连发时每次都重新定位、一帧都追不完，画面反而更卡。两段配合才既跟手又准。
                if (_seekTicks >= 0)
                {
                    long ticks = Interlocked.Exchange(ref _seekTicks, -1);
                    _precisePending = ticks;
                    try { _reader.SetCurrentPosition(ticks); } catch (Exception e) { Debug.WriteLine(e.Message); }

                    if (DecodeNext(out var still, out int stillSub, out long stillTs))
                    {
                        RenderFrame(still, stillSub);
                        _videoPosHns = stillTs;
                        FrameRendered?.Invoke();
                    }
                    _sw.Restart();
                }
                else if (_precisePending >= 0 && Environment.TickCount - _lastSeekAt >= 180)
                {
                    // 停手了：从当前解码位置一路解到目标，不重新定位（重新定位等于白解）。
                    long target = _precisePending;
                    _precisePending = -1;

                    for (int guard = 0; guard < 2000; guard++)
                    {
                        if (Interlocked.Read(ref _seekTicks) >= 0)
                            break;                       // 又按了，交给上面的快速路径

                        if (!DecodeNext(out var still, out int stillSub, out long stillTs))
                        {
                            _diagStage = 32;
                            break;
                        }

                        if (stillTs < target - 1_000_000)
                        {
                            _diagDropped++;              // 还没到目标，丢
                            continue;
                        }

                        RenderFrame(still, stillSub);
                        _videoPosHns = stillTs;
                        FrameRendered?.Invoke();
                        break;
                    }
                    _sw.Restart();
                }

                _wake.Wait(40);
                _wake.Reset();
                return;
            }

            if (_seekTicks >= 0)
            {
                _diagStage = 2;
                long ticks = Interlocked.Exchange(ref _seekTicks, -1);
                try { _reader.SetCurrentPosition(ticks); } catch (Exception e) { Debug.WriteLine(e.Message); }
                Interlocked.Exchange(ref _dropBeforeHns, ticks);
                _sw.Restart();
                _wallOriginHns = -1;
                _acLast = -1;
                _acSameSince = 0;
                _useAudioClock = _audioOk;
            }

            _diagStage = 3;
            if (!DecodeNext(out var tex, out int sub, out long timestamp))
            {
                _diagStage = 30;
                if (Loop)
                {
                    try { _reader.SetCurrentPosition(0); } catch { }
                    _sw.Restart();
                    _wallOriginHns = -1;
                    _acLast = -1;
                    _acSameSince = 0;
                    Thread.Sleep(30);
                }
                else
                {
                    // 停在最后一帧，等 seek 或重新播放
                    _diagStage = 31;
                    _wake.Wait(100);
                    _wake.Reset();
                }
                return;
            }

            _diagStage = 4;

            // seek 预卷帧：还没走到目标时间的一律丢弃，不渲染。
            // 只丢不渲染，解码本身很快，画面会停在上一帧直到追上目标。
            long dropBefore = Interlocked.Read(ref _dropBeforeHns);
            if (dropBefore >= 0)
            {
                if (timestamp < dropBefore - 1_000_000)     // 容差 100ms
                {
                    _diagDropped++;
                    _diagStage = 40;
                    // 这里以前写着「tex 由 using 释放」，其实下面那个 using 还没轮到，
                    // 每丢一帧就漏一张整帧纹理 —— 长 GOP 的片子一次 seek 能丢几十帧。
                    // 现在解码缓冲是引擎复用的，丢掉就是丢掉，不用（也不能）再放。
                    return;
                }
                Interlocked.Exchange(ref _dropBeforeHns, -1);
            }

            WaitForFrame(timestamp);

            _diagStage = 5;
            RenderFrame(tex, sub);

            _diagStage = 6;
            _videoPosHns = timestamp;
            FrameRendered?.Invoke();
        }
    }

    /// <summary>按主时钟等到这一帧该显示的时刻。</summary>
    private void WaitForFrame(long targetHns)
    {
        if (_useAudioClock && _audio != null)
        {
            var sw = Stopwatch.StartNew();
            while (_running && !_paused)
            {
                long a = _audio.PositionHns();
                if (a >= 0)
                {
                    // 音频时钟停住（音频线程没喂上数据）时它会一直返回同一个值。
                    // 这时 d 会恒大于 500ms，下面直接 return，视频就会全速空转
                    // （实测 25fps 的片子跑到 58fps）。所以必须检测并退回墙钟。
                    if (a == _acLast)
                    {
                        if (_acSameSince == 0) _acSameSince = Environment.TickCount;
                        else if (Environment.TickCount - _acSameSince > 300)
                        {
                            _useAudioClock = false;
                            _wallOriginHns = targetHns;
                            _sw.Restart();
                            return;
                        }
                    }
                    else
                    {
                        _acLast = a;
                        _acSameSince = 0;
                    }

                    double d = (targetHns - a) / 10000.0;   // ms
                    if (d <= 4.0) return;
                    if (d > 500.0) return;                  // 视频落后太多，别阻塞
                    _wake.Wait(d > 20.0 ? 8 : 2);
                    _wake.Reset();
                }
                else
                {
                    if (sw.ElapsedMilliseconds > 400)
                    {
                        _useAudioClock = false;             // 音频起不来，退化
                        _wallOriginHns = targetHns;
                        _sw.Restart();
                        return;
                    }
                    _wake.Wait(4);
                    _wake.Reset();
                }
            }
            return;
        }

        // 无音频时钟：按帧时间戳节流。
        // 注意 frameMs 必须相对「第一帧」算，不能拿绝对时间戳直接比——
        // 否则任何 1 秒以后的帧都算「已经严重迟到」，一帧都不等。
        if (_wallOriginHns < 0)
        {
            _wallOriginHns = targetHns;
            _sw.Restart();
        }

        double frameMs = (targetHns - _wallOriginHns) / 10000.0;
        double nowMs = _sw.Elapsed.TotalMilliseconds * _rate;
        double wait = frameMs - nowMs;
        if (wait > 0 && wait < 1000) Thread.Sleep((int)wait);
        else if (wait < -500)
        {
            _wallOriginHns = targetHns;
            _sw.Restart();
        }
    }

    private bool DecodeNext(out D3D11Texture2D tex, out int subresource, out long timestamp)
    {
        // 解码也要在渲染锁里：Reconfigure 会重建视频处理器，此时若播放线程
        // 正在用立即上下文做拷贝，两者会打架
        lock (_renderLock)
        {
            return DecodeNextCore(out tex, out subresource, out timestamp);
        }
    }

    private bool DecodeNextCore(out D3D11Texture2D tex, out int subresource, out long timestamp)
    {
        tex = null;
        subresource = 0;
        timestamp = 0;

        // 连续拿到「非 D3D 缓冲」就放弃这一帧。绝不能无上限 continue——
        // 那会死循环并且一直霸占渲染锁，表现就是整个播放器卡死。
        int skipped = 0;

        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlags.None, out int actualIndex, out SourceReaderFlags flags, out timestamp);

            if (sample == null) { _diagNull++; return false; }
            if ((flags & SourceReaderFlags.Error) != 0) { sample.Dispose(); _diagErr++; return false; }

            // 注意：ReadSample 返回的 actualIndex 是「真实流号」，而 FirstVideoStream 是
            // 符号索引（-4）。带音轨的文件里视频往往是流 1，拿 1 != 0 去过滤会把整部片丢光。

            using (sample)
            using (var buffer = sample.ConvertToContiguousBuffer())
            using (var dxgi = buffer.QueryInterfaceOrNull<DXGIBuffer>())
            {
                if (dxgi == null)
                {
                    if (++skipped >= 8) { _diagNoDxgi++; return false; }
                    continue;
                }

                dxgi.GetResource(Utilities.GetGuidFromType(typeof(D3D11Texture2D)), out IntPtr ptr);
                if (ptr == IntPtr.Zero)
                {
                    if (++skipped >= 8) { _diagNoDxgi++; return false; }
                    continue;
                }

                // 解码结果是纹理数组的一片，必须按子资源索引复制出来
                var src = new D3D11Texture2D(ptr);
                subresource = dxgi.SubresourceIndex;
                var d = src.Description;

                // 解码缓冲复用：以前是每帧 new 一张整帧纹理、渲染完就 Dispose。
                // 25~60fps 下就是每秒几十次「分配整帧 + 释放」，驱动侧分配器只涨不缩
                // （实测单次预览光这一项就能多出上百 MB 私有内存）。
                // 立即上下文上拷贝和渲染是严格有序的，重复用同一张纹理不会串帧。
                if (_decodeTex == null || _decodeW != d.Width || _decodeH != d.Height || _decodeFmt != d.Format)
                {
                    Release("decodeTex(换尺寸)", _decodeTex);
                    _decodeTex = new D3D11Texture2D(_device, new Texture2DDescription
                    {
                        Width = d.Width,
                        Height = d.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = d.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    });
                    _decodeW = d.Width;
                    _decodeH = d.Height;
                    _decodeFmt = d.Format;
                }

                tex = _decodeTex;
                _ctx.CopySubresourceRegion(src, subresource, null, tex, 0, 0, 0, 0);
                src.Dispose();
                return true;
            }
        }
    }

    private void RenderFrame(D3D11Texture2D nv12, int subresource)
    {
        // 闸门关上时（UI 线程正在初始化 NGX）在这里停住，一帧都不许进设备。
        _renderGate.Wait();
        lock (_renderLock)
        {
            RenderFrameCore(nv12, subresource);
        }
        Interlocked.Increment(ref _frameCount);
    }

    private void RenderFrameCore(D3D11Texture2D nv12, int subresource)
    {
        // 1) NV12 → BGRA
        _diagStage = 51;
        // nv12 是 DecodeNext 拷出来的单切片纹理，ArraySlice 必须是 0。
        // 解码器那张纹理数组的子资源索引只在 CopySubresourceRegion 时用得上。
        _videoDevice.CreateVideoProcessorInputView(nv12, _vpEnum,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                Dimension = VpivDimension.Texture2D,
                Texture2D = new Texture2DVpiv { MipSlice = 0, ArraySlice = 0 },
            }, out var inView);

        // 这一句决定「黑是不是黑的」：NV12 的 16-235 要靠它才会被展开成 0-255。
        // 缺了它驱动会按全范围直通，整幅画面就卡在 16..234 发灰。
        TrySetStreamColorSpace(_vp, false);

        _videoContext.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(_vp, 0, true, new RawRectangle(0, 0, _srcW, _srcH));
        _videoContext.VideoProcessorSetStreamDestRect(_vp, 0, true, new RawRectangle(0, 0, _bgraW, _bgraH));
        _videoContext.VideoProcessorSetOutputTargetRect(_vp, true, new RawRectangle(0, 0, _bgraW, _bgraH));
        _videoContext.VideoProcessorBlt(_vp, _vpOut, 0, 1, new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                PInputSurface = inView,
            },
        });
        inView.Dispose();
        _diagStage = 52;

        // 1.5) HDR → SDR。必须在 VSR 之前：NGX 是在 SDR 的 8bit RGB 上训练的，
        // 把 PQ 编码的值直接喂进去，它会把亮度编码当成画面内容。
        D3D11Texture2D baseTex = _bgraTex;
        if (_hdrToneMap && _toneMap != null)
        {
            _diagStage = 57;
            _toneMap.Run(_bgraSrv, _bgraW, _bgraH, _toneMapTransfer, HdrPeakNits, HdrSdrWhiteNits);
            // 输出纹理是 Run 里按需分配的，所以这里不能提前用 Output != null 做门槛，
            // 那样第一帧永远不跑，Output 也就永远不存在（自锁）。
            if (_toneMap.Output != null) baseTex = _toneMap.Output;
        }

        // 2) VSR（可选）：输出写进固定纹理的左上角 outW x outH 区域
        int outW = 0, outH = 0;

        // 这里绝不初始化 NGX。渲染线程里初始化实测必挂死（3020ms 超时、0 帧），
        // 初始化统一走 ActivateVsr()，由 UI 线程在闸门关上的情况下完成。
        if (VsrActive && _vsrOutTex != null)
        {
            double s = EffectiveVsrScale();
            outW = Math.Min(_vsrMaxW, Even(_srcW * s));
            outH = Math.Min(_vsrMaxH, Even(_srcH * s));

            _lastOutW = outW;
            _lastOutH = outH;

            if (VsrTwoStage && EnsureVsrMidTex())
            {
                // 两级：源 → 中间 → 目标。倍率开平方，乘积正好等于总倍率。
                // 第一级的结果先落在一块固定纹理上，第二级再吃它——
                // 中间尺寸只通过 inW/inH 传，纹理本身不用重建。
                double half = Math.Sqrt(s);
                int midW = Math.Min(_vsrMaxW, Even(_srcW * half));
                int midH = Math.Min(_vsrMaxH, Even(_srcH * half));

                _diagStage = 58;
                int rc1 = VsrNative.VsrEvaluate(_ctx.NativePointer,
                    baseTex.NativePointer, _bgraW, _bgraH,
                    _vsrMidTex.NativePointer, midW, midH, VsrQuality);

                if (rc1 != 0)
                {
                    VsrActive = false;
                    LastError = $"VSR 第一级失败({midW}x{midH}): " + VsrNative.LastError;
                }
                else
                {
                    int rc2 = VsrNative.VsrEvaluate(_ctx.NativePointer,
                        _vsrMidTex.NativePointer, midW, midH,
                        _vsrOutTex.NativePointer, outW, outH, VsrQuality);
                    if (rc2 != 0)
                    {
                        VsrActive = false;
                        LastError = $"VSR 第二级失败({midW}x{midH} → {outW}x{outH}): " + VsrNative.LastError;
                    }
                }
            }
            else
            {
                int rc = VsrNative.VsrEvaluate(_ctx.NativePointer, baseTex.NativePointer, _bgraW, _bgraH,
                    _vsrOutTex.NativePointer, outW, outH, VsrQuality);
                if (rc != 0)
                {
                    VsrActive = false;
                    LastError = "VSR evaluate 失败: " + VsrNative.LastError;
                }
            }
        }

        // 3) 缩放到显示尺寸，再送到共享表面
        _diagStage = 53;
        var src = VsrActive ? _vsrOutTex : baseTex;
        int sw = VsrActive ? outW : _srcW;
        int sh = VsrActive ? outH : _srcH;
        int texW = VsrActive ? _vsrMaxW : _srcW;
        int texH = VsrActive ? _vsrMaxH : _srcH;

        if (!VsrActive && _srcW == _targetW && _srcH == _targetH)
        {
            // 直通：源就是显示尺寸，拷的必须是「实际跑的那条链路」的产物
            // （HDR 片这里是 tone map 的输出，不是 _bgraTex）。
            _ctx.CopyResource(baseTex, _sharedTex);
        }
        else
        {
            // 两条缩放路径写到不同的纹理里：
            //   驱动路径（VpBlt）→ _outTex
            //   着色器路径（Spline36/Jinc3）→ _scaler.Output
            // 拷错了就是「切算法后画面定格」——屏幕一直拿到上一帧的旧内容。
            D3D11Texture2D scaled;

            if (ScaleWithDriver(src, sw, sh))
            {
                scaled = _outTex;
            }
            else if (ScaleWithShader(src, texW, texH, sw, sh) && _scaler?.Output != null)
            {
                scaled = _scaler.Output;
            }
            else
            {
                // 两条路都没成，别把没写过的纹理送上去（会闪黑/花屏）
                scaled = null;
            }

            _diagStage = 54;

            if (scaled != null) _ctx.CopyResource(scaled, _sharedTex);
        }

        _diagStage = 55;
        _ctx.Flush();
        _diagStage = 56;
    }

    /// <summary>驱动缩放：同一个视频处理器改一下源/目标矩形再 Blt。</summary>
    private bool ScaleWithDriver(D3D11Texture2D src, int sw, int sh)
    {
        if (DownscaleAlgo != 0 || _vpOutFinal == null || _vp == null) return false;

        try
        {
            _videoDevice.CreateVideoProcessorInputView(src, _vpEnum,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    Dimension = VpivDimension.Texture2D,
                    Texture2D = new Texture2DVpiv { MipSlice = 0, ArraySlice = 0 },
                }, out var inView);

            // 这一趟吃的是 RGB（VSR 输出或上一趟的转换结果），必须声明成 full range，
            // 否则驱动会拿它当电视范围再压一次，画面二次发灰。
            TrySetStreamColorSpace(_vp, true);

            _videoContext.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamSourceRect(_vp, 0, true, new RawRectangle(0, 0, sw, sh));
            _videoContext.VideoProcessorSetStreamDestRect(_vp, 0, true, new RawRectangle(0, 0, _targetW, _targetH));
            _videoContext.VideoProcessorSetOutputTargetRect(_vp, true, new RawRectangle(0, 0, _targetW, _targetH));
            _videoContext.VideoProcessorBlt(_vp, _vpOutFinal, 0, 1, new[]
            {
                new VideoProcessorStream
                {
                    Enable = true,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    PInputSurface = inView,
                },
            });
            inView.Dispose();
            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            LastError = "驱动缩放失败: " + e.Message;
            return false;
        }
    }

    /// <summary>着色器缩放：Spline36 / Jinc3。成功时结果在 _scaler.Output 里。</summary>
    private bool ScaleWithShader(D3D11Texture2D src, int texW, int texH, int sw, int sh)
    {
        if (_scaler == null) return false;

        // 三条来源三种 SRV：VSR 输出 / tone map 输出 / 原始 BGRA。
        // 认错了就是「读着未 tone map 的 PQ 数据去做缩放」，HDR 下画面会直接错。
        ShaderResourceView srv;
        if (ReferenceEquals(src, _vsrOutTex)) srv = _vsrSrv;
        else if (_toneMap != null && ReferenceEquals(src, _toneMap.Output)) srv = _toneMap.OutputSrv;
        else srv = _bgraSrv;
        if (srv == null) return false;

        try
        {
            _scaler.EnsureTargets(_targetW, _targetH, sh);
            _scaler.Run(srv, texW, texH, sw, sh, _targetW, _targetH, DownscaleAlgo);
            return _scaler.Output != null;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            LastError = "着色器缩放失败: " + e.Message;
            return false;
        }
    }

    // ================================================================ audio

    private void OpenAudio(string path)
    {
        try
        {
            _aReader = new SourceReader(path);
            _aReader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _aReader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

            int rate = 48000;
            try
            {
                using (var native = _aReader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0))
                {
                    int r = native.Get(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                    if (r > 0) rate = r;
                }
            }
            catch (Exception e) { Debug.WriteLine(e.Message); }

            // 统一成 16bit / 立体声（多声道交给 MF 下混）
            using (var mt = new MediaType())
            {
                mt.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                mt.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                mt.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, rate);
                mt.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                mt.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                _aReader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, mt);
            }

            int outRate = rate, outCh = 2, outBits = 16;
            using (var cur = _aReader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
            {
                int r = cur.Get(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                int c = cur.Get(MediaTypeAttributeKeys.AudioNumChannels);
                int b = cur.Get(MediaTypeAttributeKeys.AudioBitsPerSample);
                if (r > 0) outRate = r;
                if (c > 0) outCh = c;
                if (b > 0) outBits = b;
            }

            _aChannels = outCh;
            _aBits = outBits;
            _aFloatOut = new float[8192 * _aChannels];

            _audio = new AudioOutput();
            if (!_audio.Open(outRate, outCh, outBits))
            {
                _audio.Dispose();
                _audio = null;
                return;
            }
            _audio.Volume = _volume;
            _audio.Muted = _muted;
            _audio.Rate = _rate;

            _stretch = new TimeStretcher(outCh);
            _stretch.Rate = _rate;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            try { _audio?.Dispose(); } catch { }
            _audio = null;
        }
    }

    private void AudioLoop()
    {
        bool pausedApplied = false;

        while (_aRunning)
        {
            try
            {
                long seek = Interlocked.Read(ref _aSeekHns);
                if (seek >= 0)
                {
                    Interlocked.Exchange(ref _aSeekHns, -1);
                    DoAudioSeek(seek);
                    continue;
                }

                // _aHold 是"临时挂住"（首次激活 VSR 时用），和 _paused 走同一条
                // Pause/Rebaseline+Resume 路径，这样放开后视频不用追帧。
                if (_paused || _aHold)
                {
                    if (!pausedApplied)
                    {
                        pausedApplied = true;
                        _audio.Pause();
                    }
                    _aWake.Wait(20);
                    _aWake.Reset();
                    continue;
                }

                if (pausedApplied)
                {
                    pausedApplied = false;
                    _audio.Rebaseline();
                    _audio.Resume();
                }

                if (_audio.BuffersQueued >= 4)
                {
                    _aWake.Wait(6);
                    _aWake.Reset();
                    continue;
                }

                if (!ReadAudioSample(out long ptsHns))
                {
                    if (Loop)
                    {
                        DoAudioSeek(0);
                        Thread.Sleep(20);
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }
                    continue;
                }

                PushAudio(ptsHns);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                Thread.Sleep(10);
            }
        }
    }

    private void DoAudioSeek(long hns)
    {
        try
        {
            _audio?.Flush();
            try { _aReader?.SetCurrentPosition(hns); } catch (Exception e) { Debug.WriteLine(e.Message); }
            _stretch?.Reset();
            _audio?.SetBase(hns);
            _aAwaitFirst = true;
            if (_audio != null && !_paused) _audio.Resume();
        }
        catch (Exception e) { Debug.WriteLine(e.Message); }
    }

    private bool ReadAudioSample(out long ptsHns)
    {
        ptsHns = 0;
        while (true)
        {
            var sample = _aReader.ReadSample(SourceReaderIndex.FirstAudioStream,
                SourceReaderControlFlags.None, out int index, out SourceReaderFlags flags, out ptsHns);

            if (sample == null) return false;
            if ((flags & SourceReaderFlags.Error) != 0) { sample.Dispose(); return false; }
            // 同视频侧：actualIndex 是真实流号，不能拿符号索引去比

            _aBytesLen = 0;
            using (sample)
            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                IntPtr dp = buffer.Lock(out _, out int currentLength);
                try
                {
                    if (currentLength > 0)
                    {
                        if (_aBytes.Length < currentLength) _aBytes = new byte[currentLength + 4096];
                        Marshal.Copy(dp, _aBytes, 0, currentLength);
                        _aBytesLen = currentLength;
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            return _aBytesLen > 0;
        }
    }

    private void PushAudio(long ptsHns)
    {
        bool first = _aAwaitFirst;

        // 原速：PCM 原样送出，零处理
        if (_stretch == null || _stretch.Passthrough)
        {
            _audio.Submit(_aBytes, _aBytesLen, ptsHns, first);
            _aAwaitFirst = false;
            return;
        }

        // 变速：int16 → float → WSOLA → int16
        int samples = _aBytesLen / 2;
        if (samples <= 0) return;

        if (_aShort.Length < samples) _aShort = new short[samples + 1024];
        if (_aFloat.Length < samples) _aFloat = new float[samples + 1024];
        System.Buffer.BlockCopy(_aBytes, 0, _aShort, 0, _aBytesLen);
        for (int i = 0; i < samples; i++) _aFloat[i] = _aShort[i] * (1f / 32768f);

        _stretch.Feed(_aFloat, samples / _aChannels);

        int cap = _aFloatOut.Length / _aChannels;
        while (true)
        {
            int got = _stretch.Read(_aFloatOut, cap, false);
            if (got <= 0) break;

            int n = got * _aChannels;
            if (_aShort.Length < n) _aShort = new short[n + 1024];
            for (int i = 0; i < n; i++)
            {
                float v = _aFloatOut[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                _aShort[i] = (short)(v * 32767f);
            }

            int bytes = n * 2;
            if (_aOut.Length < bytes) _aOut = new byte[bytes + 4096];
            System.Buffer.BlockCopy(_aShort, 0, _aOut, 0, bytes);

            _audio.Submit(_aOut, bytes, ptsHns, first);
            first = false;
            _aAwaitFirst = false;
        }
    }

    // ================================================================ dispose

    /// <summary>
    /// 释放一件资源，失败只记日志、不往外抛。
    /// 一整串裸 Dispose() 里只要有一个抛（设备丢失、解码器已经关掉自己），
    /// 后面的设备/纹理就永远不会释放，显存会一直挂着。
    /// </summary>
    private static void Release(string name, IDisposable resource)
    {
        if (resource == null) return;

        try
        {
            resource.Dispose();
        }
        catch (Exception e)
        {
            Log($"dispose {name} 失败: {e.GetType().Name}: {e.Message}");
        }
    }

    public void Dispose()
    {
        _running = false;
        _aRunning = false;
        _aHold = false;
        _wdRunning = false;

        // 万一是在 ActivateVsr 中途被 Dispose，闸门必须强行打开，否则渲染线程永远醒不来
        try { _renderGate.Set(); } catch { }

        _wake.Set();
        _aWake.Set();

        // 线程可能还卡在 ReadSample 里。没等到它退出就释放它正在用的 COM 对象，
        // 轻则死锁、重则崩溃——这种情况宁可泄漏也不动。
        bool videoStopped = true;
        bool audioStopped = true;
        var swJoin = Stopwatch.StartNew();
        try { if (_thread != null) videoStopped = _thread.Join(800); } catch { }
        try { if (_aThread != null) audioStopped = _aThread.Join(800); } catch { }
        swJoin.Stop();

        // 这两行是长时间运行的体温计：正常一次预览应该 video=True audio=True，
        // 而且线程数回到基线。出现 False 就说明这次预览的显卡资源整套没还回去。
        int threadsNow = 0;
        try { threadsNow = Process.GetCurrentProcess().Threads.Count; } catch { }
        Log($"dispose video={videoStopped} audio={audioStopped} " +
                              $"join={swJoin.ElapsedMilliseconds}ms stage={_diagStage} threads={threadsNow}");

        if (!videoStopped || !audioStopped)
            Debug.WriteLine($"Dispose: videoStopped={videoStopped} audioStopped={audioStopped}，跳过对应释放");

        // NGX 绝不能在渲染线程还在 Evaluate 的时候关，那是必死锁
        if (_ngxReady && videoStopped)
        {
            try { VsrNative.VsrShutdown(); } catch { }
            _ngxReady = false;
        }

        if (audioStopped)
        {
            try { _audio?.Dispose(); } catch { }
            try { _aReader?.Dispose(); } catch { }
        }

        // 渲染线程还活着的时候绝不能碰它正在用的 D3D/MF 对象：
        // 一边释放一边渲染，轻则卡死、重则崩溃。这种情况宁可泄漏，交给 GC 收。
        if (videoStopped)
        {
            // 上下文里可能还绑着共享纹理，直接释放会阻塞等 GPU；先解绑再放。
            // 注意上下文是共用的：这里只清状态，不碰任何别人持有的对象。
            lock (_renderLock)
            {
                try { _ctx?.ClearState(); _ctx?.Flush(); } catch { }
            }

            // 逐个释放，单个失败不能拖垮后面的。
            // 原来这里是一串裸 Dispose()：任意一个抛异常（设备丢失、解码器已关），
            // 后面十几个纹理就全留在那里 —— 表现就是「关掉预览显存不还」。
            Release("sharedTex", _sharedTex);
            Release("surface9", _surface9);
            Release("texture9", _texture9);
            Release("device9", _device9);
            Release("d3d9", _d3d9);

            Release("scaler", _scaler);
            Release("toneMap", _toneMap);
            Release("vpOutFinal", _vpOutFinal);
            Release("outTex", _outTex);
            Release("bgraSrv", _bgraSrv);
            Release("vsrSrv", _vsrSrv);

            Release("vpOut", _vpOut);
            Release("vpIn", _vpIn);
            Release("vp", _vp);
            Release("vpEnum", _vpEnum);
            Release("vsrOutTex", _vsrOutTex);
            Release("vsrMidTex", _vsrMidTex);
            Release("bgraTex", _bgraTex);
            Release("decodeTex", _decodeTex);

            Release("reader", _reader);

            // 设备 / 立即上下文 / 视频处理器 / DXGIDeviceManager / MF 平台
            // 全部是进程级的（GpuContext），这里一个都不能放：
            //   * 放了就是把别的引擎正在用的设备抽走；
            //   * 而且给过 MF 硬解的设备本来也放不掉，Dispose 只是把我们的引用退掉，
            //     资源照样挂在进程里（实测 42 线程 / 40MB 显存 / 181MB 内存不回落）。
            // 要真正还回去只能重启进程 —— 那是 ViewerPanel 空闲回收的活。
            _ctx = null;
            _videoContext = null;
            _videoDevice = null;
            _dxgiManager = null;
            _device = null;
        }
    }
}
