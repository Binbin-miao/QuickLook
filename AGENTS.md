# QuickLook VideoViewer + RTX VSR —— 维护须知

这个插件把 QuickLook 的视频预览换成自建引擎：MF(DXVA) 解码 → D3D11 处理 →
[NGX VSR 超分] → [驱动/着色器降采样] → D3D9Ex 共享表面 → WPF D3DImage。

链路很长，而且大量问题只在真机、真窗口、真 GPU 上才暴露。下面每一条都是实际踩过的坑。

## 先跑回归，再改代码

```bash
bash _tools/regress.sh                 # 一条命令，五条回路，红即失败
bash _tools/regress.sh "某个视频.mp4"   # 指定素材
```

它依次检查：着色器 ASCII → 编译 → 四条引擎回路（vsr/algo/storm/rate）→
真实面板里用真实按钮事件驱动一遍 VSR 菜单。

日志在 `%TEMP%\quicklook-vsr.log`。`[beat]` 每秒一条：

```
[beat] frames=352 pos=14.05s vpos=14.04s aclock=1 vsr=on out=1834x1034 algo=1 dirty=351 uiPos=14.05s front=ok pending=0 target=False err=-
```

三个数字分工明确，不要混着看：

| 现象 | 含义 |
|---|---|
| `frames` 不涨 | 引擎没出帧（渲染线程卡住或解码停了） |
| `dirty` 不涨而 `frames` 涨 | 引擎出了帧但 UI 没重绘（D3DImage / 帧回调门闩） |
| `pos` 不涨而 `frames` 涨 | 时钟坏了（音频时钟停住） |

`[stall]` 会带上 `at=` 说明卡在哪一步：`nv12->bgra` / `ngx-evaluate` /
`final-scale` / `copy-to-shared` / `flush`。

## 引擎跑在子进程里（这决定了一大半约束）

主程序（QuickLook.exe）**永不重启**，整套显卡栈住在 `QuickLook.VideoEngineHost.exe`。
「回收」= 把这个子进程关掉，主进程一根线程都不动。

```
子进程  D3D11 设备 + MF 解码 + NGX + 缩放 + 音频 + 时间轴
        输出表面：D3D11 共享纹理（OptionFlags.Shared）→ 交出 SharedHandle
父进程  用自己的 D3D9Ex CreateTexture(handle) 打开同一块显存 → IDirect3DSurface9 → D3DImage
        控制命令走命名管道；帧号/播放位置走共享内存；每帧 Set 一次命名事件
```

排错先看 `%TEMP%\quicklook-vsr-host.log`（子进程自己的日志）和 `%TEMP%\quicklook-vsr.log`（主程序）。

- **画面零拷贝**：只有句柄/帧号在进程间传，像素一直在显存里。实测子进程改一次颜色，
  父进程 `GetRenderTargetData` 立刻读到。
- **子进程里绝不能建 D3D9Ex**（`VideoEngine.SharedOutputOnly = true`）。实测它建第二张共享
  表面时会吊死 30 秒以上不返回，而且只在「进程里没有 WPF + DXVA 正在解码」时必现；
  换 D3D11 共享纹理就没这问题，父进程照样能用 D3D9Ex 打开。
- **管道读不许用 StreamReader/StreamWriter**。实测两边一写一读会互锁：子进程卡在
  `WriteLine`，父进程 `Peek()` 一直返回 -1。现在 `LineChannel` 是裸字节读到 `'\n'` 为止。
- **命令必须一问一答**。子进程对每条命令都回一行；只要有一条只发不等，后面所有应答
  就会错位（表现：改尺寸没反应、seek 不生效、最后整个会话卡死）。
- **改尺寸要先把父进程的 D3D9 设备整个拆掉**（`SetTargetSize` 里 `_surface.Dispose()`），
  子进程重建完表面再 `Create` 回来。理由是 D3D9Ex 共享表面只要在别的进程里还挂着，
  新的那张就建不出来。
- 主程序这边只用 D3D9Ex 打开表面（`SharedSurfaceHost`），`Multithreaded` 标志必须有（WPF 合成线程也会碰）。

## 硬性不变量

**设备与线程**

