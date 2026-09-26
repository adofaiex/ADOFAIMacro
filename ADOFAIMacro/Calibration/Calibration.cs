using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ADOFAIMacro.Core;
using UnityEngine;

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 校准与诊断统计：判定误差回灌与自动校准、击发延迟统计、GC 抑制、
    /// 手法表摘要与日志出口。
    ///
    /// 这些都与「按出手法」无关 —— 手法本身在 Technique\MacroEngine.cs 与
    /// 原生 TechniqueSimulator.dll 里。全部走 Main.Log，受日志总开关控制。
    ///
    /// ⚠️ 本文件是 partial class MacroEngine 的一部分，与 Technique\ 下的其余
    ///    7 个文件同属 ADOFAIMacro.Technique 命名空间 —— partial 的所有部分
    ///    必须同命名空间，否则 C# 会当成两个不同的类。放在 Calibration\ 目录
    ///    只是为了按职责分类，IDE0130 是可接受的代价。
    /// </summary>
    internal static partial class MacroEngine
    {
        // ── 判定误差统计与自动校准 ──────────────────────────────
        //  环路为纯单局控制器：每局从 0 起步，主段 ~1s 收敛，无跨局状态。
        private static float _judgeErrSum;
        private static float _judgeErrSqSum;
        private static int _judgeErrCount;
        private static int _judgeLastAdaptMs;
        private static float _autoOffsetMs;
        // ⚠️ 持久化已回滚（2026-08-16 事故）：学到的偏移依赖本局运行状态
        // （offsetTick 收敛相位决定公式路径是否接管，两种基线所需偏移差 ~45ms），
        // 跨运行灌入会造成系统性错位 → 判定 ±700ms 摆动 → 死亡退局。
        // 学习表只在本局内存内有效，每局重新收敛（主段 ~1s）。

        /// <summary>判定探针回灌（AddHit 后缀调用，主线程）。</summary>
        internal static void RecordJudgedError(float errMs, float spdUsed)
        {
        if (errMs < -40f) errMs = -40f;
        else if (errMs > 40f) errMs = 40f;   // 钳制离群值（死亡/重生/变速瞬态）
        _judgeErrSum += errMs;
        _judgeErrSqSum += errMs * errMs;
        _judgeErrCount++;
        // 注：按速度的学习表已切除（2026-08-16 二次事故）——公式基线下
        // 各段所需偏移本就是同一常数，跨运行/跨条件的学习值只会在
        // TimeOffset 等条件变化后变成毒药（实测三局 2.6→6.8→9.7ms 漂移）。
        // 现在是纯单局控制器：每局从 0 起步，快速档 <1s 收敛，无任何跨状态。
        }

        private static void StepAutoCalibration()
        {
        if (!Main.Settings.AutoCalibrateJudgement)
        {
            _autoOffsetMs = 0;
            _judgeErrSum = 0;
            _judgeErrSqSum = 0;
            _judgeErrCount = 0;
            return;
        }
        int now = Environment.TickCount;
        int sinceLast = unchecked(now - _judgeLastAdaptMs);
        // 密集窗口（≥6 样本/100ms）；稀疏窗口（≥4 样本/700ms）只用于慢速段。
        // 稀疏段样本少且帧粒度噪声大（±1 帧量化），增益降到 0.2、死区 3ms，
        // 只追真实偏移不追噪声——否则环路随机游走反而制造"跳动"。
        bool dense = _judgeErrCount >= 6 && sinceLast >= 100;
        bool sparse = _judgeErrCount >= 4 && sinceLast >= 700;
        if (!dense && !sparse) return;

        _judgeLastAdaptMs = now;
        float mean = _judgeErrSum / _judgeErrCount;
        // 测量可信度门控：窗口标准差 > 12ms 判定为不可信（管线饱和/极端密度/
        // 变速瞬态——例如 20 万 BPM 段的固有消费延迟，与偏移量无关），
        // 冻结本窗口不调整。否则环路会把饱和误差当偏移硬追，越调越偏。
        float variance = _judgeErrSqSum / _judgeErrCount - mean * mean;
        float std = variance > 0f ? (float)Math.Sqrt(variance) : 0f;
        _judgeErrSum = 0;
        _judgeErrSqSum = 0;
        _judgeErrCount = 0;
        if (std > 12f) return;

        // 双档：公式基线已与判定恒等，残差主体是引擎量化噪声。
        // 近零档（|err|<15ms）：死区 3ms 内静默；动作时步长 ≤0.8ms，
        //   抖动上限低于可感知度——环路自己不再制造"晃动"。
        // 快速档（|err|≥15ms）：真实偏移（如分段相位 −40ms 类），1 秒内吃掉。
        if (Math.Abs(mean) < 3f) return;

        float step;
        if (Math.Abs(mean) >= 15f)
        {
            step = mean * 0.4f;
            if (step > 4f) step = 4f; else if (step < -4f) step = -4f;
        }
        else
        {
            step = mean * 0.06f;
            if (step > 0.8f) step = 0.8f; else if (step < -0.8f) step = -0.8f;
        }
        // 符号约定（2026-09-25 用 dnlib 反编译游戏侧独立核对）：
        //   scrController.UpdateHitErrorMeter 传给 AddHit 的 angleDiff =
        //       (planet.cachedAngle - planet.targetExitAngle) * (isCCW ? -1 : +1)
        //   归一化后「正值 = 迟」（该值即 SelectHitMarginByTimeBoundary 的 timeDiff，
        //   > +Counted 判为 TooLate）；而 AddHit 内部会再乘 -57.29578（弧度→度，取负），
        //   所以探针拿到的 errMs 符号与「迟」相反：
        //       errMs > 0 = 早，errMs < 0 = 迟
        // 结论：早发必须【增大】触发偏移让按键变晚，即 += step。
        // ⚠️ 旧实现写的是 -= step —— 那是因为当时 errMs 被多乘了 -57.29578×(60/boundary)
        //    变成约 -114 倍，负号恰好抵消；修正量纲后必须同时把方向改回来，
        //    否则闭环成为正反馈，偏移会被推到 ±60ms 轨道。
        _autoOffsetMs += step;
        if (_autoOffsetMs > 60f) _autoOffsetMs = 60f;
        else if (_autoOffsetMs < -60f) _autoOffsetMs = -60f;

        Main.Log($"[Macro-Cali] err={mean:F2}ms autoOffset={_autoOffsetMs:F2}ms");
        }

        // ─────────────────────────────────────────────
        //  方案8：游玩期 GC 停顿抑制（极端密度图）
        //  GC 全线程暂停（50~360ms）会同时冻结宏工作线程和游戏判定，
        //  是高密度图上最大的可见误差尖峰来源。
        //  进关卡时 TryStartNoGCRegion（大预算推迟所有 GC），关卡结束/
        //  重开时 End（GC 在加载画面发生，玩家无感）。预算被突破时
        //  运行时自动回退正常 GC 行为，无风险。
        // ─────────────────────────────────────────────
        private static bool _noGcActive;
        private static bool _noGcBroken;

        private static void TryBeginNoGC()
        {
        if (_noGcActive || _noGcBroken || !Main.Settings.SuppressGcPauses) return;
        try
        {
            if (GC.TryStartNoGCRegion(256L << 20))
            {
                _noGcActive = true;
                Main.Log("[Macro-GC] NoGCRegion 已启用（游玩期抑制 GC 停顿）");
            }
            else _noGcBroken = true;   // 本关不再重试（避免每帧空转）
        }
        catch { _noGcBroken = true; }
        }

        private static void TryEndNoGC()
        {
        if (!_noGcActive) return;
        _noGcActive = false;
        try
        {
            GC.EndNoGCRegion();
            // NoGCRegion 会重置延迟模式，恢复低延迟设置
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            Main.Log("[Macro-GC] NoGCRegion 已结束");
        }
        catch { /* 预算被突破时 End 会抛异常，属正常回退 */ }
        }

        // ─────────────────────────────────────────────
        //  击发精度统计（Release 可见的低频诊断日志）
        //  lateSec = 触发时刻 audioNow − triggerAt，恒 ≥0，衡量调度精度
        // ─────────────────────────────────────────────
        private static double _fireErrSum;
        private static double _fireErrMax;
        private static int _fireCount;
        private static int _fireStatLastMs;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecordFireError(double lateSec)
        {
        _fireErrSum += lateSec;
        if (lateSec > _fireErrMax) _fireErrMax = lateSec;
        _fireCount++;
        }

        private static void FlushFireStats()
        {
        if (_fireCount == 0) return;
        int now = Environment.TickCount;
        if (unchecked(now - _fireStatLastMs) < 3000) return;
        _fireStatLastMs = now;
        Main.Log($"[Macro-Diag] 击发 {_fireCount} 次 | 平均迟发 {_fireErrSum / _fireCount * 1000.0:F3}ms | 最大 {_fireErrMax * 1000.0:F3}ms");
        _fireErrSum = 0; _fireErrMax = 0; _fireCount = 0;
        }

        /// <summary>
        /// 手法表摘要（每次建表一条，用于从日志判断换手结构是否合理）：
        /// 换手次数/单音碎块/最长连击/左右键用量 + 本图速度倍率范围。
        /// 变速谱面上若"最长连击"异常大（整段一只手）或"单音碎块"异常多
        /// （每个音都换手），能直接从这行看出来。
        /// </summary>
        private static void LogTechniqueTableSummary(HitEvent[] events, List<double> speeds)
        {
        try
        {
            double smin = 0, smax = 0;
            for (int i = 0; i < speeds.Count; i++)
            {
                double v = speeds[i];
                if (v <= 0) continue;
                if (smin == 0 || v < smin) smin = v;
                if (v > smax) smax = v;
            }

            int presses = 0, turns = 0, frag = 0, longest = 0, cur = 0;
            int leftUsed = 0, rightUsed = 0;
            bool lastLeft = false, has = false;
            foreach (var e in events)
            {
                if (e.ReleaseOnly || e.KeyCode == 0) continue;
                presses++;
                bool left = false;
                for (int i = 0; i < _techLeftKeys.Length; i++)
                    if (_techLeftKeys[i] == e.KeyCode) { left = true; break; }
                if (left) leftUsed++; else rightUsed++;

                if (!has) { cur = 1; has = true; }
                else if (left == lastLeft) cur++;
                else
                {
                    if (cur == 1) frag++;
                    if (cur > longest) longest = cur;
                    turns++;
                    cur = 1;
                }
                lastLeft = left;
            }
            if (cur > 0)
            {
                if (cur == 1) frag++;
                if (cur > longest) longest = cur;
            }

            // 注意：Macro.Log 目前是硬编码空实现（logToMod=false），诊断必须
            // 直接走 UMM logger，否则日志里看不到。
            Main.Log($"[Macro-Tech] 手法表: 按下={presses} 换手={turns} 单音碎块={frag} 最长连击={longest} " +
                $"左键={leftUsed} 右键={rightUsed} | 速度倍率 {smin:F2}~{smax:F2} | 拍号BPM={conductor?.bpm:F1}");
        }
        catch (Exception ex) { Main.Log($"[Macro-Tech] 摘要失败: {ex.Message}"); }
        }

        public static void Log(string message)
        {
        Main.Log(message);
        }

        /// <summary>
        /// 逐音符 / 逐按键级别的高频日志（WorkerLoop 内每次按下·松开、每次 SendKey）。
        /// 默认关闭：50~100 音/秒的谱面会每秒写 200+ 行，而 UMM 日志是同步写盘，
        /// 会拖慢工作线程、影响击发时序。需要逐键排障时把 _verboseLog 置 true。
        /// </summary>
        private static bool _verboseLog = false;
        [System.Diagnostics.Conditional("DEBUG")]
        public static void LogVerbose(string message)
        {
            if (_verboseLog) Main.Log(message);
        }
    }
}
