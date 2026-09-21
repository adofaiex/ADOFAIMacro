using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#nullable enable
namespace ADOFAIMacro.Macro
{
    internal class TechniqueSimulator
    {
        // ─────────────────────────────────────────────
        //  DLL 句柄与委托
        // ─────────────────────────────────────────────
        private static IntPtr _techDllHandle = IntPtr.Zero;
        private static DelegateSetTechConfig? _setTechConfig;
        private static DelegateBuildTechEvents? _buildTechEvents;
        private static DelegateBuildTechEventsEx? _buildTechEventsEx;
        private static DelegateFreeTechEvents? _freeTechEvents;
        private static bool _dllLoadAttempted = false;

        // ─────────────────────────────────────────────
        //  缓存配置数据
        // ─────────────────────────────────────────────
        private static byte[]? _cachedLeftKeys;
        private static byte[]? _cachedRightKeys;
        private static int[][]? _cachedLeftKeyOrders;
        private static int[][]? _cachedRightKeyOrders;
        private static double[]? _cachedLeftPressTimes;
        private static double[]? _cachedRightPressTimes;
        private static int _cachedBpmLimit;
        private static int _cachedHandPreference;
        private static double _cachedSpeedChangeTolerance;
        private static int _cachedPressDurationMode;
        private static int _cachedMultiChordBalance;
        private static Settings.TechniqueSegment[]? _cachedSegments;

