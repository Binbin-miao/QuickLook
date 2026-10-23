// 宿主协议 —— 主程序（父）和引擎子进程之间的全部约定，两边都编译这一份文件。
//
// 为什么是行协议而不是 JSON：这条链路每秒只走几次命令（打开/seek/暂停/换档），
// 帧数据根本不走管道——画面走共享显存，帧号走共享内存，管道只送信号。
// 所以行协议足够，而且出问题时用肉眼就能看懂日志。
//
// 三条通道（都由父进程创建，子进程按名字打开）：
//   pipe  控制命令 + 应答       qlvideo_pipe_<token>
//   event 每渲染一帧 Set 一次    qlvideo_frame_<token>
//   map   帧号/播放位置/状态     qlvideo_map_<token>
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QuickLook.Plugin.VideoViewer.Engine;

/// <summary>子进程每帧往共享内存里写的状态。只放定长字段，读写都在毫秒级。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameShared
{
    public long PositionTicks;
    public long FrameCount;
    public long DurationTicks;
    public int VsrActive;
    public int OutputW;
    public int OutputH;

    public const int Size = 40;
}

internal static class HostProtocol
{
    public const string PipeNameFormat = "qlvideo_pipe_{0}";
    public const string EventNameFormat = "qlvideo_frame_{0}";
    public const string MapNameFormat = "qlvideo_map_{0}";

    // 父 → 子
    public const string Open = "OPEN";          // OPEN <w> <h> <vsr> <cap> <quality> <algo> <rate> <volume> <loop> <path...>
    public const string Start = "START";
    public const string Seek = "SEEK";          // SEEK <ticks>
    public const string Pause = "PAUSE";        // PAUSE 0|1
    public const string Rate = "RATE";          // RATE <double>
    public const string Volume = "VOLUME";      // VOLUME <float>
    public const string Mute = "MUTE";          // MUTE 0|1
    public const string Algo = "ALGO";          // ALGO <int>
    public const string Target = "TARGET";      // TARGET <w> <h>
    public const string Quality = "QUALITY";    // QUALITY <1..4>
    public const string Reconfig = "RECONFIG";  // RECONFIG <cap> <quality> <enabled>
    public const string ActivateVsr = "ACTVSr";
    public const string Probe = "PROBE";
    public const string Close = "CLOSE";
    public const string Quit = "QUIT";

    // 子 → 父
    public const string Ok = "OK";
    public const string Err = "ERR";
    public const string Opened = "OPENED";      // OPENED <srcW> <srcH> <outW> <outH> <surfW> <surfH> <handle> <durTicks> <vsr> <audio> <mtp> <vendor>
    public const string OpenFail = "OPENFAIL";
    public const string Surface = "SURFACE";    // SURFACE <w> <h> <handle>
    public const string ProbeResult = "PROBERESULT"; // PROBERESULT <ok|0> <reason...>
    public const string Bye = "BYE";

    public static string Token() => Guid.NewGuid().ToString("N").Substring(0, 12);

    public static string PipeName(string token) => string.Format(PipeNameFormat, token);
    public static string EventName(string token) => string.Format(EventNameFormat, token);
    public static string MapName(string token) => string.Format(MapNameFormat, token);

    public static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    public static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    public static long L(string s) => long.Parse(s, CultureInfo.InvariantCulture);
    public static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    public static bool B(string s) => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 行读写：UTF8 + '\n' 分隔。
///
/// 之前用 StreamReader/StreamWriter 包管道，两边一写一读时会诡异地互锁
/// （子进程卡在 WriteLine、父进程 Peek 返回 -1 空转）。管道是裸字节流，
/// 直接按字节读写最省事：读到 '\n' 就是一条。
/// </summary>
internal sealed class LineChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly object _writeLock = new();
    private readonly byte[] _one = new byte[1];

    public LineChannel(Stream stream)
    {
        _stream = stream;
    }

    public void Send(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeLock)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    /// <summary>读一行；超时/对端关闭返回 null（不抛）。</summary>
    public string Read(int timeoutMs)
    {
        var sb = new System.Collections.Generic.List<byte>(256);
        var deadline = Environment.TickCount + Math.Max(0, timeoutMs);
        bool unlimited = timeoutMs <= 0;

        while (unlimited || Environment.TickCount < deadline)
        {
            int n;
            try { n = _stream.Read(_one, 0, 1); }
            catch { return null; }

            if (n <= 0) return null;                       // 对端关了
            if (_one[0] == (byte)'\n') return Encoding.UTF8.GetString(sb.ToArray());

            sb.Add(_one[0]);
            if (sb.Count > 65536) return null;             // 跑飞了，别接着吃
        }
        return null;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}
