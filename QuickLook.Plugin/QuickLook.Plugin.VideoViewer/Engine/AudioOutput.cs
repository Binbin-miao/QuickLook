// AudioOutput —— XAudio2 音频输出 + 主时钟
//
// 职责：
//   * 持有 XAudio2 / MasteringVoice / SourceVoice
//   * 用固定大小的非托管缓冲池提交 PCM（XAudio2 不复制数据，必须自己保活）
//   * 以 SamplesPlayed 推算「当前播放到哪」，作为音画同步的主时钟
//
// 缓冲生命周期：Context 里塞 (generation << 16 | index)，OnBufferEnd 归还到池。
// Flush 时 generation +1，旧世代的迟到回调直接丢弃，避免同一块缓冲被重复提交。
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using XAudio2Device = SharpDX.XAudio2.XAudio2;

namespace QuickLook.Plugin.VideoViewer.Engine;

internal sealed class AudioOutput : IDisposable
{
    private const int PoolCount = 8;
    private const int PoolSize = 192 * 1024;   // 单块 192KB ≈ 1s @48k/16bit/立体声

    private XAudio2Device _xaudio;
    private MasteringVoice _master;
    private SourceVoice _voice;
    private VoiceNotify _notify;

    private IntPtr[] _pool;
    private int _generation = 1;
    private readonly ConcurrentQueue<int> _free = new();

    private long _basePtsHns;
    private long _samplesAtFlush;
    private volatile bool _clockReady;

    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public int BitsPerSample { get; private set; } = 16;
    public bool IsReady { get; private set; }
    public string LastError { get; private set; }

    /// <summary>播放速率（1.0 = 原速），用于把「已播样本数」换算回输入时间。</summary>
    public double Rate { get; set; } = 1.0;

