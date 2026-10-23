// VideoEngineProxy —— 面板眼里的「引擎」，实际跑在子进程里。
//
// 分工：
//   子进程（QuickLook.VideoEngineHost.exe）  D3D11 设备 + MF 解码 + NGX + 缩放 + 音频 + 时间轴
//   本进程（这里）                           D3D9Ex 打开共享表面 → D3DImage，转发命令，收帧信号
//
// 为什么值得这么折腾：那个 D3D11 设备一旦交给 MF 硬解过，进程内就放不掉
// （见 GpuContext / docs/PENDING.md 的实测数据）。把整套显卡栈关进子进程之后，
// 「回收」就等于「杀掉子进程」——主程序不用重启，托盘不闪，游戏++ 也不会再把它当游戏结束。
//
// 帧数据不走管道：画面走共享显存（零拷贝），帧号/播放位置走共享内存，
// 管道只走「打开/seek/暂停/换档」这种每秒几次的控制命令。
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class VideoEngineProxy : IVideoEngine
{
    /// <summary>默认走子进程。出问题可在配置里把它关掉，退回进程内引擎。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>探针专用：完全不碰共享表面，用来隔离「父进程是否开着表面」这个变量。</summary>
    internal static bool SkipSurfaceForTest;

    private const int ConnectTimeoutMs = 8000;
    private const int CommandTimeoutMs = 25000;

    // ================================================================ 宿主（全进程共一个）

    private static readonly object _hostLock = new();
    private static Process _host;
    private static LineChannel _channel;
    private static EventWaitHandle _frameEvent;
    private static MemoryMappedFile _map;
    private static MemoryMappedViewAccessor _view;
    private static string _token;

    /// <summary>当前附着在宿主上的代理（同一时刻只允许一个，QuickLook 一次只预览一个文件）。</summary>
    private static VideoEngineProxy _attached;

    // ================================================================ 实例状态

    private readonly SharedSurfaceHost _surface = new();
    private Thread _pump;
    private volatile bool _pumping;
    private bool _opened;
    private bool _disposed;

    private long _posTicks;
    private long _durTicks;
    private long _frameCount;
    private int _srcW, _srcH, _outW, _outH, _surfW, _surfH;
    private IntPtr _lastHandle;
    private IntPtr _hwnd;
    private bool _paused;
    private volatile bool _vsrActive;
    private bool _vsrReady;
    private bool _audioAvail;
    private bool _mtp;
    private uint _vendor;
    private string _lastError;
    private double _rate = 1.0;
    private float _volume = 0.8f;
    private bool _muted;

    // ================================================================ IVideoEngine：配置

    public bool VsrEnabled { get; set; }
    public int VsrCapHeight { get; set; } = 2160;
    public int VsrQuality { get; set; } = 3;
    public Action<string> DiagSink { get; set; }
    public Func<string> DiagExtra { get; set; }

    public int DownscaleAlgo
    {
        get => _algo;
        set
        {
            _algo = value;
            if (_opened) Command(string.Format("{0} {1}", HostProtocol.Algo, value));
        }
    }
    private int _algo;

    public bool Loop
    {
        get => _loop;
        set { _loop = value; }
    }
    private bool _loop = true;

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = value;
            if (_opened) Command(HostProtocol.Rate + " " + HostProtocol.F(value));
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_opened) Command(HostProtocol.Volume + " " + HostProtocol.F(value));
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_opened) Command(string.Format("{0} {1}", HostProtocol.Mute, value ? 1 : 0));
        }
    }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (_opened) Command(string.Format("{0} {1}", HostProtocol.Pause, value ? 1 : 0));
        }
    }

    // ================================================================ IVideoEngine：状态

    public TimeSpan Duration => TimeSpan.FromTicks(_durTicks);
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _posTicks));
    public int SourceWidth => _srcW;
    public int SourceHeight => _srcH;
    public int OutputWidth => _outW;
    public int OutputHeight => _outH;
    public bool VsrActive => _vsrActive;
    public bool VsrReady => _vsrReady;
    public bool AudioAvailable => _audioAvail;
    public bool MultithreadProtected => _mtp;
    public uint AdapterVendor => _vendor;
    public bool IsNvidiaAdapter => _vendor == 0x10DE;
    public string LastError => _lastError;

    public IntPtr SurfacePointer => _surface.SurfacePointer;
    public int SurfaceWidth => _surface.Width;
    public int SurfaceHeight => _surface.Height;

    public event Action FrameRendered;

    // ================================================================ 打开 / 关闭

    public bool Open(string path, IntPtr hwnd)
    {
        try
        {
            if (!EnsureHost())
            {
                _lastError = "引擎宿主进程起不来: " + (_lastError ?? "-");
                return false;
            }

            _hwnd = hwnd;
            if (!SkipSurfaceForTest && !_surface.Create(hwnd))
            {
                _lastError = "D3D9Ex 设备创建失败";
                return false;
            }

            string cmd = string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9}",
                HostProtocol.Open,
                VsrEnabled ? 1 : 0,
                VsrCapHeight,
                VsrQuality,
                DownscaleAlgo,
                HostProtocol.F(_rate),
                HostProtocol.F(_volume),
                Loop ? 1 : 0,
                _muted ? 1 : 0,
                path);

            string[] r = Command(cmd);
            if (r == null)
            {
                _lastError = "OPEN 没有应答: " + (_lastError ?? "-");
                return false;
            }

            if (r[0] == HostProtocol.OpenFail)
            {
                _lastError = r.Length > 1 ? string.Join(" ", r, 1, r.Length - 1) : "打开失败";
                return false;
            }

            if (r[0] != HostProtocol.Opened || r.Length < 13)
            {
                _lastError = "OPEN 应答无法解析: " + string.Join(" ", r);
                return false;
            }

            _srcW = HostProtocol.I(r[1]);
            _srcH = HostProtocol.I(r[2]);
            _outW = HostProtocol.I(r[3]);
            _outH = HostProtocol.I(r[4]);
            _surfW = HostProtocol.I(r[5]);
            _surfH = HostProtocol.I(r[6]);
            IntPtr handle = new IntPtr(HostProtocol.L(r[7]));
            _durTicks = HostProtocol.L(r[8]);
            _vsrActive = HostProtocol.B(r[9]);
            _audioAvail = HostProtocol.B(r[10]);
            _mtp = HostProtocol.B(r[11]);
            _vendor = (uint)HostProtocol.I(r[12]);

            _opened = true;
            _vsrReady = _vsrActive;

            if (!SkipSurfaceForTest && !_surface.Open(handle, _surfW, _surfH))
            {
                _lastError = "父进程打不开共享表面（handle=0x" + handle.ToInt64().ToString("X") + "）";
                Send(HostProtocol.Close);
                _opened = false;
                return false;
            }

            StartPump();
            VideoEngine.Log($"proxy: open OK {Path.GetFileName(path)}  {_srcW}x{_srcH} → {_surfW}x{_surfH}  " +
                            $"VSR={(_vsrActive ? "on" : "off")}  handle=0x{handle.ToInt64():X}");
            return true;
        }
        catch (Exception e)
        {
            _lastError = e.GetType().Name + ": " + e.Message;
            VideoEngine.Log("proxy: open 异常 " + _lastError);
            return false;
        }
    }

    public void Start()
    {
        if (!_opened) return;
        Command(HostProtocol.Start);
    }

    public void Seek(TimeSpan position)
    {
        if (!_opened) return;
        Command(string.Format("{0} {1}", HostProtocol.Seek, position.Ticks));
    }

    public bool ProbeVsr()
    {
        if (!_opened) return false;
        string[] r = Command(HostProtocol.Probe);
        if (r == null || r[0] != HostProtocol.ProbeResult) return false;

        bool ok = HostProtocol.B(r[1]);
        if (!ok)
        {
            string reason = r.Length > 2 ? string.Join(" ", r, 2, r.Length - 2) : "VSR 不可用";
            _lastError = reason;
            _vsrReady = false;
            VideoEngine.RaiseVsrUnavailable(reason);
        }
        else
        {
            _vsrReady = true;
        }
        return ok;
    }

    public bool ActivateVsr()
    {
        if (!_opened) return false;

        // 子进程可能要重建共享表面（换倍率不一定，但 VSR 首次分配会动管线）；
        // 动手前必须先放开本进程的引用，否则子进程会在销毁旧表面那一步卡死。
        _surface.Dispose();
        string[] r = Command(HostProtocol.ActivateVsr);
        _surface.Create(_hwnd);
        bool changed = ApplySurface(r);
        _vsrActive = _vsrReady;
        return changed;
    }

    public void SetQuality(int quality)
    {
        VsrQuality = quality;
        if (_opened) Command(string.Format("{0} {1}", HostProtocol.Quality, quality));
    }

    public bool Reconfigure(int capHeight, int quality, bool enabled)
    {
        VsrCapHeight = capHeight;
        VsrQuality = quality;
        VsrEnabled = enabled;
        if (!_opened) return false;

        _surface.Dispose();
        string[] r = Command(string.Format("{0} {1} {2} {3}",
            HostProtocol.Reconfig, capHeight, quality, enabled ? 1 : 0));
        _surface.Create(_hwnd);
        return ApplySurface(r);
    }

    public bool SetTargetSize(int width, int height)
    {
        if (!_opened) return false;

        // 这一步必须彻底放手：子进程要销毁旧的共享纹理再建一张新的，
        // 而 D3D9Ex 共享表面在**别的进程**里只要还挂着（哪怕只是一个设备句柄），
        // 新表面就会卡在建纹理那一步（实测卡 30 秒以上不返回）。
        // 所以这里连 D3D9Ex 设备一起拆掉，拿回新句柄之后再重建。
        if (!SkipSurfaceForTest) _surface.Dispose();

        string[] r = Command(string.Format("{0} {1} {2}", HostProtocol.Target, width, height));
        if (!SkipSurfaceForTest) _surface.Create(_hwnd);

        if (r == null)
        {
            _lastError = "TARGET 没有应答";
            return false;
        }
        return ApplySurface(r);
    }

    /// <summary>
    /// 处理一条应答，顺带处理可能带回来的新表面。
    /// 返回 true 表示表面真的换了，调用方要重新挂 D3DImage。
    /// </summary>
    private bool ApplySurface(string[] r)
    {
        if (r == null) return false;

        if (r[0] == HostProtocol.Surface && r.Length >= 4)
        {
            int w = HostProtocol.I(r[1]);
            int h = HostProtocol.I(r[2]);
            IntPtr handle = new IntPtr(HostProtocol.L(r[3]));
            if (handle == IntPtr.Zero || w <= 0 || h <= 0) return false;

            bool same = _surface.HasSurface && _surface.Width == w && _surface.Height == h
                        && handle == _lastHandle;
            _surfW = w;
            _surfH = h;
            _lastHandle = handle;
            if (same) return false;
            return _surface.Open(handle, w, h);
        }

        if (r[0] == HostProtocol.Err)
            _lastError = r.Length > 1 ? string.Join(" ", r, 1, r.Length - 1) : "命令被拒绝";

        return false;
    }

    // ================================================================ 帧泵

    private void StartPump()
    {
        if (_pumping) return;
        _pumping = true;
        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "ql-engine-frame-pump",
            Priority = ThreadPriority.AboveNormal,
        };
        _pump.Start();
    }

    private void StopPump()
    {
        _pumping = false;
        try { _pump?.Join(500); } catch { }
        _pump = null;
    }

    private void PumpLoop()
    {
        while (_pumping)
        {
            bool signaled;
            try { signaled = _frameEvent != null && _frameEvent.WaitOne(150); }
            catch { return; }

            if (!_pumping) return;
            if (!signaled) continue;

            RefreshShared();
            FrameRendered?.Invoke();
        }
    }

    private void RefreshShared()
    {
        try
        {
            var view = _view;
            if (view == null) return;

            view.Read(0, out FrameShared s);
            Interlocked.Exchange(ref _posTicks, s.PositionTicks);
            if (s.DurationTicks > 0) _durTicks = s.DurationTicks;
            if (s.FrameCount == _frameCount) return;

            _frameCount = s.FrameCount;
            _vsrActive = s.VsrActive != 0;
            if (s.OutputW > 0 && s.OutputH > 0)
            {
                _outW = s.OutputW;
                _outH = s.OutputH;
            }
        }
        catch
        {
            // 共享内存被拆掉（宿主正在退出）：下一帧自然就没了，别抛
        }
    }

    // ================================================================ 命令通道

    /// <summary>
    /// 只发不等（只有 CLOSE/QUIT 这种马上要断开的场景才用）。
    /// 其它命令一律走 <see cref="Command"/>：子进程对每条命令都回一行，
    /// 一旦有一条没读，后面的应答就全部错位（实测会话直接卡死）。
    /// </summary>
    private void Send(string line)
    {
        try
        {
            var ch = _channel;
            if (ch != null) ch.Send(line);
        }
        catch (Exception e)
        {
            _lastError = "发送失败: " + e.Message;
        }
    }

    /// <summary>发一条命令并等一条应答。超时/异常返回 null（调用方各自兜）。</summary>
    private string[] Command(string line)
    {
        lock (_hostLock)
        {
            try
            {
                var ch = _channel;
                if (ch == null) return null;

                ch.Send(line);
                string reply = ch.Read(CommandTimeoutMs);
                if (reply != null && (reply.StartsWith("SURFACE") || reply.StartsWith("ERR")))
                    VideoEngine.Log("proxy: 收到 " + Head(reply, 60));
                if (reply == null)
                {
                    bool dead;
                    try { dead = _host == null || _host.HasExited; } catch { dead = true; }
                    VideoEngine.Log($"proxy: 命令无应答 {Head(line, 24)}  宿主还活着={(dead ? "否" : "是")}");
                    return null;
                }
                return reply.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (Exception e)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    // ================================================================ 宿主生命周期

    private bool EnsureHost()
    {
        lock (_hostLock)
        {
            if (_host != null && !_host.HasExited && _channel != null) return true;
            DetachHost();

            try
            {
                string dir = Path.GetDirectoryName(typeof(VideoEngineProxy).Assembly.Location);
                string exe = Path.Combine(dir ?? ".", "QuickLook.VideoEngineHost.exe");
                if (!File.Exists(exe))
                {
                    _lastError = "缺少 " + exe;
                    VideoEngine.Log("proxy: " + _lastError);
                    return false;
                }

                string token = HostProtocol.Token();
                string pipe = HostProtocol.PipeName(token);
                string evt = HostProtocol.EventName(token);
                string map = HostProtocol.MapName(token);

                _map = MemoryMappedFile.CreateNew(map, FrameShared.Size);
                _view = _map.CreateViewAccessor(0, FrameShared.Size);
                _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, evt);

                var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);

                var si = new ProcessStartInfo(exe, string.Format(
                    "--pipe \"{0}\" --event \"{1}\" --map \"{2}\" --parent {3}",
                    pipe, evt, map, Process.GetCurrentProcess().Id))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir ?? ".",
                };

                _host = Process.Start(si);
                if (_host == null)
                {
                    _lastError = "CreateProcess 返回空";
                    DetachHost();
                    return false;
                }

                bool connected = false;
                try
                {
                    var wait = server.WaitForConnectionAsync();
                    connected = wait.Wait(ConnectTimeoutMs) && server.IsConnected;
                }
                catch (Exception e)
                {
                    _lastError = "管道连接失败: " + e.Message;
                }

                if (!connected)
                {
                    _lastError = "宿主进程没有连上管道";
                    DetachHost();
                    return false;
                }

                _channel = new LineChannel(server);
                _token = token;
                VideoEngine.Log($"proxy: 宿主进程已就绪 pid={_host.Id}");
                return true;
            }
            catch (Exception e)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
                VideoEngine.Log("proxy: 起宿主失败 " + _lastError);
                DetachHost();
                return false;
            }
        }
    }

    /// <summary>关掉宿主进程 —— 这就是「只重启内核」。主程序不动。</summary>
    public static void ShutdownHost()
    {
        lock (_hostLock)
        {
            try { _channel?.Send(HostProtocol.Quit); } catch { }
            try { _host?.WaitForExit(1200); } catch { }
            try { if (_host != null && !_host.HasExited) _host.Kill(); } catch { }
            DetachHost();
        }
    }

    public static bool HostAlive
    {
        get
        {
            lock (_hostLock) return _host != null && !_host.HasExited;
        }
    }

    private static void DetachHost()
    {
        try { _channel?.Dispose(); } catch { }
        _channel = null;

        try { _frameEvent?.Dispose(); } catch { }
        _frameEvent = null;

        try { _view?.Dispose(); } catch { }
        _view = null;
        try { _map?.Dispose(); } catch { }
        _map = null;

        try
        {
            if (_host != null && !_host.HasExited) _host.Kill();
        }
        catch { }

        try { _host?.Dispose(); } catch { }
        _host = null;
        _token = null;
    }

    // ================================================================ 释放

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先放本进程的 D3D9 表面，再让子进程拆引擎：反过来的话
        // 我们手上会短暂握着一个已经没人管的共享表面。
        try { _surface.Dispose(); } catch { }

        StopPump();

        if (_opened)
        {
            Send(HostProtocol.Close);
            // CLOSE 不给应答也可以，但给它一点时间把设备放掉再结束本次会话
            try { _channel?.Read(3000); } catch { }
            _opened = false;
        }

        if (ReferenceEquals(_attached, this)) _attached = null;
    }
}
