using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#nullable enable

namespace ADOFAIMacro.Macro
{
#pragma warning disable CS0420
    #region TimeBasedMacro

    internal static class Macro
    {
        // ─────────────────────────────────────────────
        //  预处理事件
        // ─────────────────────────────────────────────
        internal readonly struct HitEvent(double triggerTime, byte keyCode, bool releaseOnly,
                                          bool isHoldRelated = false, byte releaseKeyCode = 0)
        {
            public readonly double TriggerTime = triggerTime;
            public readonly byte KeyCode = keyCode;
            public readonly bool ReleaseOnly = releaseOnly;
            public readonly bool IsHoldRelated = isHoldRelated;
            public readonly byte ReleaseKeyCode = releaseKeyCode;
        }

        private readonly struct PieceInfo(int ec, int h, double pl, double st, double et, int es, int mult = 0)
        {
            public readonly int EvCount = ec;
            public readonly int Hand = h;
            public readonly double PieceLen = pl;
            public readonly double StartTime = st;
            public readonly double EndTime = et;
            public readonly int EvStart = es;
            public readonly int Multiplier = mult;
        }

        // ─────────────────────────────────────────────
        //  主线程专属数据
        // ─────────────────────────────────────────────
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;
        private static scrFloor[]? cachedFloors;
        private static bool initialized = false;
        private static string lastKeysSetting = "";
        // Immutable snapshot - updated atomically, read without lock
        private static volatile byte[] _keyCodesSnapshot = [];

        // ─────────────────────────────────────────────
        //  只读共享数据（初始化后不变）
        // ─────────────────────────────────────────────
        private static HitEvent[]? _hitEvents;
        private static int _hitEventCount;
        private static int floorCount;
        // 初始化时所属关卡路径：仅靠地板数量判断是否需要重建会在"两张谱面地板数
        // 相同"时漏判，导致沿用错误的时间表。Reset() 通常会兜住，但这里更稳。
        private static string? _initializedLevelPath;

        // ─────────────────────────────────────────────
        //  预分配对象池（减少 GC 压力）
        // ─────────────────────────────────────────────
        private static readonly HitEvent[] _hitEventPool = new HitEvent[65536];
        private static int _hitEventPoolUsed;

        // 复用缓冲区
        private static readonly List<double> _evTimeRecycle = [with(4096)];
        private static readonly List<int> _evPressRecycle = [with(4096)];
        private static readonly List<int> _evFloorRecycle = [with(4096)];
        // 逐地板速度倍率（scrFloor.speed）：变速谱面必须按每个音符所在楼层的
        // 实际速度折算局部速率，否则快段仍按进关时的全局速度切片，换手全错。
        private static readonly List<double> _evSpeedRecycle = [with(4096)];
        private static readonly List<PieceInfo> _piecesRecycle = [with(1024)];

        // ─────────────────────────────────────────────
        //  时间锚点（双缓冲）
        // ─────────────────────────────────────────────
        private sealed class TimeAnchor
        {
            public double songPosRef;
            public long qpcSnapshot;
            public double rate;          // 位置推进速率（≈ pitch，由最小二乘拟合）
            public double timeOffset;
            public bool simulateKeyPress;

            public HitEvent[]? hitEvents;
            public int hitEventCount;

            public int validFlag;
            public int staticVersion;

#pragma warning disable IDE1006
            public bool valid
#pragma warning restore IDE1006
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref validFlag) == 1;
            }
        }

        private static readonly TimeAnchor _anchorA = new();
        private static readonly TimeAnchor _anchorB = new();
        private static volatile TimeAnchor _currentAnchor = _anchorA;

        // 锚点被视为"过期"的阈值（秒）。主线程每帧刷新锚点，正常 elapsed 远小于一帧；
        // 该阈值仅用于防止外推失控（见 WorkerLoop 中的过期保护），取值远大于正常帧间隔
        // 与常见卡顿，不会影响正常判定。
        private const double AnchorStaleSec = 0.5;

        private static int _staticAnchorVersion = 0;

        // 方案9：minusi 兜底低通状态（主线程；公式基线接管时清除）
        private static double _songPosSm;
        private static long _smRefTick;
        private static double _smPitch;
        private static bool _slewSeeded;
        private static bool _formulaEngaged;

        private static readonly double perfFreqInv;

        private static volatile int _workerLastTriggeredFloor = -1;
        private static volatile int _workerNeedsHit = 0;
        private static volatile int _resetVersion = 0;
        // 入场索引"已就绪"标志：ResetState 置 false，Initialize 写完 SyncFloor 后置 true。
        // 用途见 WorkerLoop 的重同步分支：ResetState 与 Initialize 之间存在毫秒级窗口
        // （BuildHitEvents 可能耗时毫秒级），期间 _workerLastTriggeredFloor 处于中间态。
        private static volatile bool _syncFloorReady;
#if DEBUG
        private static volatile bool _debugWorkerInitLogged = false;
