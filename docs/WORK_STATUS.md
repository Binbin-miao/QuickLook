# QuickLook VideoViewer + RTX VSR 工作状态

> 最后更新：2026-10-25 09:15
> 这份文档记录当前进度、已做改动、实测数据和后续待办。
> 维护须知（硬性不变量）在仓库根目录的 `AGENTS.md`，两者互补：本文档讲"做到哪了"，AGENTS.md 讲"什么不能碰"。

---

## 一、项目一句话

把 QuickLook 的视频预览换成自建引擎，链路是：

```
MF(DXVA) 解码 → NV12 → VpBlt 转 BGRA（展开 16-235）→ [NGX VSR] → 降采样 → D3D11 共享纹理
→ 跨进程 → D3D9Ex 表面 → D3DImage → WPF → 屏幕
```

引擎跑在独立子进程 `QuickLook.VideoEngineHost.exe`，主程序永不重启；「回收」= 关掉这个子进程。

---

## 二、当前进度

| 工作项 | 状态 | 验证方式 |
|---|---|---|
| 色彩范围（16-235 → 0-255） | ✅ 完成 | dump 直方图：黑 16→0，白 234→254 |
| 打开速度（NGX 不挡首帧） | ✅ 完成 | 1096ms → 576ms |
| 暂停态 seek 精度 | ✅ 完成 | 落点误差 6.1s → 0.1s |
| 按住方向键连发 | ✅ 完成 | 25 次连发落点误差 0.1s，出帧 26 次 |
| VSR 强制按档位超分 | ✅ 完成 | 1080p 源输出 3840×2160（原来根本不超分） |
| 色彩空间切 v1 接口 | ✅ 完成 | `接口=v1`，输出与 v0 一致（黑 0 / 白 254） |
| HDR 识别 + tone mapping | ✅ 代码完成 | 管线连通（`hdr=on tm=ready`），**待真 HDR 素材验证效果** |
| 双层 VSR（实验） | ✅ 完成并实测 | 单级 28.26dB vs 双层 27.19dB，**级联更差** |

---

## 三、已做改动清单

### 3.1 色彩空间（`Engine/VideoEngine.cs`）

| 位置 | 改动 |
|---|---|
| `ReadColorMetadata()` | 从原生媒体类型读 `MF_MT_YUV_MATRIX` / `MF_MT_VIDEO_NOMINAL_RANGE` |
| `DecideColorSpace()` | 判定矩阵与范围；流里没写按惯例：≥720p→BT.709、SD→BT.601，范围一律 16-235 |
| `BuildColorSpace()` / `MakeColorSpace()` | 组 `DXVA2_ExtendedFormat`（v0 位域）。SharpDX 没暴露字段，用 `Marshal` 按 4 字节布局构造 |
| `TrySetStreamColorSpace()` | 两趟 Blt 分别声明：第一趟 NV12(studio+矩阵)，第二趟 RGB(full) |
| `CreateVideoProcessor` 之后 | 输出声明 RGB full |
| 失败策略 | 任一环节失败 → `_colorSpaceUsable=false` 整体回退，绝不每帧抛异常 |

**为什么必须声明**：NV12 是有限取值，不声明时 NVIDIA 驱动按 0-255 直通，
16-235 的片整幅画面卡在 16..234（实测 119 万像素挤在 16）。这就是"黑色不黑"。

### 3.2 交互性能（`Engine/VideoEngine.cs` + `ViewerPanel.xaml.cs`）

| 位置 | 改动 |
|---|---|
| 暂停态 seek 分支 | 改成两段式：拖动中（<180ms）只跳关键帧（3ms 出画面），停手后追精确位置（累计 ~280ms，误差 ≤0.1s） |
| `Seek()` | 记录 `_lastSeekAt`，渲染线程据此判断"还在拖"还是"停手了" |
| `Position` getter | `!_paused` 才用音频时钟；暂停时只信视频位置 |
| `ProbeVsr()` | 只做厂商门控（`VsrAdapterVendor`），**不再初始化 NGX** |
| `SetupPipeline()` | 删掉 Open 里的 NGX 初始化 |
| `ViewerPanel.TryStartEngine` | 画面出来之后用 `DispatcherPriority.ApplicationIdle` 调 `ActivateVsr()` |

### 3.3 VSR 倍率（`EffectiveVsrScale`）

删掉"不超过显示区域需要"的夹紧，改为按用户选的档位老实超分，只夹 NGX 的 4x 上限。

| 源 | 改前 | 改后 |
|---|---|---|
| 1920×1080（4K 档） | 1920×1080（倍率 0.955，不启用） | **3840×2160** |
| 848×478（4K 档） | 1834×1034 | **3392×1912** |

代价：GPU 开销约 4 倍。实测 4K 超分下播放仍 25.4fps，seek 3ms 无退化。