- **D3D11 设备是全进程共用的（`Engine/GpuContext.cs`），引擎只是借用，`Dispose` 里绝不能放。**
  这条是实测数据支撑的硬约束：一个设备**只要交给 MF 硬解过**，驱动层就不再回收它。
  每预览新建一个 = 每预览漏 21 线程 / 8MB 显存 / 15MB 内存（接进真实引擎是 55 线程 /
  250MB 显存 / 350MB 内存），`Dispose` + 强制 GC + `MFShutdown` + `ResetDevice(null)`
  全部无效（后者本身就被拒，`E_INVALIDARG`），只有进程退出才清。
  证据留在 `_ql_probe layer <dev|dev9|mf|mfsw|mfkeep|mfkeeprel>`，别把这条改回去。
- 因此「把资源还给系统」只有一条路：**杀掉引擎子进程**。`ViewerPanel` 在最后一个面板关掉
  **30 秒**后调用 `VideoEngineProxy.ShutdownHost()`；主程序不重启，托盘不闪，
  游戏++ 也不会再把它当「程序结束」。别在 `Dispose` 里放设备，也别再引入「重启整个程序」那种兜底。
- **自重启必须带 `--ql-restart`**。宿主是单实例（`QuickLook.App.Mutex`），新进程若在旧进程
  还没走完 `OnExit` 时抢跑，会命中 `App.EnsureFirstInstance` 的重复分支，弹出
  「另一个 QuickLook 进程正在运行」。带标记的实例改为每 120 ms 关掉自己的句柄重建命名 Mutex，
  等到旧进程放手再接管（最多 20 秒，超时就安静退出，绝不弹窗）。
  **这里有个坑**：不先 `Dispose` 自己那份句柄，命名对象会活过旧进程，`createdNew` 永远 false。
  验证脚本：`_tools/restart_handshake.py`（对照 + 握手）、`_tools/recycle_stability.py`（端到端无弹窗）。
- 解码缓冲 `_decodeTex` 按尺寸复用，不要改回每帧 `new` 一张：25~60fps 下就是每秒几十次
  整帧分配，驱动分配器只涨不缩。**预卷丢帧那条分支**以前直接 `return`，注释写着
  「tex 由 using 释放」但 `using` 在后面——每丢一帧漏一张整帧纹理，seek 一次能漏几十张。
- 缩放着色器只编译一次（`Scaler.EnsureBytecode`）。字节码是 `CompilationResult` 里的 Blob，
  放掉 `CompilationResult` 再建 `VertexShader` 就是野指针，所以那两个结果对象故意静态留着。
- D3D11 设备被三方共用：渲染线程、MF 的 DXVA 工作队列、DXGIDeviceManager。
  **必须** `ID3D11Multithread::SetMultithreadProtected(true)`，否则
  `_ctx.Flush()` 会在驱动内部永久阻塞（表现为画面卡死、渲染锁永不释放）。
- NGX 只能在 UI 线程初始化，且必须先把渲染线程停在闸门外（`_renderGate`）。
  在渲染线程里初始化实测挂死；不关闸门直接初始化也会挂死。
- `VsrShutdown` 绝不能在渲染线程还在 `Evaluate` 时调用。
- `Dispose` 时若渲染线程没退出（`Join` 超时），**本引擎的纹理/视图全部跳过不释放**。
  宁可泄漏，也不要一边释放一边渲染。
- **NGX 是进程级单例，feature 绑死在初始化时的那个 `ID3D11Device` 上。**
  所以「本引擎是否就绪」只能看自己的 `_ngxReady`，**绝不能**拿 `VsrNative.VsrIsReady()`
  当证据——那是全进程状态。第二个视频直接复用它 = 拿旧设备的 feature 求值新设备的
  纹理，实测是访问冲突（进程无提示消失）。一律调 `VsrInit(device)`，同一个设备它
  立刻返回 0，换了设备它自己先关旧的再重开（实测 500ms）。
- 桥接层用 `SRWLOCK` 保护：`VsrEvaluate` 走共享锁，`VsrInit`/`VsrShutdown` 走独占锁。
  两个引擎短暂共存时（关预览→立刻开下一个），没有这把锁就是「一边 Evaluate 一边
  Shutdown」的必崩竞态。

**VSR**