#endif

        // ─────────────────────────────────────────────
        //  工作线程控制
        // ─────────────────────────────────────────────
        private static volatile Thread? _workerThread;
        private static volatile bool _workerRunning = false;
        private static readonly SemaphoreSlim _startSignal = new(0, 1);
        private static volatile bool _workerStarted = false;

        private static byte _pendingKey;
        private static bool _isKeyDown;
        private static byte _holdKey;
        private static bool _isHoldDown;

        private static volatile bool _cachedSkyHookMode = false;
        private static volatile bool _cachedHighPrecision = false;
        private static volatile bool skyHookInitialized = false;
        private static bool _virtualAsyncInitDone = false;

        // 时间源委托（消除分支）
        private static Func<long> _getTicksImpl;
        private static readonly Func<long> _getTicksNormal;
        private static readonly Func<long> _getTicksHigh;

        // ─────────────────────────────────────────────
        //  Win32
        // ─────────────────────────────────────────────
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        // ─────────────────────────────────────────────
        //  手法模拟全局数据
        // ─────────────────────────────────────────────
        private static byte[] _techLeftKeys = [];
        private static byte[] _techRightKeys = [];
        private static readonly int[][][] _techKeyOrders = [[], []];
        private static readonly double[][] _techPressDur = [[], []];
        private static List<Settings.TechniqueSegment>? _currentSegments;

        [ThreadStatic]
        private static SkyHookSystem.INPUT _cachedInput;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, IntPtr pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);
        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        // ─────────────────────────────────────────────
        //  高分辨率可等待定时器（工作线程近未来等待用）
        //  Sleep(1) 粒度 1~2ms；Yield/SpinWait 空转烧 CPU。
        //  Win10 1803+ 的 HIGH_RESOLUTION 定时器粒度 ~0.5ms 且不占 CPU。
        //  创建失败（旧系统）时回退 Thread.Sleep，最后 1.5ms 自旋兜底精度。
        // ─────────────────────────────────────────────
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private static IntPtr _hWaitTimer = IntPtr.Zero;

        // 无 SetLastError：从不读取 GetLastWin32Error，省掉每次调用的错误码存取
        [DllImport("Kernel32.dll")]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);
        [DllImport("Kernel32.dll")]
        private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);
        [DllImport("Kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
        // 用于关闭高分辨率可等待定时器句柄（旧实现只创建、从不关闭 → 句柄泄漏；
        // 且工作线程每次重启都会复用同一个句柄，线程退出后没人释放）
        [DllImport("Kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // 睡眠至多 seconds 秒（可能略短）；到点必然返回
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HighResSleep(double seconds)
        {
            if (seconds <= 0) return;

            if (_hWaitTimer == IntPtr.Zero)
                _hWaitTimer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (_hWaitTimer == IntPtr.Zero)
            {
                // 回退：Sleep 粒度 1~2ms，会睡过头——少睡 1ms，宁可早醒进自旋，不可迟到
                int ms = (int)Math.Ceiling(seconds * 1000.0) - 1;
                Thread.Sleep(ms < 1 ? 1 : ms);
                return;
            }

            long due = -(long)Math.Ceiling(seconds * 1e7); // 负值 = 相对时间（100ns 单位）
            if (SetWaitableTimer(_hWaitTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_hWaitTimer, unchecked((uint)-1)); // INFINITE
        }

        /// <summary>
        /// 工作线程确认退出后释放可等待定时器句柄。
        /// 句柄仅工作线程使用，因此必须在 <c>IsAlive == false</c> 时关闭，避免"释放后使用"；
        /// 若线程仍在运行（Join 超时）则保留句柄，下次线程退出时再关，不重复创建。
        /// </summary>
        private static void CloseWaitTimerIfWorkerStopped()
        {
            if (_workerThread?.IsAlive == true) return;
            IntPtr h = Interlocked.Exchange(ref _hWaitTimer, IntPtr.Zero);
            if (h != IntPtr.Zero) CloseHandle(h);
        }

        private static readonly long perfFrequency;
        private static readonly bool usePerfCounter;
        private static readonly byte[] scanCodeCache = new byte[256];
        private static IntPtr _gameWindowHandle = IntPtr.Zero;

        // ─────────────────────────────────────────────
        //  按键名称 → VK 映射（internal，供 TechniqueSimulator 复用）
        // ─────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] ParseTechKeyList(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return [0x4A];
            var result = new List<byte>();
            foreach (var part in input!.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                // 统一解析：单字符 / 键名 / 十六进制 0xNN（旧实现丢弃 0xNN）
                if (KeyMap.TryParse(part, out byte code)) result.Add(code);
            }
            return result.Count == 0 ? [0x4A] : [.. result];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Macro()
        {
            usePerfCounter = QueryPerformanceFrequency(out perfFrequency);
            perfFreqInv = (usePerfCounter && perfFrequency > 0) ? 1.0 / perfFrequency : 1e-7;
            for (int i = 0; i < 256; i++) scanCodeCache[i] = (byte)MapVirtualKey((uint)i, 0);
            // 预分配时间源委托，避免每帧创建
            _getTicksNormal = GetTicks;
            _getTicksHigh = new Func<long>(DSPTimeSimulater.GetDSPTimeAsFileTime);
            _getTicksImpl = _getTicksNormal; // 默认
        }

        // ─────────────────────────────────────────────
        //  方案7b：判定误差闭环校准（按速度学习）
        //  7a 的问题（实测）：① 窗口横跨变速段时均值被污染，积分缠绕到撞限；
        //  ② 换段后旧偏移失效，重收敛 2~3 秒。
        //  改进：误差样本按当前速度段分桶；换段（滞回 4 样本）时提交已收敛的
        //  偏移到速度→偏移学习表，重进同速度段直接装载（瞬间收敛）；
        //  环路加速（100ms 窗 / 增益 0.5 / 步长 4ms / 死区 1ms）。
        //  全部主线程操作，无并发问题。
        // ─────────────────────────────────────────────
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

            Main.Mod?.Logger.Log($"[Macro-Cali] err={mean:F2}ms autoOffset={_autoOffsetMs:F2}ms");
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
                    Main.Mod?.Logger.Log("[Macro-GC] NoGCRegion 已启用（游玩期抑制 GC 停顿）");
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
                Main.Mod?.Logger.Log("[Macro-GC] NoGCRegion 已结束");
            }
            catch { /* 预算被突破时 End 会抛异常，属正常回退 */ }
        }

        // ═══════════════════════════════════════════════════════════════
        //  主线程：每帧写锚点
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            var settings = Main.Settings;

            if (!settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
            {
                StopWorkerIfNeeded();
                // 宏关闭时把 requireHolding 交还游戏（幂等，单次字段写入）
                if (!settings.Macro) RestoreHoldBehavior();
                return;
            }

            EnsureWorkerRunning();

            if (settings.SkyHookMode != skyHookInitialized) SwitchMode(settings.SkyHookMode);

            // 虚拟异步键盘状态刷新（主线程；工作线程只读 volatile）
            if (!_virtualAsyncInitDone) { _virtualAsyncInitDone = true; VirtualAsyncInput.Initialize(); }
            VirtualAsyncInput.RefreshActive();
            bool hp = settings.HighPrecisionTime;
            if (hp != _cachedHighPrecision)
            {
                _cachedHighPrecision = hp;
                UpdateTicksDelegate();
            } // 仅当设置变化时更新时间源委托

            if (!initialized)
            {
                Initialize();
                if (!initialized) return;
            }
            else if (NeedReinitialize())
            {
                ResetState(controller);
                Initialize();
                if (!initialized) return;
            }

            // 先无条件取走积压计数：失焦时命中不能留在计数器里。
            // 工作线程在 SimulateKeyPress=false 路径不做焦点判断，失焦期间会持续累加，
            // 回到前台时旧实现会在单帧内把数百次 Hit() 一次性灌进游戏（洪峰）。
            int pendingHits = Interlocked.Exchange(ref _workerNeedsHit, 0);
            if (!Main.Settings.BlockInputWhenUnfocused || IsGameWindowFocused())
            {
                scrPlanet? hitPlanet = controller.chosenPlanet;
                scrPlayer? hitPlayer = hitPlanet != null ? hitPlanet.player : null;
                if (hitPlayer != null)
                {
                    // 2026-09 游戏 API：Hit(long? hitTick, bool isAuto)。
                    // 与游戏 scrPlayer.Die 内部同款调用：无输入事件(null)、非自动命中。
                    for (int h = 0; h < pendingHits; h++) hitPlayer.Hit(null, false);
                }
            }

            // 方案8：游玩期 GC 抑制（每次进关尝试，失败自动回退）
            TryBeginNoGC();

#if DEBUG
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
#endif
            float pitch = conductor!.song.pitch;
            long qpcSnap = GetRawTicks();
            double currentSongPos = conductor!.songposition_minusi;

            // 方案9：判定公式基线（修复时钟域 bug）。
            // 判定位置 = ((T − offsetTick)/1e7 − dspTimeSong − cal_i)·pitch − addoffset，
            // 其中 T 必须与 offsetTick 同域：DateTime 域 ticks。方案6 起误用了
            // 工作线程的 dsp/QPC 时钟（纪元不同）→ 公式从未生效，所有 session
            // 实际都是 minusi 裸采样基线（阶梯锯齿直接透传，开局 ±0~60ms 彩票）。
            // 现在 T = PreciseNow.LocalTicks()——与 Update_1 补丁后的 currFrameTick
            // 同源同钟，公式真正可用且无需任何击中即可对齐判定（消灭开局彩票）。
            // minusi 兜底恢复有界低通（平滑音频缓冲阶梯波）。
            double rate = pitch;
            double anchorPos;
            bool formulaOk = false;
            double judgedPos = double.NaN;
            try
            {
                if (global::AsyncInputManager.offsetTickUpdated)
                {
                    double dspNow = (PreciseNow.LocalTicks() - (long)global::AsyncInputManager.offsetTick) / 1e7;
                    judgedPos = (dspNow - conductor.dspTimeSong - (double)scrConductor.calibration_i)
                                * pitch - conductor.addoffset;
                    formulaOk = Math.Abs(judgedPos - currentSongPos) <= 0.03;
                }
            }
            catch { }

            if (formulaOk)
            {
                if (!_formulaEngaged)
                {
                    _formulaEngaged = true;
                    Main.Mod?.Logger.Log("[Macro] 判定公式基线已接管（无需击中即对齐判定）");
                }
                anchorPos = judgedPos;
                _slewSeeded = false;   // 公式接管时清除兜底低通状态
            }
            else
            {
                // minusi 兜底：有界低通锁相（音频 dspTime 是 ~10-21ms 阶梯波）
                if (!_slewSeeded)
                {
                    _songPosSm = currentSongPos;
                    _smRefTick = qpcSnap;
                    _smPitch = pitch;
                    _slewSeeded = true;
                }
                double projected = _songPosSm + ElapsedSec(_smRefTick, qpcSnap) * _smPitch;
                double err = currentSongPos - projected;
                if (Math.Abs(err) > 0.05)
                    _songPosSm = currentSongPos;   // 跳变（暂停恢复等）
                else
                {
                    double adj = err * 0.15;
                    if (adj > 0.001) adj = 0.001;
                    else if (adj < -0.001) adj = -0.001;
                    _songPosSm += adj;
                }
                _smRefTick = qpcSnap;
                _smPitch = pitch;
                anchorPos = _songPosSm;
            }

            // 方案7：闭环校准步进（判定误差 → 自动偏移）
            StepAutoCalibration();

            var anchor = ReferenceEquals(_currentAnchor, _anchorA) ? _anchorB : _anchorA;

            anchor.songPosRef = anchorPos;
            anchor.qpcSnapshot = qpcSnap;
            anchor.rate = rate;
            anchor.timeOffset = (settings.TimeOffset + _autoOffsetMs) * 0.001;
            anchor.simulateKeyPress = settings.SimulateKeyPress;

            if (anchor.staticVersion != _staticAnchorVersion)
            {
                anchor.hitEvents = _hitEvents;
                anchor.hitEventCount = _hitEventCount;
                anchor.staticVersion = _staticAnchorVersion;
            }

            Volatile.Write(ref anchor.validFlag, 1);
            Volatile.Write(ref _currentAnchor, anchor);

            if (!_workerStarted) { _workerStarted = true; _startSignal.Release(); }

#if DEBUG
            Log($"[Macro-Main] ANCHOR posRef={anchorPos:F6} rate={rate:F4} qpcSnap={qpcSnap} lastFloor={lastFloor} judgedAligned={anchorPos != currentSongPos}");
            _debugWorkerInitLogged = false;
#endif

            // Hotkey handling (merged from HandleInput to reduce call overhead)
            if (Main.Settings.EnableKeyAdjust || Main.Settings.EnableArrowTimeAdjust)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl && Main.Settings.EnableKeyAdjust)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow)) Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                    else if (Input.GetKeyDown(KeyCode.RightArrow)) Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                }
                else if (!ctrl && Main.Settings.EnableArrowTimeAdjust)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow)) Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                    else if (Input.GetKeyDown(KeyCode.RightArrow)) Main.Settings.TimeOffset += Main.Settings.AdjustStep;
                }
            }
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
            Main.Mod?.Logger.Log($"[Macro-Diag] 击发 {_fireCount} 次 | 平均迟发 {_fireErrSum / _fireCount * 1000.0:F3}ms | 最大 {_fireErrMax * 1000.0:F3}ms");
            _fireErrSum = 0; _fireErrMax = 0; _fireCount = 0;
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerLoop()
        {
            Log("[Macro-Worker] 工作线程启动");
            timeBeginPeriod(1);
            try
            {
                _startSignal.Wait();
                if (!_workerRunning) return;

                int localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                int localResetVer = Volatile.Read(ref _resetVersion);
                // 若启动时主线程刚做完 ResetState（索引仍是 -1 中间态）、或该次 Initialize
                // 失败，则挂起等待就绪信号，避免用中间态索引从错误位置开始。
                bool needsResync = !Volatile.Read(ref _syncFloorReady);

                while (_workerRunning)
                {
                    var anchor = Volatile.Read(ref _currentAnchor);

                    if (!anchor.valid || anchor.hitEvents == null || anchor.hitEventCount == 0)
                    {
                        Thread.Sleep(1); continue;
                    }

                    int curResetVer = Volatile.Read(ref _resetVersion);
                    if (curResetVer != localResetVer)
                    {
                        // ⚠️ 不能在这里直接读共享索引：ResetState 与 Initialize 之间
                        // 存在窗口（BuildHitEvents 是毫秒级），此刻读到的是中间态 -1。
                        // 若在此锁存 -1，等新锚点发布时 version 已被消费、不会再次重同步，
                        // 结果是从事件表第 0 个事件重放整首（大规模误触发）。
                        // 改为挂起，等主线程发布"入场索引已就绪"后再取。
                        localResetVer = curResetVer;
                        needsResync = true;
                    }
                    if (needsResync)
                    {
                        if (!Volatile.Read(ref _syncFloorReady)) { Thread.Sleep(1); continue; }
                        localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                        needsResync = false;
                    }

                    var events = anchor.hitEvents;
                    int evCount = anchor.hitEventCount;
                    bool simulateKey = anchor.simulateKeyPress;
                    double timeOffset = anchor.timeOffset;
                    double rate = anchor.rate;
                    double songPosRef = anchor.songPosRef;
                    long qpcSnapshot = anchor.qpcSnapshot;

#if DEBUG
                    if (!_debugWorkerInitLogged)
                    {
                        Log($"[Macro-Worker] EXTRACT (init) rate={rate:F4} songPosRef={songPosRef:F6} qpc={qpcSnapshot} evCount={evCount}");
                        _debugWorkerInitLogged = true;
                    }
#endif

                    int hitCount = 0;
                    bool triggered = false;

                    if (localLastFloor >= evCount - 1)
                    {
                        int ver = Volatile.Read(ref _resetVersion);
                        for (int s = 0; s < 50 && _workerRunning && Volatile.Read(ref _resetVersion) == ver; s++)
                            Thread.Sleep(1);
                        goto WriteBack;
                    }

                    for (int i = localLastFloor + 1; i < evCount;)
                    {
                        if (Volatile.Read(ref _resetVersion) != localResetVer) goto WriteBack;

                        bool hp = _cachedHighPrecision;
                        long qpcNow = GetRawTicks();
                        double elapsed = (double)(qpcNow - qpcSnapshot) * (hp ? 1e-7 : perfFreqInv);

                        // 锚点过期保护：Update 每帧刷新锚点（正常 elapsed 远小于一帧）。若主线程
                        // 长时间不再更新锚点（例如游戏暂停 / 切出 PlayerControl 状态，导致
                        // Patches 里的 Macro.Update 根本不被调用、StopWorkerIfNeeded 也不会执行），
                        // 继续外推会把 audioNow 推到很远 → 一次性触发掉大量剩余事件，且解除暂停后
                        // 宏会认为这些地板"已处理"（后续全部漏判）。超过阈值即停止外推、等新锚点。
                        if (elapsed > AnchorStaleSec) { Thread.Sleep(1); break; }

                        // 方案4：audioNow = 锚点位置 + QPC 真实流逝 × pitch（DSP 估计已退出外推链路）
                        double audioNow = songPosRef + elapsed * rate;
                        double triggerAt = events[i].TriggerTime + timeOffset;

#if DEBUG
                        if (i < 5 || Math.Abs(triggerAt - audioNow) > 0.5)
                            Log($"[Macro-Worker] TICK i={i} audioNow={audioNow:F6} triggerAt={triggerAt:F6} diff={triggerAt - audioNow:F6} elapsed={elapsed:F6}");
#endif

                        if (triggerAt > audioNow)
                        {
                            if (rate <= 0.0) { Thread.Sleep(1); break; }
                            double waitSec = (triggerAt - audioNow) / rate;
                            if (waitSec > 0.01) { Thread.Sleep(1); break; } // 远future，睡眠
                            else if (waitSec > 0.0015)
                            {
                                // 近future（1.5~10ms）：高分辨率定时器睡到目标前 1.5ms，
                                // 最后 1.5ms 走下面的自旋。原实现在 3~10ms 窗口内
                                // Thread.Yield() 满核空转（高密度段落每事件最多烧 ~10ms CPU，
                                // 是高速段 CPU 占用高的主因）。
                                // 注意：audioNow/triggerAt 的计算未变，只改等待方式。
                                HighResSleep(waitSec - 0.0015);
                            }
                            else Thread.SpinWait(1000); // 极近（≤1.5ms），自旋等待保证微秒级触发
                            continue;
                        }

                        ref readonly var ev = ref events[i];
                        RecordFireError(audioNow - triggerAt);
                        bool enableTechnique = Main.Settings.EnableTechniqueSimulation;

                        if (!simulateKey)
                        {
                            hitCount++;
                            LogVerbose($"[Macro-Worker] 请求 Hit() EventIndex={i}");
                        }
                        else if (ev.ReleaseOnly)
                        {
                            if (enableTechnique)
                            {
                                byte keyToRelease = ev.IsHoldRelated
                                    ? (ev.ReleaseKeyCode != 0 ? ev.ReleaseKeyCode : _holdKey)
                                    : ev.ReleaseKeyCode;
                                SendKey(keyToRelease, false);
                                if (ev.IsHoldRelated) { _holdKey = 0; _isHoldDown = false; }
                                LogVerbose($"[Macro-Worker] 直接释放 key=0x{keyToRelease:X2} EventIndex={i} audioNow={audioNow:F6}");
                            }
                            else
                            {
                                if (ev.IsHoldRelated) WorkerReleaseHoldKey();
                                else WorkerReleaseKey(ev.ReleaseKeyCode);
                                LogVerbose($"[Macro-Worker] 松键(hold={ev.IsHoldRelated} key=0x{ev.ReleaseKeyCode:X2}) EventIndex={i}");
                            }
                        }
                        else if (ev.IsHoldRelated)
                        {
                            if (enableTechnique)
                            {
                                if (_isHoldDown) { SendKey(_holdKey, false); _holdKey = 0; _isHoldDown = false; }
                                SendKey(ev.KeyCode, true);
                                _holdKey = ev.KeyCode; _isHoldDown = true;
                                LogVerbose($"[Macro-Worker] 直接长按 0x{ev.KeyCode:X2} EventIndex={i} audioNow={audioNow:F6}");
                            }
                            else
                            {
                                WorkerHoldKey(ev.KeyCode);
                                LogVerbose($"[Macro-Worker] Hold 按下 0x{ev.KeyCode:X2} EventIndex={i}");
                            }
                        }
                        else
                        {
                            if (enableTechnique)
                            {
                                SendKey(ev.KeyCode, true);
                                LogVerbose($"[Macro-Worker] 直接按下 0x{ev.KeyCode:X2} EventIndex={i} audioNow={audioNow:F6}");
                            }
                            else
                            {
                                WorkerPressKey(ev.KeyCode);
                                LogVerbose($"[Macro-Worker] 按下 0x{ev.KeyCode:X2} EventIndex={i}");
                            }
                        }

                        localLastFloor = i++;
                        triggered = true;

                        var fresh = Volatile.Read(ref _currentAnchor);
                        if (!ReferenceEquals(fresh, anchor) && fresh.valid
                            && Volatile.Read(ref _resetVersion) == localResetVer)
                        {
                            anchor = fresh;
                            events = anchor.hitEvents!;
                            evCount = anchor.hitEventCount;
                            qpcSnapshot = anchor.qpcSnapshot;
                            rate = anchor.rate;
                            songPosRef = anchor.songPosRef;
                            timeOffset = anchor.timeOffset;
                            simulateKey = anchor.simulateKeyPress;
#if DEBUG
                            Log($"[Macro-Worker] EXTRACT (refresh) rate={rate:F4} songPosRef={songPosRef:F6} qpc={qpcSnapshot}");
#endif
                        }
                    }

                    if (localLastFloor >= evCount - 1)
                    {
                        if (_isKeyDown) WorkerReleaseKey();
                        if (_isHoldDown) WorkerReleaseHoldKey();
                    }

                WriteBack:
                    if (triggered && Volatile.Read(ref _resetVersion) == localResetVer)
                        Volatile.Write(ref _workerLastTriggeredFloor, localLastFloor);
                    if (hitCount > 0)
                        Interlocked.Add(ref _workerNeedsHit, hitCount);
                    FlushFireStats();
                }
            }
            finally
            {
                timeEndPeriod(1);
                if (Main.Settings.EnableTechniqueSimulation)
                { if (_isHoldDown) SendKey(_holdKey, false); }
                else
                { WorkerReleaseKey(); WorkerReleaseHoldKey(); }
                Log("[Macro-Worker] 工作线程退出");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  按键操作
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerPressKey(byte keyCode)
        {
            if (_isKeyDown && _pendingKey != keyCode) WorkerReleaseKey();
            if (_isHoldDown && _holdKey == keyCode) return;
            if (!_isKeyDown) { SendKey(keyCode, isDown: true); _pendingKey = keyCode; _isKeyDown = true; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerHoldKey(byte keyCode)
        {
            if (_isHoldDown) WorkerReleaseHoldKey();
            SendKey(keyCode, isDown: true);
            _holdKey = keyCode; _isHoldDown = true;
            LogVerbose($"[Macro-Worker] Hold 按下 0x{keyCode:X2}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerReleaseHoldKey()
        {
            if (!_isHoldDown) return;
            SendKey(_holdKey, isDown: false);
            _holdKey = 0; _isHoldDown = false;
            LogVerbose("[Macro-Worker] Hold 释放");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerReleaseKey(byte targetKey = 0)
        {
            if (!_isKeyDown) return;
            if (targetKey != 0 && _pendingKey != targetKey) return;
            SendKey(_pendingKey, isDown: false);
            _pendingKey = 0; _isKeyDown = false;
        }

        private static bool IsGameWindowFocused()
        {
            if (_gameWindowHandle == IntPtr.Zero)
            {
                try { _gameWindowHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
                catch { _gameWindowHandle = (IntPtr)(-1); }
            }
            if (_gameWindowHandle == (IntPtr)(-1)) return true;
            return GetForegroundWindow() == _gameWindowHandle;
        }

        private static bool _keyPathProbeDone;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SendKey(byte keyCode, bool isDown)
        {
            // 一次性路径探针：定位镜像链路断点（每次会话首键输出）
            // 注意用 UMM Logger 直调——Macro.Log 是 [Conditional("DEBUG")]，Release 会整体剥除
            if (!_keyPathProbeDone)
            {
                _keyPathProbeDone = true;
                Main.Mod?.Logger.Log($"[Macro-KeyPath] active={VirtualAsyncInput.Active} mirror={Main.Settings.MirrorVirtualKeys} " +
                    $"skyHook={_cachedSkyHookMode} blockUF={Main.Settings.BlockInputWhenUnfocused} " +
                    $"focus={IsGameWindowFocused()} key=0x{keyCode:X2} down={isDown}");
            }

            if (Main.Settings.BlockInputWhenUnfocused && !IsGameWindowFocused()) return;

            // 虚拟异步键盘：合成事件直喂游戏 keyQueue（零注入抖动，详见 VirtualAsyncInput）
            if (VirtualAsyncInput.Active && VirtualAsyncInput.Send(keyCode, isDown)) return;

            // 直喂未接管 → 这次按键会经系统注入回流到游戏的 HookCallback，
            // 从而落入【按键过滤】的判定范围。若开了过滤，先登记放行配额，
            // 否则白名单模式下宏会把自己的键全部拦掉（详见 VirtualAsyncInput 注释）。
            if (Main.Settings.EnableKeyFilter)
                VirtualAsyncInput.RegisterInjectedKey(keyCode, isDown);

            if (_cachedSkyHookMode)
            {
                int r = AsyncInputManager.DirectPushKey(keyCode, isDown);
                if (r != 0) Log($"[Macro-Worker] PushKeyEvent 失败 result={r} key=0x{keyCode:X2}");
                LogVerbose($"[Macro-Worker] SkyHook direct key=0x{keyCode:X2} down={isDown}");
            }
            else
            {
                _cachedInput.type = INPUT_KEYBOARD;
                _cachedInput.u.ki.wVk = keyCode;
                _cachedInput.u.ki.wScan = scanCodeCache[keyCode];
                _cachedInput.u.ki.dwFlags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                fixed (SkyHookSystem.INPUT* ptr = &_cachedInput)
                    SendInput(1, (IntPtr)ptr, sizeof(SkyHookSystem.INPUT));
                LogVerbose($"[Macro-Worker] SendInput key=0x{keyCode:X2} down={isDown}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  初始化
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Initialize()
        {
            levelMaker = scrLevelMaker.instance;
            if (levelMaker?.listFloors == null || levelMaker.listFloors.Count == 0) return;

            cachedFloors = [.. levelMaker.listFloors];
            floorCount = cachedFloors.Length;
            _initializedLevelPath = SafeLevelPath();
            conductor = scrConductor.instance;

            ParseKeyCodes();
            BuildHitEvents();

            initialized = true;
            _staticAnchorVersion++;

            // 方案6：锚点每帧由判定公式/采样直接给出，无需播种状态
            double startPos = conductor!.songposition_minusi;
            int syncFloor = SyncFloor(startPos);
            Volatile.Write(ref _workerLastTriggeredFloor, syncFloor);
            // 事件表与入场索引都已就绪，允许工作线程取用（与上面两句构成握手）
            Volatile.Write(ref _syncFloorReady, true);

            Log("[Macro-Main] 初始化完成");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildHitEvents()
        {
            if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation)
            {
                BuildTechniqueHitEvents();
                return;
            }

            var floors = cachedFloors!;
            int n = floors.Length;
            bool simulate = Main.Settings.SimulateKeyPress;

            byte[] keys = _keyCodesSnapshot;
            int keyLen = keys.Length;
            int keyIdx = 0;

            // 使用对象池，减少 GC
            _hitEventPoolUsed = 0;
            var pool = _hitEventPool;

            // 事件数上界 = 地板数 - 1。池容量不足时必须退化为动态分配：
            // 旧实现在每次写入前判 `_hitEventPoolUsed < pool.Length`，超出部分被
            // 静默丢弃 —— 事件数超过 65536 的极端长谱后半段会完全不触发。
            var overflow = (n - 1) > pool.Length ? new List<HitEvent>(n) : null;

            for (int i = 0; i < n - 1; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;
                if ((floor.nextfloor != null && floor.nextfloor.auto) || floor.midSpin) continue;

                double t = floors[i + 1]?.entryTime ?? double.MaxValue;

                if (simulate && floor.holdLength > -1 && i + 1 < n)
                {
                    var nf = floors[i + 1];
                    if (nf != null && nf.holdLength == -1)
                    {
                        if (overflow != null) overflow.Add(new HitEvent(t, 0, releaseOnly: true));
                        else pool[_hitEventPoolUsed++] = new HitEvent(t, 0, releaseOnly: true);
                        continue;
                    }
                }

                byte key = keys[keyIdx];
                if (++keyIdx >= keyLen) keyIdx = 0;
                if (overflow != null) overflow.Add(new HitEvent(t, key, releaseOnly: false));
                else pool[_hitEventPoolUsed++] = new HitEvent(t, key, releaseOnly: false);
            }

            if (overflow != null)
            {
                _hitEvents = overflow.ToArray();
                _hitEventCount = _hitEvents.Length;
                Main.Mod?.Logger.Log($"[Macro-Main] 事件数 {_hitEventCount} 超出事件池容量 {pool.Length}，已改用动态分配");
            }
            else if (_hitEventPoolUsed > 0)
            {
                _hitEvents = pool.AsSpan(0, _hitEventPoolUsed).ToArray();
                _hitEventCount = _hitEvents.Length;
            }
            else
            {
                _hitEvents = [];
                _hitEventCount = 0;
            }

            Log($"[Macro-Main] BuildHitEvents 完成，共 {_hitEventCount} 个事件");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SyncFloor(double currentTime)
        {
            if (_hitEvents == null || _hitEvents.Length == 0) return -1;
            int left = 0, right = _hitEvents.Length - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                double t = _hitEvents[mid].TriggerTime;
                if (t < currentTime) left = mid + 1;
                else if (t > currentTime) right = mid - 1;
                else return mid;
            }
            return left - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize() =>
            levelMaker?.listFloors.Count != floorCount ||
            !string.Equals(SafeLevelPath(), _initializedLevelPath, StringComparison.Ordinal);

        /// <summary>
        /// 安全读取当前关卡路径。
        /// ADOBase.levelPath 的实际实现是 `scnGame.instance.levelPath`（ldsfld + ldfld），
        /// 当 scnGame 实例不存在时（编辑器试玩、非 scnGame 场景）会对 null 取字段并抛
        /// NullReferenceException —— 作者在 LevelTechniqueManager 里正是用
        /// catch (NullReferenceException) 兜住它的。
        /// 而 Initialize/NeedReinitialize 都在 Harmony prefix 里（NeedReinitialize 每帧调用），
        /// 异常会直接打断游戏的 scrController.PlayerControl_Update，因此这里必须自己兜住。
        /// </summary>
        private static string? SafeLevelPath()
        {
            try { return ADOBase.levelPath; }
            catch { return null; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && _keyCodesSnapshot.Length > 0) return;

            lastKeysSetting = keysSetting;
            var newList = new List<byte>(4);
            foreach (string part in keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                // KeyMap.TryParse 统一处理 单字符 / 键名 / 十六进制 0xNN
                if (KeyMap.TryParse(part, out byte code)) newList.Add(code);
            }
            if (newList.Count == 0) newList.Add(0x4A);
            var newArray = newList.ToArray();
            System.Threading.Interlocked.Exchange(ref _keyCodesSnapshot, newArray);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyHoldBehavior(scrController controller)
        {
            if (controller == null || !Main.Settings.Macro) return;
            bool simulate = Main.Settings.SimulateKeyPress;
            controller.requireHolding = simulate && Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
        }

        /// <summary>
        /// 把 requireHolding 还给游戏自身语义。
        /// 游戏在 scrController.SetupImportantVariables（进关）与 UpdateSetting（改设置）
        /// 里都写入 `Persistence.holdBehavior &lt; HoldBehavior.NoHoldNeeded`；
        /// 而 ApplyHoldBehavior 在"直接判定"模式下会把它强制为 false。
        /// 旧实现关闭宏时从不还原 —— 后果是长按地板在整个关卡里一直保持
        /// "不需要按住"，与玩家设置的长按判定不符，直到下次进关或改设置。
        /// </summary>
        internal static void RestoreHoldBehavior()
        {
            var controller = ADOBase.controller;
            if (controller == null) return;
            try
            {
                bool gameValue = Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
                if (controller.requireHolding != gameValue) controller.requireHolding = gameValue;
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════
        //  手法模拟：解析全局配置
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseTechniqueConfig()
        {
            // 优先使用关卡特定配置（不会覆盖 Settings 中的用户设置）
            var levelConfig = LevelTechniqueManager.GetCurrentLevelConfig();
            if (levelConfig != null)
            {
                _techLeftKeys = ParseTechKeyList(levelConfig.leftHandKeys);
                _techRightKeys = ParseTechKeyList(levelConfig.rightHandKeys);
                _techKeyOrders[0] = ParseTechOrders(levelConfig.leftHandOrders, _techLeftKeys.Length);
                _techKeyOrders[1] = ParseTechOrders(levelConfig.rightHandOrders, _techRightKeys.Length);
                _techPressDur[0] = ParseTechPressTimes(levelConfig.leftHandPressTimes, _techLeftKeys.Length);
                _techPressDur[1] = ParseTechPressTimes(levelConfig.rightHandPressTimes, _techRightKeys.Length);
                _currentSegments = levelConfig.techniqueSegments ?? new List<Settings.TechniqueSegment>();
                return;
            }

            var s = Main.Settings;
            _techLeftKeys = ParseTechKeyList(s.TechLeftHandKeys);
            _techRightKeys = ParseTechKeyList(s.TechRightHandKeys);
            _techKeyOrders[0] = ParseTechOrders(s.TechLeftHandOrders, _techLeftKeys.Length);
            _techKeyOrders[1] = ParseTechOrders(s.TechRightHandOrders, _techRightKeys.Length);
            _techPressDur[0] = ParseTechPressTimes(s.TechLeftHandPressTimes, _techLeftKeys.Length);
            _techPressDur[1] = ParseTechPressTimes(s.TechRightHandPressTimes, _techRightKeys.Length);

            var profiles = s.TechniqueProfiles;
            if (profiles != null && profiles.Count > 0 &&
                s.SelectedTechniqueProfileIndex >= 0 && s.SelectedTechniqueProfileIndex < profiles.Count)
                _currentSegments = profiles[s.SelectedTechniqueProfileIndex].techniqueSegments;
            else
                _currentSegments = new List<Settings.TechniqueSegment>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int[][] ParseTechOrders(string? input, int keyCount)
        {
            int slots = keyCount;
            var result = new int[slots][];
            for (int n = 0; n < slots; n++) { result[n] = new int[n + 1]; for (int i = 0; i <= n; i++) result[n][i] = i % keyCount; }
            if (string.IsNullOrWhiteSpace(input)) return result;

            string[] groups = input!.Split('|');
            for (int n = 0; n < slots; n++)
            {
                string group = n < groups.Length ? groups[n] : groups[groups.Length - 1];
                var indices = new List<int>();
                foreach (var p in group.Split([','], StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(p.Trim(), out int idx))
                        indices.Add(Math.Max(0, Math.Min(idx - 1, keyCount - 1)));
                if (indices.Count > 0) result[n] = [.. indices];
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double[] ParseTechPressTimes(string? input, int keyCount)
        {
            var result = new double[keyCount];
            for (int i = 0; i < result.Length; i++) result[i] = 0.8;
            if (string.IsNullOrWhiteSpace(input)) return result;
            var parts = input!.Split([','], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(parts.Length, result.Length); i++)
                if (double.TryParse(parts[i].Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    result[i] = v;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetAdviceBpm(double limit)
        {
            // 防御死循环：limit ≤ 0 时下面第二个折叠循环 `bpm <= limit/2` 恒真
            // （bpm 被反复乘 2 仍是 0）；speed 缺失（controller 未就绪）时 bpm 折叠到 0
            // 同样死循环。关卡配置是磁盘上的 JSON，bpmLimit 可能被手工改成 0 或负数。
            if (limit <= 0.0) limit = 500.0;
            double speed = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 0.0;
            double bpm = (double)(conductor!.bpm * speed);
            if (bpm <= 0.0) return limit;
            while (bpm > limit) bpm /= 2.0;
            while (bpm <= limit / 2.0) bpm *= 2.0;
            return bpm;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetAdviceBpm() => GetAdviceBpm(Main.Settings.TechniqueBpmLimit);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetSegmentBpmLimit(int floorIdx)
        {
            if (_currentSegments != null)
                foreach (var seg in _currentSegments)
                    if (floorIdx >= seg.startFloor && floorIdx <= seg.endFloor)
                        return seg.bpmLimit;
            return Main.Settings.TechniqueBpmLimit;
        }

        // ─────────────────────────────────────────────
        //  手法模拟：分段有效配置（C# 调试路径用）
        // ─────────────────────────────────────────────
        private readonly struct EffectiveTechConfig
        {
            public readonly byte[] LeftKeys;
            public readonly byte[] RightKeys;
            public readonly int[][] LeftOrders;
            public readonly int[][] RightOrders;
            public readonly double[] LeftPressTimes;
            public readonly double[] RightPressTimes;

            public EffectiveTechConfig(byte[] lk, byte[] rk,
                int[][] lo, int[][] ro, double[] lp, double[] rp)
            {
                LeftKeys = lk; RightKeys = rk;
                LeftOrders = lo; RightOrders = ro;
                LeftPressTimes = lp; RightPressTimes = rp;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EffectiveTechConfig GetEffectiveConfig(int floorIdx)
        {
            if (_currentSegments != null)
            {
                foreach (var seg in _currentSegments)
                {
                    if (floorIdx < seg.startFloor || floorIdx > seg.endFloor) continue;
                    if (!seg.HasKeyOverride) break; // BPM only — keys fall through to global

                    byte[] lk = string.IsNullOrWhiteSpace(seg.leftHandKeys)
                        ? _techLeftKeys
                        : ParseTechKeyList(seg.leftHandKeys);
                    byte[] rk = string.IsNullOrWhiteSpace(seg.rightHandKeys)
                        ? _techRightKeys
                        : ParseTechKeyList(seg.rightHandKeys);

                    int[][] lo = string.IsNullOrWhiteSpace(seg.leftHandOrders)
                        ? _techKeyOrders[0]
                        : ParseTechOrders(seg.leftHandOrders, lk.Length);
                    int[][] ro = string.IsNullOrWhiteSpace(seg.rightHandOrders)
                        ? _techKeyOrders[1]
                        : ParseTechOrders(seg.rightHandOrders, rk.Length);

                    double[] lp = string.IsNullOrWhiteSpace(seg.leftHandPressTimes)
                        ? _techPressDur[0]
                        : ParseTechPressTimes(seg.leftHandPressTimes, lk.Length);
                    double[] rp = string.IsNullOrWhiteSpace(seg.rightHandPressTimes)
                        ? _techPressDur[1]
                        : ParseTechPressTimes(seg.rightHandPressTimes, rk.Length);

                    return new EffectiveTechConfig(lk, rk, lo, ro, lp, rp);
                }
            }
            return new EffectiveTechConfig(
                _techLeftKeys, _techRightKeys,
                _techKeyOrders[0], _techKeyOrders[1],
                _techPressDur[0], _techPressDur[1]);
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
                Main.Mod?.Logger.Log($"[Macro-Tech] 手法表: 按下={presses} 换手={turns} 单音碎块={frag} 最长连击={longest} " +
                    $"左键={leftUsed} 右键={rightUsed} | 速度倍率 {smin:F2}~{smax:F2} | 拍号BPM={conductor?.bpm:F1}");
            }
            catch (Exception ex) { Main.Mod?.Logger.Log($"[Macro-Tech] 摘要失败: {ex.Message}"); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildTechniqueHitEvents()        {
            ParseTechniqueConfig();

            var floors = cachedFloors!;
            bool sim = Main.Settings.SimulateKeyPress;

            _evTimeRecycle.Clear();
            _evPressRecycle.Clear();
            _evFloorRecycle.Clear();
            _evSpeedRecycle.Clear();

            var evTime = _evTimeRecycle;
            var evPress = _evPressRecycle;
            var evFloor = _evFloorRecycle;
            var evSpeed = _evSpeedRecycle;

            for (int i = 0; i < floors.Length - 1; i++)
            {
                var fl = floors[i];
                if (fl == null) continue;
                if ((fl.nextfloor?.auto ?? false) || fl.midSpin) continue;

                var nf = floors[i + 1];
                double t = nf?.entryTime ?? double.MaxValue;

                if (sim && fl.holdLength > -1 && nf != null && nf.holdLength == -1)
                {
                    evTime.Add(t); evPress.Add(-1); evFloor.Add(i); evSpeed.Add(fl.speed);
                    continue;
                }

                bool isHoldHead = sim && nf != null && nf.holdLength > -1;
                evTime.Add(t);
                evPress.Add(isHoldHead ? 2 : 1);
                evFloor.Add(i);
                evSpeed.Add(fl.speed);
            }

            int total = evTime.Count;
            if (total == 0) { _hitEvents = []; _hitEventCount = 0; return; }

#if DEBUG
            bool useCppVersion = Main.Settings.UseCppTechniqueInDebug;
#else
            bool useCppVersion = true;
#endif
            if (useCppVersion)
            {
                try
                {
                    var levelConfig = LevelTechniqueManager.GetCurrentLevelConfig();
                    Settings.TechniqueSegment[] segments;
                    int handPref;
                    if (levelConfig != null)
                    {
                        segments = levelConfig.techniqueSegments?.ToArray() ?? [];
                        handPref = levelConfig.handPreference;
                    }
                    else
                    {
                        var currentProfile = Main.Settings.CurrentTechniqueProfile;
                        segments = currentProfile?.techniqueSegments?.ToArray() ?? [];
                        handPref = Main.Settings.TechniqueHandPreference;
                    }

                    double speedChangeTolerance = levelConfig?.speedChangeTolerance
                        ?? Main.Settings.SpeedChangeTolerance;
                    TechniqueSimulator.UpdateConfig(
                        _techLeftKeys, _techRightKeys,
                        _techKeyOrders[0], _techKeyOrders[1],
                        _techPressDur[0], _techPressDur[1],
                        Main.Settings.TechniqueBpmLimit,
                        handPref,
                        speedChangeTolerance,
                        segments);

                    if (TechniqueSimulator.BuildHitEvents(
                            [.. evTime], [.. evPress], [.. evFloor], [.. evSpeed],
                            total,
                            conductor!.bpm, ADOBase.controller.playerOne.planetarySystem.speed,
                            out var nativeEvents))
                    {
                        _hitEvents = nativeEvents;
                        _hitEventCount = nativeEvents!.Length;
                        Log($"[Macro-Main] C++ 手法模拟（原生分段）完成：{_hitEventCount} 事件");
                        LogTechniqueTableSummary(nativeEvents!, evSpeed);
                        return;
                    }
                }
                catch (Exception ex) { Log($"[Macro-Main] C++ 手法模拟异常: {ex.Message}，回退 C# 版本"); }
            }

#if DEBUG
            // Debug 构建保留 C# 复刻实现作为回退
            BuildCSHarpTechniqueHitEvents();
#else
            // Release 没有 C# 回退：原生路径失败时必须显式清空事件表。
            // 否则 _hitEvents 仍是上一张谱面的数组，而 Initialize() 依旧会置
            // initialized=true 并发布锚点 → 工作线程按旧谱时间戳乱按键。
            _hitEvents = [];
            _hitEventCount = 0;
            Main.Mod?.Logger.Log("[Macro-Main] C++ 手法模拟未产出事件，事件表已清空（Release 无 C# 回退）");
#endif
        }

#if DEBUG
        // ═══════════════════════════════════════════════════════════════
        //  C# 回退路径（Release 模式下也作为 DLL 加载失败的备份）
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildCSHarpTechniqueHitEvents()
        {
            ParseTechniqueConfig();

            var  floors  = cachedFloors!;
            bool sim     = Main.Settings.SimulateKeyPress;

            // 使用对象池，避免分配
            _evTimeRecycle.Clear();
            _evPressRecycle.Clear();
            _evFloorRecycle.Clear();

            for (int i = 0; i < floors.Length - 1; i++)
            {
                var fl = floors[i];
                if (fl == null) continue;
                if ((fl.nextfloor?.auto ?? false) || fl.midSpin) continue;

                var    nf = floors[i + 1];
                double t  = nf?.entryTime ?? double.MaxValue;

                if (sim && fl.holdLength > -1 && nf != null && nf.holdLength == -1)
                {
                    _evTimeRecycle.Add(t); _evPressRecycle.Add(-1); _evFloorRecycle.Add(i);
                    continue;
                }

                bool isHoldHead = sim && nf != null && nf.holdLength > -1;
                _evTimeRecycle.Add(t);
                _evPressRecycle.Add(isHoldHead ? 2 : 1);
                _evFloorRecycle.Add(i);
            }

            int total = _evTimeRecycle.Count;
            if (total == 0) { _hitEvents = []; _hitEventCount = 0; return; }

            _piecesRecycle.Clear();
            BuildPieces(_evTimeRecycle, _evPressRecycle, _evFloorRecycle, total, _piecesRecycle);

            if (_piecesRecycle.Count > 0)
            {
                var lp = _piecesRecycle[_piecesRecycle.Count - 1];
                _piecesRecycle.Add(new PieceInfo(0, 1 - lp.Hand, lp.PieceLen,
                                         lp.EndTime, lp.EndTime + lp.PieceLen, total));
            }

            var output = GenerateHitEventsFromPieces(_evTimeRecycle, _evPressRecycle, _evFloorRecycle, _piecesRecycle, sim);
            FixSameKeyOverlaps(output);

            // 使用对象池
            if (output.Count > _hitEventPool.Length)
            {
                _hitEvents = output.ToArray(); // Fallback for overflow
            }
            else
            {
                for (int i = 0; i < output.Count; i++)
                    _hitEventPool[i] = output[i];
                _hitEvents = _hitEventPool.AsSpan(0, output.Count).ToArray();
            }

            _hitEventCount = _hitEvents.Length;
            Log($"[Macro-Main] C# 手法模拟完成：{_hitEventCount} 事件，{_piecesRecycle.Count} 时间片");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindSegmentIndex(int floorIdx)
        {
            if (_currentSegments == null) return -1;
            for (int i = 0; i < _currentSegments.Count; i++)
            {
                var seg = _currentSegments[i];
                if (floorIdx >= seg.startFloor && floorIdx <= seg.endFloor)
                    return i;
            }
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            int total, List<PieceInfo> pieces)
        {
            double nowT    = 0.0;
            int    nowD    = 0;
            var    _levelTechHandPref = LevelTechniqueManager.GetCurrentLevelConfig()?.handPreference ?? Main.Settings.TechniqueHandPreference;
            int    cHand   = (_levelTechHandPref == 0) ? -1 : 1;
            int    mult    = 0;

            var mCnt    = new long[16];
            var mCntPre = new long[16];
            int  canMulti  = 0;
            bool needBack  = false;

            float  lastSegLimit = GetSegmentBpmLimit(evFloor[0]);
            double nowBpm       = GetAdviceBpm(lastSegLimit);
            int    lastSegIdx   = FindSegmentIndex(evFloor[0]);

            while (nowD < total)
            {
                int   curFloorIdx = evFloor[nowD];
                int   curSegIdx   = FindSegmentIndex(curFloorIdx);
                float curSegLimit = GetSegmentBpmLimit(curFloorIdx);

                if (curSegIdx != lastSegIdx)
                {
                    cHand   = (_levelTechHandPref == 0) ? -1 : 1;
                    mult    = 0;
                    Array.Clear(mCnt,    0, mCnt.Length);
                    Array.Clear(mCntPre, 0, mCntPre.Length);
                    canMulti  = 0;
                    needBack  = false;
                    lastSegLimit = curSegLimit;
                    nowBpm       = GetAdviceBpm(curSegLimit);
                    lastSegIdx   = curSegIdx;
                }

                if (pieces.Count > total * 64) break;

                double pLen = 60.0 / (nowBpm * Math.Pow(2, mult)) / 2.0;
                if (pLen < 1e-9) pLen = 1e-9;

                int cnt   = CountEventsInRange(evTime, nowD, nowT + pLen * 0.995);
                int csH   = (cHand == 1) ? 1 : 0;

                // 使用分段有效配置来确定当前手的最大按键数
                var   ec   = GetEffectiveConfig(curFloorIdx);
                int   maxK = (csH == 0) ? ec.LeftKeys.Length : ec.RightKeys.Length;

                int  mainHand  = (_levelTechHandPref == 0) ? -1 : 1;
                bool isOffHand = (cHand != mainHand);

                if (cnt > maxK)
                {
                    if (canMulti == 1 && isOffHand) needBack = true;
                    if (mult < 7) { mult++; mCnt[mult] = 0; continue; }
                    else           cnt = maxK;
                }

                if (needBack && pieces.Count > 0)
                {
                    needBack = false;
                    cHand    = mainHand;
                    var prev = pieces[pieces.Count - 1];
                    nowT = prev.StartTime;
                    nowD = prev.EvStart;
                    Array.Copy(mCntPre, mCnt, 16);
                    mult = prev.Multiplier + 1;
                    if (mult > 7) mult = 7;
                    pieces.RemoveAt(pieces.Count - 1);
                    canMulti = 0;
                    continue;
                }

                // ── 自适应时间片延伸（仅在下一片更稀疏时合并）────
                float speedChangeTolerance = LevelTechniqueManager.GetCurrentLevelConfig()?.speedChangeTolerance
                    ?? Main.Settings.SpeedChangeTolerance;
                if (speedChangeTolerance > 0f && cnt > 0 && nowD + cnt < total)
                {
                    double nextEvTime = evTime[nowD + cnt];
                    double diff = nextEvTime - (nowT + pLen);
                    if (diff > pLen * 0.001 && diff < pLen * speedChangeTolerance)
                    {
                        int nextCnt = CountEventsInRange(evTime, nowD + cnt, (nowT + pLen) + pLen * 0.995);
                        if (nextCnt < cnt)
                        {
                            pLen = nextEvTime - nowT;
                        }
                    }
                }

                Array.Copy(mCnt, mCntPre, 16);
                pieces.Add(new PieceInfo(cnt, csH, pLen, nowT, nowT + pLen, nowD, mult));

                for (int c = mult; c > 0; c--)
                {
                    mCnt[c] += (long)Math.Pow(2, 16 - (mult - c));
                    mCnt[c] %= (1L << 18);
                }
                while (mult > 0 && mCnt[mult] == 0) mult--;

                nowD += cnt;
                nowT += pLen;
                cHand = -cHand;
                canMulti = 1;

                if (nowD < total && Math.Abs(evTime[nowD] - nowT) < pLen * 0.01)
                    nowT = evTime[nowD];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<HitEvent> GenerateHitEventsFromPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            List<PieceInfo> pieces, bool sim)
        {
            int total  = evTime.Count;
            var output = new List<HitEvent>(total * 2);

            bool          activeHold    = false;
            byte          activeHoldKey = 0;
            int           lastSegIdxEvent = -2;

            for (int pcnt = 0; pcnt < pieces.Count - 1; pcnt++)
            {
                var    cur    = pieces[pcnt];
                var    next   = pieces[pcnt + 1];
                double pStart = (pcnt > 0) ? pieces[pcnt - 1].EndTime : 0.0;

                for (int i = 0; i < cur.EvCount; i++)
                {
                    int    idx   = cur.EvStart + i;
                    int    press = evPress[idx];
                    double t     = evTime[idx];

                    if (press == -1)
                    {
                        if (activeHold)
                        {
                            output.Add(new HitEvent(t, 0, releaseOnly: true,
                                isHoldRelated: true, releaseKeyCode: activeHoldKey));
                            activeHold = false; activeHoldKey = 0;
                        }
                        continue;
                    }

                    // 按当前事件的地板索引解析有效键位
                    int curFloor = (idx < evFloor.Count) ? evFloor[idx] : evFloor[evFloor.Count - 1];

                    // 段边界：释放活跃 hold 键
                    int curSegIdx = FindSegmentIndex(curFloor);
                    if (curSegIdx != lastSegIdxEvent)
                    {
                        if (activeHold && lastSegIdxEvent != -2)
                        {
                            output.Add(new HitEvent(t - 0.000001, 0, releaseOnly: true,
                                isHoldRelated: true, releaseKeyCode: activeHoldKey));
                            activeHold = false;
                            activeHoldKey = 0;
                        }
                        lastSegIdxEvent = curSegIdx;
                    }

                    var ec = GetEffectiveConfig(curFloor);

                    byte[]   hK = (cur.Hand == 0) ? ec.LeftKeys        : ec.RightKeys;
                    int[][]  hO = (cur.Hand == 0) ? ec.LeftOrders       : ec.RightOrders;
                    double[] hT = (cur.Hand == 0) ? ec.LeftPressTimes   : ec.RightPressTimes;

                    int oi = Math.Min(cur.EvCount - 1, hK.Length - 1);
                    int ki = (i < hO[oi].Length) ? hO[oi][i] : (i % hK.Length);
                    ki = Mathf.Clamp(ki, 0, hK.Length - 1);

                    byte   kc          = hK[ki];
                    double ratio       = (ki < hT.Length) ? hT[ki] : 0.8;
                    bool   isHoldHead  = (press == 2);

                    if (isHoldHead && activeHold)
                    {
                        output.Add(new HitEvent(t - 0.000001, 0, releaseOnly: true,
                            isHoldRelated: true, releaseKeyCode: activeHoldKey));
                        activeHold = false; activeHoldKey = 0;
                    }

                    output.Add(new HitEvent(t, kc, false, isHoldHead));

                    if (isHoldHead) { activeHold = true; activeHoldKey = kc; }
                    if (!sim || isHoldHead) continue;

                    // 计算松键时间
                    double dur;
                    if (next.PieceLen > cur.PieceLen + 5e-6)
                    {
                        dur = (pStart + cur.PieceLen > cur.EndTime + 5e-6)
                            ? (next.EndTime - t) * ratio / 2.0
                            : (pStart + cur.PieceLen * 2.0 - t) * ratio / 2.0;
                    }
                    else
                    {
                        dur = (pStart + cur.PieceLen + 5e-6 < cur.EndTime)
                            ? (pStart + cur.PieceLen + next.PieceLen - t) * ratio / 2.0
                            : (next.EndTime - t) * ratio / 2.0;
                    }

                    double rel = t + dur;

                    if (next.Hand != cur.Hand || next.EvCount == 0)
                        { if (rel >= next.EndTime) rel = next.EndTime - 1e-6; }
                    else
                        { if (rel >= cur.EndTime)  rel = cur.EndTime  - 1e-6; }

                    if (rel <= t) rel = t + (next.EndTime - t) * 0.4;

                    output.Add(new HitEvent(rel, 0, true, false, releaseKeyCode: kc));
                }
            }

            if (activeHold && pieces.Count > 0)
            {
                double lastTime = pieces[pieces.Count - 1].EndTime;
                output.Add(new HitEvent(lastTime, 0, releaseOnly: true,
                    isHoldRelated: true, releaseKeyCode: activeHoldKey));
            }

            return output;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FixSameKeyOverlaps(List<HitEvent> events)
        {
            events.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));

            int n = events.Count;
            var pending = new Dictionary<byte, int>(8);

            for (int i = 0; i < n; i++)
            {
                var ev = events[i];

                if (ev.ReleaseOnly)
                {
                    if (ev.ReleaseKeyCode != 0) pending.Remove(ev.ReleaseKeyCode);
                    continue;
                }

                byte kc = ev.KeyCode;
                if (kc == 0) continue;

                if (pending.TryGetValue(kc, out int relIdx))
                {
                    var relEv = events[relIdx];
                    if (relEv.TriggerTime >= ev.TriggerTime)
                    {
                        events[relIdx] = new HitEvent(ev.TriggerTime - 1e-6,
                            relEv.KeyCode, releaseOnly: true,
                            isHoldRelated: relEv.IsHoldRelated,
                            releaseKeyCode: relEv.ReleaseKeyCode);
                    }
                    pending.Remove(kc);
                }

                for (int j = i + 1; j < n; j++)
                {
                    var fwd = events[j];
                    if (fwd.ReleaseOnly && fwd.ReleaseKeyCode == kc && !fwd.IsHoldRelated)
                    {
                        pending[kc] = j;
                        break;
                    }
                }
            }

            events.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountEventsInRange(List<double> times, int start, double endTime)
        {
            if (start >= times.Count) return 0;
            int left = start, right = times.Count - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (times[mid] < endTime) left  = mid + 1;
                else                      right = mid - 1;
            }
            return left - start;
        }
#endif

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

            if (skyHookInitialized) AsyncInputManager.ClearQueue();
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
                AsyncInputManager.Stop();
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
                AsyncInputManager.Start();
                if (!AsyncInputManager.IsInitialized)
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
                AsyncInputManager.Stop();
                skyHookInitialized = false;
                Main.Settings.SkyHookMode = false;
            }
        }

        // ─────────────────────────────────────────────
        //  计时器（委托优化：消除分支）
        // ─────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetRawTicks() => _getTicksImpl();

        // 两个时间戳之间的真实流逝秒（按当前时间源的刻度换算）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ElapsedSec(long from, long to)
            => (double)(to - from) * (_cachedHighPrecision ? 1e-7 : perfFreqInv);

        // 切换时间源委托（根据 HighPrecision 设置）
        private static void UpdateTicksDelegate()
        {
            _getTicksImpl = _cachedHighPrecision ? _getTicksHigh : _getTicksNormal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long c)) return c;
            return DateTime.UtcNow.Ticks;
        }

        // ─────────────────────────────────────────────
        //  输入调整
        // ─────────────────────────────────────────────

        // ─────────────────────────────────────────────
        //  日志
        // ─────────────────────────────────────────────
        // ⚠️ 这里不能加 [Conditional("DEBUG")]：那会让 Release 构建把所有调用点
        // 整个删掉——此前"手法表诊断在 Player.log 里看不到"就是这个原因
        // （叠加 logToMod=false 的空实现，双保险地什么都打不出来）。
        public static void Log(string message)
        {
            Main.Mod?.Logger.Log(message);
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
            if (_verboseLog) Main.Mod?.Logger.Log(message);
        }
    }

    #endregion
#pragma warning restore CS0420
}