### 3.4 探针与工具

| 文件 | 用途 |
|---|---|
| `_ql_probe/EngineRun.cs` → `PerfTest` | 交互基线：打开首帧 / 单次 seek（首帧+落点误差）/ 连发 seek |
| `_ql_probe/Program.cs` | 新增 `perf [novsr]` 模式 |
| `_ql_ui_harness/Program.cs` | `shot` 改为 GDI 截屏（RenderTargetBitmap 抓不到 D3DImage）；新增 `QL_CMP=1` 双路径截图模式 |
| `_tools/_color_full.py` / `_color_dark.py` / `_color_map.py` | 直方图、暗部细节、两帧线性拟合 |
| `_tools/_sps_color.py` / `_mp4_color.py` | 解析 MP4 box 与 H.264 SPS VUI，判断源有没有色彩标记 |
| `_tools/_vsr2_psnr.py` | 单级/双层 VSR 与原片的 PSNR 对比 |
| `Engine/ToneMapper.cs` | HDR → SDR 的 tone mapping pass（新文件） |
| `_tools/cmp_paths.py` | 真实 QuickLook 双路径截图（当前受限于资源管理器选中，未跑通） |

---

## 四、实测基线（179s / 25fps / 1920×1080 素材）

```
打开到首帧    Open 455ms + 首帧 135ms = 590ms   （VSR 开，后台激活 466ms）
单次 seek     首帧 2~4ms，落到精确位置 ~280ms，误差 ≤0.10s
连发 25 次    1166ms 内出帧 26 次，落点偏差 -0.1s，松手后追上 342ms
播放帧率      25.4fps（4K 超分下仍达标）
```

复现命令：

```bash
bash _tools/regress.sh "素材路径"          # 五条回路
./_ql_probe/bin/Release/net462/Probe.exe "素材" perf vsr    # 交互基线
./_ql_probe/bin/Release/net462/Probe.exe "素材" dump <dir> 2160 5   # 导三路帧
```

---

## 五、本轮新增：v1 / HDR / 双层 VSR

### 5.1 v1 色彩空间接口（`VsrBridge.cpp` + `VideoEngine.cs`）

新增三个 C 导出（**不在 C# 手写 COM vtable**，索引错一位就是 AV）：

| 导出 | 作用 |
|---|---|
| `VpHasColorSpace1(ctx)` | 探测有没有 `ID3D11VideoContext1`（Win10 15063+） |
| `VpSetStreamColorSpace1(...)` | 声明输入色彩空间 |
| `VpSetOutputColorSpace1(...)` | 声明输出色彩空间 |

参数是**语义化整数**（matrix: 0=601/1=709/2=2020，transfer: 0=G22/1=PQ/2=HLG），
由 C++ 映射成 `DXGI_COLOR_SPACE_TYPE`，避免 C# 传错枚举值。

策略：v1 优先 → 拿不到接口或调用失败就整条回退 v0（`_colorSpace1Broken`）。
实测 v1 与 v0 的像素输出一致，没有二次压缩。

### 5.2 HDR 支持

链路：读元数据 → v1 声明 HDR → **tone map** → VSR → 缩放。

| 环节 | 实现 |
|---|---|
| 识别 | `MF_MT_TRANSFER_FUNCTION`：15=PQ、16=HLG；`MF_MT_VIDEO_PRIMARIES`：9=BT.2020（值来自 `mfobjects.h`，别靠记忆） |
| 声明 | v1 的 `YCBCR_STUDIO_G2084_LEFT_P2020`（PQ）/ `YCBCR_STUDIO_GHLG_TOPLEFT_P2020`（HLG） |
| 转换 | 新文件 `Engine/ToneMapper.cs`：PQ/HLG EOTF → Hable 曲线 → sRGB。峰值 1000nits，SDR 白点 203nits |

**位置**：tone map 必须在 VSR **之前**。NGX 只认 SDR 8bit RGB，
把 PQ 编码的值喂进去，它会把亮度编码当画面内容。

**SDR 片一步都不走这条路**（实测平均亮度 123.50，与改动前的 124.19 一致）。

诊断开关：`QL_FORCE_HDR=1` 把 SDR 内容强行按 PQ 跑一遍，用来验证管线连通。
注意它的灵敏度有限——PQ 解码 SDR 中灰恰好接近恒等（124.19 → 127.02），
看不出大变化是正常的，不能当成"没生效"。

### 5.3 双层 VSR（实验，默认关）

`QL_VSR2=1` 打开。把总倍率开平方，分两级跑（中间结果进 `_vsrMidTex`，按 4K 预分配）。

**实测（480p → 1080p，参考 = 1080p 原片，PSNR 越高越好）：**

| 方案 | PSNR | 边缘能量 |
|---|---|---|
| 单级 VSR | **28.26 dB** | 4.556 |
| 双层 VSR | 27.19 dB | 4.852 |