- 不支持的显卡**必须优雅降级，绝不能崩**。三道闸门，缺一不可：
  1. 厂商门控（`VsrBridge.cpp` 的 `GetAdapterInfo` + `VENDOR_NVIDIA`）：问的是
     **渲染设备上的适配器**，不是注册表里装了哪几块卡。双显卡笔记本上核显建了设备，
     注册表照样查得到 N 卡，但 VSR 用不了。非 0x10DE 直接返回 -3，一步都不进 NGX——
     在别家驱动上调 NGX 是未定义行为，返回码都指望不上。
  2. SEH 保护（`SafeNgxInit` / `SafeCreateVsr` / `SafeEvaluateVsr` / `SafeShutdownNgx`）：
     NGX 在不受支持的硬件上可能直接 AV，C++ 的 `try/catch` 抓不到，只有
     `__try/__except` 能。这几个薄壳里**只许有 POD 局部变量**，否则报 C2712。
  3. 失败即清场：`VsrInit` 任何一步失败都调 `VsrShutdownLocked()`。不清的话用户多点
     几次「2x」就漏几次引用。
- `VideoEngine.VsrUnsupported` 是**进程级闩锁**：一旦确认不可用就不再重试（重试要重建
  上下文、走一遍 NGX，慢且没意义）。重启进程自然清空，用户升驱动后能重试。
- 按钮策略：非 N 卡直接隐藏；N 卡但 NGX 说不可用则**保留按钮、置灰、标签 `VSR ✕`、
  原因写进 ToolTip**。直接隐藏会让用户困惑「我刚点的东西呢」。
- 桥接层返回的错误串**必须是宽字符**（`VsrLastError` 返回 `const wchar_t*`，C# 用
  `PtrToStringUni`）。源码是 `/utf-8` 编译，中文若走 `char*` 再按 ANSI 解码全是乱码。

- `_vsrOutTex` 一次性按 4K 分配，换倍率只改每次 `Evaluate` 的 `outW/outH`。
  不要在切换时重建任何东西。
- `EffectiveVsrScale()` 会把倍率**抬到至少铺满显示区域**（上限 4x，VSR 规格）。
  只超分到 2x 而窗口需要 2.16x 时，最后那 1.08x 由双线性补上，AI 细节会被抹掉
  （实测边缘能量 4.10 → 4.47）。
- 菜单第二列是**输出高度的上限**（1K=1080 / 2K=1440 / 4K=2160），不是倍率。
  倍率由 `EffectiveVsrScale()` 从上限换算，再夹两个上界：
  `min(上限, 显示区域需要的倍数)`，最后再夹 NGX 的 4x。
  所以小窗口下三档可能完全一样——这是对的，多产出的像素只会被缩回去。
- 上限语义换了，配置键也从 `VsrScale`（double）换成 `VsrCap`（int），
  老键直接忽略、一律从 4K 起，不做倍率→上限的换算（换算出来的数字是编的）。
- 输出纹理按「4K 上限 + 当前源宽高比」分配（`capW = srcW * 2160 / srcH`），
  不能只按 3840 定宽：21:9 的源按 4K 高度超分会比 3840 更宽，
  那样 outW 被单独夹住、outH 不会，画面直接变形。

**S/W · H/W 是「走哪条播放路径」，不是解码器标志位**

| 档位 | 路径 | VSR |
|---|---|---|
| **H/W** | 新引擎（D3D11 + MF DXVA） | 可用 |
| **S/W** | 原播放器（WPFMediaKit + LAV 软解），**一个 D3D 对象都不建** | 不可用 |

- 默认 `true`（H/W）。**不做老配置迁移**：配置里写着 `false` 就按 S/W 解释——
  用户换硬件、或者想用软解省电，都靠这个档位自己切，代码不去覆盖。
- H/W 模式下如果这台机器跑不了 VSR，**整条退回原播放器**（仍按开关走 LAV 硬解），
  而不是留在新引擎里给一个用不了的按钮。判断靠 `VideoEngine.ProbeVsr()`：
  它**不看用户的 VSR 开关**，只回答「能不能」，必须在 `Start()` 之前调用
  （那时渲染线程还没起来，不需要闸门）。
- VSR 按钮可见性 = `_hasVideo && _engineActive && UseHardwareAcceleration && 卡是 N 卡`。
  S/W 下必须隐藏：那条路径根本没有超分链路。

**切换路径（运行中点开关）**

