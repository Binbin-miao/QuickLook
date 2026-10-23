// IVideoEngine —— 面板只认这一层，底下是「在进程内跑」还是「跑在子进程里」它不需要知道。
//
// 为什么要这层：D3D11 设备一旦交给 Media Foundation 硬解过，进程内就放不掉
// （见 GpuContext 顶部注释与 docs/PENDING.md）。真要把那 150~200MB 还回去，
// 只能把整套显卡栈搬到一个能随手杀掉的子进程里，主程序永远不退出。
//
// 成员就是 ViewerPanel 实际用到的那批，多一个都不要加：接口越窄，代理越好维护。
using System;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal interface IVideoEngine : IDisposable
{
    // ---------- 配置（打开前设） ----------
    bool VsrEnabled { get; set; }
    int VsrCapHeight { get; set; }
    int VsrQuality { get; set; }
    int DownscaleAlgo { get; set; }
    bool Loop { get; set; }
    double Rate { get; set; }
    float Volume { get; set; }
    bool Muted { get; set; }
    Action<string> DiagSink { get; set; }
    Func<string> DiagExtra { get; set; }

    // ---------- 生命周期 ----------
    bool Open(string path, IntPtr hwnd);
    void Start();
    void Seek(TimeSpan position);

    // ---------- 状态 ----------
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    int SourceWidth { get; }
    int SourceHeight { get; }
    int OutputWidth { get; }
    int OutputHeight { get; }
    bool IsPaused { get; set; }
    bool VsrActive { get; }
    bool VsrReady { get; }
    bool AudioAvailable { get; }
    bool MultithreadProtected { get; }
    uint AdapterVendor { get; }
    bool IsNvidiaAdapter { get; }
    string LastError { get; }

    // ---------- 输出表面（给 D3DImage） ----------
    IntPtr SurfacePointer { get; }
    int SurfaceWidth { get; }
    int SurfaceHeight { get; }

    // ---------- VSR ----------
    bool ProbeVsr();

    /// <summary>初始化 NGX。返回 true 表示表面换了，调用方要重新挂 D3DImage。</summary>
    bool ActivateVsr();

    void SetQuality(int quality);

    /// <summary>换 VSR 上限/质量/开关。返回 true 表示表面换了。</summary>
    bool Reconfigure(int capHeight, int quality, bool enabled);

    /// <summary>改显示尺寸。返回 true 表示表面换了，调用方要重新挂 D3DImage。</summary>
    bool SetTargetSize(int width, int height);

    /// <summary>每渲染完一帧触发（可能在后台线程）。</summary>
    event Action FrameRendered;
}
