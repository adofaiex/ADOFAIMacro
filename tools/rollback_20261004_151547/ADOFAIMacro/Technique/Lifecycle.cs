using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Technique;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 生命周期：关卡切换时的状态重置、工作线程启停、输入模式切换。
    /// </summary>
    internal static partial class MacroEngine
    {
        // ═══════════════════════════════════════════════════════════════
        //  生命周期
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset(scrController controller) => ResetState(controller);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ResetState(scrController? controller)
        {
            initialized = false;
            _hitEvents = null;
            _hitEventCount = 0;
            cachedFloors = null;
            levelMaker = null;
            conductor = null;
            _initializedLevelPath = null;

            // 轨迹播放器：清状态并让它回到「在线贪心」这条兜底路上
            ResetTraceState();

            Volatile.Write(ref _workerLastTriggeredFloor, -1);
            Interlocked.Exchange(ref _workerNeedsHit, 0);
            // 该 -1 是"重建中"的中间态，必须先宣告未就绪，工作线程才不会把它当作
            // 入场索引锁存（否则会从事件表第 0 个事件重放整首）。
            Volatile.Write(ref _syncFloorReady, false);
            Volatile.Write(ref _anchorA.validFlag, 0);
            Volatile.Write(ref _anchorB.validFlag, 0);
            Interlocked.Increment(ref _resetVersion);

            // 闭环校准：测量状态重置（纯单局控制器，无跨局状态）
            _autoOffsetMs = 0;
            _judgeErrSum = 0;
            _judgeErrSqSum = 0;
            _judgeErrCount = 0;

            // 方案8：关卡结束/重开时释放 NoGCRegion（GC 转到加载期发生），下关重试
            TryEndNoGC();
            _noGcBroken = false;

            // 公式基线/低通状态随关卡重置
            _slewSeeded = false;
            _formulaEngaged = false;

            if (skyHookInitialized) Input.AsyncInputManager.ClearQueue();
            if (controller != null) ApplyHoldBehavior(controller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureWorkerRunning()
        {
            if (_workerRunning && _workerThread?.IsAlive == true) return;

            if (_workerThread != null)
            {
                // 必须无界等待上一位 worker 真正退出后再置 null 并新建：若这里改成
                // 带超时并直接 null，就会有两个 WorkerLoop 并发发按键（重复触发）。
                _workerThread.Join();
                CloseWaitTimerIfWorkerStopped();
                _workerThread = null;
            }

            _workerRunning = true;
            _workerStarted = false;
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest,
                Name = "MacroWorkerThread"
            };
            _workerThread.Start();
            Log("[Macro-Main] 工作线程已启动");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StopWorkerIfNeeded()
        {
            if (!_workerRunning) return;

            _workerRunning = false;

            if (!_workerStarted)
            {
                _workerStarted = true;
                try { _startSignal.Release(); }
                catch (SemaphoreFullException) { }
            }

            // 有界等待：worker 最多在 Sleep(1) 循环里待约 50ms 就会看到 _workerRunning=false
            if (_workerThread != null && !_workerThread.Join(500))
            Log("[Macro-Main] 工作线程 500ms 内未退出，交由下一次 EnsureWorkerRunning 回收");
            CloseWaitTimerIfWorkerStopped();

            if (skyHookInitialized)
            {
                _cachedSkyHookMode = false;
                Input.AsyncInputManager.Stop();
                skyHookInitialized = false;
            }
            TryEndNoGC(); // 暂停期间释放，避免长时间累积内存
            Log("[Macro-Main] 工作线程已停止");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwitchMode(bool useSkyHook)
        {
            if (useSkyHook == skyHookInitialized) return;
            Log($"[Macro-Main] 切换模式: {(useSkyHook ? "SkyHook" : "SendInput")}");

            if (useSkyHook)
            {
                Input.AsyncInputManager.Start();
                if (!Input.AsyncInputManager.IsInitialized)
                {
                    Log("[Macro-Main] SkyHook 启动失败，回退到 SendInput");
                    Main.Settings.SkyHookMode = false; return;
                }
                skyHookInitialized = true;
                _cachedSkyHookMode = true;
                Main.Settings.SkyHookMode = true;
            }
            else
            {
                _cachedSkyHookMode = false;
                Input.AsyncInputManager.Stop();
                skyHookInitialized = false;
                Main.Settings.SkyHookMode = false;
            }
        }
    }
}