- 位置和播放状态必须带过去，否则「切一下回到片头」。
- 补位置要等图真的跑起来（开图后约 1.2s，由进度泵触发），只做一次。
  **不能**在 `MediaOpened` 里直接 seek，也**不能**每 100ms 重试：
  DirectShow 图还没跑起来时被反复 seek，实测进程直接消失（EXIT=139）。
- `MediaPosition` 的 setter **自己会下发 seek**。只想更新进度条时必须压 `_suppressSeek`，
  否则「同步 UI」就变成「又 seek 一次」。
- 切到原播放器前必须 `Close()` + `Source = null`：`HardwareAccelerationModeChanged`
  看到 `Source != null` 会走 `ApplyHardwareAcceleration`（关掉重开当前源），
  跟我们马上要设的新源打架，表现是刚 seek 好的位置被重置回 0。

**S/W 模式的进度泵**

- 原播放器没有帧回调，进度只能靠轮询。**三种定时器里只有第三种能用**：
  | 方式 | 结果 |
  |---|---|
  | `DispatcherTimer`（Background / Normal） | 宿主里根本不 tick |
  | `System.Threading.Timer` + `BeginInvoke` | 也不触发（实测连回调都不进） |
  | `CompositionTarget.Rendering` + 限流到 10Hz | **可用**，由合成器驱动 |
- 轮询时**不能**在开图后立刻读 `mediaElement.MediaPosition`：DirectShow 还在过渡状态，
  读了直接访问冲突（进程消失）。开图后留 600ms 冷静期（`_legacyReadyAt`），
  seek 后同样要留。

**缩放路径**

- 驱动路径（VpBlt）写 `_outTex`；着色器路径写 `Scaler.Output`。**两条路写的是
  不同的纹理**，拷到共享表面时必须拷「实际跑的那一条」。拷错的表现是：切到
  Spline36/Jinc3 后画面定格在驱动路径的最后一帧。

**交互（打开 / 拖动 / 按住方向键）**

- **暂停态的 seek 必须两段走**，这是「跟手」和「停得准」的取舍：
  - 拖动中（距上次 seek < 180ms）只解到最近关键帧，一帧就出画面 → 实测 3ms；
  - 停手 180ms 后，从当前解码位置继续解到精确时间 → 累计 250~300ms，误差 ≤0.1s。
  只做前半段 = 画面永远停在关键帧（实测 seek 到 177s 停在 171s，差 6 秒）；只做后半段 =
  连发时每次都要重新定位、一帧都追不完，25 次按键只出 7 帧，反而更卡。
- `SetCurrentPosition` 每次成本约 100ms 量级（MF 重新定位 + 解码器重建），
  这是「按住键卡」的物理下限。别指望优化它，要减少调用次数（上面那套 + 每帧检查有没有新 seek）。
- **NGX 初始化不能挡在首帧前面**。它是 400ms 级的同步操作：
  - 以前 `ProbeVsr()` 里直接 `SetupVsrResources()`，而 `ProbeVsr` 在 `Start()` 之前调，
    于是 VSR 开时打开要 1050ms、关时只要 620ms；
  - 现在 `ProbeVsr()` 只做厂商门控（`VsrAdapterVendor`，微秒级），
    `Open()` 里也不初始化；真正的初始化由 `ViewerPanel` 在画面出来之后
    用 `DispatcherPriority.ApplicationIdle` 调 `ActivateVsr()` 补上（实测 438ms，用户无感）。
  - 「跑不了 VSR 就整条退回原播放器」的语义因此收窄成「**非 N 卡才退回**」；
    N 卡但 NGX 不可用时留在 H/W 硬解 + 按钮置灰，这符合按钮策略那条。
- **暂停时 `Position` 绝不能读音频时钟**。音频线程暂停不推进，它的时钟停在旧位置，
  帧回调把旧值写回进度条，表现就是「拖完弹回原处」。判据：`!_paused` 才用音频时钟。

**UI / 计时**

- `MediaPosition` 的 setter 在 `_suppressSeek == false` 时会下发 `engine.Seek()`。
  帧回调里回写播放位置**必须**压住它，否则每帧一次原地 seek，画面永远停在同一帧。