    private float _volume = 0.8f;
    private bool _muted;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0f, Math.Min(1f, value));
            ApplyVolume();
        }
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; ApplyVolume(); }
    }

    private void ApplyVolume()
    {
        try { _voice?.SetVolume(_muted ? 0f : _volume); } catch (Exception e) { Debug.WriteLine(e.Message); }
    }

    public int BuffersQueued
    {
        get
        {
            try { return _voice?.State.BuffersQueued ?? 0; }
            catch { return 0; }
        }
    }

    // ================================================================ open

    public bool Open(int sampleRate, int channels, int bits)
    {
        try
        {
            SampleRate = sampleRate > 0 ? sampleRate : 48000;
            Channels = channels > 0 ? channels : 2;
            BitsPerSample = bits > 0 ? bits : 16;

            _xaudio = new XAudio2Device();
            _master = new MasteringVoice(_xaudio);

            _pool = new IntPtr[PoolCount];
            for (int i = 0; i < PoolCount; i++)
            {
                _pool[i] = Marshal.AllocHGlobal(PoolSize);
                _free.Enqueue(i);
            }

            _notify = new VoiceNotify { OnEnd = ReturnBuffer };
            var wf = new WaveFormat(SampleRate, BitsPerSample, Channels);
            _voice = new SourceVoice(_xaudio, wf, VoiceFlags.None, 1.0f, _notify);
            ApplyVolume();
            _voice.Start();

            IsReady = true;
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            IsReady = false;
            return false;
        }
    }

    // ================================================================ submit

    /// <summary>提交一段交错 PCM。firstOfRun 时把 ptsHns 记为这一轮音频的起点。</summary>
    public void Submit(byte[] data, int bytes, long ptsHns, bool firstOfRun)
    {
        if (!IsReady || _voice == null || data == null || bytes <= 0) return;
        if (bytes > PoolSize) bytes = PoolSize;

        // XAudio2 要求 AudioBytes 是 block align 的整数倍，否则 E_INVALID_CALL
        int align = Channels * BitsPerSample / 8;
        if (align > 0) bytes -= bytes % align;
        if (bytes <= 0) return;

        int idx = -1;
        if (!_free.TryDequeue(out idx))
        {
            idx = -1;
            for (int i = 0; i < 80 && idx < 0; i++)
            {
                System.Threading.Thread.Sleep(3);
                if (_free.TryDequeue(out int got)) idx = got;
            }
            if (idx < 0) return;    // 池耗尽就丢这一块，别把线程卡死
        }

        IntPtr dst = _pool[idx];
        Marshal.Copy(data, 0, dst, bytes);

        if (firstOfRun)
        {
            _basePtsHns = ptsHns;
            try { _samplesAtFlush = _voice.State.SamplesPlayed; } catch { _samplesAtFlush = 0; }
            _clockReady = true;
        }

        var buffer = new AudioBuffer
        {
            AudioDataPointer = dst,
            AudioBytes = bytes,
            Flags = BufferFlags.None,
            Context = (IntPtr)((_generation << 16) | idx),
        };

        try
        {
            _voice.SubmitSourceBuffer(buffer, null);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _free.Enqueue(idx);
        }
    }

    private void ReturnBuffer(IntPtr ctx)
    {
        long v = ctx.ToInt64();
        int gen = (int)((v >> 16) & 0x7FFF);
        if (gen != _generation) return;      // 旧世代，忽略

        int idx = (int)(v & 0xFFFF);
        if (idx >= 0 && idx < PoolCount) _free.Enqueue(idx);
    }

    // ================================================================ clock

    /// <summary>当前播放位置（输入时间轴，100ns）。未就绪返回 -1。</summary>
    public long PositionHns()
    {
        if (!IsReady || !_clockReady || _voice == null) return -1;
        try
        {
            long played = _voice.State.SamplesPlayed;
            long delta = played - _samplesAtFlush;
            double seconds = (double)delta / SampleRate;
            return _basePtsHns + (long)(seconds * Rate * 1e7);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>变速：先在旧速率下取出当前位置，再切速率并把该位置设为新基准。</summary>
    public void ChangeRate(double newRate)
    {
        long pos = PositionHns();
        Rate = newRate;
        if (pos < 0) pos = _basePtsHns;
        _basePtsHns = pos;
        try { _samplesAtFlush = _voice?.State.SamplesPlayed ?? 0; } catch { _samplesAtFlush = 0; }
    }

    /// <summary>把「当前时刻」重新设为基准（暂停恢复时用）。</summary>
    public void Rebaseline()
    {
        if (!IsReady || _voice == null) return;
        long pos = PositionHns();
        if (pos < 0) pos = _basePtsHns;
        try
        {
            _samplesAtFlush = _voice.State.SamplesPlayed;
            _basePtsHns = pos;
        }
        catch (Exception e) { Debug.WriteLine(e.Message); }
    }

    /// <summary>指定新的基准时间（seek 后调用）。</summary>
    public void SetBase(long ptsHns)
    {
        _basePtsHns = ptsHns;
        _clockReady = false;
        try { _samplesAtFlush = _voice?.State.SamplesPlayed ?? 0; } catch { _samplesAtFlush = 0; }
    }

    public void ClearClock() => _clockReady = false;

    public void Pause()
    {
        try { _voice?.Stop(); } catch (Exception e) { Debug.WriteLine(e.Message); }
    }

    public void Resume()
    {
        try { _voice?.Start(); } catch (Exception e) { Debug.WriteLine(e.Message); }
    }

    /// <summary>丢掉所有排队缓冲，进入新一轮。</summary>
    public void Flush()
    {
        if (_voice == null) return;
        try { _voice.Stop(); } catch { }
        try { _voice.FlushSourceBuffers(); } catch { }

        _generation = (_generation + 1) & 0x7FFF;
        if (_generation == 0) _generation = 1;

        while (_free.TryDequeue(out _)) { }
        for (int i = 0; i < PoolCount; i++) _free.Enqueue(i);

        _clockReady = false;
    }

    // ================================================================ dispose

    public void Dispose()
    {
        IsReady = false;
        try { _voice?.Stop(); } catch { }
        try { _voice?.FlushSourceBuffers(); } catch { }
        try { _voice?.Dispose(); } catch { }
        try { _master?.Dispose(); } catch { }
        try { _xaudio?.Dispose(); } catch { }
        _voice = null;
        _master = null;
        _xaudio = null;

        // VoiceNotify 是 CallbackBase：内部拿着一个 GCHandle 指向自己。
        // 不 Dispose 的话这把句柄永远不释放，这个 AudioOutput 连带它的委托
        // 就一直挂在进程里（每预览一次漏一个，托管堆上还留着它的池数组）。
        try { _notify?.Dispose(); } catch { }
        _notify = null;

        if (_pool != null)
        {
            for (int i = 0; i < _pool.Length; i++)
            {
                if (_pool[i] != IntPtr.Zero) { try { Marshal.FreeHGlobal(_pool[i]); } catch { } }
                _pool[i] = IntPtr.Zero;
            }
            _pool = null;
        }
    }

    private sealed class VoiceNotify : CallbackBase, VoiceCallback
    {
        public Action<IntPtr> OnEnd;

        public void OnVoiceProcessingPassStart(int bytesRequired) { }
        public void OnVoiceProcessingPassEnd() { }
        public void OnStreamEnd() { }
        public void OnBufferStart(IntPtr bufferContext) { }
        public void OnBufferEnd(IntPtr bufferContext) => OnEnd?.Invoke(bufferContext);
        public void OnLoopEnd(IntPtr bufferContext) { }
        public void OnVoiceError(IntPtr bufferContext, Result error) { }
    }
}
