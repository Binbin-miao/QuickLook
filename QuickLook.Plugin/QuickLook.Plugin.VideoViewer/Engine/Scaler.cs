// Scaler —— 最终缩放：把 VSR 输出（或原始帧）缩到显示尺寸
//
// 两种滤波器共用一条两趟分离式管线：
//   pass 1  水平：src(W×H) → tmp(dstW×H)
//   pass 2  垂直：tmp(dstW×H) → out(dstW×dstH)
//
// 权重在着色器里按解析式现算，不建权重表：
//   Spline36  3 抽头三次样条，无负瓣、无振铃
//   Jinc3     J1(πx)/(πx) 加 Jinc 窗，锐度更高、有轻微振铃
//
// 缩小时滤波器支撑域按缩放比展宽（taps ≈ 支撑半径 × 缩放比），这是正确的低通；
// 放大时保持原始支撑。滤波在线性光下做：第一趟解码 pow(2.2)，最后一趟编码回 1/2.2。
//
// 注意：着色器源码里不要写非 ASCII 字符，D3DCompiler 会在多字节处截断。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class Scaler : IDisposable
{
    private const string ShaderSource = @"
cbuffer Params : register(b0)
{
    float4 SrcDst;   // activeSrcW, activeSrcH, dstW, dstH
    float4 Dir;      // dirX, dirY, support, filter
    float4 Flags;    // decodeInput, encodeOutput, debug, 0
    float4 TexSize;  // texW, texH (real texture size, may exceed active region)
};

Texture2D SrcTex : register(t0);
SamplerState Samp : register(s0);

struct VSOut
{
    float4 pos : SV_POSITION;
};

VSOut vsMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    return o;
}

float spline36(float x)
{
    x = abs(x);
    if (x < 1.0) return ((13.0 / 11.0 * x - 453.0 / 209.0) * x - 3.0 / 209.0) * x + 1.0;
    if (x < 2.0) return ((-6.0 / 11.0 * (x - 1.0) + 270.0 / 209.0) * (x - 1.0) - 156.0 / 209.0) * (x - 1.0);
    if (x < 3.0) return ((1.0 / 11.0 * (x - 2.0) - 45.0 / 209.0) * (x - 2.0) + 26.0 / 209.0) * (x - 2.0);
    return 0.0;
}

float besselJ1(float x)
{
    float ax = abs(x);
    if (ax < 1e-4) return x * 0.5;

    if (ax < 8.0)
    {
        float y = x * x;
        float a1 = x * (72362614232.0 + y * (-7895059235.0 + y * (242396853.1 + y * (-2972611.439 + y * (15704.48260 + y * (-30.16036606))))));
        float a2 = 144725228442.0 + y * (2300535178.0 + y * (18583304.74 + y * (99447.43394 + y * (376.9991397 + y))));
        return a1 / a2;
    }

    float z = 8.0 / ax;
    float yy = z * z;
    float xx = ax - 2.356194491;
    float b1 = 1.0 + yy * (0.183105e-2 + yy * (-0.3516396496e-4 + yy * (0.2457520174e-5 + yy * (-0.240337019e-6))));
    float b2 = 0.04687499995 + yy * (-0.2002690873e-3 + yy * (0.8449199096e-5 + yy * (-0.88228987e-6 + yy * 0.105787412e-6)));
    float ans = sqrt(0.636619772 / ax) * (cos(xx) * b1 - z * sin(xx) * b2);
    return x < 0.0 ? -ans : ans;
}

float jinc(float x)
{
    if (abs(x) < 1e-4) return 0.5;
    float px = 3.14159265358979 * x;
    return besselJ1(px) / px;
}

float filterWeight(float x)
{
    if ((int)Dir.w == 0) return spline36(x);
    return jinc(x) * jinc(x / 3.0);
}