        // ─────────────────────────────────────────────
        //  Native 结构体（必须与 C++ Pack=8 完全对齐）
        // ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NativeHitEvent
        {
            public double TriggerTime;
            public byte KeyCode;
            // 3 bytes padding (Pack=8，下一个字段对齐到 4)
            [MarshalAs(UnmanagedType.Bool)]
            public bool ReleaseOnly;
            [MarshalAs(UnmanagedType.Bool)]
            public bool IsHoldRelated;
            public byte ReleaseKeyCode;
            // 3 bytes padding
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NativeTechniqueSegment
        {
            public int startFloor;        // 0
            public int endFloor;          // 4
            public double bpmLimit;          // 8

            // 可选按键覆盖
            public IntPtr leftKeys;          // 16
            public int leftKeyCount;      // 24
            // pad 4                         // 28
            public IntPtr rightKeys;         // 32
            public int rightKeyCount;     // 40
            // pad 4                         // 44

            public IntPtr leftKeyOrders;     // 48
            public IntPtr leftOrderLengths;  // 56
            public int leftOrderCounts;   // 64
            // pad 4                         // 68
            public IntPtr rightKeyOrders;    // 72
            public IntPtr rightOrderLengths; // 80
            public int rightOrderCounts;  // 88
            // pad 4                         // 92

            public IntPtr leftPressTimes;    // 96
            public IntPtr rightPressTimes;   // 104

            [MarshalAs(UnmanagedType.Bool)]
            public bool hasKeyOverride;    // 112
            // pad 4                         // 116 => sizeof = 120
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NativeTechniqueConfig
        {
            public IntPtr LeftKeys;
            public int LeftKeyCount;
            // pad 4
            public IntPtr RightKeys;
            public int RightKeyCount;
            // pad 4

            public IntPtr LeftKeyOrders;
            public IntPtr LeftOrderLengths;
            public int LeftOrderCounts;
            // pad 4
            public IntPtr RightKeyOrders;
            public IntPtr RightOrderLengths;
            public int RightOrderCounts;
            // pad 4

            public IntPtr LeftPressTimes;
            public IntPtr RightPressTimes;

            public double BpmLimit;
            public int HandPreference;
            // pad 4

            public IntPtr Segments;
            public int SegmentCount;
            // 4 bytes padding
            public double SpeedChangeTolerance;
            // 按压时长风格：0=跟随音符片长（新），1=旧版 1.3.0.30 折叠片长
            public int PressDurationMode;
            // 多押按键均分：0=关，1=开
            public int MultiChordBalance;
        }

        // ─────────────────────────────────────────────
        //  委托定义
        // ─────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DelegateSetTechConfig(ref NativeTechniqueConfig config);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DelegateBuildTechEvents(
            [In] double[] entryTimes,
            [In] int[] pressTypes,
            [In] int[] floorIndices,
            int eventCount,
            double bpm, double speed,
            out int outEventCount);

        // 新版接口：逐事件速度倍率（scrFloor.speed，相对基准 BPM）
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DelegateBuildTechEventsEx(
            [In] double[] entryTimes,
            [In] int[] pressTypes,
            [In] int[] floorIndices,
            [In] double[] speedMuls,
            int eventCount,
            double bpm, double speed,
            out int outEventCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DelegateFreeTechEvents(IntPtr events);

        // ─────────────────────────────────────────────
        //  Kernel32
        // ─────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // ─────────────────────────────────────────────
        //  配置输入快照（替代 12 参数；构造时深拷贝，运行期不受外部修改影响）
        // ─────────────────────────────────────────────
        internal sealed class TechniqueConfigSnapshot
        {
            public byte[] LeftKeys = [];
            public byte[] RightKeys = [];
            public int[][] LeftKeyOrders = [];
            public int[][] RightKeyOrders = [];
            public double[] LeftPressTimes = [];
            public double[] RightPressTimes = [];
            public double BpmLimit;
            public int HandPreference;
            public double SpeedChangeTolerance;
            public Settings.TechniqueSegment[] Segments = [];
            public int PressDurationMode;
            public int MultiChordBalance;
        }

        // ─────────────────────────────────────────────
        //  公开 API
        // ─────────────────────────────────────────────

        /// <summary>更新缓存配置（含分段覆盖；输入会被深拷贝为快照）。</summary>
        public static void UpdateConfig(TechniqueConfigSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            _cachedLeftKeys = snapshot.LeftKeys == null ? null : (byte[])snapshot.LeftKeys.Clone();
            _cachedRightKeys = snapshot.RightKeys == null ? null : (byte[])snapshot.RightKeys.Clone();
            _cachedLeftKeyOrders = CloneOrders(snapshot.LeftKeyOrders);
            _cachedRightKeyOrders = CloneOrders(snapshot.RightKeyOrders);
            _cachedLeftPressTimes = snapshot.LeftPressTimes == null ? null : (double[])snapshot.LeftPressTimes.Clone();
            _cachedRightPressTimes = snapshot.RightPressTimes == null ? null : (double[])snapshot.RightPressTimes.Clone();
            _cachedBpmLimit = (int)snapshot.BpmLimit;
            _cachedHandPreference = snapshot.HandPreference;
            _cachedSpeedChangeTolerance = snapshot.SpeedChangeTolerance;
            _cachedSegments = CloneSegments(snapshot.Segments);
            _cachedPressDurationMode = snapshot.PressDurationMode;
            _cachedMultiChordBalance = snapshot.MultiChordBalance;
        }

        private static int[][]? CloneOrders(int[][]? orders)
        {
            if (orders == null) return null;
            var result = new int[orders.Length][];
            for (int i = 0; i < orders.Length; i++)
                result[i] = (int[])orders[i].Clone();
            return result;
        }

        private static Settings.TechniqueSegment[]? CloneSegments(Settings.TechniqueSegment[]? segments)
        {
            if (segments == null) return null;
            var result = new Settings.TechniqueSegment[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                var s = segments[i];
                result[i] = new Settings.TechniqueSegment
                {
                    startFloor = s.startFloor,
                    endFloor = s.endFloor,
                    bpmLimit = s.bpmLimit,
                    leftHandKeys = s.leftHandKeys,
                    rightHandKeys = s.rightHandKeys,
                    leftHandOrders = s.leftHandOrders,
                    rightHandOrders = s.rightHandOrders,
                    leftHandPressTimes = s.leftHandPressTimes,
                    rightHandPressTimes = s.rightHandPressTimes,
                };
            }
            return result;
        }

        /// <summary>加载 TechniqueSimulator.dll</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LoadTechniqueDll()
        {
            if (_dllLoadAttempted) return _techDllHandle != IntPtr.Zero;
            _dllLoadAttempted = true;

            // ABI 自检：手工声明的 NativeHitEvent 布局必须与 C++ 一致，
            // 不一致时拒绝加载（而不是静默读错内存）
            if (!VerifyHitEventAbi(out string abiError))
            {
                Main.Mod?.Logger.Log($"[Macro] NativeHitEvent ABI 自检失败，拒绝加载手法DLL: {abiError}");
                return false;
            }

            try
            {
                string modPath = Main.Mod?.Path
                    ?? Path.GetDirectoryName(typeof(InputSystem).Assembly.Location);
                string dllPath = Path.Combine(modPath, "TechniqueSimulator.dll");

                if (!File.Exists(dllPath))
                {
                    Main.Mod?.Logger.Log($"[Macro] 找不到手法模拟DLL: {dllPath}");
                    return false;
                }

                Main.Mod?.Logger.Log($"[Macro] 加载DLL: {dllPath}");
                _techDllHandle = LoadLibrary(dllPath);

                if (_techDllHandle == IntPtr.Zero)
                {
                    Main.Mod?.Logger.Log($"[Macro] LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                IntPtr setPtr = GetProcAddress(_techDllHandle, "SetTechniqueConfig");
                IntPtr buildExPtr = GetProcAddress(_techDllHandle, "BuildTechniqueHitEventsEx");
                IntPtr buildPtr = GetProcAddress(_techDllHandle, "BuildTechniqueHitEvents");
                IntPtr freePtr = GetProcAddress(_techDllHandle, "FreeHitEvents");

                if (setPtr == IntPtr.Zero || (buildExPtr == IntPtr.Zero && buildPtr == IntPtr.Zero) || freePtr == IntPtr.Zero)
                {
                    Main.Mod?.Logger.Log("[Macro] 获取函数地址失败");
                    FreeLibrary(_techDllHandle);
                    _techDllHandle = IntPtr.Zero;
                    return false;
                }

                _setTechConfig = Marshal.GetDelegateForFunctionPointer<DelegateSetTechConfig>(setPtr);
                _buildTechEventsEx = buildExPtr != IntPtr.Zero
                    ? Marshal.GetDelegateForFunctionPointer<DelegateBuildTechEventsEx>(buildExPtr)
                    : null;
                _buildTechEvents = buildPtr != IntPtr.Zero
                    ? Marshal.GetDelegateForFunctionPointer<DelegateBuildTechEvents>(buildPtr)
                    : null;
                _freeTechEvents = Marshal.GetDelegateForFunctionPointer<DelegateFreeTechEvents>(freePtr);

                Main.Mod?.Logger.Log($"[Macro] 手法模拟DLL加载成功（接口: {(_buildTechEventsEx != null ? "Ex(per-floor speed)" : "Legacy")}）");
                return true;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[Macro] 加载DLL异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>构建手法模拟事件，结果以 Macro.HitEvent[] 返回</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool BuildHitEvents(
            double[] entryTimes,
            int[] pressTypes,
            int[] floorIndices,
            double[] speedMuls,
            int eventCount,
            double bpm, double speed,
            out Macro.HitEvent[]? hitEvents)
        {
            hitEvents = null;

            if (_cachedLeftKeys == null)
            {
                Main.Mod?.Logger.Log("[Macro] 手法模拟配置未初始化");
                return false;
            }
            if (_buildTechEventsEx == null && _buildTechEvents == null)
            {
                Main.Mod?.Logger.Log("[Macro] 手法模拟DLL未加载");
                return false;
            }

            NativeTechniqueConfig config = default;
            IntPtr nativeEvents = IntPtr.Zero;

            try
            {
                config = PrepareNativeConfig();
                _setTechConfig!(ref config);

                int outCount;
                if (_buildTechEventsEx != null)
                {
                    nativeEvents = _buildTechEventsEx(
                        entryTimes, pressTypes, floorIndices, speedMuls,
                        eventCount, bpm, speed,
                        out outCount);
                }
                else
                {
                    nativeEvents = _buildTechEvents!(
                        entryTimes, pressTypes, floorIndices,
                        eventCount, bpm, speed,
                        out outCount);
                }

                if (nativeEvents != IntPtr.Zero && outCount > 0)
                {
                    var events = new Macro.HitEvent[outCount];
                    int size = Marshal.SizeOf<NativeHitEvent>();

                    // 安全封送：走结构体声明（布局已由 LoadTechniqueDll 的 ABI 自检保证），
                    // 不再手写字段偏移——结构体调整后会编译/自检失败，而非静默读错内存
                    for (int i = 0; i < outCount; i++)
                    {
                        var native = Marshal.PtrToStructure<NativeHitEvent>(
                            IntPtr.Add(nativeEvents, i * size));
                        events[i] = new Macro.HitEvent(
                            native.TriggerTime,
                            native.KeyCode,
                            native.ReleaseOnly,
                            native.IsHoldRelated,
                            native.ReleaseKeyCode);
                    }

                    hitEvents = events;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[Macro] DLL调用异常: {ex.Message}");
                return false;
            }
            finally
            {
                FreeNativeConfig(ref config);
                if (nativeEvents != IntPtr.Zero)
                    _freeTechEvents!(nativeEvents);
            }
        }

        public static bool IsDllLoaded() => _techDllHandle != IntPtr.Zero;

        /// <summary>NativeHitEvent 布局自检（与 C++ Pack=8 定义比对）。</summary>
        private static bool VerifyHitEventAbi(out string error)
        {
            int size = Marshal.SizeOf<NativeHitEvent>();
            int offTrigger = (int)Marshal.OffsetOf<NativeHitEvent>(nameof(NativeHitEvent.TriggerTime));
            int offKey = (int)Marshal.OffsetOf<NativeHitEvent>(nameof(NativeHitEvent.KeyCode));
            int offRelease = (int)Marshal.OffsetOf<NativeHitEvent>(nameof(NativeHitEvent.ReleaseOnly));
            int offHold = (int)Marshal.OffsetOf<NativeHitEvent>(nameof(NativeHitEvent.IsHoldRelated));
            int offReleaseKey = (int)Marshal.OffsetOf<NativeHitEvent>(nameof(NativeHitEvent.ReleaseKeyCode));
            bool ok = size == 24 && offTrigger == 0 && offKey == 8 && offRelease == 12
                      && offHold == 16 && offReleaseKey == 20;
            error = ok ? "" :
                $"sizeof={size}, offsets=({offTrigger},{offKey},{offRelease},{offHold},{offReleaseKey})，期望 sizeof=24, offsets=(0,8,12,16,20)";
            return ok;
        }

        public static void Reset() { Unload(); _dllLoadAttempted = false; }

        public static void Unload()
        {
            if (_techDllHandle != IntPtr.Zero) { FreeLibrary(_techDllHandle); _techDllHandle = IntPtr.Zero; }
            _setTechConfig = null; _buildTechEventsEx = null; _buildTechEvents = null; _freeTechEvents = null;
            _dllLoadAttempted = false;
        }

        // ─────────────────────────────────────────────
        //  内部：组装 NativeTechniqueConfig
        // ─────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NativeTechniqueConfig PrepareNativeConfig()
        {
            if (_cachedLeftKeys == null || _cachedRightKeys == null)
                throw new InvalidOperationException("请先调用 UpdateConfig");

            var config = new NativeTechniqueConfig
            {
                BpmLimit = _cachedBpmLimit,
                HandPreference = _cachedHandPreference,
                SegmentCount = _cachedSegments?.Length ?? 0,
                SpeedChangeTolerance = _cachedSpeedChangeTolerance,
                PressDurationMode = _cachedPressDurationMode,
                MultiChordBalance = _cachedMultiChordBalance
            };

            try
            {
                // ── 全局左右手按键 ─────────────────────────────────
                config.LeftKeys = AllocBytes(_cachedLeftKeys);
                config.LeftKeyCount = _cachedLeftKeys.Length;

                config.RightKeys = AllocBytes(_cachedRightKeys);
                config.RightKeyCount = _cachedRightKeys.Length;

                // ── 全局按键顺序 ───────────────────────────────────
                if (_cachedLeftKeyOrders != null)
                    AllocOrderData(_cachedLeftKeyOrders, ref config.LeftKeyOrders,
                                   ref config.LeftOrderLengths, ref config.LeftOrderCounts);
                if (_cachedRightKeyOrders != null)
                    AllocOrderData(_cachedRightKeyOrders, ref config.RightKeyOrders,
                                   ref config.RightOrderLengths, ref config.RightOrderCounts);

                // ── 全局按键时长 ───────────────────────────────────
                if (_cachedLeftPressTimes != null)
                    config.LeftPressTimes = AllocDoubles(_cachedLeftPressTimes);
                if (_cachedRightPressTimes != null)
                    config.RightPressTimes = AllocDoubles(_cachedRightPressTimes);

                // ── 分段数组（含可选按键覆盖）─────────────────────
                if (_cachedSegments != null && _cachedSegments.Length > 0)
                {
                    int segSize = Marshal.SizeOf<NativeTechniqueSegment>();
                    config.Segments = Marshal.AllocCoTaskMem(segSize * _cachedSegments.Length);
                    config.SegmentCount = _cachedSegments.Length;

                    for (int i = 0; i < _cachedSegments.Length; i++)
                    {
                        var s = _cachedSegments[i];
                        var ns = new NativeTechniqueSegment
                        {
                            startFloor = s.startFloor,
                            endFloor = s.endFloor,
                            bpmLimit = s.bpmLimit,
                            hasKeyOverride = s.HasKeyOverride
                        };

                        if (s.HasKeyOverride)
                        {
                            // 左手覆盖
                            if (!string.IsNullOrWhiteSpace(s.leftHandKeys))
                            {
                                byte[] lk = ParseKeys(s.leftHandKeys, _cachedLeftKeys);
                                ns.leftKeys = AllocBytes(lk);
                                ns.leftKeyCount = lk.Length;

                                double[] lp = ParsePressTimes(s.leftHandPressTimes, lk.Length, _cachedLeftPressTimes);
                                ns.leftPressTimes = AllocDoubles(lp);

                                int[][] lo = ParseOrders(s.leftHandOrders, lk.Length);
                                AllocOrderData(lo, ref ns.leftKeyOrders,
                                               ref ns.leftOrderLengths, ref ns.leftOrderCounts);
                            }

                            // 右手覆盖
                            if (!string.IsNullOrWhiteSpace(s.rightHandKeys))
                            {
                                byte[] rk = ParseKeys(s.rightHandKeys, _cachedRightKeys);
                                ns.rightKeys = AllocBytes(rk);
                                ns.rightKeyCount = rk.Length;

                                double[] rp = ParsePressTimes(s.rightHandPressTimes, rk.Length, _cachedRightPressTimes);
                                ns.rightPressTimes = AllocDoubles(rp);

                                int[][] ro = ParseOrders(s.rightHandOrders, rk.Length);
                                AllocOrderData(ro, ref ns.rightKeyOrders,
                                               ref ns.rightOrderLengths, ref ns.rightOrderCounts);
                            }
                        }

                        Marshal.StructureToPtr(ns,
                            IntPtr.Add(config.Segments, i * segSize), false);
                    }
                }

                return config;
            }
            catch
            {
                FreeNativeConfig(ref config);
                throw;
            }
        }

        // ─────────────────────────────────────────────
        //  内部：释放 NativeTechniqueConfig 所有内存
        // ─────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FreeNativeConfig(ref NativeTechniqueConfig config)
        {
            FreeIfNotZero(ref config.LeftKeys);
            FreeIfNotZero(ref config.RightKeys);
            FreeIfNotZero(ref config.LeftPressTimes);
            FreeIfNotZero(ref config.RightPressTimes);

            FreeOrderData(ref config.LeftKeyOrders, ref config.LeftOrderLengths, config.LeftOrderCounts);
            FreeOrderData(ref config.RightKeyOrders, ref config.RightOrderLengths, config.RightOrderCounts);

            // 释放分段内部分配
            if (config.Segments != IntPtr.Zero)
            {
                int segSize = Marshal.SizeOf<NativeTechniqueSegment>();
                for (int i = 0; i < config.SegmentCount; i++)
                {
                    var ns = Marshal.PtrToStructure<NativeTechniqueSegment>(
                                 IntPtr.Add(config.Segments, i * segSize));

                    if (!ns.hasKeyOverride) continue;

                    if (ns.leftKeys != IntPtr.Zero) Marshal.FreeCoTaskMem(ns.leftKeys);
                    if (ns.rightKeys != IntPtr.Zero) Marshal.FreeCoTaskMem(ns.rightKeys);
                    if (ns.leftPressTimes != IntPtr.Zero) Marshal.FreeCoTaskMem(ns.leftPressTimes);
                    if (ns.rightPressTimes != IntPtr.Zero) Marshal.FreeCoTaskMem(ns.rightPressTimes);

                    FreeOrderPtrs(ns.leftKeyOrders, ns.leftOrderLengths, ns.leftOrderCounts);
                    FreeOrderPtrs(ns.rightKeyOrders, ns.rightOrderLengths, ns.rightOrderCounts);
                }
                Marshal.FreeCoTaskMem(config.Segments);
                config.Segments = IntPtr.Zero;
            }
        }

        // ─────────────────────────────────────────────
        //  内存分配辅助
        // ─────────────────────────────────────────────
        private static IntPtr AllocBytes(byte[] src)
        {
            IntPtr p = Marshal.AllocCoTaskMem(src.Length);
            Marshal.Copy(src, 0, p, src.Length);
            return p;
        }

        private static IntPtr AllocDoubles(double[] src)
        {
            IntPtr p = Marshal.AllocCoTaskMem(src.Length * sizeof(double));
            Marshal.Copy(src, 0, p, src.Length);
            return p;
        }

        private static unsafe void AllocOrderData(int[][] orders,
            ref IntPtr ordersPtr, ref IntPtr lengthsPtr, ref int count)
        {
            count = orders.Length;
            ordersPtr = Marshal.AllocCoTaskMem(count * IntPtr.Size);
            lengthsPtr = Marshal.AllocCoTaskMem(count * sizeof(int));
            int[] lens = new int[count];

            for (int i = 0; i < count; i++)
            {
                lens[i] = orders[i].Length;
                IntPtr p = Marshal.AllocCoTaskMem(orders[i].Length * sizeof(int));
                Marshal.Copy(orders[i], 0, p, orders[i].Length);
                Marshal.WriteIntPtr(ordersPtr, i * IntPtr.Size, p);
            }
            Marshal.Copy(lens, 0, lengthsPtr, count);
        }

        // ─────────────────────────────────────────────
        //  内存释放辅助
        // ─────────────────────────────────────────────
        private static void FreeIfNotZero(ref IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(ptr);
            ptr = IntPtr.Zero;
        }

        private static void FreeOrderData(ref IntPtr ptrs, ref IntPtr lens, int count)
        {
            FreeOrderPtrs(ptrs, lens, count);
            ptrs = IntPtr.Zero;
            lens = IntPtr.Zero;
        }

        private static void FreeOrderPtrs(IntPtr ptrs, IntPtr lens, int count)
        {
            if (ptrs == IntPtr.Zero) return;
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(ptrs, i * IntPtr.Size);
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
            Marshal.FreeCoTaskMem(ptrs);
            if (lens != IntPtr.Zero) Marshal.FreeCoTaskMem(lens);
        }

        // ─────────────────────────────────────────────
        //  解析辅助（复用 Macro 中的字典，并提供 fallback）
        // ─────────────────────────────────────────────

        /// <summary>解析按键字符串；若为空则返回 fallback</summary>
        private static byte[] ParseKeys(string? input, byte[] fallback)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;

            var result = new List<byte>();
            foreach (var part in input!.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(name)) continue;

                if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z')
                {
                    result.Add((byte)name[0]); continue;
                }
                if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
                {
                    result.Add((byte)name[0]); continue;
                }
                // 复用 Macro 类中的统一映射表
                if (KeyMap.TryGetKeyCode(name, out byte code))
                    result.Add(code);
            }

            return result.Count > 0 ? [.. result] : fallback;
        }

        /// <summary>解析时长比例字符串；若为空则返回 fallback</summary>
        private static double[] ParsePressTimes(string? input, int keyCount, double[]? fallback)
        {
            if (string.IsNullOrWhiteSpace(input))
                return fallback ?? [.. Enumerable.Repeat(0.8, keyCount)];

            var result = new double[keyCount];
            for (int i = 0; i < result.Length; i++) result[i] = 0.8;

            var parts = input!.Split([','], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(parts.Length, result.Length); i++)
                if (double.TryParse(parts[i].Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    result[i] = v;

            return result;
        }

        /// <summary>解析按键顺序字符串（格式同 Macro.ParseTechOrders）</summary>
        private static int[][] ParseOrders(string? input, int keyCount)
        {
            int slots = keyCount;
            var result = new int[slots][];

            for (int n = 0; n < slots; n++)
            {
                result[n] = new int[n + 1];
                for (int i = 0; i <= n; i++) result[n][i] = i % keyCount;
            }

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
    }
}