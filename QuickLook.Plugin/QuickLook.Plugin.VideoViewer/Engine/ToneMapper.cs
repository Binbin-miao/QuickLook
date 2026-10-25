// ToneMapper —— 把 HDR（PQ / HLG）的帧压到 SDR。
//
// 位置很关键：必须插在 VpBlt 之后、VSR 之前。
//   * NGX VSR 是在 SDR 的 8bit RGB 上训练的。把 PQ 编码的值直接喂进去，
//     模型会把「亮度编码」当成「画面内容」，出来的东西既不对也没意义。
//   * 也不能放在缩放着色器里：VSR 开着的时候那一趟根本不跑。
//
// 曲线用 Hable（Uncharted 2）：高光滚降比 Reinhard 平缓，暗部不会糊成一团。
// 逐通道应用会轻微改变饱和度——这是 SDR 映射的常规取舍，先用简单实现，
// 真要调色再换成「按亮度 tone map + 保持色度」的版本。
//
// 注意：着色器源码里不能有任何非 ASCII 字符（包括中文注释）。
// D3DCompiler 会在多字节处截断，报 error X3000 且行号指向文件末尾。
using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class ToneMapper : IDisposable
{
    private const string ShaderSource = @"
cbuffer Params : register(b0)
{
    float4 Info;   // x = transfer (1=PQ, 2=HLG), y = peak nits, z = sdr white nits, w unused
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

// SMPTE ST 2084 (PQ) EOTF: encoded 0..1 -> absolute nits
float3 PqToNits(float3 e)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    float3 p = pow(max(e, 1e-5), 1.0 / m2);
    float3 num = max(p - c1, 0.0);
    float3 den = max(c2 - c3 * p, 1e-5);
    return pow(num / den, 1.0 / m1) * 10000.0;
}

// ARIB STD-B67 (HLG): encoded 0..1 -> scene linear 0..1
float3 HlgToLinear(float3 e)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;
    float3 lo = e * e / 3.0;
    float3 hi = (exp((e - c) / a) + b) / 12.0;
    return lerp(hi, lo, step(e, 0.5));
}

// Hable curve is kept for reference / future tuning.
float3 Hable(float3 x)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

// Soft-knee tone map.
//   x is in units of the SDR reference white (x = 1 means it should look white).
//   At or below 1.0 the value passes through untouched, so SDR-range content is
//   bit-exact. Above 1.0 an exponential shoulder rolls off towards 1.0,
//   keeping highlight detail instead of clipping it to flat white.
//
// Why not Hable: normalising Hable to the peak made x = 1 land near 0.43,
// i.e. reference white came out as mid-grey and every highlight above 230 died.
// That is the wrong contract for an SDR display.
float3 ToneMapKnee(float3 x)
{
    float3 over = max(x - 1.0, 0.0);
    float3 rolled = 1.0 - exp(-over * 0.7);
    return lerp(x, rolled, step(1.0, x));
}

float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(hi, lo, step(c, 0.0031308));
}