- `Seek()` 自身会忽略与当前帧相差 300ms 以内的请求（第二层保护）。
- `DispatcherTimer` 默认是 `Background` 优先级，会被 `Render` 优先级的帧回调饿死；
  改成 `Normal` 也不可靠（实测在 QuickLook 宿主里根本不触发）。
  **正确性不要依赖定时器**，用事件 + `Dispatcher.BeginInvoke` 收尾。
- 面板在宿主收起 busy 层之前 `ActualWidth` 恒为 0。`ApplyTargetSize` 必须有
  窗口尺寸兜底，否则共享表面会停在默认尺寸，超分结果再被 WPF 拉伸一次。
- 帧回调门闩 `_framePending` 必须有超时，否则一次 `BeginInvoke` 丢失就永久冻结。

**色彩空间（「黑色不黑」专用）**

- NV12 存的是有限取值：电视范围 16-235 或全范围 0-255。**必须显式告诉视频处理器**，
  否则 NVIDIA 驱动在「未指定」时按全范围直通——16-235 的片子整幅画面卡在 16..234，
  黑是 6% 的灰、白差一口气。实测一部 1080p 片：119 万像素挤在 16、81 万挤在 234。
- 元数据从**原生媒体类型**上读 `MF_MT_YUV_MATRIX` / `MF_MT_VIDEO_NOMINAL_RANGE`；
  读不到（网络压制的 mp4 经常连 VUI 都没有）就按惯例：高度 ≥ 720 用 BT.709、
  否则 BT.601，范围一律 studio。**不要**只按分辨率判而不看元数据。
- 声明走 `VideoProcessorSetStreamColorSpace`（v0，DXVA2_ExtendedFormat 位域）：
  `SampleFormat:8 | Chroma:4 | Range:3 | Matrix:3 | Lighting:4 | Primaries:5 | Transfer:5`。
  这个 SharpDX 版本**没有**包装 `VideoProcessorSetStreamColorSpace1`，
  而 `VideoProcessorColorSpace` 一个字段都没暴露——用 `Marshal` 按 4 字节布局造一个，
  别去猜字段名。
- **两趟 Blt 都要声明，而且必须不同**：第一趟输入是 NV12（声明 studio + 矩阵），
  第二趟输入是 RGB（声明 full）。第二趟漏了就沿用第一趟的 studio 设定，
  展开过的数据会被再压一次。输出侧另调 `VideoProcessorSetOutputColorSpace`（RGB full）。
- 声明失败要**整体回退**（`_colorSpaceUsable = false`）：只关了输入没关输出，
  或者第一趟成功第二趟失败，都会让画面过爆。一次失败就别再试，更不能每帧抛异常。
- v0 接口在某些驱动上可能**静默忽略**（返回成功但画面没变）。改完必须用
  `Probe.exe <file> dump <dir> 2160 5` 导帧看直方图：黑场应该堆在 0 而不是 16。
  只看返回值会误判。

**HDR 与色彩空间 v1**

- **v1 必须在 C++ 桥里调，不要在 C# 手写 COM vtable。** SharpDX 只包装了 v0，
  v1 得自己 `QueryInterface` 拿 `ID3D11VideoContext1` 再算函数索引——索引错一位
  就调到别的方法上，那是 AV（进程直接消失），不是能 catch 的异常。
  `VsrBridge.cpp` 里的 `VpSetStreamColorSpace1` 由编译器按 `d3d11_1.h` 生成调用，不可能错位。
- **上 v1 的唯一硬理由：v0 表达不了 HDR。** `DXVA2_ExtendedFormat` 的传递函数枚举
  只有 gamma 1.0/1.8/2.0/2.2/2.8/709/240M/sRGB，**没有 PQ、没有 HLG**，
  矩阵枚举也只到 SMPTE240M、**没有 BT.2020**。所以「补 HDR」和「切 v1」是同一件事。
- v1 参数用**语义化整数**（matrix 0/1/2、transfer 0/1/2），由 C++ 映射成
  `DXGI_COLOR_SPACE_TYPE`。让 C# 直接传 DXGI 枚举值，传错会静默出错。
- v1 失败要整条回退 v0（`_colorSpace1Broken`），且**只失败一次就不再试**。
- **tone map 必须在 VSR 之前。** NGX 是在 SDR 8bit RGB 上训练的，把 PQ 编码的值
  直接喂进去，它会把亮度编码当成画面内容。位置在 `RenderFrameCore` 的 1.5 步。
