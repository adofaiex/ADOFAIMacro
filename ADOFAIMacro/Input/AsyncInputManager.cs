using ADOFAIMacro.Platform;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static ADOFAIMacro.Macro.SkyHookSystem;

#nullable enable

namespace ADOFAIMacro.Macro
{
    public static class AsyncInputManager
    {
        // ══════════════════════════════════════════════════════
        //  Win32
        // ══════════════════════════════════════════════════════
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);


        private static volatile int _writeIndex = 0;
        private static volatile int _readIndex = 0;

        private static volatile bool _isInitialized = false;

        // 统计（跨线程写用 Interlocked）
        private static long _totalProcessed = 0;
        private static long _totalDropped = 0;

        public static bool IsInitialized => _isInitialized;

        // ══════════════════════════════════════════════════════
        //  启动
        //
        //  修复：移除 Start() 内的强制 Gen2 GC。
        //  原代码在此处调用三次阻塞式 GC（50~200ms），若在关卡进行中切换模式，
        //  主线程会卡顿导致锚点时间数据失效、音符触发全部偏移。
        //  GC 预热应在模组加载入口（Main.OnLoad）执行一次，远离关卡运行期。
        //
        //  修复：不再启动内部消费者线程。
        //  热路径已改为工作线程直接调用 InputSystem.PushKeyEvent()，
        //  中间的 ring buffer → ConsumeLoop → SpinWait 链路（0~1ms 抖动）已彻底消除。
        //  AsyncInputManager 现在只负责：
        //    ① 初始化 / 清理 C++ InputSystem 资源
        //    ② timeBeginPeriod(1) 保证 Sleep(1) 精度
        //    ③ GC LatencyMode 切换
        //    ④ 统计（可选）
        // ══════════════════════════════════════════════════════
        public static void Start()
        {
            if (_isInitialized)
            {
                Macro.Log("[InputSystem] 已在运行中");
                return;
            }

            try
            {
                // ① 时钟精度 15.6ms → 1ms
                //    工作线程 Sleep(1) 退化时，精度从 15.6ms 变成 1ms
                timeBeginPeriod(1);

                // ② GC 低延迟模式
                //    SustainedLowLatency：允许 Gen0/1 GC，抑制 Gen2 阻塞式 GC
                //    避免 50-200ms Stop-the-World 打断工作线程
                System.Runtime.GCSettings.LatencyMode =
                    System.Runtime.GCLatencyMode.SustainedLowLatency;

                // ③ 重置统计
                _writeIndex = 0;
                _readIndex = 0;
                _totalProcessed = 0;
                _totalDropped = 0;

                // ④ 初始化并启动 C++ 处理层
                InputSystem.StartProcessing();

                _isInitialized = true;

                Macro.Log("[InputSystem] 启动成功（直接调用模式）");
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 启动失败: {ex.Message}");
                _isInitialized = false;
                timeEndPeriod(1);
            }
        }

        // ══════════════════════════════════════════════════════
        //  停止
        // ══════════════════════════════════════════════════════
        public static void Stop()
        {
            if (!_isInitialized) return;

            InputSystem.EmergencyStop();
            InputSystem.StopProcessing();

            System.Runtime.GCSettings.LatencyMode =
                System.Runtime.GCLatencyMode.Interactive;
            timeEndPeriod(1);

            _isInitialized = false;
            Macro.Log($"[InputSystem] 已停止 | 处理: {_totalProcessed} | 丢弃: {Interlocked.Read(ref _totalDropped)}");
        }

        // ══════════════════════════════════════════════════════
        //  直接调用入口（热路径，由 Macro 工作线程调用）
        //
        //  真正的同步直发：SendKeyDirect 在调用线程上立即执行注入
        //  （C++ sendKeyCore + 按键状态更新），零中转。
        //
        //  历史坑：这里曾调 PushKeyEvent——那其实是"入队"：事件先进
        //  C++ 环形队列，再由原生工作线程经条件变量唤醒后取出注入，
        //  每次事件引入 10µs~2ms 的唤醒抖动。宏工作线程已经精确等待到
        //  触发时刻，注入必须同步完成，不能再到别的线程绕一圈。
        // ══════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DirectPushKey(byte keyCode, bool isDown)
        {
            if (!_isInitialized) return -1;

            // 首选同步直发（调用线程立即注入，零中转）
            int result = InputSystem.HasSendKeyDirect
                ? InputSystem.SendKeyDirect(keyCode, isDown)
                : -1;

            // 兜底：旧版原生 DLL 没有 SendKeyDirect 导出时走入队路径，
            // 绝不能静默丢键（否则宏会"整个失效"）
            if (result != 0)
                result = InputSystem.PushKeyEvent(keyCode, isDown, 0);

            // 统计（非热路径分支，result!=0 极少发生）
            if (result == 0)
                // 仅调用方线程写 _totalProcessed，无竞争，直接 ++
                _totalProcessed++;
            else
                Interlocked.Increment(ref _totalDropped);

            return result;
        }

        // ══════════════════════════════════════════════════════
        //  队列清空（Reset 时调用，清除 C++ 内部残留事件）
        // ══════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearQueue()
        {
            // 同步 ring buffer 指针（保持内部一致性）
            Interlocked.Exchange(ref _readIndex, _writeIndex);

            // 清空 C++ 层内部队列
            if (_isInitialized)
                InputSystem.ClearQueue();

            Macro.Log("[InputSystem] 队列已清空");
        }

        // ══════════════════════════════════════════════════════
        //  统计
        // ══════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int queueSize, long processed, long dropped) GetStats() =>
            (_writeIndex - _readIndex, _totalProcessed, Interlocked.Read(ref _totalDropped));
    }
}