float4 main(float4 pos : SV_POSITION) : SV_TARGET
{
    uint w, h;
    SrcTex.GetDimensions(w, h);
    float2 uv = pos.xy / float2(w, h);
    float3 c = SrcTex.SampleLevel(Samp, uv, 0).rgb;

    int transfer = (int)Info.x;
    float peak = max(Info.y, 1.0);
    float white = max(Info.z, 1.0);

    float3 nits;
    if (transfer == 1) nits = PqToNits(c);
    else               nits = HlgToLinear(c) * peak;

    // express in units of SDR reference white, then roll off the highlights
    float3 x = nits / white;
    float3 mapped = saturate(ToneMapKnee(x));

    return float4(LinearToSrgb(mapped), 1.0);
}
";

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector4 Info;
    }

    // 着色器只编译一次（进程级）。和 Scaler 一样：字节码放在静态字段里，
    // 放掉 CompilationResult 再建 Shader 就是野指针。
    private static readonly object CompileLock = new();
    private static ShaderBytecode _vsBytecode;
    private static ShaderBytecode _psBytecode;
    private static bool _compileTried;
    private static string _compileError;

    private static bool EnsureBytecode()
    {
        lock (CompileLock)
        {
            if (_compileTried) return _vsBytecode != null && _psBytecode != null;
            _compileTried = true;

            try
            {
                var vs = ShaderBytecode.Compile(ShaderSource, "vsMain", "vs_5_0");
                var ps = ShaderBytecode.Compile(ShaderSource, "main", "ps_5_0");
                _vsBytecode = vs.Bytecode;
                _psBytecode = ps.Bytecode;
                return true;
            }
            catch (Exception e)
            {
                _compileError = e.Message;
                return false;
            }
        }
    }

    private readonly D3D11Device _device;
    private readonly DeviceContext _ctx;

    private VertexShader _vs;
    private PixelShader _ps;
    private Buffer _cb;
    private SamplerState _sampler;
    private RasterizerState _raster;

    private Texture2D _out;
    private RenderTargetView _rtv;
    private ShaderResourceView _srv;
    private int _w, _h;

    public ToneMapper(D3D11Device device, DeviceContext ctx)
    {
        _device = device;
        _ctx = ctx;
    }

    public string LastError { get; private set; }
    public Texture2D Output => _out;
    public ShaderResourceView OutputSrv => _srv;

    public bool Prepare()
    {
        try
        {
            if (!EnsureBytecode())
            {
                LastError = "tone map 着色器编译失败: " + _compileError;
                return false;
            }

            _vs = new VertexShader(_device, _vsBytecode);
            _ps = new PixelShader(_device, _psBytecode);
            _cb = new Buffer(_device, Utilities.SizeOf<Params>(),
                ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MinimumLod = 0,
                MaximumLod = float.MaxValue,
            });

            _raster = new RasterizerState(_device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                IsDepthClipEnabled = false,
            });

            return true;
        }
        catch (Exception e)
        {
            LastError = "tone map 初始化失败: " + e.Message;
            return false;
        }
    }

    private void EnsureTargets(int w, int h)
    {
        if (_out != null && _w == w && _h == h) return;

        _srv?.Dispose();
        _rtv?.Dispose();
        _out?.Dispose();

        _out = new Texture2D(_device, new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _rtv = new RenderTargetView(_device, _out);
        _srv = new ShaderResourceView(_device, _out);
        _w = w;
        _h = h;
    }

    /// <summary>把 src（PQ 或 HLG 编码的 BGRA）映射成 SDR sRGB，写进 Output。</summary>
    public void Run(ShaderResourceView src, int w, int h, int transfer, float peakNits, float sdrWhiteNits)
    {
        if (src == null || w <= 0 || h <= 0) return;
        if (_ps == null || _vs == null) return;

        EnsureTargets(w, h);

        var p = new Params { Info = new Vector4(transfer, peakNits, sdrWhiteNits, 0) };
        _ctx.UpdateSubresource(ref p, _cb);

        _ctx.InputAssembler.InputLayout = null;
        _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.VertexShader.Set(_vs);
        _ctx.PixelShader.Set(_ps);
        _ctx.PixelShader.SetConstantBuffer(0, _cb);
        _ctx.PixelShader.SetSampler(0, _sampler);
        _ctx.Rasterizer.State = _raster;
        _ctx.OutputMerger.SetDepthStencilState(null);
        _ctx.OutputMerger.SetBlendState(null);

        _ctx.OutputMerger.SetRenderTargets(_rtv);
        _ctx.Rasterizer.SetViewport(0, 0, w, h);
        _ctx.PixelShader.SetShaderResource(0, src);
        _ctx.Draw(3, 0);

        _ctx.PixelShader.SetShaderResource(0, null);
        _ctx.OutputMerger.SetRenderTargets((RenderTargetView)null);
    }

    public void Dispose()
    {
        _srv?.Dispose(); _srv = null;
        _rtv?.Dispose(); _rtv = null;
        _out?.Dispose(); _out = null;
        _raster?.Dispose(); _raster = null;
        _sampler?.Dispose(); _sampler = null;
        _cb?.Dispose(); _cb = null;
        _ps?.Dispose(); _ps = null;
        _vs?.Dispose(); _vs = null;
    }
}