**级联更差 1.07 dB（误差多约 11%）**，虽然边缘能量更高——典型的「更锐但更假」：
第二级把第一级的推断伪影当真细节放大，锐度上去了，离原片更远。

结论：**保持默认单级**，这个开关只作研究用途。

---

## 六、已知问题与待办

### P0 · 真 HDR 素材验证（等素材）

代码链路已通并且**数学验证过**，但缺一部真正的高光 HDR 片。

已做：
- 用 ffmpeg 把 SDR 片转成合法 HDR10（HEVC 10bit / BT.2020 / PQ / TV range，ffprobe 确认），
  引擎识别为 `BT.2020 STUDIO PQ(HDR10)`、走 v1、`hdr=on tm=ready`，不崩。
- **映射精度验证**：实测线性亮度比 0.4825，理论值 0.4926（100nits 内容 → 203nits 白点），
  误差 2%，说明 PQ 解码 / 白点归一化 / 线性转 sRGB 三步都对。

仍未验证：**高光滚降的观感**。转出来的测试片内容白点只有 100nits（`zscale` 的 `npl` 不生效，
第一级和第二级都试过），峰值到不了 203nits 以上，所以软膝曲线实际没被触发。

需要一部真实 HDR10 片（峰值 1000nits 左右）才能看出高光是否自然、暗部有没有压死。
参数现在是写死的：peak 1000nits、SDR 白点 203nits。

### P1 · tone map 曲线已从 Hable 换成软膝

第一版用 Hable + 按峰值归一化，实测把参考白压到 0.43——画面暗一半、高光 230 以上全丢。
现在改成：`x = nits / white`，x ≤ 1 完全线性通过（SDR 内容 bit-exact），
x > 1 用 `1 - exp(-(x-1)*0.7)` 指数软膝渐进到 1.0。Hable 保留在文件里备查。

### P1 · 色彩路径的未解之谜

用户反馈"S/W 的黑不如 H/W 黑"。已确认：
- H/W 实测输出 0..254（正确展开）
- S/W（LAV + EVR）无法回读像素，无法直接量

**待办**：需要一次能跑通的 UI 截图对比（`cmp_paths.py` 卡在资源管理器选中）。
或者从 EVRPresenter 的实现侧确认它有没有做范围展开。

### P2 · 其他

- `LoadAndInsertGlassLayer` 在自建宿主里会抛 `序列不包含任何匹配元素`
  （因为找不到主程序程序集）。真实 QuickLook 里不会抛，属宿主环境噪音。
- N 卡但 NGX 不可用时，现在留在 H/W 硬解 + 按钮置灰；非 N 卡才整条退回原播放器。
  这是语义变更，已记入 AGENTS.md。

---

## 七、构建与部署

```bash
bash _tools/build_bridge.bat        # C++ 桥接层（改 VsrBridge.cpp 后必须）
bash _tools/build_plugin_staging.sh # 插件 → Build/_staging
bash _tools/build_probe.sh          # 探针（会从源码重新复制插件 DLL）
bash _tools/build_harness.sh        # UI 宿主
bash _tools/deploy_all.sh           # staging → Build/Release 运行目录
```

**构建纪律**：改完插件后必须走 `build_probe.sh` 重新编译探针，
它会用 MSBuild 从源码复制 DLL。手动 `cp` 到 bin 目录会被随后的构建覆盖回旧版
（这一轮踩过，表现为"改了代码但运行时行为没变"）。

安装目录：`%LOCALAPPDATA%\Programs\QuickLook\QuickLook.Plugin\QuickLook.Plugin.VideoViewer\`
替换 DLL 后必须重启 QuickLook（主程序常驻，不重启加载不到新程序集）。

---

## 八、配置项速查

`%APPDATA%\pooi.moe\QuickLook\QuickLook.Plugin.VideoViewer.config`

| 键 | 含义 |
|---|---|
| `UseHardwareAcceleration` | 走新引擎（H/W）还是原播放器（S/W，LAV 软解） |
| `VsrEnabled` | VSR 开关 |
| `VsrCap` | 输出高度上限：1080 / 1440 / 2160（不是倍率） |
| `VsrQuality` | 1~4 |
| `VsrAlgo` | 0=驱动 VpBlt，1=Spline36，2=Jinc3 |
| `VsrScale` | **已废弃**（老倍率键），一律忽略 |

## 九、诊断用环境变量

这些只影响诊断，不进正式路径，不改界面：

| 变量 | 作用 |
|---|---|
| `QL_VSR2=1` | 双层 VSR（实验，实测更差，仅用于对比研究） |
| `QL_FORCE_HDR=1` | 把 SDR 内容强行按 PQ 跑一遍 tone map，验证管线连通 |
