// GpuContext —— 全进程共用一套 D3D11 设备
//
// 为什么必须共用（这不是设计偏好，是实测出来的硬约束）：
//
//   用 _ql_probe 的 layer 回路把「一次预览」拆开，每轮强制 GC，各跑 5 轮：
//
//   | 只做这些                                     | 线程   | 显存    | 内存     |
//   |----------------------------------------------|--------|---------|----------|
//   | 建/销 D3D11 设备                              | 平坦   | 0       | 平坦     |
//   | + D3D9Ex + 共享纹理                           | 平坦   | 0       | 平坦     |
//   | + SourceReader 挂 DXGIDeviceManager（不解码） | +21/轮 | +8MB/轮 | +15MB/轮 |
//   | 软解（不挂 DXGI 管理器）                      | 平坦   | 0       | 平坦     |
//
// 也就是说：**一个 D3D11 设备只要交给 Media Foundation 硬解过，驱动就不再回收它。**
// 它的解码线程、句柄、CPU 侧分配、显存全部挂在进程里，Dispose + 强制 GC +
// MFShutdown + ResetDevice(null) 都无效（ResetDevice(null) 本身就被拒，E_INVALIDARG），
// 只有进程退出才清。
//
// 所以「每预览一次新建一个设备」= 每预览一次往进程里钉 55 个线程 + 250MB 显存。
// 反过来，设备全程共用、只开关 SourceReader，第 3 轮起线程/显存/内存就是一条直线。
//
// 代价：这套设备一旦建起来就住在进程里（实测 40MB 显存 + 150~200MB 内存，
// 看过 4K 片会更多）。想把它还回去只能重启进程 —— 那件事交给 ViewerPanel 的
// 空闲回收（所有预览关掉 30 秒后静默重启），不要在这里试图 Dispose。
using System;
using System.Diagnostics;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal static class GpuContext
{
    private static readonly object _sync = new();

    private static D3D11Device _device;
    private static DeviceContext _ctx;
    private static VideoDevice _videoDevice;
    private static VideoContext _videoContext;
    private static DXGIDeviceManager _dxgiManager;
    private static bool _tried;

    /// <summary>建设备失败的原因，供日志显示。</summary>
    public static string LastError { get; private set; }

    /// <summary>设备是否已就绪（只回答「建过没有」，不保证显卡还能用）。</summary>
    public static bool Ready => _device != null;

    public static D3D11Device Device => _device;
    public static DeviceContext Context => _ctx;
    public static VideoDevice VideoDevice => _videoDevice;
    public static VideoContext VideoContext => _videoContext;
    public static DXGIDeviceManager DxgiManager => _dxgiManager;

    /// <summary>
    /// 取回共享设备，第一次调用时创建。
    /// 失败只返回 false，不抛——上层据此退回原播放器。
    /// </summary>
    public static bool Acquire()
    {
        if (_device != null) return true;

        lock (_sync)
        {
            if (_device != null) return true;
            if (_tried && _device == null) return false;   // 已确认建不出来，不再重试

            _tried = true;
            try
            {
                // MF 平台跟设备同寿命：每次预览 Startup/Shutdown 会跟
                // 「另一个引擎可能正在解码」打架（关掉 A 的时候顺手把 B 的平台关了）。
                MediaManager.Startup();

                var dev = new D3D11Device(DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    FeatureLevel.Level_11_0);

                // 这个设备被三方共用：渲染线程、MF 的 DXVA 工作队列、DXGIDeviceManager。
                // 不开多线程保护，_ctx.Flush() 会在驱动内部永久阻塞。
                using (var mt = dev.QueryInterfaceOrNull<Multithread>())
                {
                    mt?.SetMultithreadProtected(true);
                }

                var ctx = dev.ImmediateContext;
                var videoDevice = dev.QueryInterface<VideoDevice>();
                var videoContext = ctx.QueryInterface<VideoContext>();

                var manager = new DXGIDeviceManager();
                manager.ResetDevice(dev);

                _device = dev;
                _ctx = ctx;
                _videoDevice = videoDevice;
                _videoContext = videoContext;
                _dxgiManager = manager;

                LastError = null;
                ViewerPanel.EngineLog($"gpu context 已建立  adapter=vendor ok  " +
                                      $"threads={Process.GetCurrentProcess().Threads.Count}");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                ViewerPanel.EngineLog("gpu context 建立失败: " + LastError);
                try { _dxgiManager?.Dispose(); } catch { }
                try { _videoContext?.Dispose(); } catch { }
                try { _videoDevice?.Dispose(); } catch { }
                try { _ctx?.Dispose(); } catch { }
                try { _device?.Dispose(); } catch { }
                _dxgiManager = null;
                _videoContext = null;
                _videoDevice = null;
                _ctx = null;
                _device = null;
                return false;
            }
        }
    }
}
