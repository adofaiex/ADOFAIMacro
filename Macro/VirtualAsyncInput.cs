using System;
using System.Collections.Generic;
using ADOFAIMacro.Platform;
using SkyHook;

#nullable enable

namespace ADOFAIMacro.Macro
{
    /// <summary>
    /// 虚拟异步键盘：宏工作线程在精确触发时刻，把合成事件直接塞进游戏
    /// AsyncInputManager.keyQueue（与真实 skyhook 钩子线程同一入口）。
    ///
    /// 与系统注入路径（SendInput/Nt）的对比：
    ///  - 注入路径：宏线程 → 系统输入流 → LL 钩子线程唤醒 → 时间戳 → keyQueue
    ///    （注入+钩子唤醒抖动 0.05~2ms；依赖窗口焦点；有 UIPI 拦截问题）
    ///  - 本路径：宏线程 → keyQueue（时间戳由 PreciseNow 生成，亚微秒抖动；
    ///    零系统副作用；游戏未聚焦时事件由游戏自身丢弃，与真实按键一致）
    ///
    /// 游戏侧消费链（scrController.UpdateInput，主线程每帧）：
    ///  keyQueue → 按 GetTimeInTicks() 排序 → keyMask/keyDownMask →
    ///  ProcessKeyInputs(事件tick) → Simulated_PlayerControl_Update(事件tick)
    ///  → 判定角度按事件时间戳计算 —— 亚帧精度由此保留。
    ///
    /// 安全机制：
    ///  - 结构体布局自检（SelfTest）：游戏更新若改变 SkyHookEvent 字段布局，
    ///    自检失败自动禁用本路径，回退系统注入，绝不带错继续跑；
    ///  - 未映射按键（KeyLabel.Unknown）自动回退注入路径，避免掩码相等性冲突。
    /// </summary>
    internal static unsafe class VirtualAsyncInput
    {
        // SkyHookEvent 字段偏移（LayoutKind.Sequential，无引用字段）：
        //   TimeSec long @0, TimeSubsecNano uint @8, Type EventType(int) @12,
        //   Label KeyLabel(ushort) @16, Key ushort @18 —— 由 SelfTest 运行时验证
        private const int OffTimeSec = 0;
        private const int OffSubsecNano = 8;
        private const int OffType = 12;
        private const int OffLabel = 16;
        private const int OffKey = 18;

        private static volatile bool _layoutOk;
        private static volatile bool _active;

        // 焦点缓存：SkyHookManager.IsFocused 是静态 bool 默认 false，仅在其 Update 里
        // requireFocus==true 时才同步——本游戏的实例不同步它（僵尸值恒 false，曾把
        // 镜像的所有 down 误判为失焦拦掉）。改为 RefreshActive（主线程）每帧缓存
        // Application.isFocused，工作线程只读缓存。
        private static volatile bool _appFocused = true;

        // 内置按键显示状态（worker 写 / OnGUI 读，lock 保护）
        internal static readonly object DisplayLock = new();
        internal static readonly Dictionary<byte, int> DisplayDown = new(8);          // vk → 按下时刻 ms
        internal static readonly List<(byte vk, int time)> DisplayUps = new(8);       // 最近释放（淡出用）

        /// <summary>当前是否处于直喂模式（主线程每帧刷新，工作线程只读）。</summary>
        public static bool Active => _active;

        /// <summary>结构体布局自检 + 按键映射可用性检查（一次性，主线程调用）。</summary>
        public static bool Initialize()
        {
            _layoutOk = SelfTest();
            if (!_layoutOk)
                Main.Mod?.Logger.Log("[VirtualAsyncInput] SkyHookEvent 布局自检失败，直喂模式禁用（回退系统注入）");
            else
                Main.Mod?.Logger.Log("[VirtualAsyncInput] 布局自检通过，虚拟异步键盘就绪");
            return _layoutOk;
        }

        /// <summary>主线程每帧刷新：设置开启 + 布局自检通过 + 游戏异步输入链路活跃。</summary>
        public static void RefreshActive()
        {
            bool active;
            try
            {
                active = _layoutOk
                         && Main.Settings.UseVirtualAsyncInput
                         && Main.Settings.SkyHookMode
                         && Main.Settings.SimulateKeyPress
                         && global::AsyncInputManager.isActive
                         && RDInput.asyncKeyboard != null
                         && RDInput.asyncKeyboard.isActive;
            }
            catch
            {
                // 游戏侧类型尚未初始化 / 场景切换等任何异常 → 禁用
                active = false;
            }
            _active = active;
            _appFocused = UnityEngine.Application.isFocused;
        }

