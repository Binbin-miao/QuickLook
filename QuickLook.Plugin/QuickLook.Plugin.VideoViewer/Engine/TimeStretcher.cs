// TimeStretcher —— WSOLA 时间伸缩（变速不变调）
//
// 原理：输入切成帧，以固定合成跳距（Hop）重叠相加；分析位置按 Ha = Hop * rate
//       单调推进（浮点累积，保证长期平均严格等于 Ha），再在 ±R 内做互相关微调，
//       使相邻帧波形相位对齐。Hann 窗 + 50% 重叠满足 COLA，重建无幅度起伏。
//
// 为什么分析位置要"理想累积"而不是"从上一帧实际位置再前进"：
//   减速时 Ha < Hop，参考段（上一帧 + Hop）会落在搜索窗口边缘，相关度最大的
//   候选恰好就是参考段本身，于是每帧实际前进 Hop，减速完全失效。用独立的理想
//   位置累积后，搜索只在理想位置附近微调，长期速率才准确。
//
// rate > 1 加速，rate < 1 减速；rate == 1 由调用方走直通路径，本类不参与。
using System;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class TimeStretcher
{
    private const int Frame = 1024;        // 合成帧长（样本/声道）
    private const int Hop = Frame / 2;     // 合成跳距
    private const int Capacity = Frame * 96;

    private readonly int _ch;
    private readonly float[] _window;
    private readonly float[] _in;
    private readonly float[] _acc;         // Frame 帧

    private int _inLen;      // 有效输入帧数
    private double _ideal;   // 理想分析位置（浮点，单调推进）
    private int _refPos;     // 「自然延续」参考点
    private bool _primed;
    private double _rate = 1.0;

    public TimeStretcher(int channels)
    {
        _ch = Math.Max(1, channels);
        _window = new float[Frame];
        for (int i = 0; i < Frame; i++)
            _window[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / Frame);

        _in = new float[Capacity * _ch];
        _acc = new float[Frame * _ch];
        Reset();
    }

    public int Channels => _ch;

    /// <summary>rate == 1 时无需伸缩，调用方应直接透传。</summary>
    public bool Passthrough => Math.Abs(_rate - 1.0) < 1e-3;

    public double Rate
    {
        get => _rate;
        set
        {
            double r = Math.Max(0.5, Math.Min(4.0, value));
            if (Math.Abs(r - _rate) < 1e-6) return;
            _rate = r;
            Reset();
        }
    }

    private double Ha => Hop * _rate;

    /// <summary>相位搜索半径：跟分析跳距挂钩，避免减速时被拉到参考段上。</summary>
    private int SearchRadius
    {
        get
        {
            double r = Ha * 0.5;
            if (r < 32) r = 32;
            if (r > Hop) r = Hop;
            return (int)r;
        }
    }

    public void Reset()
    {
        _inLen = 0;
        _ideal = 0;
        _refPos = 0;
        _primed = false;
        Array.Clear(_acc, 0, _acc.Length);
    }

    /// <summary>追加交错浮点采样。</summary>
    public void Feed(float[] src, int frames)
    {
        if (frames <= 0) return;

        Compact(frames);

        int room = Capacity - _inLen;
        if (frames > room) frames = room;
        if (frames <= 0) return;

        Array.Copy(src, 0, _in, _inLen * _ch, frames * _ch);
        _inLen += frames;
    }

    private void Compact(int incoming)
    {
        if (_inLen + incoming <= Capacity) return;

        int shift = (int)_ideal - SearchRadius;
        if (shift <= 0) shift = Math.Max(0, _inLen + incoming - Capacity);
        if (shift <= 0) return;
        if (shift > _inLen) shift = _inLen;

        Array.Copy(_in, shift * _ch, _in, 0, (_inLen - shift) * _ch);
        _inLen -= shift;
        _ideal -= shift;
        if (_ideal < 0) _ideal = 0;
        _refPos -= shift;
        if (_refPos < 0) _refPos = 0;
    }

    /// <summary>取出成品帧。flush = true 时允许零填充收尾。</summary>
    public int Read(float[] dest, int maxFrames, bool flush)
    {
        int produced = 0;
        while (produced + Hop <= maxFrames)
        {
            if (!Pump(flush)) break;

            Array.Copy(_acc, 0, dest, produced * _ch, Hop * _ch);
            produced += Hop;

            Array.Copy(_acc, Hop * _ch, _acc, 0, Hop * _ch);
            Array.Clear(_acc, Hop * _ch, Hop * _ch);
        }
        return produced;
    }

    /// <summary>处理一帧，成功后 _acc[0, Hop) 即为可输出数据。</summary>
    private bool Pump(bool flush)
    {
        int radius = SearchRadius;
        int center = (int)Math.Round(_ideal);

        int avail = _inLen - center;
        if (avail < Frame + radius && !(flush && avail >= Hop)) return false;

        int best;
        if (!_primed)
        {
            best = center;
            _primed = true;
        }
        else
        {
            best = center + BestOffset(center, radius, flush);
        }
        if (best < 0) best = 0;

        int ch = _ch;
        for (int f = 0; f < Frame; f++)
        {
            int sf = best + f;
            if (sf >= _inLen) continue;     // 收尾时零填充
            float w = _window[f];
            int di = f * ch;
            int si = sf * ch;
            for (int c = 0; c < ch; c++)
                _acc[di + c] += _in[si + c] * w;
        }

        _refPos = best + Hop;
        _ideal += Ha;                        // 严格按速率推进，不跟随 best
        return true;
    }

    /// <summary>在 center ± radius 内找与参考段最相似的起点偏移。</summary>
    private int BestOffset(int center, int radius, bool flush)
    {
        int ch = _ch;

        int lo = center - radius;
        if (lo < 0) lo = 0;

        int hi = center + radius;
        int maxStart = _inLen - Hop;
        if (flush) maxStart = Math.Max(0, _inLen - 1);
        if (hi > maxStart) hi = maxStart;

        if (hi < lo) return 0;

        double bestScore = double.MinValue;
        int bestOff = 0;

        for (int cand = lo; cand <= hi; cand += 2)
        {
            double corr = 0, energy = 0;

            for (int f = 0; f < Hop; f += 2)
            {
                int a = (cand + f) * ch;
                int b = (_refPos + f) * ch;
                if (b >= _inLen * ch) break;
                for (int c = 0; c < ch; c++)
                {
                    float x = _in[a + c];
                    float y = _in[b + c];
                    corr += x * y;
                    energy += x * x;
                }
            }

            double score = corr / Math.Sqrt(energy + 1e-9);
            if (score > bestScore)
            {
                bestScore = score;
                bestOff = cand - center;
            }
        }

        return bestOff;
    }
}
