// SharedSurfaceHost —— 父进程这边的「半个显示链路」
//
// 引擎搬到子进程之后，画面不再经由本进程的 D3D11 设备，而是：
//   子进程：D3D9Ex 建共享表面 → D3D11 把帧写进去 → 交出共享句柄
//   本文件：拿句柄用**自己的** D3D9Ex 设备 CreateTexture → GetSurfaceLevel(0)
//          → 那个 IDirect3DSurface9 就是 D3DImage.SetBackBuffer 要的东西
//
// 实测（_ql_xproc_spike parent9 回路）：子进程改一次颜色，父进程立刻读到，
// 说明两块「纹理对象」指向同一块显存，零 CPU 拷贝。
//
// 设备每个预览建一套（和引擎原来的做法一致，实测这条路本身不漏），
// 窗口尺寸变了就换一张表面、设备不动。
using System;
using System.Diagnostics;
using D3D9 = SharpDX.Direct3D9;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class SharedSurfaceHost : IDisposable
{
    private D3D9.Direct3DEx _d3d9;
    private D3D9.DeviceEx _device9;
    private D3D9.Texture _texture9;
    private D3D9.Surface _surface9;
    private IntPtr _handle;
    private int _width;
    private int _height;

    public IntPtr SurfacePointer => _surface9?.NativePointer ?? IntPtr.Zero;
    public int Width => _width;
    public int Height => _height;
    public bool HasSurface => _surface9 != null;

    /// <summary>建设备。窗口句柄只用来满足 D3D9Ex 的创建条件，我们从不 Present。</summary>
    public bool Create(IntPtr hwnd)
    {
        try
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

            // Multithreaded 必须有：WPF 的合成线程也会碰这个表面
            _device9 = new D3D9.DeviceEx(_d3d9, 0, D3D9.DeviceType.Hardware, hwnd,
                D3D9.CreateFlags.HardwareVertexProcessing
                | D3D9.CreateFlags.Multithreaded
                | D3D9.CreateFlags.FpuPreserve,
                new[] { present });
            return true;
        }
        catch (Exception e)
        {
            VideoEngine.Log("SharedSurfaceHost 建设备失败: " + e.Message);
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// 打开子进程交过来的共享表面。返回 true 表示表面换了（调用方要重新挂 D3DImage）。
    /// </summary>
    public bool Open(IntPtr handle, int width, int height)
    {
        if (_device9 == null) return false;
        if (handle == IntPtr.Zero || width <= 0 || height <= 0) return false;

        if (_surface9 != null && handle == _handle && width == _width && height == _height)
            return false;

        ReleaseSurfaceOnly();

        IntPtr h = handle;
        try
        {
            _texture9 = new D3D9.Texture(_device9, width, height, 1,
                D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref h);
            _surface9 = _texture9.GetSurfaceLevel(0);
            _handle = handle;
            _width = width;
            _height = height;
            return true;
        }
        catch (Exception e)
        {
            VideoEngine.Log($"SharedSurfaceHost 打开共享表面失败 {width}x{height} " +
                            $"handle=0x{handle.ToInt64():X}: {e.Message}");
            ReleaseSurfaceOnly();
            return false;
        }
    }

    /// <summary>只放开本进程对共享表面的引用，D3D9Ex 设备留着（下次直接用）。</summary>
    public void ReleaseSurfaceOnly()
    {
        try { _surface9?.Dispose(); } catch { }
        _surface9 = null;
        try { _texture9?.Dispose(); } catch { }
        _texture9 = null;
        _handle = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        ReleaseSurfaceOnly();
        try { _device9?.Dispose(); } catch { }
        _device9 = null;
        try { _d3d9?.Dispose(); } catch { }
        _d3d9 = null;
    }
}
