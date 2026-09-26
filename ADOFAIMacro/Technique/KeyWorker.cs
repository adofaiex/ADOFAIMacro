using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Input;
using UnityEngine;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 工作线程与按键注入：在独立线程上按事件表的触发时刻发键。
    ///
    /// 主线程只负责在每帧写时间锚点（TimeAnchor），工作线程读锚点换算
    /// 真实等待时间 —— 这样即便主线程卡帧，按键时序仍由锚点线性外推，
    /// 不会跟着帧率抖。
    /// </summary>
    internal static partial class MacroEngine
    {
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
                        // Patches 里的 MacroEngine.Update 根本不被调用、StopWorkerIfNeeded 也不会执行），
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
            // 注意用 UMM Logger 直调——MacroEngine.Log 是 [Conditional("DEBUG")]，Release 会整体剥除
            if (!_keyPathProbeDone)
            {
                _keyPathProbeDone = true;
                Main.Log($"[Macro-KeyPath] active={VirtualAsyncInput.Active} mirror={Main.Settings.MirrorVirtualKeys} " +
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
                int r = Input.AsyncInputManager.DirectPushKey(keyCode, isDown);
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
    }
}
