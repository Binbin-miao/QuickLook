// QuickLook.VideoEngineHost —— 只干一件事：把整套显卡栈养在自己的进程里，
// 主程序要回收就把它杀掉，主程序自己永远不退出。
//
// 它是 VideoEngine 的宿主，不是「另一个播放器」：
//   * 画面不走管道：引擎把帧写进共享显存表面，把句柄报给主程序；
//   * 每渲染一帧：更新共享内存里的帧号/播放位置，Set 一下命名事件；
//   * 管道只走控制命令（打开/seek/暂停/倍速/音量/换档），一条命令一条应答。
//
// 为什么值得单独一个进程：那个 D3D11 设备一旦交给 Media Foundation 硬解过，
// 进程内就再也放不掉（实测 Dispose/GC/MFShutdown 全无效）。让进程消失是唯一办法。
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using QuickLook.Plugin.VideoViewer.Engine;

internal static class Program
{
    private static LineChannel _channel;
    private static MemoryMappedViewAccessor _view;
    private static EventWaitHandle _frameEvent;
    private static VideoEngine _engine;
    private static IntPtr _hwnd;
    private static long _frameCount;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "quicklook-vsr-host.log");

    private static void Log(string message)
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + "\n",
                Encoding.UTF8);
        }
        catch { }
    }

    private static int Main(string[] args)
    {
        // 这是 WinExe（没有控制台），碰 Console.OutputEncoding 会直接抛 IOException，
        // 进程在写第一行日志之前就死了——排查时表现为「宿主起不来，日志也没有」。

        string pipe = ArgValue(args, "--pipe");
        string evt = ArgValue(args, "--event");
        string map = ArgValue(args, "--map");
        if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(map))
        {
            Log("参数不全，退出");
            return 2;
        }

        Log($"host 启动 pid={Process.GetCurrentProcess().Id}");

        // 引擎的日志也进同一个文件，出问题只有一处要看
        VideoEngine.LogSink = Log;

        if (Environment.GetEnvironmentVariable("QL_HOST_SELFTEST") == "1")
        {
            _hwnd = HiddenWindow.Ensure();
            SelfTest();
            return 0;
        }

        try
        {
            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
            client.Connect(5000);
            _channel = new LineChannel(client);

            using var mmf = MemoryMappedFile.OpenExisting(map);
            _view = mmf.CreateViewAccessor(0, FrameShared.Size);
            _frameEvent = EventWaitHandle.OpenExisting(evt);

            _hwnd = HiddenWindow.Ensure();   // D3D9Ex 建设备要一个窗口句柄
            Log($"host 已连接管道/事件/共享内存 hwnd=0x{_hwnd.ToInt64():X}");

            Loop();
            return 0;
        }
        catch (Exception e)
        {
            Log("host 异常: " + e.GetType().Name + " " + e.Message);
            return 1;
        }
        finally
        {
            DisposeEngine();
            Log("host 退出");
        }
    }

    /// <summary>
    /// 自检：在宿主进程里连续建两张 D3D9Ex 共享纹理，看第二张会不会卡。
    /// 用来区分「宿主进程环境有问题」还是「引擎状态有问题」。
    /// </summary>
    private static void SelfTest()
    {
        var d3d9 = new SharpDX.Direct3D9.Direct3DEx();
        var present = new SharpDX.Direct3D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = SharpDX.Direct3D9.SwapEffect.Discard,
            DeviceWindowHandle = _hwnd,
            PresentationInterval = SharpDX.Direct3D9.PresentInterval.Immediate,
            BackBufferFormat = SharpDX.Direct3D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };
        var dev = new SharpDX.Direct3D9.DeviceEx(d3d9, 0, SharpDX.Direct3D9.DeviceType.Hardware, _hwnd,
            SharpDX.Direct3D9.CreateFlags.HardwareVertexProcessing
            | SharpDX.Direct3D9.CreateFlags.Multithreaded, new[] { present });
        Log("selftest: 设备 ok");

        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IntPtr h = IntPtr.Zero;
            var t = new SharpDX.Direct3D9.Texture(dev, 848 + i * 200, 478, 1,
                SharpDX.Direct3D9.Usage.RenderTarget, SharpDX.Direct3D9.Format.A8R8G8B8,
                SharpDX.Direct3D9.Pool.Default, ref h);
            Log($"selftest: 第 {i + 1} 张共享纹理 ok {sw.ElapsedMilliseconds}ms handle=0x{h.ToInt64():X}");
            var s = t.GetSurfaceLevel(0);
            s.Dispose();
            t.Dispose();
            Log($"selftest: 第 {i + 1} 张释放 ok {sw.ElapsedMilliseconds}ms");
        }

        dev.Dispose();
        d3d9.Dispose();
        Log("selftest: 完成");
    }

    // ---------------------------------------------------------------- 命令循环

    private static void Loop()
    {
        while (true)
        {
            string line = _channel.Read(0);
            if (line == null) break;          // 父进程走了

            line = line.Trim();
            if (line.Length == 0) continue;

            // 命令词 + 参数；OPEN 的文件路径放最后，允许带空格
            string[] p = line.Split(new[] { ' ' }, 10, StringSplitOptions.None);
            string verb = p[0];

            try
            {
                switch (verb)
                {
                    case HostProtocol.Open:
                        HandleOpen(p);
                        break;

                    case HostProtocol.Start:
                        _engine?.Start();
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Seek:
                        _engine?.Seek(TimeSpan.FromTicks(Long(p, 1)));
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Pause:
                        if (_engine != null) _engine.IsPaused = Bool(p, 1);
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Rate:
                        if (_engine != null) _engine.Rate = Double(p, 1, 1.0);
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Volume:
                        if (_engine != null) _engine.Volume = (float)Double(p, 1, 0.8);
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Mute:
                        if (_engine != null) _engine.Muted = Bool(p, 1);
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Algo:
                        if (_engine != null) _engine.DownscaleAlgo = Int(p, 1, 0);
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Quality:
                        if (_engine != null) _engine.SetQuality(Int(p, 1, 3));
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Target:
                        if (_engine != null) _engine.SetTargetSize(Int(p, 1, 0), Int(p, 2, 0));
                        ReplySurface();
                        break;

                    case HostProtocol.Reconfig:
                        if (_engine != null)
                            _engine.Reconfigure(Int(p, 1, 2160), Int(p, 2, 3), Bool(p, 3));
                        ReplySurface();
                        break;

                    case HostProtocol.ActivateVsr:
                        _engine?.ActivateVsr();
                        ReplySurface();
                        break;

                    case HostProtocol.Probe:
                        {
                            bool ok = _engine != null && _engine.ProbeVsr();
                            string reason = ok ? "-" : (_engine?.LastError ?? "VSR 不可用");
                            Reply($"{HostProtocol.ProbeResult} {(ok ? 1 : 0)} {reason}");
                            break;
                        }

                    case HostProtocol.Close:
                        DisposeEngine();
                        Reply(HostProtocol.Ok);
                        break;

                    case HostProtocol.Quit:
                        DisposeEngine();
                        Reply(HostProtocol.Bye);
                        return;

                    default:
                        Reply($"{HostProtocol.Err} 未知命令 {verb}");
                        break;
                }
            }
            catch (Exception e)
            {
                Log($"命令 {verb} 异常: {e.GetType().Name} {e.Message}");
                Reply($"{HostProtocol.Err} {e.GetType().Name} {e.Message}");
            }
        }
    }

    private static void HandleOpen(string[] p)
    {
        // OPEN <vsr> <cap> <quality> <algo> <rate> <volume> <loop> <muted> <path...>
        string path = p.Length > 9 ? p[9] : string.Empty;
        if (path.Length == 0)
        {
            Reply(HostProtocol.OpenFail + " 路径为空");
            return;
        }

        DisposeEngine();

        var engine = new VideoEngine
        {
            VsrEnabled = Bool(p, 1),
            VsrCapHeight = Int(p, 2, 2160),
            VsrQuality = Int(p, 3, 3),
            DownscaleAlgo = Int(p, 4, 0),
            Rate = Double(p, 5, 1.0),
            Volume = (float)Double(p, 6, 0.8),
            Loop = Bool(p, 7),
            Muted = Bool(p, 8),
            DiagSink = m => Log("  " + m),

            // 子进程里不建 D3D9Ex：那个设备在建第二张共享表面时会吊死（见 VideoEngine.SharedOutputOnly）
            SharedOutputOnly = true,
        };

        _frameCount = 0;

        if (!engine.Open(path, _hwnd))
        {
            string err = engine.LastError ?? "Open 返回 false";
            engine.Dispose();
            Reply($"{HostProtocol.OpenFail} {err}");
            return;
        }

        _engine = engine;
        _engine.FrameRendered += OnFrame;
        PublishShared(reset: true);

        Reply(string.Format(CultureInfo.InvariantCulture,
            "{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12}",
            HostProtocol.Opened,
            _engine.SourceWidth, _engine.SourceHeight,
            _engine.OutputWidth, _engine.OutputHeight,
            _engine.SurfaceWidth, _engine.SurfaceHeight,
            _engine.SharedHandle.ToInt64(),
            _engine.Duration.Ticks,
            _engine.VsrActive ? 1 : 0,
            _engine.AudioAvailable ? 1 : 0,
            _engine.MultithreadProtected ? 1 : 0,
            (int)_engine.AdapterVendor));
    }

    /// <summary>渲染线程每出一帧走这里。只写 40 字节 + Set 一次事件，不阻塞。</summary>
    private static void OnFrame()
    {
        try
        {
            PublishShared(reset: false);
            _frameEvent?.Set();
        }
        catch { }
    }

    private static void PublishShared(bool reset)
    {
        var e = _engine;
        if (e == null) return;

        long count = reset ? 0 : ++_frameCount;

        var s = new FrameShared
        {
            PositionTicks = e.Position.Ticks,
            FrameCount = count,
            DurationTicks = e.Duration.Ticks,
            VsrActive = e.VsrActive ? 1 : 0,
            OutputW = e.OutputWidth,
            OutputH = e.OutputHeight,
        };
        _view?.Write(0, ref s);
    }

    private static void ReplySurface()
    {
        Log("reply: 准备回 SURFACE");
        // 尺寸变化 → 引擎会重建共享表面，新句柄必须立刻告诉父进程
        if (_engine == null)
        {
            Reply($"{HostProtocol.Surface} 0 0 0");
            return;
        }

        Reply(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}",
            HostProtocol.Surface,
            _engine.SurfaceWidth, _engine.SurfaceHeight,
            _engine.SharedHandle.ToInt64()));
    }

    private static void DisposeEngine()
    {
        var e = _engine;
        _engine = null;
        if (e == null) return;

        try { e.FrameRendered -= OnFrame; } catch { }
        try { e.Dispose(); } catch (Exception ex) { Log("dispose 异常: " + ex.Message); }
    }

    private static void Reply(string line)
    {
        try
        {
            _channel?.Send(line);
            if (line.StartsWith("SURFACE") || line.StartsWith("OPENED") || line.StartsWith("BYE"))
                Log("reply: " + line);
        }
        catch (Exception e)
        {
            Log("reply 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static string ArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    private static string Tok(string[] p, int i) => p.Length > i ? p[i] : null;

    private static int Int(string[] p, int i, int def)
    {
        string s = Tok(p, i);
        return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
    }

    private static long Long(string[] p, int i, long def = 0)
    {
        string s = Tok(p, i);
        return s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : def;
    }

    private static double Double(string[] p, int i, double def)
    {
        string s = Tok(p, i);
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }

    private static bool Bool(string[] p, int i)
    {
        string s = Tok(p, i);
        return s == "1" || (s != null && s.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 带消息泵的隐藏窗口。D3D9Ex 建设备要一个 HWND，而且设备会往窗口发消息，
/// 没有消息泵会偶发卡住（探针里踩过）。
/// </summary>
internal static class HiddenWindow
{
    private static Form _form;
    private static readonly ManualResetEventSlim Ready = new(false);

    public static IntPtr Ensure()
    {
        if (_form != null) return _form.Handle;

        var t = new Thread(() =>
        {
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-4000, -4000),
                Size = new System.Drawing.Size(64, 64),
            };
            _form.Load += (_, __) => Ready.Set();
            Application.Run(_form);
        })
        { IsBackground = true, Name = "engine-host-pump" };

        t.SetApartmentState(ApartmentState.STA);
        t.Start();

        Ready.Wait(4000);
        return _form?.Handle ?? IntPtr.Zero;
    }
}
