using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Input;
using ADOFAIMacro.Timing;
using UnityEngine;
using Settings = ADOFAIMacro.Settings.Settings;

#nullable enable

namespace ADOFAIMacro.Technique
{
    internal static partial class MacroEngine
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
        // 同上：工作线程用 Volatile.Read 读、主线程用 Volatile.Write 写，
        // 互锁语义由 Volatile.* 提供，字段上不再重复声明 volatile（否则 CS0420）。
        private static TimeAnchor _currentAnchor = _anchorA;

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

        // 这四个字段全部通过 Volatile.Read / Volatile.Write 访问（见 KeyWorker.cs 的
        // WorkerLoop 与 Lifecycle.cs 的 ResetState），那已经是官方的互锁访问。
        // 字段上再加 volatile 修饰符属于重复声明，而且 Volatile.Read(ref x) 走 ref
        // 传参会让编译器认为「引用不被视为 volatile」→ 每个调用点报一条 CS0420。
        // 所以这里不加 volatile，互锁语义完全由 Volatile.* 负责。
        private static int _workerLastTriggeredFloor = -1;
        private static int _workerNeedsHit = 0;
        private static int _resetVersion = 0;
        // 入场索引"已就绪"标志：ResetState 置 false，Initialize 写完 SyncFloor 后置 true。
        // 用途见 WorkerLoop 的重同步分支：ResetState 与 Initialize 之间存在毫秒级窗口
        // （BuildHitEvents 可能耗时毫秒级），期间 _workerLastTriggeredFloor 处于中间态。
        private static bool _syncFloorReady;
#if DEBUG
        private static bool _debugWorkerInitLogged = false;
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


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static MacroEngine()
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
                    Main.Log("[Macro] 判定公式基线已接管（无需击中即对齐判定）");
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
            // 注意：这里的 Input 是 UnityEngine.Input。因为本文件同时 using 了
            // ADOFAIMacro.Input，裸写 `Input` 会被解析成命名空间 → CS0234，
            // 所以全部写成 UnityEngine.Input.X。
            if (Main.Settings.EnableKeyAdjust || Main.Settings.EnableArrowTimeAdjust)
            {
                bool ctrl = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
                if (ctrl && Main.Settings.EnableKeyAdjust)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                }
                else if (!ctrl && Main.Settings.EnableArrowTimeAdjust)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) Main.Settings.TimeOffset += Main.Settings.AdjustStep;
                }
            }
        }
    }
}