        /// <summary>
        /// 合成并直喂一个按键事件。返回 false 表示本次未发送（调用方应回退到系统注入路径）。
        /// 仅由宏工作线程调用；keyQueue 是 ConcurrentQueue，跨线程入队即钩子线程的同款用法。
        ///
        /// targetLocalTicks：事件时间戳（本地 .NET ticks）。游戏判定只认事件时间戳（亚帧），
        /// 宏的 WorkerLoop 用“批锚定”给出每个事件的理想本地时刻（批首真实发送时刻 +
        /// 理想相对时间），避免双押/多押逐个发送的累积延迟与高密度段的外推漂移。
        /// 传 0 = 用当前时刻（非批锚定路径/释放兜底）。

        public static bool Send(byte keyCode, bool isDown, long targetLocalTicks = 0)
        {
            if (!_active) return false;

            try
            {
                KeyLabel label = SkyHookKeyMapper.NativeKeyCodeToKeyLabel(keyCode);
                if (label == KeyLabel.Unknown)
                    return false; // 无 KeyLabel 的键走掩码相等性会互相合并，必须回退注入路径

                long localTicks = targetLocalTicks != 0 ? targetLocalTicks : PreciseNow.LocalTicks();
                PreciseNow.SplitLocalUnix(localTicks, out long sec, out uint nano);

                SkyHookEvent evt = BuildEvent(sec, nano,
                    isDown ? EventType.KeyPressed : EventType.KeyReleased,
                    label, keyCode);

                // 走真实钩子的同一入口：游戏 Setup 挂的监听器会把事件送进 keyQueue，
                // 订阅 KeyUpdated 的其他模组（键位显示器等）也能看到虚拟按键。
                // （游戏自身就是从钩子线程 Invoke 的，跨线程安全性与原生路径一致）
                SkyHookManager.KeyUpdated.Invoke(evt);

                // 内置按键显示：记录投递成功的虚拟键（ShowText 覆盖层渲染）
                lock (DisplayLock)
                {
                    int nowMs = Environment.TickCount;
                    if (isDown)
                        DisplayDown[keyCode] = nowMs;
                    else
                    {
                        DisplayDown.Remove(keyCode);
                        DisplayUps.Add((keyCode, nowMs));
                        if (DisplayUps.Count > 8) DisplayUps.RemoveAt(0);
                    }
                }

                // 镜像同步：注入一份真实按键让 OS 键盘/Unity Input 层可见
                // （JipperKeyViewer 等按键显示器读 Input.GetKeyDown，看不见直喂事件）
                if (_sendProbeDone == false)
                {
                    _sendProbeDone = true;
                    Main.Mod?.Logger.Log($"[Macro-KeyPath] Send-first-ok mirror={Main.Settings.MirrorVirtualKeys} " +
                              $"settings#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Main.Settings)}");
                }
                if (Main.Settings.MirrorVirtualKeys)
                    SendMirror(keyCode, isDown);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────
        //  镜像同步（Mirror）
        //
        //  直喂事件只进游戏 keyQueue，OS 键盘/Unity Input 层看不见，
        //  JipperKeyViewer 等按键显示器（读 Input.GetKeyDown）无显示。
        //  解决：直喂成功后同步注入一份真实按键（DirectPushKey → SendInput）。
        //
        //  回声问题：注入的键会经 skyhook LL 钩子回流 → HookCallback →
        //  KeyUpdated → keyQueue，同一击打被判定两次。解决：注入【前】
        //  给该键登记一次回声配额，SkyHookManager_HookCallback_Patch 的
        //  Prefix 消耗配额并丢弃该事件（虚拟直喂走 KeyUpdated.Invoke，
        //  不经过 HookCallback，不受影响）。
        // ─────────────────────────────────────────────────────────
        // 一次性探针标志（[Macro-KeyPath] Send-first-ok）
        private static bool _sendProbeDone;

        private static readonly object MirrorLock = new();
        private static readonly int[] _mirrorEchoDown = new int[256];   // 每键待消耗回声（按下）
        private static readonly int[] _mirrorEchoUp = new int[256];     // 每键待消耗回声（松开）
        private static readonly int[] _mirrorEchoExpire = new int[256]; // Environment.TickCount
        private const int MirrorEchoTimeoutMs = 120;

        // 快路径指示：存在任何未过期配额时才进锁检查（HookCallback 每个真实按键都过一遍）
        private static volatile int _mirrorQuotaActive;

        // 诊断计数（Interlocked，[Macro-Mirror] 每 3s 输出一次）：
        // 回声已丢 > 0 说明注入确实穿过了 OS 输入流（LL 钩子看见了），
        // 查看器仍不亮 = Unity Input 不报注入键；回声已丢 = 0 = 注入没发生/没到钩子
        private static long _mirrorStatSends;
        private static long _mirrorStatFail;
        private static long _mirrorStatEcho;
        private static long _mirrorStatUnfocused;
        private static int _mirrorStatLastFlush;

        private static void SendMirror(byte keyCode, bool isDown)
        {
            // 未聚焦时不注入新按下（SendInput 会打进别的应用）；
            // up 仍然要发——清理可能卡住的键状态，孤立的 up 无副作用
            if (isDown && !_appFocused)
            {
                System.Threading.Interlocked.Increment(ref _mirrorStatUnfocused);
                return;
            }

            lock (MirrorLock)
            {
                // 配额必须先于注入登记：注入事件 ~0.05-2ms 后到达 HookCallback，
                // 顺序颠倒会存在竞态窗口漏吃回声 → 双判定
                int idx = keyCode;
                _mirrorEchoExpire[idx] = Environment.TickCount + MirrorEchoTimeoutMs;
                if (isDown) _mirrorEchoDown[idx]++; else _mirrorEchoUp[idx]++;
                _mirrorQuotaActive = 1;
            }

            System.Threading.Interlocked.Increment(ref _mirrorStatSends);
            int result = AsyncInputManager.DirectPushKey(keyCode, isDown);
            if (result != 0)
                System.Threading.Interlocked.Increment(ref _mirrorStatFail);

            FlushMirrorStats();
        }

        private static void FlushMirrorStats()
        {
            int now = Environment.TickCount;
            int last = _mirrorStatLastFlush;
            if (now - last < 3000 || System.Threading.Interlocked.CompareExchange(ref _mirrorStatLastFlush, now, last) != last)
                return;

            long sends = System.Threading.Interlocked.Read(ref _mirrorStatSends);
            long fails = System.Threading.Interlocked.Read(ref _mirrorStatFail);
            long echoes = System.Threading.Interlocked.Read(ref _mirrorStatEcho);
            long unfocused = System.Threading.Interlocked.Read(ref _mirrorStatUnfocused);
            System.Threading.Interlocked.Add(ref _mirrorStatSends, -sends);
            System.Threading.Interlocked.Add(ref _mirrorStatFail, -fails);
            System.Threading.Interlocked.Add(ref _mirrorStatEcho, -echoes);
            System.Threading.Interlocked.Add(ref _mirrorStatUnfocused, -unfocused);
            if (sends > 0 || echoes > 0 || unfocused > 0)
                Main.Mod?.Logger.Log($"[Macro-Mirror] 注入 {sends} 次(失败 {fails}) | 回声已丢 {echoes} | 失焦跳过 {unfocused}");
        }

        /// <summary>
        /// HookCallback Prefix 调用：该事件是否为镜像注入的回声（是 → 应丢弃）。
        /// </summary>
        internal static bool ShouldDropMirrorEcho(ushort key, EventType type)
        {
            if (_mirrorQuotaActive == 0 || key >= 256) return false;

            lock (MirrorLock)
            {
                int idx = key;
                if (_mirrorEchoExpire[idx] != 0 &&
                    Environment.TickCount - _mirrorEchoExpire[idx] > 0)
                {
                    // 超时：回声没来（钩子未运行/被系统吃掉），清配额防误吞真实按键
                    _mirrorEchoDown[idx] = 0;
                    _mirrorEchoUp[idx] = 0;
                    _mirrorEchoExpire[idx] = 0;
                }

                int[] counts = type == EventType.KeyPressed ? _mirrorEchoDown : _mirrorEchoUp;
                if (counts[idx] > 0)
                {
                    counts[idx]--;
                    System.Threading.Interlocked.Increment(ref _mirrorStatEcho);
                    FlushMirrorStats();
                    return true;
                }
            }
            return false;
        }

        /// <summary>按已知偏移构造事件（readonly 字段无法用初始化器，只能指针写入）。</summary>
        private static SkyHookEvent BuildEvent(long timeSec, uint subsecNano, EventType type, KeyLabel label, byte key)
        {
            SkyHookEvent evt = default;
            byte* p = (byte*)&evt;
            *(long*)(p + OffTimeSec) = timeSec;
            *(uint*)(p + OffSubsecNano) = subsecNano;
            *(uint*)(p + OffType) = (uint)type;
            *(ushort*)(p + OffLabel) = (ushort)label;
            *(ushort*)(p + OffKey) = key;
            return evt;
        }

        /// <summary>
        /// 布局自检：用已知值构造事件，经公共只读字段 + GetTimeInTicks() 读回校验。
        /// 任何字段错位都会在此暴露（例如游戏更新改了结构体）。
        /// </summary>
        private static bool SelfTest()
        {
            try
            {
                const long testSec = 1786159187L;
                const uint testNano = 987654321u;
                var type = EventType.KeyPressed;
                var label = KeyLabel.J;
                const byte testKey = 0x4A;

                SkyHookEvent evt = BuildEvent(testSec, testNano, type, label, testKey);

                long expectedTicks = testSec * 10_000_000L + testNano / 100 + 621355968000000000L;
                return evt.TimeSec == testSec
                       && evt.TimeSubsecNano == testNano
                       && evt.Type == type
                       && evt.Label == label
                       && evt.Key == testKey
                       && evt.GetTimeInTicks() == expectedTicks;
            }
            catch
            {
                return false;
            }
        }
    }
}
