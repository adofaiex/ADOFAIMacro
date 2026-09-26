using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ADOFAIMacro.Macro
{
    public enum InputMode : int
    {
        Auto = 0,
        NtUserInjectKeyboard = 1,
        NtUserSendInput = 2,
        SendInput = 3,
    }

    [Flags]
    public enum AvailableModeMask : int
    {
        Auto = 1 << 0,
        NtUserInjectKeyboard = 1 << 1,
        NtUserSendInput = 1 << 2,
        SendInput = 1 << 3,
    }

    public static class InputSystem
    {
        private static IntPtr _hModule = IntPtr.Zero;
        private static bool _isInitialized = false;

        // ══════════════════════════════════════════════════════
        //  Win32
        // ══════════════════════════════════════════════════════
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // ══════════════════════════════════════════════════════
        //  委托定义
        // ══════════════════════════════════════════════════════
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(int maxQueueSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PushKeyEventDelegate(
            byte keyCode,
            [MarshalAs(UnmanagedType.Bool)] bool isDown,
            uint delayMs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendKeyDirectDelegate(
            byte keyCode,
            [MarshalAs(UnmanagedType.Bool)] bool isDown);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int SendKeyCombinationDelegate(
            byte* keys, int keyCount, uint delayMs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendTextDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string text);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartProcessingDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StopProcessingDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClearQueueDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInputQueueStatusDelegate(
            out int queueSize, out int processedCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EmergencyStopDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool IsUsingNtFunctionsDelegate();

        // ── 新增：已缺失的委托 ─────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPressedKeysCountDelegate();

        // ── 模式控制 ───────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetInputModeDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInputModeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAvailableModesDelegate();

        // ══════════════════════════════════════════════════════
        //  函数指针
        // ══════════════════════════════════════════════════════
        private static InitializeDelegate InitializeFunc;
        private static PushKeyEventDelegate PushKeyEventFunc;
        private static SendKeyDirectDelegate SendKeyDirectFunc;
        private static SendKeyCombinationDelegate SendKeyCombinationFunc;
        private static SendTextDelegate SendTextFunc;
        private static StartProcessingDelegate StartProcessingFunc;
        private static StopProcessingDelegate StopProcessingFunc;
        private static ClearQueueDelegate ClearQueueFunc;
        private static GetInputQueueStatusDelegate GetInputQueueStatusFunc;
        private static ShutdownDelegate ShutdownFunc;
        private static EmergencyStopDelegate EmergencyStopFunc;
        private static IsUsingNtFunctionsDelegate IsUsingNtFunctionsFunc;
        private static GetPressedKeysCountDelegate GetPressedKeysCountFunc; // ← 新增
        private static SetInputModeDelegate SetInputModeFunc;
        private static GetInputModeDelegate GetInputModeFunc;
        private static GetAvailableModesDelegate GetAvailableModesFunc;

        // ══════════════════════════════════════════════════════
        //  初始化
        // ══════════════════════════════════════════════════════
        public static bool Initialize()
        {
            if (_isInitialized) return true;

            try
            {
                string modPath = Main.Mod?.Path
                    ?? Path.GetDirectoryName(typeof(InputSystem).Assembly.Location);
                string dllPath = Path.Combine(modPath, "InputSystem.dll");

                if (!File.Exists(dllPath))
                {
                    LogDiag($"[InputSystem] 文件不存在: {dllPath}");
                    return false;
                }

                _hModule = LoadLibrary(dllPath);
                if (_hModule == IntPtr.Zero)
                {
                    LogDiag($"[InputSystem] LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // 必须项
                InitializeFunc = GetDelegate<InitializeDelegate>("Initialize");
                PushKeyEventFunc = GetDelegate<PushKeyEventDelegate>("PushKeyEvent");

                if (InitializeFunc == null || PushKeyEventFunc == null)
                {
                    LogDiag("[InputSystem] 缺少必要的导出函数");
                    FreeLibrary(_hModule);
                    _hModule = IntPtr.Zero;
                    return false;
                }

                // 可选项（GetDelegate 内部已做 null 保护）
                SendKeyDirectFunc = GetDelegate<SendKeyDirectDelegate>("SendKeyDirect");
                SendKeyCombinationFunc = GetDelegate<SendKeyCombinationDelegate>("SendKeyCombination");
                SendTextFunc = GetDelegate<SendTextDelegate>("SendText");
                StartProcessingFunc = GetDelegate<StartProcessingDelegate>("StartProcessing");
                StopProcessingFunc = GetDelegate<StopProcessingDelegate>("StopProcessing");
                ClearQueueFunc = GetDelegate<ClearQueueDelegate>("ClearQueue");
                GetInputQueueStatusFunc = GetDelegate<GetInputQueueStatusDelegate>("GetInputQueueStatus");
                ShutdownFunc = GetDelegate<ShutdownDelegate>("Shutdown");
                EmergencyStopFunc = GetDelegate<EmergencyStopDelegate>("EmergencyStop");
                IsUsingNtFunctionsFunc = GetDelegate<IsUsingNtFunctionsDelegate>("IsUsingNtFunctions");
                GetPressedKeysCountFunc = GetDelegate<GetPressedKeysCountDelegate>("GetPressedKeysCount"); // ← 新增
                SetInputModeFunc = GetDelegate<SetInputModeDelegate>("SetInputMode");
                GetInputModeFunc = GetDelegate<GetInputModeDelegate>("GetInputMode");
                GetAvailableModesFunc = GetDelegate<GetAvailableModesDelegate>("GetAvailableModes");

                // C++ 环形缓冲区固定 1024，maxQueueSize 参数已忽略
                // 传 0 即可，保持向后兼容
                int result = InitializeFunc(0);
                LogDiag($"[InputSystem] 初始化结果: {result}");

                _isInitialized = (result == 0);
                if (_isInitialized)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                    SyncModeFromSettings();
                }

                return _isInitialized;
            }
            catch (Exception ex)
            {
                LogDiag($"[InputSystem] 初始化异常: {ex.Message}");
                return false;
            }
        }

        private static T GetDelegate<T>(string name) where T : Delegate
        {
            IntPtr ptr = GetProcAddress(_hModule, name);
            if (ptr == IntPtr.Zero)
            {
                LogDiag($"[InputSystem] 找不到函数: {name}");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        /// <summary>
        /// 诊断输出。Unity 的 Windows GUI 进程没有附加 stdout，Console.WriteLine
        /// 的内容不会出现在任何日志文件里 —— 而这里输出的恰恰是最需要排查的信息
        /// （DLL 缺失、LoadLibrary 失败、导出函数缺失、模式切换）。统一改走 UMM 日志；
        /// 模组尚未加载完成时退回 Console。
        /// </summary>
        private static void LogDiag(string message)
        {
            if (!Main.LoggingEnabled) return;
            try
            {
                var logger = Main.Mod?.Logger;
                if (logger != null) logger.Log(message);
                else Console.WriteLine(message);
            }
            catch { /* 日志失败绝不影响初始化流程 */ }
        }

        private static void SyncModeFromSettings()
        {
            try
            {
                if (Main.Mod != null && Main.Settings is Settings settings)
                {
                    Main.Log($"[InputSystem] 从设置同步模式: {settings.InputMode}");
                    SetInputMode((InputMode)settings.InputMode);
                }
            }
            catch (Exception ex)
            {
                Main.Log($"[InputSystem] 同步模式失败: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════
        //  模式控制
        // ══════════════════════════════════════════════════════
        public static InputMode SetInputMode(InputMode mode)
        {
            if (!_isInitialized || SetInputModeFunc == null) return InputMode.SendInput;
            int result = SetInputModeFunc((int)mode);
            if (result < 0)
            {
                LogDiag($"[InputSystem] SetInputMode 失败: {result}");
                return GetInputMode();
            }
            LogDiag($"[InputSystem] 模式切换 → 请求={mode}, 实际={((InputMode)result)}");
            return (InputMode)result;
        }

        public static InputMode GetInputMode()
        {
            if (!_isInitialized || GetInputModeFunc == null) return InputMode.SendInput;
            return (InputMode)GetInputModeFunc();
        }

        public static AvailableModeMask GetAvailableModes()
        {
            if (!_isInitialized || GetAvailableModesFunc == null) return AvailableModeMask.SendInput;
            return (AvailableModeMask)GetAvailableModesFunc();
        }

        public static bool IsModeAvailable(InputMode mode)
        {
            var mask = GetAvailableModes();
            return mode switch
            {
                InputMode.Auto => mask.HasFlag(AvailableModeMask.Auto),
                InputMode.NtUserInjectKeyboard => mask.HasFlag(AvailableModeMask.NtUserInjectKeyboard),
                InputMode.NtUserSendInput => mask.HasFlag(AvailableModeMask.NtUserSendInput),
                InputMode.SendInput => mask.HasFlag(AvailableModeMask.SendInput),
                _ => false
            };
        }

        // ══════════════════════════════════════════════════════
        //  输入 API
        // ══════════════════════════════════════════════════════
        public static int PushKeyEvent(byte keyCode, bool isDown, uint delayMs = 0)
        {
            if (!_isInitialized || PushKeyEventFunc == null) return -1;
            return PushKeyEventFunc(keyCode, isDown, delayMs);
        }

        public static int SendKeyDirect(byte keyCode, bool isDown)
        {
            if (!_isInitialized || SendKeyDirectFunc == null) return -1;
            return SendKeyDirectFunc(keyCode, isDown);
        }

        /// <summary>当前 DLL 是否提供同步直发（旧版 DLL 可能缺失，调用方需回退 PushKeyEvent）。</summary>
        public static bool HasSendKeyDirect => _isInitialized && SendKeyDirectFunc != null;

        public static unsafe int SendKeyCombination(byte[] keys, uint delayMs = 50)
        {
            if (!_isInitialized || SendKeyCombinationFunc == null
                || keys == null || keys.Length == 0) return -1;
            fixed (byte* pKeys = keys)
                return SendKeyCombinationFunc(pKeys, keys.Length, delayMs);
        }

        public static int SendText(string text)
        {
            if (!_isInitialized || SendTextFunc == null || string.IsNullOrEmpty(text)) return -1;
            return SendTextFunc(text);
        }

        public static int StartProcessing() => _isInitialized && StartProcessingFunc != null ? StartProcessingFunc() : -1;
        public static int StopProcessing() => _isInitialized && StopProcessingFunc != null ? StopProcessingFunc() : -1;
        public static void ClearQueue() { if (_isInitialized && ClearQueueFunc != null) ClearQueueFunc(); }

        public static int GetInputQueueStatus(out int queueSize, out int processedCount)
        {
            queueSize = 0; processedCount = 0;
            if (!_isInitialized || GetInputQueueStatusFunc == null) return -1;
            return GetInputQueueStatusFunc(out queueSize, out processedCount);
        }

        /// <summary>新增：获取当前仍处于按下状态的按键数量</summary>
        public static int GetPressedKeysCount()
        {
            if (!_isInitialized || GetPressedKeysCountFunc == null) return 0;
            return GetPressedKeysCountFunc();
        }

        public static void Shutdown()
        {
            try { ShutdownFunc?.Invoke(); } catch { }
            if (_hModule != IntPtr.Zero) { FreeLibrary(_hModule); _hModule = IntPtr.Zero; }
            _isInitialized = false;
        }

        public static void EmergencyStop()
        {
            if (_isInitialized && EmergencyStopFunc != null) EmergencyStopFunc();
        }

        public static bool IsUsingNtFunctions()
        {
            if (!_isInitialized || IsUsingNtFunctionsFunc == null) return false;
            try { return IsUsingNtFunctionsFunc(); } catch { return false; }
        }

        // ══════════════════════════════════════════════════════
        //  便捷方法
        // ══════════════════════════════════════════════════════
        public static void KeyDown(byte keyCode) => PushKeyEvent(keyCode, true);
        public static void KeyUp(byte keyCode) => PushKeyEvent(keyCode, false);

        public static void KeyPress(byte keyCode, uint durationMs = 50)
        {
            PushKeyEvent(keyCode, true, durationMs);
            PushKeyEvent(keyCode, false, 0);
        }

        public static void KeyDownDirect(byte keyCode) => SendKeyDirect(keyCode, true);
        public static void KeyUpDirect(byte keyCode) => SendKeyDirect(keyCode, false);

        public static (int queueSize, int processedCount) GetStatus()
        {
            GetInputQueueStatus(out int q, out int p);
            return (q, p);
        }

        public static bool IsInitialized => _isInitialized;
    }
}