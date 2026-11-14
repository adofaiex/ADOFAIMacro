using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ADOFAIMacro.Core;
using ADOFAIMacro.Timing;
using UnityEngine;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// L0 偏差探针（2026-11-12）：纯观测，零行为变更。
    ///
    /// 【为什么需要它】用户报「遇到角度时间偏差后手法会崩」——伪双押回归、
    /// 整段单手/起手乱、强弱拍消失。诊断结论：片界守卫保证的是**计划时刻**
    /// 上的"跨手不同帧"，而运行时补发/钳位（TracePlayer 的 FloorIndex&lt;sweepFloor
    /// 直发、&gt;250ms 钳到 minPast）把计划时刻压扁，守卫前提在发键层失效。
    /// 动手修（运行时跨手守卫/补发节流）之前，先拿铁证：到底多少事件走了补发、
    /// 实际发出时刻上跨手同帧有多少帧、判定偏差分布长什么样。
    ///
    /// 【度量口径】
    ///  · 实际发键时刻 = SendKey 收到的 eventTicks（虚拟直喂路径的游戏可见时间戳；
    ///    0 = 当下发，注入路径也是当下）。按 16.7ms 桶聚合——同一桶内的按下手性
    ///    组合就是游戏 UpdateInput 同帧消费的近似。
    ///  · 跨手同帧 = 同一桶内同时出现左右手按下（伪双押的直接可观测量）。
    ///  · 门延迟 = AngleDriverTick 发布时 nowTicks − hitStarTicks（理想判定时刻
    ///    已过多久），即判定偏差本身；分布进 8 桶直方图。
    ///  · 补发数 = FloorIndex 已过、当下直发的事件数；钳位数 = 时间戳被打到
    ///    minPast 下限的事件数。这两个数就是"守卫被绕过"的规模。
    ///  · 短按 = 松键与按下落在同一桶（时长 &lt;16.7ms）——强弱拍塌缩的信号。
    ///
    /// 【线程】OnKeySent 来自主线程 + AngleAsyncDriver 工作线程 + KeyWorker
    /// 工作线程，全部走同一把锁；发键速率每秒最多几百次，锁开销可忽略。
    /// 报告只从主线程（AngleDriverTick / ResetTraceState）发出。
    /// </summary>
    internal static class DeviationProbe
    {
        private const long BucketTicks = 167_000;   // 16.7ms，与 AngleAsyncDriver 突发统计同网格

        private static readonly object _lock = new();

        // ── 当前桶状态 ─────────────────────────────────────────
        private static long _curBucket = long.MinValue;
        private static int _curPresses;
        private static bool _curHasL, _curHasR;
        private static byte _curLKey, _curRKey;

        // ── 本局累计 ───────────────────────────────────────────
        private static long _presses, _releases;
        private static long _catchUp, _clamped, _gateEvents;
        private static long _crossHandFrames, _multiPressFrames, _maxPressesInFrame;
        private static long _shortHolds;
        private static long _gateLateTicksMax;
        private static readonly long[] _gateHist = new long[8];
        // 按下的键 → 最近按下 tick（hold 时长记账）
        private static readonly Dictionary<byte, long> _downAt = new(16);

        // ── 最差帧样本（跨手且多按）───────────────────────────
        private struct WorstFrame
        {
            public long Bucket;
            public int Presses;
            public bool Cross;
            public byte LKey, RKey;
        }
        private static readonly List<WorstFrame> _worst = new(20);

        // ── 帧间隔（AngleDriverTick 实际游玩帧）────────────────
        private static long _lastFrameTick;
        private static readonly long[] _frameDeltas = new long[64];
        private static int _fdCount, _fdIdx;

        // ── 报告去重与探针自检 ─────────────────────────────────
        // 去重口径 = 上次报告以来无新发键/门事件（不是整局只报一次）：
        // 暂停或死亡报过之后，重试继续玩出的新数据不能被吞掉。
        private static long _lastReportedPresses, _lastReportedGate;
        private static long _probeErrors;

        // ═══════════════════════════════════════════════════════
        //  挂点 1：SendKey 实际发键（三线程共用）
        // ═══════════════════════════════════════════════════════
        internal static void OnKeySent(byte keyCode, bool isDown, long eventTicks)
        {
            if (!Main.Settings.EnableTechniqueSimulation) return;
            try
            {
                // 游戏可见时间戳：虚拟直喂 = eventTicks；0/注入 = 当下
                long tick = eventTicks != 0 ? eventTicks : PreciseNow.LocalTicks();
                lock (_lock)
                {
                    if (isDown) RecordPressLocked(keyCode, tick);
                    else RecordReleaseLocked(keyCode, tick);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _probeErrors) <= 3)
                    Main.Log($"[Macro-Probe] 记录异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RecordPressLocked(byte key, long tick)
        {
            _presses++;
            long bucket = tick / BucketTicks;
            if (bucket != _curBucket)
            {
                FlushBucketLocked();
                _curBucket = bucket;
                _curPresses = 0;
                _curHasL = _curHasR = false;
                _curLKey = _curRKey = 0;
            }
            _curPresses++;
            int side = MacroEngine.ClassifyKeySide(key);
            if (side < 0) { _curHasL = true; _curLKey = key; }
            else if (side > 0) { _curHasR = true; _curRKey = key; }

            if (key != 0) _downAt[key] = tick;
        }

        private static void RecordReleaseLocked(byte key, long tick)
        {
            _releases++;
            long bucket = tick / BucketTicks;
            if (bucket != _curBucket)
            {
                FlushBucketLocked();
                _curBucket = bucket;
                _curPresses = 0;
                _curHasL = _curHasR = false;
                _curLKey = _curRKey = 0;
            }
            // 短按：与按下同桶（时长 <16.7ms）。高倍乘片长本身可能小于一帧，
            // 但 bpmLimit 折叠后片长普遍 ≥30ms，同桶松开主要是压扁/钳位的产物。
            if (key != 0 && _downAt.TryGetValue(key, out long pt))
            {
                if (tick - pt < BucketTicks) _shortHolds++;
                _downAt.Remove(key);
            }
        }

        private static void FlushBucketLocked()
        {
            if (_curPresses <= 0) return;
            if (_curPresses >= 2)
            {
                _multiPressFrames++;
                if (_curPresses > _maxPressesInFrame) _maxPressesInFrame = _curPresses;
            }
            bool cross = _curHasL && _curHasR;
            if (cross) _crossHandFrames++;
            if (cross && _curPresses >= 2) AddWorstLocked(new WorstFrame
            {
                Bucket = _curBucket,
                Presses = _curPresses,
                Cross = true,
                LKey = _curLKey,
                RKey = _curRKey,
            });
        }

        private static void AddWorstLocked(WorstFrame f)
        {
            if (_worst.Count < 20) { _worst.Add(f); return; }
            int minIdx = 0;
            for (int i = 1; i < _worst.Count; i++)
                if (_worst[i].Presses < _worst[minIdx].Presses) minIdx = i;
            if (f.Presses > _worst[minIdx].Presses) _worst[minIdx] = f;
        }

        // ═══════════════════════════════════════════════════════
        //  挂点 2：AngleDriverTick 门级事件（主线程）
        // ═══════════════════════════════════════════════════════
        /// <summary>门延迟记录：nowTicks − hitStarTicks = 判定偏差（理想判定时刻已过多久）。</summary>
        internal static void OnGateEvent(long hitStarTicks, long nowTicks, bool clamped)
        {
            if (!Main.Settings.EnableTechniqueSimulation) return;
            try
            {
                lock (_lock)
                {
                    _gateEvents++;
                    long late = nowTicks - hitStarTicks;
                    double ms = late / 1e4;
                    int b = ms <= 0 ? 0
                          : ms <= 1 ? 1
                          : ms <= 5 ? 2
                          : ms <= 16.7 ? 3
                          : ms <= 33 ? 4
                          : ms <= 100 ? 5
                          : ms <= 250 ? 6 : 7;
                    _gateHist[b]++;
                    if (late > _gateLateTicksMax) _gateLateTicksMax = late;
                    if (clamped) _clamped++;
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _probeErrors) <= 3)
                    Main.Log($"[Macro-Probe] 记录异常: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>补发事件计数（FloorIndex 已过、当下直发——计划时刻已不可还原）。</summary>
        internal static void OnCatchUp()
        {
            if (!Main.Settings.EnableTechniqueSimulation) return;
            lock (_lock) _catchUp++;
        }

        /// <summary>游玩帧计数（帧间隔测量；暂停/非游玩态在 AngleDriverTick 早退，不计入）。</summary>
        internal static void OnFrame()
        {
            long now = PreciseNow.LocalTicks();
            lock (_lock)
            {
                if (_lastFrameTick != 0)
                {
                    long d = now - _lastFrameTick;
                    // 跳过 &gt;1s 的空洞（断点续玩/换段），避免污染帧间隔统计
                    if (d > 0 && d < 10_000_000)
                    {
                        _frameDeltas[_fdIdx] = d;
                        _fdIdx = (_fdIdx + 1) % _frameDeltas.Length;
                        if (_fdCount < _frameDeltas.Length) _fdCount++;
                    }
                }
                _lastFrameTick = now;
            }
        }

        /// <summary>砖序回退（重试/检查点）：打一行紧凑归因日志。</summary>
        internal static void OnRollback(long oldFloor, int newFloor)
        {
            if (!Main.Settings.EnableTechniqueSimulation) return;
            lock (_lock)
            {
                Main.Log($"[Macro-Probe] 砖序回退 {oldFloor}→{newFloor}：" +
                         $"累计补发 {_catchUp} / 钳位 {_clamped} / 跨手同帧 {_crossHandFrames} 帧");
            }
        }

        // ═══════════════════════════════════════════════════════
        //  报告与重置
        // ═══════════════════════════════════════════════════════
        /// <summary>有新数据才报告（暂停/死亡/事件表跑完反复触发不刷屏，恢复游玩后可再报）。</summary>
        internal static void ReportOnce(string reason) => ReportLocked(reason);

        /// <summary>有数据才报告（换关/重试收尾上一局；口径与 ReportOnce 相同）。</summary>
        internal static void ReportIfAny(string reason) => ReportOnce(reason);

        private static void ReportLocked(string reason)
        {
            string line, fileText;
            lock (_lock)
            {
                if (_presses == 0 && _gateEvents == 0) return;
                // 去重 = 自上次报告以来无新发键/门事件：同一状态反复触发只报一次，
                // 但恢复游玩产生新数据后允许再次报告，重试后的数据不会被吞掉。
                if (_presses == _lastReportedPresses && _gateEvents == _lastReportedGate) return;
                _lastReportedPresses = _presses;
                _lastReportedGate = _gateEvents;

                FlushBucketLocked();

                long presses = _presses, releases = _releases;
                long catchUp = _catchUp, clamped = _clamped, gateEvents = _gateEvents;
                long cross = _crossHandFrames, multi = _multiPressFrames, maxP = _maxPressesInFrame;
                long shortHolds = _shortHolds;
                long gateMaxMs = _gateLateTicksMax / 10_000;
                var hist = (long[])_gateHist.Clone();
                var worst = new List<WorstFrame>(_worst);
                long frameMedianMs = MedianFrameDeltaMsLocked();
                long errors = Interlocked.Read(ref _probeErrors);

                line =
                    $"[Macro-Probe] {reason}: 按下 {presses}(松 {releases}) · 补发 {catchUp} · 钳位 {clamped}" +
                    $" · 门延迟 max {gateMaxMs}ms · 跨手同帧 {cross} 帧 · 同帧最多 {maxP} 键" +
                    $" · 多按帧 {multi} · 短按 {shortHolds} · 帧中位 {frameMedianMs}ms";

                var sb = new StringBuilder(2048);
                sb.AppendLine($"偏差探针报告（{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 原因: {reason}）");
                sb.AppendLine($"按下 {presses} / 松开 {releases} / 门级事件 {gateEvents} / 探针异常 {errors}");
                sb.AppendLine($"补发(FloorIndex已过) {catchUp} · 钳位(>250ms) {clamped} —— 这两项是\"片界守卫被绕过\"的规模");
                sb.AppendLine($"跨手同帧 {cross} 帧 · 同帧最多 {maxP} 键 · 多按帧 {multi} · 短按(<16.7ms) {shortHolds}");
                sb.AppendLine($"门延迟 max {gateMaxMs}ms · 帧间隔中位 {frameMedianMs}ms（守卫阈值 16.7ms 按 60fps 假设）");
                sb.AppendLine();
                sb.AppendLine("门延迟分布（now − 理想判定时刻）:");
                string[] labels =
                {
                    "≤0ms(准点/提前)", "0–1ms", "1–5ms", "5–16.7ms",
                    "16.7–33ms", "33–100ms", "100–250ms", ">250ms(钳位)",
                };
                for (int i = 0; i < hist.Length; i++)
                    sb.AppendLine($"  {labels[i],-16} {hist[i]}");
                sb.AppendLine();
                if (worst.Count > 0)
                {
                    sb.AppendLine("最差帧（跨手且同帧 ≥2 按下，按按下数取前 20）:");
                    foreach (var w in worst)
                    {
                        double t = w.Bucket * BucketTicks / 1e7;
                        sb.AppendLine($"  t={t:F2}s  按下 {w.Presses}  " +
                            $"L=0x{w.LKey:X2} R=0x{w.RKey:X2}");
                    }
                }
                fileText = sb.ToString();
            }
            Main.Log(line);
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "ADOFAIMacro-偏差探针.txt");
                // 追加而非覆盖：一局内多次报告（暂停/死亡/跑完）与多关历史都留档，
                // 便于对拍「崩之前 vs 崩之后」的尝试。
                System.IO.File.AppendAllText(path, fileText + System.Environment.NewLine);
            }
            catch { }
        }

        private static long MedianFrameDeltaMsLocked()
        {
            if (_fdCount == 0) return -1;
            var arr = new long[_fdCount];
            Array.Copy(_frameDeltas, arr, _fdCount);
            Array.Sort(arr);
            return arr[_fdCount / 2] / 10_000;
        }

        /// <summary>新的一局：清空全部状态（ResetTraceState / 换关时调用）。</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _curBucket = long.MinValue;
                _curPresses = 0;
                _curHasL = _curHasR = false;
                _curLKey = _curRKey = 0;
                _presses = _releases = 0;
                _catchUp = _clamped = _gateEvents = 0;
                _crossHandFrames = _multiPressFrames = _maxPressesInFrame = 0;
                _shortHolds = 0;
                _gateLateTicksMax = 0;
                Array.Clear(_gateHist, 0, _gateHist.Length);
                _downAt.Clear();
                _worst.Clear();
                _lastFrameTick = 0;
                _fdCount = _fdIdx = 0;
                _lastReportedPresses = _lastReportedGate = 0;
            }
        }
    }
}
