using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using ADOFAIMacro.Core;
using ADOFAIMacro.Timing;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// Bounded FIFO from the angle player to its single key-emission thread. Flush
    /// invalidates even an edge already dequeued to wait, and returns the first
    /// canceled index so pausing does not advance the playback cursor past it.
    /// </summary>
    internal static partial class MacroEngine
    {
        // ── 待发事件（值类型，零 GC 压力）────────────────────────
        private struct AadPending
        {
            public int Index;
            public long Generation;
            public long EventTicks;     // 事件时间戳（= hitStar - lead，主线程已钳位）
            public double TriggerTime;  // 诊断用
            public byte KeyCode;
            public byte ReleaseKeyCode;
            public bool ReleaseOnly;
            public bool IsHoldRelated;
            public bool SimulateKey;
            public bool EnableTechnique;
        }

        private const int AadCapacity = 4096;

        private static readonly AadPending[] _aadQueue = new AadPending[AadCapacity];
        private static int _aadHead;
        private static int _aadTail;
        private static int _aadCount;
        private static int _aadInFlightIndex = -1;
        private static long _aadGeneration;
        private static readonly object _aadLock = new();

        // 唤醒信号（空队列时等待；100ms 超时兜底，保证 _aadRunning/_aadHold 变化能被看到）
        private static readonly SemaphoreSlim _aadWake = new(0, 1);

        private static volatile bool _aadRunning;
        private static volatile bool _aadHold;          // 停等（暂停/回退/换关）
        private static volatile bool _aadNeedRelease;   // 停等时要求线程松开自己的键
        private static Thread? _aadThread;

        // 本线程私有的按键记账（不碰主线程的 _angleDownKeys —— 跨线程访问不安全）
        private static readonly HashSet<byte> _aadDownKeys = new(4);
        private static byte _aadHoldKey;
        private static bool _aadHoldDown;

        // ── 诊断计数 ──────────────────────────────────────────────
        private static long _aadPublished;
        private static long _aadFired;
        private static long _aadFullRetries;
        private static long _aadLate;
        private static long _aadMaxLateTicks;
        private static long _aadErrors;
        private static long _aadBurstFires;   // 追账突发（同一 16.7ms 窗口内 >预算）

        internal static bool AngleAsyncRunning => _aadRunning;

        /// <summary>诊断行（[Macro-AngleAsync] 探针）。</summary>
        internal static string AngleAsyncStats =>
            $"发布 {Volatile.Read(ref _aadPublished)} / 发出 {Volatile.Read(ref _aadFired)}" +
            $" / 满载重试 {Volatile.Read(ref _aadFullRetries)} / 迟发 {Volatile.Read(ref _aadLate)}" +
            $" / 最大迟发 {Volatile.Read(ref _aadMaxLateTicks) / 1e4:F2}ms" +
            $" / 突发 {Volatile.Read(ref _aadBurstFires)} / 异常 {Volatile.Read(ref _aadErrors)}";

        /// <summary>启动异步发键线程（幂等）。失败返回 false，调用方退回帧同步路径。</summary>
        private static bool EnsureAngleAsyncRunning()
        {
            if (_aadRunning && _aadThread?.IsAlive == true) return true;
            // A bounded stop can return before cleanup finishes. Never spawn a second sender.
            if (_aadThread?.IsAlive == true) return false;
            try
            {
                lock (_aadLock)
                {
                    _aadHead = _aadTail = _aadCount = 0;
                    _aadInFlightIndex = -1;
                    _aadHold = false;
                    _aadNeedRelease = false;
                    _aadRunning = true;
                }
                var t = new Thread(AngleAsyncLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "MacroAngleAsync"
                };
                _aadThread = t;
                t.Start();
                Main.Log("[Macro-AngleAsync] 异步发键线程已启动");
                return true;
            }
            catch (Exception ex)
            {
                _aadRunning = false;
                Main.Log($"[Macro-AngleAsync] 线程启动失败（退回帧同步）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>停止线程（换关/模组停用）。幂等。</summary>
        private static void StopAngleAsync()
        {
            lock (_aadLock)
            {
                _aadRunning = false;
                InvalidateAngleQueue();
            }
            WakeAngleWorker();
            var t = _aadThread;
            if (t == null || !t.IsAlive || t.Join(500)) _aadThread = null;
            // On timeout the thread identity stays registered until it actually exits.
        }

        private static void WakeAngleWorker()
        {
            try { _aadWake.Release(); } catch (SemaphoreFullException) { }
        }

        // Caller holds _aadLock. A waiting edge retains its generation, never the queue's new one.
        private static int InvalidateAngleQueue()
        {
            int first = _aadInFlightIndex;
            if (_aadCount > 0 && first < 0) first = _aadQueue[_aadHead].Index;
            _aadHead = _aadTail = _aadCount = 0;
            _aadInFlightIndex = -1;
            Interlocked.Increment(ref _aadGeneration);
            return first;
        }

        /// <summary>
        /// 主线程：发布一个待发事件。返回 false 表示未入队（线程未运行/队列满），
        /// 队列满时调用方保留游标，下一帧按原顺序重试。
        /// </summary>
        private static bool PublishAngleEvent(int index, long eventTicks, double triggerTime, byte keyCode,
            byte releaseKeyCode, bool releaseOnly, bool isHoldRelated,
            bool simulateKey, bool enableTechnique)
        {
            if (!_aadRunning) return false;
            lock (_aadLock)
            {
                // Backpressure: retry the same edge next frame, without evicting any key-up.
                if (!_aadRunning || _aadHold) return false;
                if (_aadCount >= AadCapacity) { Interlocked.Increment(ref _aadFullRetries); return false; }
                _aadQueue[_aadTail] = new AadPending
                {
                    Index = index,
                    Generation = _aadGeneration,
                    EventTicks = eventTicks,
                    TriggerTime = triggerTime,
                    KeyCode = keyCode,
                    ReleaseKeyCode = releaseKeyCode,
                    ReleaseOnly = releaseOnly,
                    IsHoldRelated = isHoldRelated,
                    SimulateKey = simulateKey,
                    EnableTechnique = enableTechnique,
                };
                _aadTail = (_aadTail + 1) % AadCapacity;
                _aadCount++;
            }
            Interlocked.Increment(ref _aadPublished);
            WakeAngleWorker();
            return true;
        }

        /// <summary>
        /// 主线程：清空待发队列 + 要求线程停等并松开自己按下的键。
        /// 暂停/死亡/离开 PlayerControl/砖序回退/换关时调用（防卡键、防越界发键）。
        /// </summary>
        private static int FlushAngleAsync()
        {
            int first;
            lock (_aadLock)
            {
                if (!_aadRunning || _aadHold) return -1;
                _aadHold = true;
                _aadNeedRelease = true;
                first = InvalidateAngleQueue();
            }
            WakeAngleWorker();
            return first;
        }

        private static void ResumeAngleAsync()
        {
            lock (_aadLock)
            {
                if (!_aadRunning || !_aadHold) return;
                _aadHold = false;
            }
            WakeAngleWorker();
        }

        // ─────────────────────────────────────────────────────────
        //  线程主体
        // ─────────────────────────────────────────────────────────
        private static void AngleAsyncLoop()
        {
            // 让定时器精度生效（与 WorkerLoop 同款；工作线程自己声明一次）
            timeBeginPeriod(1);
            long lastFrameBucket = 0;
            int burstInBucket = 0;
            try
            {
                while (_aadRunning)
                {
                    AadPending ev;
                    bool has, release;
                    lock (_aadLock)
                    {
                        release = _aadNeedRelease;
                        _aadNeedRelease = false;
                        has = !_aadHold && _aadCount > 0;
                        if (has)
                        {
                            ev = _aadQueue[_aadHead];
                            _aadHead = (_aadHead + 1) % AadCapacity;
                            _aadCount--;
                            _aadInFlightIndex = ev.Index;
                        }
                        else ev = default;
                    }
                    if (release) ReleaseAllAsyncKeys("播放暂停或重新定位");
                    if (!has)
                    {
                        try { _aadWake.Wait(100); } catch { }
                        continue;
                    }

                    try
                    {
                        if (!WaitUntilTicks(ev.EventTicks, ev.Generation)) continue;

                        long now = PreciseNow.LocalTicks();
                        long late = now - ev.EventTicks;
                        if (late > 10_000)   // >1ms
                        {
                            Interlocked.Increment(ref _aadLate);
                            long prev = Volatile.Read(ref _aadMaxLateTicks);
                            if (late > prev) Volatile.Write(ref _aadMaxLateTicks, late);
                        }

                        // 追账突发统计（同一 ~16.7ms 窗口内发太多 = 跟不上）
                        long bucket = now / 167_000;
                        if (bucket == lastFrameBucket) burstInBucket++;
                        else { lastFrameBucket = bucket; burstInBucket = 1; }
                        if (burstInBucket == 257) Interlocked.Increment(ref _aadBurstFires);

                        lock (_aadLock)
                        {
                            if (!_aadRunning || _aadHold || _aadGeneration != ev.Generation) continue;
                            _aadInFlightIndex = -1;
                            FireAngleEventOnWorker(in ev, now);
                        }
                        Interlocked.Increment(ref _aadFired);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _aadErrors);
                        if (Volatile.Read(ref _aadErrors) <= 3)
                            Main.Log($"[Macro-AngleAsync] 发键异常（第 {Volatile.Read(ref _aadErrors)} 次）: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _aadErrors);
                Main.Log($"[Macro-AngleAsync] 线程退出异常: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { ReleaseAllAsyncKeys("线程退出"); } catch { }
                timeEndPeriod(1);
                _aadRunning = false;
            }
        }

        /// <summary>
        /// 精确等到目标 tick。目标 tick 与 <see cref="PreciseNow.LocalTicks"/> 同域
        ///（.NET DateTime ticks，1ms = 1e4 ticks）。远 future 用 HighResSleep 睡到
        /// 目标前 1.5ms，末段自旋 —— 与 KeyWorker.WorkerLoop 同款设施。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool WaitUntilTicks(long targetTicks, long generation)
        {
            const long spinWindow = 15_000;   // 1.5ms
            while (_aadRunning && generation == Volatile.Read(ref _aadGeneration))
            {
                long remain = targetTicks - PreciseNow.LocalTicks();
                if (remain <= 0) return true;

                if (remain > spinWindow)
                {
                    double sleepSec = (remain - spinWindow) / 1e7;
                    if (sleepSec > 0.0005) HighResSleep(Math.Min(sleepSec, 0.005));
                    else Thread.SpinWait(200);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }
            return false;
        }

        /// <summary>
        /// 工作线程上执行发键。与 TracePlayer.FireTraceEvent 逐分支同语义，
        /// 但按键记账用本线程私有的 _aadDownKeys（不碰主线程的 _angleDownKeys）。
        ///
        /// ⚠ 时间戳仍用主线程算好的 ev.EventTicks（不重算），保证与帧同步路径
        /// 发出的事件逐字段相同；发出前再封顶当下（绝无未来时间戳）。
        /// </summary>
        private static void FireAngleEventOnWorker(in AadPending ev, long nowTicks)
        {
            long ticks = ev.EventTicks;
            if (ticks > nowTicks) ticks = nowTicks;      // 封顶当下

            if (!ev.SimulateKey)
            {
                Interlocked.Increment(ref _workerNeedsHit);
                return;
            }

            if (ev.ReleaseOnly)
            {
                if (ev.EnableTechnique)
                {
                    byte keyToRelease = ev.IsHoldRelated
                        ? (ev.ReleaseKeyCode != 0 ? ev.ReleaseKeyCode : _aadHoldKey)
                        : ev.ReleaseKeyCode;
                    if (ev.IsHoldRelated ? _aadHoldDown && keyToRelease == _aadHoldKey : _aadDownKeys.Remove(keyToRelease))
                        SendKey(keyToRelease, false, ticks);
                    if (ev.IsHoldRelated) { _aadHoldKey = 0; _aadHoldDown = false; }
                }
                else
                {
                    if (ev.IsHoldRelated) WorkerReleaseHoldKey();
                    else WorkerReleaseKey(ev.ReleaseKeyCode);
                }
                return;
            }

            if (ev.IsHoldRelated)
            {
                if (ev.EnableTechnique)
                {
                    if (_aadHoldDown)
                    {
                        SendKey(_aadHoldKey, false, ticks);
                        _aadHoldKey = 0; _aadHoldDown = false;
                    }
                    if (SendKey(ev.KeyCode, true, ticks))
                    { _aadHoldKey = ev.KeyCode; _aadHoldDown = true; }
                }
                else WorkerHoldKey(ev.KeyCode);
                return;
            }

            if (ev.EnableTechnique)
            {
                // 与帧同步路径逐行同语义：裸按下，不预松旧键、不做换键判断。
                if (SendKey(ev.KeyCode, true, ticks)) _aadDownKeys.Add(ev.KeyCode);
            }
            else WorkerPressKey(ev.KeyCode);
        }

        /// <summary>
        /// 松开本线程按下的所有残留键（停等/退出兜底，防卡键）。
        /// 只在工作线程内调用（单线程访问 _aadDownKeys）。
        /// </summary>
        private static void ReleaseAllAsyncKeys(string why)
        {
            if (_aadDownKeys.Count == 0 && !_aadHoldDown) return;
            var keys = new List<byte>(_aadDownKeys);
            if (_aadHoldDown && _aadHoldKey != 0) keys.Add(_aadHoldKey);
            foreach (byte k in keys) SendKey(k, false, 0);
            if (keys.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (byte k in keys) { if (sb.Length > 0) sb.Append(','); sb.Append("0x").Append(k.ToString("X2")); }
                Main.Log($"[Macro-AngleAsync] 松开残留按键 {sb}（{why}）");
            }
            _aadDownKeys.Clear();
            if (_aadHoldDown) { _aadHoldKey = 0; _aadHoldDown = false; }
        }
    }
}