float4 main(float4 pos : SV_POSITION) : SV_TARGET
{
    float2 srcSize = SrcDst.xy;
    float2 dstSize = SrcDst.zw;
    float2 texSize = TexSize.xy;

    bool horizontal = Dir.x > 0.5;
    float srcLen = horizontal ? srcSize.x : srcSize.y;
    float dstLen = horizontal ? dstSize.x : dstSize.y;
    float p = horizontal ? pos.x : pos.y;

    float scale = srcLen / dstLen;
    float stretch = max(1.0, scale);
    float radius = Dir.z * stretch;
    // pos is already the pixel center (x + 0.5), so no extra half-pixel here
    float center = p * scale - 0.5;

    int first = (int)floor(center - radius);
    int last = (int)ceil(center + radius);

    if (Flags.z > 0.5)
        return float4(scale * 0.25, radius * 0.125, (float)(last - first + 1) * 0.0625, 1.0);

    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float wsum = 0.0;

    [loop]
    for (int i = first; i <= last; i++)
    {
        float w = filterWeight((i - center) / stretch);
        if (w == 0.0) continue;

        // the 1:1 axis uses pixel coords over the real texture size
        float2 coord;
        if (horizontal) { coord.x = (i + 0.5) / texSize.x; coord.y = pos.y / texSize.y; }
        else { coord.x = pos.x / texSize.x; coord.y = (i + 0.5) / texSize.y; }

        float4 s = SrcTex.SampleLevel(Samp, coord, 0);
        if (Flags.x > 0.5) s.rgb = pow(max(s.rgb, 0.0), 2.2);

        sum += s * w;
        wsum += w;
    }

    float4 c = wsum != 0.0 ? sum / wsum : SrcTex.SampleLevel(Samp, pos.xy / texSize, 0);
    if (Flags.y > 0.5) c.rgb = pow(max(c.rgb, 0.0), 1.0 / 2.2);
    return float4(c.rgb, 1.0);
}
";

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float SrcW, SrcH, DstW, DstH;
        public float DirX, DirY, Support, Filter;
        public float Decode, Encode, Debug, Pad0;
        public float TexW, TexH, Pad1, Pad2;
    }

    private readonly D3D11Device _device;
    private readonly DeviceContext _ctx;

    // 着色器只编译一次：以前是每开一次预览就在 D3DCompiler 里编两遍，
    // 既慢（每次几十毫秒）又会让编译器的进程内存一路往上涨。
    // 这里把编译结果（含字节码 Blob）静态留住，每个 Scaler 只是拿它建 D3D 对象。
    private static readonly object _compileLock = new();
    private static ShaderBytecode _vsBytecode;
    private static ShaderBytecode _psBytecode;
    private static string _compileError;
    private static bool _compiled;

    private static bool EnsureBytecode()
    {
        if (_compiled) return _vsBytecode != null;

        lock (_compileLock)
        {
            if (_compiled) return _vsBytecode != null;
            _compiled = true;

            var vsResult = ShaderBytecode.Compile(ShaderSource, "vsMain", "vs_5_0");
            if (vsResult == null || vsResult.HasErrors)
            {
                _compileError = "VS: " + (vsResult?.Message ?? "编译返回空");
                return false;
            }

            var psResult = ShaderBytecode.Compile(ShaderSource, "main", "ps_5_0");
            if (psResult == null || psResult.HasErrors)
            {
                _compileError = "PS: " + (psResult?.Message ?? "编译返回空");
                return false;
            }

            // 故意不 Dispose：Bytecode 指向结果对象里的 Blob，
            // 放掉它之后再建 VertexShader 就是野指针。
            _vsBytecode = vsResult.Bytecode;
            _psBytecode = psResult.Bytecode;
            return true;
        }
    }

    private VertexShader _vs;
    private PixelShader _ps;
    private D3D11Buffer _cb;
    private SamplerState _sampler;
    private RasterizerState _raster;

    private D3D11Texture2D _tmp;
    private D3D11Texture2D _out;
    private ShaderResourceView _tmpSrv;
    private ShaderResourceView _outSrv;
    private RenderTargetView _tmpRtv;
    private RenderTargetView _outRtv;
    private int _tmpH = -1;

    public Scaler(D3D11Device device, DeviceContext ctx)
    {
        _device = device;
        _ctx = ctx;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public Texture2D Output => _out;
    public ShaderResourceView OutputSrv => _outSrv;
    public string LastError { get; private set; }

    public bool Prepare()
    {
        try
        {
            if (!EnsureBytecode())
            {
                LastError = _compileError;
                return false;
            }

            _vs = new VertexShader(_device, _vsBytecode);
            _ps = new PixelShader(_device, _psBytecode);

            _cb = new D3D11Buffer(_device, new BufferDescription
            {
                SizeInBytes = Utilities.SizeOf<Params>(),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
            });

            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MinimumLod = 0,
                MaximumLod = 0,
            });

            _raster = new RasterizerState(_device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                IsDepthClipEnabled = false,
                IsMultisampleEnabled = false,
                IsScissorEnabled = false,
                IsAntialiasedLineEnabled = false,
            });

            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            return false;
        }
    }

    /// <summary>按目标尺寸准备两张中间纹理。tmpHeight 是水平趟输入的高度。</summary>
    public void EnsureTargets(int width, int height, int tmpHeight)
    {
        if (_out != null && Width == width && Height == height && _tmpH == tmpHeight)
            return;

        ReleaseTargets();

        Width = width;
        Height = height;
        _tmpH = tmpHeight;

        // 中间缓冲必须高精度：线性光下 8bit 会把暗部量化掉
        _tmp = new D3D11Texture2D(_device, new Texture2DDescription
        {
            Width = width,
            Height = Math.Max(1, tmpHeight),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _tmpSrv = new ShaderResourceView(_device, _tmp);
        _tmpRtv = new RenderTargetView(_device, _tmp);

        _out = new D3D11Texture2D(_device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _outSrv = new ShaderResourceView(_device, _out);
        _outRtv = new RenderTargetView(_device, _out);
    }

    /// <summary>把 src 缩放到内部输出纹理。filter: 1 = Spline36，2 = Jinc3。</summary>
    public void Run(ShaderResourceView src, int texW, int texH,
                    int srcW, int srcH, int dstW, int dstH, int filter, int debug = 0)
    {
        if (_out == null || _tmp == null) return;

        int f = filter >= 2 ? 1 : 0;

        _ctx.InputAssembler.InputLayout = null;
        _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.VertexShader.Set(_vs);
        _ctx.PixelShader.Set(_ps);
        _ctx.PixelShader.SetConstantBuffer(0, _cb);
        _ctx.PixelShader.SetSampler(0, _sampler);
        _ctx.Rasterizer.State = _raster;
        _ctx.OutputMerger.SetDepthStencilState(null);
        _ctx.OutputMerger.SetBlendState(null);

        // pass 1：水平 src → tmp，顺便解码到线性光
        _ctx.OutputMerger.SetRenderTargets(_tmpRtv);
        _ctx.Rasterizer.SetViewport(0, 0, dstW, Math.Max(1, srcH));
        WriteParams(texW, texH, srcW, srcH, dstW, srcH, 1f, 0f, 3f, f, true, false, debug);
        _ctx.PixelShader.SetShaderResource(0, src);
        _ctx.Draw(3, 0);

        // pass 2：垂直 tmp → out，编码回 sRGB
        _ctx.OutputMerger.SetRenderTargets(_outRtv);
        _ctx.Rasterizer.SetViewport(0, 0, dstW, dstH);
        WriteParams(dstW, srcH, dstW, srcH, dstW, dstH, 0f, 1f, 3f, f, false, true, debug);
        _ctx.PixelShader.SetShaderResource(0, _tmpSrv);
        _ctx.Draw(3, 0);

        _ctx.PixelShader.SetShaderResource(0, null);
        _ctx.OutputMerger.SetRenderTargets((RenderTargetView)null);
    }

    private void WriteParams(int texW, int texH, int srcW, int srcH, int dstW, int dstH,
                             float dirX, float dirY, float support, int filter,
                             bool decode, bool encode, int debug = 0)
    {
        var p = new Params
        {
            SrcW = srcW, SrcH = srcH, DstW = dstW, DstH = dstH,
            DirX = dirX, DirY = dirY, Support = support, Filter = filter,
            Decode = decode ? 1f : 0f, Encode = encode ? 1f : 0f, Debug = debug,
            TexW = texW, TexH = texH,
        };
        _ctx.UpdateSubresource(ref p, _cb);
    }

    private void ReleaseTargets()
    {
        _outRtv?.Dispose(); _outRtv = null;
        _outSrv?.Dispose(); _outSrv = null;
        _out?.Dispose(); _out = null;
        _tmpRtv?.Dispose(); _tmpRtv = null;
        _tmpSrv?.Dispose(); _tmpSrv = null;
        _tmp?.Dispose(); _tmp = null;
        _tmpH = -1;
    }

    public void Dispose()
    {
        try { ReleaseTargets(); } catch (Exception e) { Debug.WriteLine(e.Message); }

        _raster?.Dispose();
        _sampler?.Dispose();
        _cb?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
    }
}
