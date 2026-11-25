using System;
using System.Runtime.InteropServices;
using ADOFAIMacro.Core;

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// TriggerRs（Rust 触发内核）的 C# 互操作宿主。
    ///
    /// 职责：
    ///   - P/Invoke 绑定（macro_trigger.dll 的 5 个导出）；
    ///   - 会话生命周期（LoadTechniqueDll 同款：DLL 搜索路径 = Mods 目录）；
    ///   - 自动回退状态机：连续 N 次异常/坏输入 → 熔断，调用方退回 C# 原路径
    ///     （复用 MacroPatchHub 的「异常达阈值自动卸载」思想）；
    ///   - 诊断计数（StepStats 转发）。
    ///
    /// 安全设计：
    ///   - 所有 P/Invoke 包 try/catch（DllNotFoundException/BadImageFormatException
    ///     → 标记不可用）；
    ///   - 事件表指针只在锚点有效期内有效（C# 侧 events 数组由锚点持有），
    ///     Rust 侧只读、不缓存指针——版本切换时 C# 必须换新锚点，指针自然失效；
    ///   - Rust 侧 panic=abort：若 Rust 真崩了，整个游戏崩（与早先随模组发布的
    ///     原生手法库同等风险面，FreezeTest 兜底）。
    /// </summary>
    internal static unsafe class TriggerRsHost
    {
        // ── FFI 结构（与 TriggerRs/src/lib.rs #[repr(C)] 严格一致）────
        [StructLayout(LayoutKind.Sequential)]
        internal struct Anchor
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
        internal struct HitEventFfi
        {
            public double TriggerTime;
            public byte KeyCode;
            public byte ReleaseOnly;
            public byte IsHoldRelated;
            public byte ReleaseKeyCode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaitAdvice
        {
            public double SleepSec;
            public double DeltaSec;
        }

        // ── 动作码（与 lib.rs 常量一致）────────────────────────────
        internal const int ACTION_NONE = 0;
        internal const int ACTION_PRESS = 1;
        internal const int ACTION_RELEASE = 2;
        internal const int ACTION_HOLD_PRESS = 3;
        internal const int ACTION_ADVANCE = 4;
        internal const int ACTION_FINISHED = 5;
        internal const int ACTION_WAIT_AGAIN = 6;
        internal const int ACTION_SLEEP = 7;

        // ── 熔断参数 ──────────────────────────────────────────────
        /// <summary>连续异常达到该次数即熔断（本会话内回退 C# 路径）。</summary>
        private const int ConsecutiveFailureLimit = 30;

        private static IntPtr _session;
        private static bool _available;
        /// <summary>
        /// 已熔断。★ 语义（2026-10-12 澄清）：**进程级持久**，不在线程重启时清除
        /// —— 只有进程重启（或显式 ResetTrip，见下）才恢复尝试。
        /// 原实现把 _tripped=false 放在 TryInitialize 开头，导致换关/宏重开时
        /// 熔断被静默解除、可能反复踩同一崩溃路径；注释说的「模组重启前不再尝试」
        /// 与实现不符。现在注释与实现一致。
        /// </summary>
        private static bool _tripped;
        private static int _consecutiveFailures;
        /// <summary>本会话是否已打过探针行。★ 每次新会话（TryInitialize 成功）重置。</summary>
        private static bool _probeDone;

        /// <summary>Rust 触发器当前可用（DLL 已加载且未熔断）。</summary>
        internal static bool Available => _available && !_tripped;

        // ── 诊断统计（★ 跨会话累计，2026-10-12 修）────────────────
        // 原来 TotalTicks/BadInputs 每次 RefreshStats 直接覆盖 ⇒ 会话销毁后
        // 计数归零、跨局无法观察 Rust 路径的总工作量。改为累加：
        // _lastTicks/_lastBad 记住上一次读到的会话内计数，每次读差值累加。
        private static ulong _lastTicks;
        private static ulong _lastBad;
        /// <summary>诊断：累计坏输入次数（跨会话）。</summary>
        internal static ulong BadInputs { get; private set; }
        /// <summary>诊断：累计 StepTick 调用次数（跨会话）。</summary>
        internal static ulong TotalTicks { get; private set; }
        /// <summary>诊断：累计会话数（每次 TryInitialize 成功 +1）。</summary>
        internal static int SessionCount { get; private set; }

        // ── P/Invoke ──────────────────────────────────────────────
        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr StepCreate();

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern void StepFree(IntPtr session);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepTick(IntPtr session, ref Anchor anchor, int eventIndex, long qpcNow, ref WaitAdvice wait);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepResync(IntPtr session);

        [DllImport("macro_trigger", CallingConvention = CallingConvention.StdCall)]
        private static extern int StepStats(IntPtr session, out ulong ticks, out ulong badInputs);

        // ── 生命周期 ──────────────────────────────────────────────

        /// <summary>
        /// 尝试加载 Rust 触发器。失败（DLL 缺失/签名不符）返回 false 且记日志——
        /// 调用方退回 C# 原路径，不算错误。
        /// </summary>
        internal static bool TryInitialize()
        {
            // ★ 不在开头清 _tripped（见字段注释）：熔断是进程级状态，换关/宏重开
            //   不得静默解除。唯一解除途径是进程重启或显式 ResetTrip()。
            _consecutiveFailures = 0;
            _available = false;
            _probeDone = false;        // ★ 新会话：允许重新打一次探针行
            _lastTicks = 0;
            _lastBad = 0;
            if (_session != IntPtr.Zero) { StepFree(_session); _session = IntPtr.Zero; }
            if (_tripped)
            {
                // 已熔断：不再尝试加载（避免反复踩同一崩溃路径）
                return false;
            }
            try
            {
                // 嵌入式加载：macro_trigger.dll 内嵌在 ADOFAIMacro.dll 资源里，
                // 首次使用时解压到 %LOCALAPPDATA%\ADOFAIMacro\native\ 再 LoadLibrary
                //（Windows 加载器语义：同名校验过模块后，后续 DllImport("macro_trigger")
                //  直接复用已加载模块，不再走文件系统 probing）。
                // 若嵌入资源缺失（旧版主 DLL），退回 Mods 目录探测（SetDllDirectory）。
                IntPtr h = EmbeddedNative.LoadEmbedded();
                if (h == IntPtr.Zero)
                {
                    string modPath = Main.Mod?.Path;
                    if (!string.IsNullOrEmpty(modPath) && System.IO.Directory.Exists(modPath))
                        SetDllDirectory(modPath);
                }

                _session = StepCreate();
                _available = _session != IntPtr.Zero;
                if (_available) SessionCount++;
            }
            catch (Exception ex)
            {
                Main.Log($"[TriggerRs] 加载失败（{ex.GetType().Name}: {ex.Message}），本次会话使用 C# 触发路径");
                _available = false;
            }
            return _available;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        internal static void Shutdown()
        {
            if (_session != IntPtr.Zero)
            {
                // 退出前最后读一次统计，把本会话增量计入累计（否则最后一个会话的
                // 计数永远丢失）
                try { RefreshStats(); } catch { }
                try { StepFree(_session); } catch { }
                _session = IntPtr.Zero;
            }
            _available = false;
            // ★ 不动 _tripped（熔断是进程级状态，见字段注释）
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// 显式解除熔断（仅用于「用户重新启用模组」这类有意重试的场合）。
        /// 默认不调用 —— 熔断后换关/重开线程不应自动恢复尝试。
        /// </summary>
        internal static void ResetTrip()
        {
            _tripped = false;
            _consecutiveFailures = 0;
        }

        /// <summary>C# 侧完成重同步后调用（Rust 吸收新版本号）。</summary>
        internal static void NotifyResync()
        {
            if (!Available) return;
            try { StepResync(_session); } catch { Trip("StepResync 异常"); }
        }

        // ── 核心判定 ──────────────────────────────────────────────

        /// <summary>
        /// 单步判定：与 WorkerLoop 热循环同语义。返回动作码，wait 带等待建议。
        /// 任何异常计入熔断计数并返回 ACTION_NONE（调用方走 C# 路径语义）。
        /// </summary>
        internal static int Step(
            ref Anchor anchor, int eventIndex, long qpcNow, out WaitAdvice wait)
        {
            wait = default;
            if (!Available) return ACTION_NONE;
            try
            {
                int act = StepTick(_session, ref anchor, eventIndex, qpcNow, ref wait);
                // 成功调用即清零连续失败（NONE 在正常流里只出现在版本吸收轮，
                // 与其它动作码一样不代表失败）——原实现两个分支写了同一句，已合并。
                _consecutiveFailures = 0;
                RefreshStats();
                return act;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                Main.Log($"[TriggerRs] StepTick 异常（第 {_consecutiveFailures} 次）: {ex.GetType().Name}: {ex.Message}");
                if (_consecutiveFailures >= ConsecutiveFailureLimit)
                    Trip("连续异常达阈值");
                return ACTION_NONE;
            }
        }

        /// <summary>坏输入（Rust 侧 bad_inputs 上升）计入熔断计数。</summary>
        internal static void ReportBadInput()
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= ConsecutiveFailureLimit)
                Trip("坏输入达阈值");
        }

        /// <summary>
        /// 坏输入**增量**计入熔断计数（★ 2026-10-12 修：RefreshStats 每事件调用，
        /// 原来只要 bad>0 就 ReportBadInput 一次 ⇒ 单次坏输入会被反复计数、
        /// 30 次阈值被秒穿。改为按增量调用）。
        /// </summary>
        private static void ReportBadInputDelta(ulong delta)
        {
            if (delta == 0) return;
            _consecutiveFailures += (int)Math.Min(delta, int.MaxValue);
            if (_consecutiveFailures >= ConsecutiveFailureLimit)
                Trip("坏输入达阈值");
        }

        private static void Trip(string why)
        {
            if (_tripped) return;
            _tripped = true;
            // ★ 语义（2026-10-12 修正）：熔断是**进程级**的 —— 换关/重开工作线程
            //   不会自动恢复（原实现在 TryInitialize 里清 _tripped，与这句话不符）。
            Main.Log($"[TriggerRs] 已熔断（{why}），本次运行期间回退 C# 触发路径"
                + "（进程重启或用户重新启用模组后才会再尝试）");
            try
            {
                GameNotifier.Warning("Rust 触发器异常已熔断，回退内置路径（详见日志）", 5f);
            }
            catch { /* 通知栏不可用不阻塞 */ }
        }

        /// <summary>
        /// 读会话统计并**累加**到跨会话计数（★ 2026-10-12 修：原实现直接覆盖，
        /// 会话销毁后归零、跨局无法观察总工作量）。
        /// 每事件调用一次成本很低（一次 P/Invoke），但探针日志只在每会话首点打一次。
        /// </summary>
        private static void RefreshStats()
        {
            if (_session == IntPtr.Zero) return;
            try
            {
                StepStats(_session, out var ticks, out var bad);
                // 会话内计数单调递增；取差值累加（若 Rust 侧计数回绕/重置，差值会
                // 下溢，用 unchecked 后夹 0 处理）
                ulong dTicks = ticks >= _lastTicks ? ticks - _lastTicks : ticks;
                ulong dBad = bad >= _lastBad ? bad - _lastBad : bad;
                _lastTicks = ticks;
                _lastBad = bad;
                TotalTicks += dTicks;
                BadInputs += dBad;
                if (dBad > 0) ReportBadInputDelta(dBad);

                if (!_probeDone)
                {
                    _probeDone = true;
                    // 每会话首点打一条（含累计值），避免每事件刷日志
                    Main.Log($"[TriggerRs] 探针: 本会话 ticks={ticks} badInputs={bad}"
                        + $" | 累计 ticks={TotalTicks} badInputs={BadInputs} 会话数={SessionCount}");
                }
            }
            catch { }
        }
    }
}