- tone map 的输出纹理是 `Run()` 里按需分配的。**不能拿 `Output != null` 当进入门槛**，
  否则第一帧永远不跑、Output 永远不存在（自锁，实测亮度纹丝不动就是这么来的）。
- 三条纹理三种 SRV：VSR 输出 / tone map 输出 / 原始 BGRA。`ScaleWithShader` 里
  认错了就会「拿未 tone map 的 PQ 数据去缩放」，HDR 下直接错。
- 新增着色器同样**不能有非 ASCII**。这一条已经写在「着色器」小节里，还是又踩了一次：
  一行中文注释导致 `error X3004: undeclared identifier`，行号指向莫名其妙的位置。

**双层 VSR（实验）**

- `QL_VSR2=1` 打开，默认关。**实测级联比单级差**：480p→1080p，参考 1080p 原片，
  单级 PSNR 28.26dB、双层 27.19dB。双层边缘能量更高（4.852 vs 4.556），
  正是「更锐但更假」——第二级把第一级的推断伪影当真细节放大。
- 中间纹理按 4K 上限一次性分配，中间尺寸只通过 `inW/inH` 传，不重建纹理。

**Media Foundation**

- `ReadSample` 返回的 `actualIndex` 是**真实流号**，`FirstVideoStream` 是符号索引（-4）。
  拿它去比较会把带音轨的片子整个丢掉。
- MF 的时间单位是 100ns，`TimeSpan` 的 1 tick **也是** 100ns，直接对应，不要再除。
- DXVA 解码纹理高度按 16 对齐（478 → 480）。真实画面尺寸必须在解码**之前**读。
- 解码结果拷贝出来的单切片纹理，`VideoProcessorInputView` 的 `ArraySlice` 必须是 0。

**着色器**

- HLSL 源码里**不能有任何非 ASCII 字符**（包括中文注释）。D3DCompiler 会在多字节处
  截断，报 `error X3000: unexpected end of file`，行号指向文件末尾。
  `_tools/check_shader.py` 会在构建前拦下来。
- `SV_POSITION` 已经是像素中心（`x + 0.5`），不要再加 0.5。
- 中间缓冲必须 `R16G16B16A16_Float`：线性光下 8bit 会把暗部量化（最大误差 15/255）。

## 诊断接口

引擎上留了几个只给探针用的方法，不要在生产路径调用：

- `SharedChecksum()` —— 共享表面抽样校验和，判断画面是否在更新
- `SharedEdgeEnergy()` —— 按原生像素算的相邻梯度，判断锐度
- `DumpShared(path)` —— 整帧导出（前 8 字节是宽高，之后 BGRA），用来做像素级对比
- `Diag` / `DiagExtra` —— 看门狗拼进日志的现场信息

## 验证素材

`D:\Downloads\N卡RTX视频超分辨率测试用片 - 知乎 (1).mp4`（848×478，低分辨率，
最容易看出超分差别）。窗口必须**明显大于源分辨率**才有意义——1280×720 的片子
在 1282×722 的窗口里是 1:1，物理上就看不出来。

### 怎么在 N 卡机器上验证「不支持的显卡不崩」

手上没有 AMD 卡也能把三条路径都跑到：

| 场景 | 做法 | 期望 |
|---|---|---|
| 缺 NGX 运行库 | 把探针目录的 `nvngx_vsr.dll` 改名，跑 `Probe.exe <视频> nosupport` | `VSR=off`、播放继续出帧、第二个视频 0 重试 |
| 非 NVIDIA | 复制 `VsrBridge.cpp` 把 `#define VENDOR_NVIDIA 0x10DE` 改成别的值，单独编一个 DLL 换进去 | 同上，错误串是「非 NVIDIA 显卡 (vendor=…)」 |
| 旧 N 卡（10 系） | 只能真机，或看 `VsrInit` 的 `-2` 分支（`VSR.Available == 0`） | 同上 |

`nosupport` 回路的判定：失败后**仍在出帧**（`frames` 持续增长）、第二个引擎
`ActivateVsr` 走闩锁不再进 NGX。两者任一不满足就是 RED。

UI 侧用宿主验证（`log vsr` 会打出按钮终态）：
```
vsr-button vis=Visible enabled=False label="VSR ✕" tip="VSR 不可用: 非 NVIDIA 显卡 (...)"
```
