using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

// TriggerRsAB —— 离线 A/B：同一事件表 + 同一虚拟时钟推进，
// 分别用 C# 决策逻辑（复刻 WorkerLoop 判定式）与 Rust StepTick 判定，
// 统计动作不一致数与 delta 差最大值。阶段 3 验收的离线部分。
//
// 事件表来源：ofnr.timedata（时刻微秒, 类型 1/2/-1, 地板）→ 构造 HitEvent 序列
//（普通按下/长按头/长按尾-松开），与游戏内建表语义一致（终点砖已含在 timedata 里）。
//
// 用法：TriggerRsAB.exe [timedata路径] [步长ms=0.1] [总时长s=全部]
static class TriggerRsAB
{
    [StructLayout(LayoutKind.Sequential)]
    struct Anchor
    {
        public IntPtr Events;
        public int EventCount;
        public int ResetVersion;
        public long QpcSnapshot;
        public long QpcFreq;
        public double SongPosRef;
        public double Rate;
        public double TimeOffset;
        public double AnchorStaleSec;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Ev
    {
        public double TriggerTime;
        public byte KeyCode;
        public byte ReleaseOnly;
        public byte IsHoldRelated;
        public byte ReleaseKeyCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaitAdvice
    {
        public double SleepSec;
        public double DeltaSec;
    }

    const int ACTION_NONE = 0, ACTION_PRESS = 1, ACTION_RELEASE = 2, ACTION_HOLD_PRESS = 3,
              ACTION_ADVANCE = 4, ACTION_FINISHED = 5, ACTION_WAIT_AGAIN = 6, ACTION_SLEEP = 7;

    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern IntPtr StepCreate();
    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern void StepFree(IntPtr session);
    [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
    static extern unsafe int StepTick(IntPtr session, ref Anchor a, int idx, long qpcNow, ref WaitAdvice w);

    const long FREQ = 10_000_000L; // 虚拟时钟用 100ns 刻度，freq=1e7（等价高精度时钟域）

    // ── C# 决策（复刻 WorkerLoop 判定式，返回与 Rust 同一动作码空间）──
    // ★【2026-10-12 修正】旧版这里写了 `idx >= count - 1 → FINISHED`，与 Rust 的
    //   同款错误互相印证 → A/B 恒 0 不一致，把「最后一拍被吞」的真 bug 漏掉了。
    //   真实 C# WorkerLoop：for 条件 `i < evCount` 允许最后一个事件正常触发，
    //   FINISHED 哨兵在外层 for 之前（本地索引 >= evCount-1 时睡眠等重置）。
    //   这里按真实语义修正：只有 idx >= count（越界）才 FINISHED。
    static int CsDecision(double songPosRef, double rate, double timeOffset, double staleSec,
        double elapsed, int idx, int count, List<Ev> evs, out double deltaOut, out double sleepSec)
    {
        deltaOut = 0; sleepSec = 0.001;
        if (elapsed > staleSec) return ACTION_SLEEP;
        if (idx >= count) return ACTION_FINISHED;
        if (idx < 0) return ACTION_FINISHED;
        var e = evs[idx];
        double audioNow = songPosRef + elapsed * rate;
        double triggerAt = e.TriggerTime + timeOffset;
        double delta = triggerAt - audioNow;
        deltaOut = delta;
        if (delta > 0.0)
        {
            if (rate <= 0.0) return ACTION_SLEEP;
            double w = delta / rate;
            if (w > 0.01) { sleepSec = w; return ACTION_SLEEP; }
            if (w > 0.0015) { sleepSec = w - 0.0015; return ACTION_SLEEP; }
            return ACTION_WAIT_AGAIN;
        }
        if (e.ReleaseOnly != 0) return ACTION_RELEASE;
        if (e.IsHoldRelated != 0) return ACTION_HOLD_PRESS;
        return ACTION_PRESS;
    }

    static int Main(string[] args)
    {
        string td = args.Length > 0 ? args[0] : @"..\tools\abverify\ofnr.timedata";
        double stepMs = args.Length > 1 ? double.Parse(args[1]) : 0.1;
        if (!File.Exists(td)) { Console.WriteLine("timedata 不存在: " + td); return 2; }

        // ── 构造事件表（A/B 工装的简化模型：
        //    每块判定砖一个按下，时刻 = 下一块判定砖的 entryTime；长按尾 → 松开）──
        var raw = new List<(double t, int type)>();
        foreach (var line in File.ReadAllLines(td))
        {
            var p = line.Split(',');
            if (p.Length < 2) continue;
            if (!double.TryParse(p[0], out var us)) continue;
            raw.Add((us / 1e6, int.Parse(p[1])));
        }
        var evs = new List<Ev>();
        for (int i = 0; i < raw.Count; i++)
        {
            var (t, type) = raw[i];
            if (type == -1)
                evs.Add(new Ev { TriggerTime = t, KeyCode = 0, ReleaseOnly = 1, IsHoldRelated = 1, ReleaseKeyCode = 0x44 });
            else if (type == 2)
                evs.Add(new Ev { TriggerTime = t, KeyCode = 0x44, IsHoldRelated = 1 });
            else
                evs.Add(new Ev { TriggerTime = t, KeyCode = 0x44 });
        }
        Console.WriteLine($"事件表: {evs.Count} 条（timedata {raw.Count} 行）");

        var sess = StepCreate();
        var evArr = evs.ToArray();
        int mismatch = 0, compared = 0, rustFired = 0, csFired = 0;
        double maxDeltaDiff = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 虚拟时钟推进：songPosRef=0、rate=1 → audioNow == elapsed。
        //    对每个事件 idx，推进虚拟时钟跨过它的触发时刻（步长 stepMs），
        //    在「恰好到时刻」的 tick 上比对 C# 决策 vs Rust 决策。──
        unsafe
        {
            fixed (Ev* p = evArr)
            {
                var a = new Anchor
                {
                    Events = (IntPtr)p,
                    EventCount = evs.Count,
                    ResetVersion = 1,
                    QpcSnapshot = 0,
                    QpcFreq = FREQ,
                    SongPosRef = 0.0,
                    Rate = 1.0,
                    TimeOffset = 0.0,
                    AnchorStaleSec = 0.5,
                };
                double step = stepMs / 1000.0;
                var w = new WaitAdvice();

                StepTick(sess, ref a, 0, 0, ref w); // 版本吸收轮

                // ★【2026-10-12 修正】idx 循环包含最后一个事件（idx <= Count-1）：
                //   旧版 `idx < evs.Count - 1` 把最后一拍排除在比对之外，与两边的
                //   FINISHED 早退互相掩护。现在最后一拍也进比对（Rust 修复后应
                //   返回 PRESS/RELEASE 而非 FINISHED）。
                for (int idx = 0; idx < evs.Count; idx++)
                {
                    // 推进到「恰好到达该事件触发时刻」的 tick（elapsed == triggerTime）
                    long qpcAt = (long)(evs[idx].TriggerTime * FREQ);
                    double elapsedAt = evs[idx].TriggerTime; // stale 窗口内（elapsed=trigger 恒 <0.5? 不一定!）
                    // stale 语义: audioNow - 锚点。锚点 songPosRef=0 → elapsed == triggerTime 可能 >0.5s。
                    // 为让 stale 检查不误伤，把锚点设在「本事件前 0.1s」：songPosRef = trigger-0.1, snapshot=qpcAt-0.1*FREQ
                    a.SongPosRef = evs[idx].TriggerTime - 0.1;
                    a.QpcSnapshot = qpcAt - (long)(0.1 * FREQ);

                    int cs = CsDecision(a.SongPosRef, a.Rate, a.TimeOffset, a.AnchorStaleSec,
                        0.1, idx, evs.Count, evs, out var csDelta, out var csSleep);
                    var rw = new WaitAdvice();
                    int rs = StepTick(sess, ref a, idx, qpcAt, ref rw);

                    compared++;
                    if (cs != rs)
                    {
                        mismatch++;
                        if (mismatch <= 10)
                            Console.WriteLine($"  [不一致] idx={idx} t={evs[idx].TriggerTime:F6} C#={Name(cs)} Rust={Name(rs)} csDelta={csDelta:F6} rsDelta={rw.DeltaSec:F6}");
                    }
                    if (cs == ACTION_PRESS || cs == ACTION_HOLD_PRESS) csFired++;
                    if (rs == ACTION_PRESS || rs == ACTION_HOLD_PRESS) rustFired++;
                    double dd = Math.Abs(csDelta - rw.DeltaSec);
                    if (dd > maxDeltaDiff) maxDeltaDiff = dd;
                }
            }
        }
        StepFree(sess);
        sw.Stop();

        Console.WriteLine($"\n比对 {compared} 事件 | 动作不一致 {mismatch} | C#击发 {csFired} / Rust击发 {rustFired}");
        Console.WriteLine($"delta 最大差 {maxDeltaDiff * 1000.0:F6} ms | 耗时 {sw.ElapsedMilliseconds} ms");
        bool pass = mismatch == 0 && maxDeltaDiff < 1e-9 && csFired == rustFired;
        Console.WriteLine(pass ? "A/B 全一致 —— PASS" : "A/B 存在分歧 —— FAIL");
        return pass ? 0 : 1;
    }

    static string Name(int act) => act switch
    {
        ACTION_NONE => "NONE", ACTION_PRESS => "PRESS", ACTION_RELEASE => "RELEASE",
        ACTION_HOLD_PRESS => "HOLD_PRESS", ACTION_ADVANCE => "ADVANCE", ACTION_FINISHED => "FINISHED",
        ACTION_WAIT_AGAIN => "WAIT_AGAIN", ACTION_SLEEP => "SLEEP", _ => "?" + act,
    };
}
