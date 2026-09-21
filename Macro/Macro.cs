using System;
using System.Collections.Generic;
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

        // ─────────────────────────────────────────────
        //  预分配对象池（减少 GC 压力）
        // ─────────────────────────────────────────────
        private static readonly HitEvent[] _hitEventPool = new HitEvent[65536];
        private static int _hitEventPoolUsed;

        // 复用缓冲区
        private static readonly List<double> _evTimeRecycle = new(4096);
        private static readonly List<int> _evPressRecycle = new(4096);
        private static readonly List<int> _evFloorRecycle = new(4096);
        private static readonly List<double> _evSpeedRecycle = new(4096);
        private static readonly List<PieceInfo> _piecesRecycle = new(1024);

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

        private static int _staticAnchorVersion = 0;

        // 方案9：minusi 兜底低通状态（主线程；公式基线接管时清除）
        private static double _songPosSm;
        private static long _smRefTick;
        private static double _smPitch;
        private static bool _slewSeeded;
        private static bool _formulaEngaged;
        private static bool _formulaErrorLogged;

        private static readonly double perfFreqInv;

        // 公式基线与采样位置的最大允许偏差（判定公式接管门限，秒）
        private const double kFormulaAlignToleranceSec = 0.03;
        // 分片死循环保护：片数上限 = 事件数 × 该倍数
        private const int kMaxPiecesPerEvent = 64;

        private static volatile int _workerLastTriggeredFloor = -1;
        private static volatile int _workerNeedsHit = 0;
        private static volatile int _resetVersion = 0;
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
            {
                WaitForSingleObject(_hWaitTimer, unchecked((uint)-1)); // INFINITE
                return;
            }
            // 定时器设置失败：退回粗粒度 Sleep（宁可早醒进自旋，不可空转烧核）
            int msFallback = (int)Math.Ceiling(seconds * 1000.0) - 1;
            Thread.Sleep(msFallback < 1 ? 1 : msFallback);
        }

        // 工作线程确认退出后释放可等待定时器句柄。
        // 句柄仅工作线程使用，因此必须在 IsAlive == false 时关闭，避免释放后使用；
        // 若线程仍在运行（Join 超时）则保留句柄，下次线程退出时复用，不重复创建。
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
                var name = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') { result.Add((byte)name[0]); continue; }
                if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') { result.Add((byte)name[0]); continue; }
                if (KeyMap.TryGetKeyCode(name, out byte code)) result.Add(code);
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

        // 闭环校准参数（原散落的魔法数字，取值不变）
        private const int kDenseSampleCount = 6;        // 密集窗口样本数
        private const int kDenseWindowMs = 100;         // 密集窗口时长
        private const int kSparseSampleCount = 4;       // 稀疏窗口样本数
        private const int kSparseWindowMs = 700;        // 稀疏窗口时长
        private const float kStdDevGateMs = 12f;        // 标准差可信度门控
        private const float kNearZeroDeadbandMs = 3f;   // 近零死区
        private const float kFastStepThresholdMs = 15f; // 快速档阈值
        private const float kFastStepGain = 0.4f;       // 快速档增益
        private const float kFastStepCapMs = 4f;        // 快速档单次步长上限
        private const float kFineStepGain = 0.06f;      // 近零档增益
        private const float kFineStepCapMs = 0.8f;      // 近零档单次步长上限
        private const float kAutoOffsetClampMs = 60f;   // 偏移钳制位
        private const float kErrSampleClampMs = 40f;    // 单样本离群钳制
        // ⚠️ 持久化已回滚（2026-08-16 事故）：学到的偏移依赖本局运行状态
        // （offsetTick 收敛相位决定公式路径是否接管，两种基线所需偏移差 ~45ms），
        // 跨运行灌入会造成系统性错位 → 判定 ±700ms 摆动 → 死亡退局。
        // 学习表只在本局内存内有效，每局重新收敛（主段 ~1s）。

        /// <summary>判定探针回灌（AddHit 后缀调用，主线程）。</summary>
        internal static void RecordJudgedError(float errMs, float spdUsed)
        {
            if (errMs < -kErrSampleClampMs) errMs = -kErrSampleClampMs;
            else if (errMs > kErrSampleClampMs) errMs = kErrSampleClampMs;   // 钳制离群值（死亡/重生/变速瞬态）
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
            bool dense = _judgeErrCount >= kDenseSampleCount && sinceLast >= kDenseWindowMs;
            bool sparse = _judgeErrCount >= kSparseSampleCount && sinceLast >= kSparseWindowMs;
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
            if (std > kStdDevGateMs) return;

            // 双档：公式基线已与判定恒等，残差主体是引擎量化噪声。
            // 近零档（|err|<15ms）：死区 3ms 内静默；动作时步长 ≤0.8ms，
            //   抖动上限低于可感知度——环路自己不再制造"晃动"。
            // 快速档（|err|≥15ms）：真实偏移（如分段相位 −40ms 类），1 秒内吃掉。
            if (Math.Abs(mean) < kNearZeroDeadbandMs) return;

            float step;
            if (Math.Abs(mean) >= kFastStepThresholdMs)
            {
                step = mean * kFastStepGain;
                if (step > kFastStepCapMs) step = kFastStepCapMs; else if (step < -kFastStepCapMs) step = -kFastStepCapMs;
            }
            else
            {
                step = mean * kFineStepGain;
                if (step > kFineStepCapMs) step = kFineStepCapMs; else if (step < -kFineStepCapMs) step = -kFineStepCapMs;
            }
            // ⚠️ r150 判定误差符号：errMs>0 = 早、errMs<0 = 迟。
            // 判定探针复刻 scrHitErrorMeter.AddHit 的 `angleDiff * -57.29578`，
            // 而游戏 scrMisc.SelectHitMarginByTimeBoundary 里正号才是 Late
            // （>+Counted→TooLate），两者相差一个负号——因此探针 err<0 表示迟发。
            // 迟发必须【减小】触发偏移把按键提前，故这里为 `+= step`。
            _autoOffsetMs += step;
            if (_autoOffsetMs > kAutoOffsetClampMs) _autoOffsetMs = kAutoOffsetClampMs;
            else if (_autoOffsetMs < -kAutoOffsetClampMs) _autoOffsetMs = -kAutoOffsetClampMs;

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

            DispatchPendingHits(controller);

            // 方案8：游玩期 GC 抑制（每次进关尝试，失败自动回退）
            TryBeginNoGC();

#if DEBUG
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
#endif
            var (anchorPos, rate, qpcSnap, songPosRaw) = ComputeAnchorPosition();

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
            Log($"[Macro-Main] ANCHOR posRef={anchorPos:F6} rate={rate:F4} qpcSnap={qpcSnap} lastFloor={lastFloor} judgedAligned={anchorPos != songPosRaw}");
            _debugWorkerInitLogged = false;
#endif

            HandleHotkeys();
        }

        /// <summary>派发工作线程累积的直接判定请求（失焦时按设置丢弃）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchPendingHits(scrController controller)
        {
            if (!Main.Settings.BlockInputWhenUnfocused || IsGameWindowFocused())
            {
                int hitCount = Interlocked.Exchange(ref _workerNeedsHit, 0);
                for (int h = 0; h < hitCount; h++) controller.chosenPlanet.player!.Hit(null, false);
            }
            else
            {
                // 失焦且启用失焦拦截：丢弃工作线程累积的直接判定请求，
                // 避免回焦后一次性补发（与 SendKey 的失焦丢弃语义一致）
                Interlocked.Exchange(ref _workerNeedsHit, 0);
            }
        }

        /// <summary>
        /// 计算本帧锚点位置（判定公式基线；失败回退 minusi 有界低通）。
        /// 方案9 注释：判定位置 = ((T − offsetTick)/1e7 − dspTimeSong − cal_i)·pitch − addoffset，
        /// T 与 offsetTick 必须同域（DateTime 域 ticks，见 PreciseNow）；
        /// 公式不可用时退回 minusi 低通（平滑音频缓冲阶梯波）。
        /// </summary>
        private static (double anchorPos, double rate, long qpcSnap, double songPosRaw) ComputeAnchorPosition()
        {
            double pitch = conductor!.song.pitch;
            long qpcSnap = GetRawTicks();
            double currentSongPos = conductor!.songposition_minusi;

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
                    formulaOk = Math.Abs(judgedPos - currentSongPos) <= kFormulaAlignToleranceSec;
                }
            }
            catch (Exception ex)
            {
                // 公式基线失败时回退 minusi 低通；异常只在每局第一次记录，避免刷屏
                if (!_formulaErrorLogged)
                {
                    _formulaErrorLogged = true;
                    Main.Mod?.Logger.Log($"[Macro] 判定公式基线计算异常（已回退 minusi 兜底，仅记录首次）: {ex.Message}");
                }
            }

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

            return (anchorPos, rate, qpcSnap, currentSongPos);
        }

        /// <summary>热键：Ctrl+左右调步长、左右调时间偏移（可在设置关闭）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HandleHotkeys()
        {
            if (!Main.Settings.EnableKeyAdjust && !Main.Settings.EnableArrowTimeAdjust) return;

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

        // ─────────────────────────────────────────────
        //  UI 延迟动作队列
        //  OnGUI 回调中不直接改游戏状态：只置标志，由 ShowText.Update
        //  （主线程每帧）在下一帧统一执行。
        // ─────────────────────────────────────────────
        private static int _pendingRestart;

        /// <summary>请求重开当前关卡（下一帧主线程执行，见 ProcessPendingActions）。</summary>
        internal static void RequestRestart() => Interlocked.Exchange(ref _pendingRestart, 1);

        /// <summary>消费 UI 延迟动作（ShowText.Update 每帧调用，主线程）。</summary>
        internal static void ProcessPendingActions()
        {
            if (Interlocked.Exchange(ref _pendingRestart, 0) == 0) return;
            try
            {
                ADOBase.controller?.Restart();
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[Macro] 延迟重开关卡失败: {ex.Message}");
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
        // 诊断：实际使用的 rate（= song.pitch）与批锚定次数，定位高密度段漂移
        private static double _diagLastRate = -1.0;
        private static int _diagBatchCount;

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
            Main.Mod?.Logger.Log($"[Macro-Diag] 击发 {_fireCount} 次 | 平均迟发 {_fireErrSum / _fireCount * 1000.0:F3}ms | 最大 {_fireErrMax * 1000.0:F3}ms | rate={_diagLastRate:F4} | 批锚定 {_diagBatchCount}");
            _fireErrSum = 0; _fireErrMax = 0; _fireCount = 0; _diagBatchCount = 0;
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

                // 事件时间戳“批锚定”状态（跨 while 迭代保持）：
                // 批首事件发出时的本地 tick + 其理想歌曲时刻。批内其余事件按
                // 各自理想歌曲时间差折算真实秒排到基准上（见事件处理段）。
                long batchBaseTicks = 0;
                double batchBaseTriggerAt = double.NaN;
                const double kBatchWindowSec = 0.05;   // 批窗口 50ms

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
                        localResetVer = curResetVer;
                        localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                        batchBaseTicks = 0; batchBaseTriggerAt = double.NaN;
                        continue;
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
                        // 方案4：audioNow = 锚点位置 + QPC 真实流逝 × pitch（DSP 估计已退出外推链路）
                        double audioNow = songPosRef + elapsed * rate;
                        double triggerAt = events[i].TriggerTime + timeOffset;

#if DEBUG
                        if (i < 5 || Math.Abs(triggerAt - audioNow) > 0.5)
                            Log($"[Macro-Worker] TICK i={i} audioNow={audioNow:F6} triggerAt={triggerAt:F6} diff={triggerAt - audioNow:F6} elapsed={elapsed:F6}");
#endif

                        // 等待到事件时刻（Ready=可派发 / RetrySame=重试同一事件 / DeferNextAnchor=让出本帧）
                        EventWait wait = WaitForEvent(triggerAt, audioNow, rate);
                        if (wait == EventWait.DeferNextAnchor) break;
                        if (wait == EventWait.RetrySame) continue;

                        ref readonly var ev = ref events[i];
                        RecordFireError(audioNow - triggerAt);
                        bool enableTechnique = Main.Settings.EnableTechniqueSimulation;

                        // ── 事件时间戳“批锚定” ────────────────────────
                        // 游戏判定只认事件时间戳（亚帧）。同一时刻/极近的多个事件
                        // （双押、八押）是在本线程逐个 Send 的，每次 Send 要花 0.1~2ms，排在后面的键实际发送时刻已晚于理想，时间戳若取发送瞬间就会被判迟发（八押可累积 5~10ms）。
                        // 这里以“批首事件的真实发送本地时刻”为基准，批内其余事件按各自理想歌曲时间差折算真实秒排上去；基准来自批首的实际发送时刻，不使用 audioNow 的绝对外推，高密度连打段不会因外推累积误差越走越偏。
                        // 批窗口 50ms，超出即重新锚定。
                        long targetTicks;
                        _diagLastRate = rate;
                        if (!double.IsNaN(batchBaseTriggerAt)
                            && triggerAt >= batchBaseTriggerAt
                            && triggerAt - batchBaseTriggerAt <= kBatchWindowSec)
                        {
                            double relSec = (triggerAt - batchBaseTriggerAt)
                                            / ((rate > 1e-9) ? rate : 1.0);
                            targetTicks = batchBaseTicks + (long)(relSec * 10_000_000.0);
                        }
                        else
                        {
                            targetTicks = PreciseNow.LocalTicks();
                            batchBaseTicks = targetTicks;
                            batchBaseTriggerAt = triggerAt;
                            _diagBatchCount++;
                        }

                        hitCount += DispatchEvent(in ev, simulateKey, enableTechnique, targetTicks, i, audioNow);

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

        // 等待结果：Ready=到时刻可派发；RetrySame=重试同一事件；DeferNextAnchor=让出本帧
        private enum EventWait { Ready, RetrySame, DeferNextAnchor }

        /// <summary>
        /// 等待到事件触发时刻。近 future 用高分辨率定时器睡到目标前 1.5ms，
        /// 最后 1.5ms 自旋；远 future 睡眠让出本帧（由外层重取锚点）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EventWait WaitForEvent(double triggerAt, double audioNow, double rate)
        {
            if (triggerAt <= audioNow) return EventWait.Ready;
            if (rate <= 0.0) { Thread.Sleep(1); return EventWait.DeferNextAnchor; }
            double waitSec = (triggerAt - audioNow) / rate;
            if (waitSec > 0.01) { Thread.Sleep(1); return EventWait.DeferNextAnchor; } // 远future，睡眠
            if (waitSec > 0.0015)
            {
                // 近future（1.5~10ms）：高分辨率定时器睡到目标前 1.5ms，
                // 最后 1.5ms 走下面的自旋。原实现在 3~10ms 窗口内
                // Thread.Yield() 满核空转（高密度段落每事件最多烧 ~10ms CPU，
                // 是高速段 CPU 占用高的主因）。
                // 注意：audioNow/triggerAt 的计算未变，只改等待方式。
                HighResSleep(waitSec - 0.0015);
                return EventWait.RetrySame;
            }
            Thread.SpinWait(1000); // 极近（≤1.5ms），自旋等待保证微秒级触发
            return EventWait.RetrySame;
        }

        /// <summary>
        /// 派发单个已到时刻事件：直接判定计数 / 释放 / hold / 普通按下。
        /// 返回需主线程补发的直接判定次数。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DispatchEvent(in HitEvent ev, bool simulateKey, bool enableTechnique,
                                         long targetTicks, int eventIndex, double audioNow)
        {
            if (!simulateKey)
            {
                Log($"[Macro-Worker] 请求 Hit() EventIndex={eventIndex}");
                return 1;
            }

            if (ev.ReleaseOnly)
            {
                if (enableTechnique)
                {
                    byte keyToRelease = ev.IsHoldRelated
                        ? (ev.ReleaseKeyCode != 0 ? ev.ReleaseKeyCode : _holdKey)
                        : ev.ReleaseKeyCode;
                    SendKey(keyToRelease, false, targetTicks);
                    if (ev.IsHoldRelated) { _holdKey = 0; _isHoldDown = false; }
                    Log($"[Macro-Worker] 直接释放 key=0x{keyToRelease:X2} EventIndex={eventIndex} audioNow={audioNow:F6}");
                }
                else
                {
                    if (ev.IsHoldRelated) WorkerReleaseHoldKey();
                    else WorkerReleaseKey(ev.ReleaseKeyCode);
                    Log($"[Macro-Worker] 松键(hold={ev.IsHoldRelated} key=0x{ev.ReleaseKeyCode:X2}) EventIndex={eventIndex}");
                }
                return 0;
            }

            if (ev.IsHoldRelated)
            {
                if (enableTechnique)
                {
                    if (_isHoldDown) { SendKey(_holdKey, false, targetTicks); _holdKey = 0; _isHoldDown = false; }
                    SendKey(ev.KeyCode, true, targetTicks);
                    _holdKey = ev.KeyCode; _isHoldDown = true;
                    Log($"[Macro-Worker] 直接长按 0x{ev.KeyCode:X2} EventIndex={eventIndex} audioNow={audioNow:F6}");
                }
                else
                {
                    WorkerHoldKey(ev.KeyCode);
                    Log($"[Macro-Worker] Hold 按下 0x{ev.KeyCode:X2} EventIndex={eventIndex}");
                }
                return 0;
            }

            if (enableTechnique)
            {
                SendKey(ev.KeyCode, true, targetTicks);
                Log($"[Macro-Worker] 直接按下 0x{ev.KeyCode:X2} EventIndex={eventIndex} audioNow={audioNow:F6}");
            }
            else
            {
                WorkerPressKey(ev.KeyCode);
                Log($"[Macro-Worker] 按下 0x{ev.KeyCode:X2} EventIndex={eventIndex}");
            }
            return 0;
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
            Log($"[Macro-Worker] Hold 按下 0x{keyCode:X2}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerReleaseHoldKey()
        {
            if (!_isHoldDown) return;
            SendKey(_holdKey, isDown: false);
            _holdKey = 0; _isHoldDown = false;
            Log("[Macro-Worker] Hold 释放");
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
        private static unsafe void SendKey(byte keyCode, bool isDown, long targetLocalTicks = 0)
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

            // 虚拟异步键盘：合成事件直喂游戏 keyQueue（零注入抖动，详见 VirtualAsyncInput）。
            // targetLocalTicks：事件时间戳（批锚定后的理想本地时刻；0=用当前时刻）。
            if (VirtualAsyncInput.Active && VirtualAsyncInput.Send(keyCode, isDown, targetLocalTicks)) return;

            if (_cachedSkyHookMode)
            {
                int r = AsyncInputManager.DirectPushKey(keyCode, isDown);
                if (r != 0) Log($"[Macro-Worker] PushKeyEvent 失败 result={r} key=0x{keyCode:X2}");
                Log($"[Macro-Worker] SkyHook direct key=0x{keyCode:X2} down={isDown}");
            }
            else
            {
                _cachedInput.type = INPUT_KEYBOARD;
                _cachedInput.u.ki.wVk = keyCode;
                _cachedInput.u.ki.wScan = scanCodeCache[keyCode];
                _cachedInput.u.ki.dwFlags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                fixed (SkyHookSystem.INPUT* ptr = &_cachedInput)
                    SendInput(1, (IntPtr)ptr, sizeof(SkyHookSystem.INPUT));
                Log($"[Macro-Worker] SendInput key=0x{keyCode:X2} down={isDown}");
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
            conductor = scrConductor.instance;

            ParseKeyCodes();
            BuildHitEvents();

            initialized = true;
            _staticAnchorVersion++;

            // 方案6：锚点每帧由判定公式/采样直接给出，无需播种状态
            double startPos = conductor!.songposition_minusi;
            int syncFloor = SyncFloor(startPos);
            Volatile.Write(ref _workerLastTriggeredFloor, syncFloor);

            Log("[Macro-Main] 初始化完成");
        }

        /// <summary>可参与宏的砖：非空、下一块非 auto、非 midspin。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPlayableFloor(scrFloor? floor)
            => floor != null && !(floor.nextfloor?.auto ?? false) && !floor.midSpin;

        /// <summary>
        /// 抽取手法模拟用逐砖事件（时间/类型/楼层/逐层速度）。
        /// 事件 i 时刻 = floors[i+1].entryTime；hold 尾记 -1、hold 头记 2、普通记 1。
        /// evSpeed 传 null 时不收集速度（C# 回退路径不需要）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CollectFloorEvents(
            scrFloor[] floors, bool sim,
            List<double> evTime, List<int> evPress, List<int> evFloor, List<double>? evSpeed)
        {
            evTime.Clear();
            evPress.Clear();
            evFloor.Clear();
            evSpeed?.Clear();

            for (int i = 0; i < floors.Length - 1; i++)
            {
                var fl = floors[i];
                if (!IsPlayableFloor(fl)) continue;

                var nf = floors[i + 1];
                double t = nf?.entryTime ?? double.MaxValue;

                if (sim && fl.holdLength > -1 && nf != null && nf.holdLength == -1)
                {
                    evTime.Add(t); evPress.Add(-1); evFloor.Add(i); evSpeed?.Add(fl.speed);
                    continue;
                }

                bool isHoldHead = sim && nf != null && nf.holdLength > -1;
                evTime.Add(t);
                evPress.Add(isHoldHead ? 2 : 1);
                evFloor.Add(i);
                evSpeed?.Add(fl.speed);
            }
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

            for (int i = 0; i < n - 1; i++)
            {
                var floor = floors[i];
                if (!IsPlayableFloor(floor)) continue;

                double t = floors[i + 1]?.entryTime ?? double.MaxValue;

                if (simulate && floor.holdLength > -1 && i + 1 < n)
                {
                    var nf = floors[i + 1];
                    if (nf != null && nf.holdLength == -1)
                    {
                        if (_hitEventPoolUsed < pool.Length)
                            pool[_hitEventPoolUsed++] = new HitEvent(t, 0, releaseOnly: true);
                        continue;
                    }
                }

                byte key = keys[keyIdx];
                if (++keyIdx >= keyLen) keyIdx = 0;
                if (_hitEventPoolUsed < pool.Length)
                    pool[_hitEventPoolUsed++] = new HitEvent(t, key, releaseOnly: false);
            }

            if (_hitEventPoolUsed > 0 && _hitEventPoolUsed <= pool.Length)
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
        private static bool NeedReinitialize() => levelMaker?.listFloors.Count != floorCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && _keyCodesSnapshot.Length > 0) return;

            lastKeysSetting = keysSetting;
            var newList = new List<byte>(4);
            foreach (string part in keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                string keyName = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(keyName)) continue;
                if (keyName.Length == 1)
                {
                    char c = keyName[0];
                    if (c is >= 'A' and <= 'Z') { newList.Add((byte)c); continue; }
                    if (c is >= '0' and <= '9') { newList.Add((byte)c); continue; }
                }
                if (KeyMap.TryGetKeyCode(keyName, out byte code)) newList.Add(code);
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
            double bpm = (double)(conductor!.bpm * ADOBase.controller.playerOne.planetarySystem.speed);
            // 防御：bpm/limit 非法（损坏的分段配置或未初始化）时不做八度折叠，
            // 否则 `while (bpm <= limit/2) bpm *= 2` 对 0/负数会永不终止。
            if (bpm < 1e-9) bpm = 1e-9;
            if (limit <= 1e-9) return bpm;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildTechniqueHitEvents()
        {
            ParseTechniqueConfig();

            var floors = cachedFloors!;
            bool sim = Main.Settings.SimulateKeyPress;

            var evTime = _evTimeRecycle;
            var evPress = _evPressRecycle;
            var evFloor = _evFloorRecycle;
            var evSpeed = _evSpeedRecycle;
            CollectFloorEvents(floors, sim, evTime, evPress, evFloor, evSpeed);

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
                        var currentProfile = Main.Settings.TechniqueProfiles[Main.Settings.SelectedTechniqueProfileIndex];
                        segments = currentProfile.techniqueSegments.ToArray();
                        handPref = Main.Settings.TechniqueHandPreference;
                    }

                    double speedChangeTolerance = levelConfig?.speedChangeTolerance
                        ?? Main.Settings.SpeedChangeTolerance;
                    TechniqueSimulator.UpdateConfig(new TechniqueSimulator.TechniqueConfigSnapshot
                    {
                        LeftKeys = _techLeftKeys,
                        RightKeys = _techRightKeys,
                        LeftKeyOrders = _techKeyOrders[0],
                        RightKeyOrders = _techKeyOrders[1],
                        LeftPressTimes = _techPressDur[0],
                        RightPressTimes = _techPressDur[1],
                        BpmLimit = Main.Settings.TechniqueBpmLimit,
                        HandPreference = handPref,
                        SpeedChangeTolerance = speedChangeTolerance,
                        Segments = segments,
                        PressDurationMode = Main.Settings.TechniqueLegacyPressDuration ? 1 : 0,
                        MultiChordBalance = Main.Settings.TechniqueMultiChordBalance ? 1 : 0,
                    });

                    if (TechniqueSimulator.BuildHitEvents(
                            [.. evTime], [.. evPress], [.. evFloor], [.. evSpeed],
                            total,
                            conductor!.bpm, ADOBase.controller.playerOne.planetarySystem.speed,
                            out var nativeEvents))
                    {
                        _hitEvents = nativeEvents;
                        _hitEventCount = nativeEvents!.Length;
                        Log($"[Macro-Main] C++ 手法模拟（原生分段）完成：{_hitEventCount} 事件");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Main.Mod?.Logger.Log($"[Macro-Main] C++ 手法模拟异常: {ex.Message}");
                }
            }

#if DEBUG
            BuildCSHarpTechniqueHitEvents();
#else
            // Release 无 C# 回退（该实现被 #if DEBUG 整体排除）：DLL 缺失或构建
            // 失败时明确清空事件，避免沿用上一次构建结果；同时输出可见错误日志。
            _hitEvents = [];
            _hitEventCount = 0;
            Main.Mod?.Logger.Log("[Macro-Main] C++ 手法模拟未产出事件（Release 无 C# 回退），本局手法事件为空");
#endif
        }

#if DEBUG
        // ═══════════════════════════════════════════════════════════════
        //  C# 回退路径（仅 Debug：Release 不含本实现，DLL 失败时无回退）
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildCSHarpTechniqueHitEvents()
        {
            ParseTechniqueConfig();

            var  floors  = cachedFloors!;
            bool sim     = Main.Settings.SimulateKeyPress;

            // 使用对象池，避免分配
            CollectFloorEvents(floors, sim, _evTimeRecycle, _evPressRecycle, _evFloorRecycle, null);

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

        // 键位数组内容比较（分段键位覆盖变化判定用）
        private static bool SameKeys(byte[]? a, byte[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
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

            float  lastSegLimit = GetSegmentBpmLimit(evFloor[0]);
            double nowBpm       = GetAdviceBpm(lastSegLimit);
            int    lastSegIdx   = FindSegmentIndex(evFloor[0]);
            EffectiveTechConfig prevEc = GetEffectiveConfig(evFloor[0]);

            while (nowD < total)
            {
                int   curFloorIdx = evFloor[nowD];
                int   curSegIdx   = FindSegmentIndex(curFloorIdx);
                float curSegLimit = GetSegmentBpmLimit(curFloorIdx);
                var   ec          = GetEffectiveConfig(curFloorIdx);

                // 段边界：仅当有效键位配置变化时才重置连续状态（手交替）。
                // 只改 BPM 阈值的分段不应打断手序（历史 bug：残留的 [0,0] 空分段
                // 会在第 2 个事件处触发重置，导致起始手连按两次）。
                if (curSegIdx != lastSegIdx)
                {
                    bool keysChanged = !SameKeys(ec.LeftKeys, prevEc.LeftKeys)
                                    || !SameKeys(ec.RightKeys, prevEc.RightKeys);
                    if (keysChanged)
                    {
                        cHand = (_levelTechHandPref == 0) ? -1 : 1;
                    }
                    prevEc       = ec;
                    lastSegLimit = curSegLimit;
                    nowBpm       = GetAdviceBpm(curSegLimit);
                    lastSegIdx   = curSegIdx;
                }

                if (pieces.Count > total * kMaxPiecesPerEvent) break;

                double pLen = 60.0 / nowBpm / 2.0;
                if (pLen < 1e-9) pLen = 1e-9;

                int cnt   = CountEventsInRange(evTime, nowD, nowT + pLen * 0.995);
                int csH   = (cHand == 1) ? 1 : 0;

                int   maxK = (csH == 0) ? ec.LeftKeys.Length : ec.RightKeys.Length;

                // 密度超过单手指数：本片取满全部按键（maxK 个事件），
                // 替代旧的 2 的幂细分（在极高 BPM 下会停在只用少数按键的尺寸）。
                if (cnt > maxK)
                {
                    cnt = maxK;
                    if (nowD + maxK < total) pLen = evTime[nowD + maxK] - nowT;
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

                pieces.Add(new PieceInfo(cnt, csH, pLen, nowT, nowT + pLen, nowD));

                nowD += cnt;
                nowT += pLen;
                cHand = -cHand;

                if (nowD < total && Math.Abs(evTime[nowD] - nowT) < pLen * 0.01)
                    nowT = evTime[nowD];
            }
        }

        // 按分片结构计算松键偏移（与原生 DLL 的 CalculateReleaseTime 一致）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double CalculateReleaseTime(double pStart, PieceInfo cur, PieceInfo next, double t, double ratio)
        {
            if (next.PieceLen > cur.PieceLen + 5e-6)
            {
                return (pStart + cur.PieceLen > cur.EndTime + 5e-6)
                    ? (next.EndTime - t) * ratio / 2.0
                    : (pStart + cur.PieceLen * 2.0 - t) * ratio / 2.0;
            }
            return (pStart + cur.PieceLen + 5e-6 < cur.EndTime)
                ? (pStart + cur.PieceLen + next.PieceLen - t) * ratio / 2.0
                : (next.EndTime - t) * ratio / 2.0;
        }

        /// <summary>hold 松键事件（Debug 路径统一构造）。</summary>
        private static HitEvent MakeHoldRelease(double t, byte key)
            => new(t, 0, releaseOnly: true, isHoldRelated: true, releaseKeyCode: key);

        /// <summary>按当前片与片内序号解析该事件的手/键与时长比例。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ResolveHandKey(EffectiveTechConfig ec, PieceInfo cur, int i, out byte kc, out double ratio)
        {
            byte[]   hK = (cur.Hand == 0) ? ec.LeftKeys       : ec.RightKeys;
            int[][]  hO = (cur.Hand == 0) ? ec.LeftOrders     : ec.RightOrders;
            double[] hT = (cur.Hand == 0) ? ec.LeftPressTimes : ec.RightPressTimes;

            int oi = Math.Min(cur.EvCount - 1, hK.Length - 1);
            int ki = (i < hO[oi].Length) ? hO[oi][i] : (i % hK.Length);
            ki = Mathf.Clamp(ki, 0, hK.Length - 1);

            kc = hK[ki];
            ratio = (ki < hT.Length) ? hT[ki] : 0.8;
        }

        /// <summary>计算松键时长：分片结构基准 → 本片跨度上限截断 → 阈值折叠下限。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ComputeReleaseDuration(
            List<double> evTime, int idx, int floorIdx, PieceInfo cur, PieceInfo next,
            double pStart, double t, double ratio)
        {
            double dur = CalculateReleaseTime(pStart, cur, next, t, ratio);
            double span = 0.0, mi = 0.0;
            if (cur.EvCount > 1)
            {
                int first = cur.EvStart, last = cur.EvStart + cur.EvCount - 1;
                span = evTime[last] - evTime[first];
                mi = evTime[first + 1] - evTime[first];
                for (int q = first + 1; q < last; q++)
                {
                    double g = evTime[q + 1] - evTime[q];
                    if (g < mi) mi = g;
                }
            }
            else
            {
                mi = GetLocalPressInterval(evTime, idx, cur.PieceLen);
            }
            double cap = ratio * (span + mi);
            if (dur > cap) dur = cap;

            // 仿人下限：不低于“阈值折叠基准 × 比例”（约 40~80ms）
            double segLimit = GetSegmentBpmLimit(floorIdx);
            double floorDur = ratio * (30.0 / GetAdviceBpm(segLimit));
            if (dur < floorDur) dur = floorDur;

            return dur;
        }

        // 新版（默认）按压时长基准：该音与前后相邻“不同时刻”音符的较小间隔。
        // 用于在片尾跨暂停/长空拍时限制按住时长。同刻和弦跳过后取另一侧，
        // 孤立音回退片长。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetLocalPressInterval(List<double> evTime, int idx, double fallback)
        {
            int n = evTime.Count;
            double gp = 0.0, gn = 0.0;
            int k = idx - 1;
            while (k >= 0 && evTime[idx] <= evTime[k] + 1e-9) k--;
            if (k >= 0) gp = evTime[idx] - evTime[k];
            int j = idx + 1;
            while (j < n && evTime[j] <= evTime[idx] + 1e-9) j++;
            if (j < n) gn = evTime[j] - evTime[idx];

            double unit;
            if (gp > 1e-9 && gn > 1e-9) unit = Math.Min(gp, gn);
            else                        unit = Math.Max(gp, gn);
            if (unit <= 1e-9) unit = fallback;
            return unit;
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
                            output.Add(MakeHoldRelease(t, activeHoldKey));
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
                            output.Add(MakeHoldRelease(t - 0.000001, activeHoldKey));
                            activeHold = false;
                            activeHoldKey = 0;
                        }
                        lastSegIdxEvent = curSegIdx;
                    }

                    var ec = GetEffectiveConfig(curFloor);
                    ResolveHandKey(ec, cur, i, out byte kc, out double ratio);
                    bool isHoldHead = (press == 2);

                    if (isHoldHead && activeHold)
                    {
                        output.Add(MakeHoldRelease(t - 0.000001, activeHoldKey));
                        activeHold = false; activeHoldKey = 0;
                    }

                    output.Add(new HitEvent(t, kc, false, isHoldHead));

                    if (isHoldHead) { activeHold = true; activeHoldKey = kc; }
                    if (!sim || isHoldHead) continue;

                    // 计算松键时间：分片结构基准 → 本片跨度上限截断 → 阈值折叠下限
                    double dur = ComputeReleaseDuration(evTime, idx, curFloor, cur, next, pStart, t, ratio);
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
                output.Add(MakeHoldRelease(lastTime, activeHoldKey));
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

            Volatile.Write(ref _workerLastTriggeredFloor, -1);
            Interlocked.Exchange(ref _workerNeedsHit, 0);
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
            _formulaErrorLogged = false;

            if (skyHookInitialized) AsyncInputManager.ClearQueue();
            if (controller != null) ApplyHoldBehavior(controller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureWorkerRunning()
        {
            if (_workerRunning && _workerThread?.IsAlive == true) return;

            if (_workerThread != null)
            {
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

            _workerThread?.Join(500);
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
        private static long GetRawTicks() => Volatile.Read(ref _getTicksImpl)();

        // 两个时间戳之间的真实流逝秒（按当前时间源的刻度换算）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ElapsedSec(long from, long to)
            => (double)(to - from) * (_cachedHighPrecision ? 1e-7 : perfFreqInv);

        // 切换时间源委托（根据 HighPrecision 设置）
        private static void UpdateTicksDelegate()
        {
            // 主线程写、工作线程读（GetRawTicks）：走 Volatile 保证可见性
            Volatile.Write(ref _getTicksImpl,
                _cachedHighPrecision ? _getTicksHigh : _getTicksNormal);
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
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Log(string message)
        {
            // Debug 通道：Release 下所有调用点被 [Conditional] 整体剥除；
            // 线上诊断请使用 Main.Mod?.Logger.Log（见开发者说明书 §10）。
        }
    }

    #endregion
#pragma warning restore CS0420
}