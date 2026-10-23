# 待修复事项

当前版本：`4.5.0-14-g0792ffd`。

「每预览一次 +250MB 显存 / +55 线程、关掉也不回落」已经修掉（显卡设备改全进程共用一套），
重启相关的弹窗也处理了一半。下面是还没做完的，按优先级排列。

---

## P0 · 引擎拆独立进程（真正的「只重启内核」）

**为什么必须拆**

一个 D3D11 设备只要交给 Media Foundation 硬解过，驱动层就不再回收它。两种关闭顺序都实测过：

| 顺序 | 每轮泄漏 |
|---|---|
| 先放设备、后关 MF 平台 | +21 线程 / +8MB 显存 / +15MB 内存 |
| 先关 MF 平台、后放设备 | +21 线程 / +25MB 显存 / +80MB 内存 |

`Dispose`、强制 GC、`MFShutdown`、`ResetDevice(null)`（直接被拒 `E_INVALIDARG`）全部无效。
**进程内无解**，只能靠「进程消失」把它带走。

**现在的代价**

所有预览关掉、闲置 30 秒会重启整个 QuickLook，副作用有两个：

1. QuickLook 自己的 `APP_START` toast「QuickLook 正在后台运行」——**已修**（带 `--ql-restart` 时不再弹）；
2. 游戏++ 把 `QuickLook.exe` 当游戏监控，进程一退就弹性能报告——只有「进程不退出」能根治。

**技术底座已验证**（`_ql_xproc_spike/`，GREEN）

```
子进程  D3D11 OptionFlags.Shared 纹理 → SharedHandle 数值直接交给父进程
父进程  D3D9Ex CreateTexture(ref handle) 打开同一张表面
        → 拿到 IDirect3DSurface9（D3DImage 要的就是这个）
        → GetRenderTargetData 读回：BGRA=0,0,255 红 ✓
子进程改成绿 → 父进程再读：BGRA=0,255,0 绿 ✓
```

三个坑记下来，别再踩：

- SharpDX 4.2 的 `Resource1.CreateSharedHandle` 在本机三种参数组合全报 `E_INVALIDARG`，**别用它**；
- `SharedHandle` **不能**用 `DuplicateHandle` 搬（它不是真实内核句柄，报 `ERROR_INVALID_HANDLE`），
  直接把数值传给另一个进程的 D3D9Ex 就行；
- 读回共享渲染目标要用 `GetRenderTargetData`，`StretchRectangle` 会报 `D3DERR_INVALIDCALL`；
- 枚举是 `D3D9.Pool.SystemMemory`（不是 `SystemMem`）。

**清单**

- [ ] 子进程宿主 exe：复用 `VideoEngine`，加一层管道循环（open / seek / pause / rate / volume / VSR / targetSize）
- [ ] 父进程 `SharedSurfaceHost`：用 D3D9Ex 打开共享句柄，窗口尺寸变化时重建
- [ ] 抽 `IVideoEngine` 接口 + `VideoEngineProxy`（约 25 个成员转发，帧通知走命名事件）
- [ ] 面板切到接口，默认走子进程；保留进程内路径作为开关兜底
- [ ] 空闲回收从「重启整个程序」改成「关掉子进程」
- [ ] 回归：`vsr` / `algo` / `storm` / `rate` / `nosupport` 五条回路 + UI 宿主菜单连点 + 真机开片关片

**验收标准**

连续开关 10 部片：主进程 PID 不变，线程 / 显存 / 内存曲线是一条直线；空闲后子进程消失、资源归零；
全程零弹窗。

预估 2~4 小时连续作业，期间安装版必须保持可用，验收通过才切默认路径。

---

## P1 · 真机上每预览还有 5~10MB 的水位漂移

引擎层已经是一条直线（第 3 轮起 74 线程 / 246MB 显存 / 488MB 内存不动），
但挂上真实 WPF 面板之后，每开一部片内存还会涨 3~11MB、句柄 +2。
来源大概率在 `D3DImage` / WPF 的 D3D9 桥那一侧，没继续深挖。空闲重启会一次性清掉，不累积。

## P1 · 发版：MSI 同版本号不覆盖安装

`msiexec REINSTALL=ALL REINSTALLMODE=vomus` 之后文件哈希没变，最后是直接拷 `Build/Release`
覆盖安装目录，才让安装版=构建产物。发版要么 bump `<Product Version>`，要么文档写清「先卸载再装」。

## P2 · 4K 素材的常驻占用没实测

共用设备之后，常驻显存跟「最后播放的那部片」的分辨率有关。1080p 实测 40MB 左右，
4K 源（VSR 输出到 2160 高）没单独量过，需要补一组数据。

## P2 · 游戏++ 误判 QuickLook.exe

外部工具行为，代码侧只能靠「进程不退出」根治（见 P0）。临时缓解：在游戏++里排除 `QuickLook.exe`。

## P3 · 音频预览自动聚焦（历史遗留，此前要求暂缓）

`Plugin.cs` 的音频分支也设 `FocusOnOpen = true` 即可，影响面小。

---

## 已经修掉的（留个记录，别改回去）

- 每预览一套新设备 → 全进程共用一套（`Engine/GpuContext.cs`），引擎只借不放；
- 预卷丢帧分支 `return` 时漏一张整帧纹理（注释写着「tex 由 using 释放」，using 在后面）；
- 解码纹理每帧 new 一张 → 按尺寸复用；
- 缩放着色器每预览编译两遍 → 进程内编译一次；
- `AudioOutput` 的 `VoiceNotify` 没有 Dispose（GCHandle 泄漏）；
- 自重启抢在旧进程释放 Mutex 之前 → 带 `--ql-restart`，等 Mutex 空出来再接管；
- 自重启弹「QuickLook 正在后台运行」toast → 带标记时跳过。
