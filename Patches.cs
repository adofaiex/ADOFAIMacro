using ADOFAIMacro.Macro;
using ADOFAIMacro.Platform;
using HarmonyLib;
using SkyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace ADOFAIMacro
{
    public class Patches
    {
        // 普通按键缓存状态（位图优化）
        private static bool[] _keyFilterMap; // 索引 = (int)KeyCode, true=在列表中, false=不在
        private static string _lastFilteredKeysString = "";
        private static int _lastFilterMode = 0;
        private static bool _lastEnableFilter = false;

        // [Macro-Judge] 低频诊断状态（主线程：AddHit postfix 专用）
        private static double _judgeLogSum;
        private static int _judgeLogCount;
        private static int _judgeLogLastMs;

        // 异步按键缓存状态（独立，不共享）
        private static bool[] _asyncKeyFilterMap; // 索引 = VK code (0-255), true=在列表中
        private static string _lastFilteredAsyncKeysString = "";
        private static int _lastAsyncFilterMode = 0;
        private static bool _lastAsyncEnableFilter = false;

        private static readonly WeakReference<scrController> _cachedControllerRef = new WeakReference<scrController>(null);

        private static scrController CachedController
        {
            get
            {
                scrController ctrl;
                if (_cachedControllerRef.TryGetTarget(out ctrl) && ctrl != null)
                    return ctrl;
                return null;
            }
            set => _cachedControllerRef.SetTarget(value);
        }

        [HarmonyPatch(typeof(scrController), "PlayerControl_Update")]
        public static class Patch_PlayerControl_Update
        {
            [HarmonyPrefix]
            public static void Prefix(scrController __instance)
            {
                Macro.Macro.Update(__instance);
            }
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Awake_Rewind))]
        public static class Patch_Awake_Rewind
        {
            [HarmonyPostfix]
            public static void Postfix(scrController __instance)
            {
                Macro.Macro.Reset(__instance);
                // 关卡重置时，尝试加载关卡特定配置
                if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                {
                    LevelTechniqueManager.ResetCheckState();
                    LevelTechniqueManager.CheckAndLoadLevelConfig();
                }
            }
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Restart))]
        public static class Patch_Restart
        {
            [HarmonyPrefix]
            public static void Prefix(scrController __instance)
            {
                Macro.Macro.Reset(__instance);
                // 关卡重启时，尝试加载关卡特定配置
                if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                {
                    LevelTechniqueManager.ResetCheckState();
                    LevelTechniqueManager.CheckAndLoadLevelConfig();
                }
            }
        }

        // 关卡加载完成时也检测一次
        [HarmonyPatch(typeof(scnGame), "LoadAndPlayLevel")]
        public static class Patch_scnGame_LoadAndPlayLevel
        {
            [HarmonyPostfix]
            public static void Postfix(bool __result, string levelPath)
            {
                if (__result && Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                {
                    LevelTechniqueManager.ResetCheckState();
                    LevelTechniqueManager.CheckAndLoadLevelConfig();
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Patch_scnEdityor_Start
        {
            [HarmonyPostfix]
            public static void Postfix(scnEditor __instance)
            {
                if (Main.Settings.LockLevelEditor)
                    __instance.LockPathEditing(true);
            }
        }

        [HarmonyPatch(typeof(scrController), "Fail2Action")]
        public static class Patch_FailAction
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                if (Main.Settings.EnableDeathKey && Main.IsEnabled && Main.Settings.Macro)
                {
                    // 旧实现在 controller / editor / customLevel 上各启一个协程，
                    // 多个对象同时存在时会把同一个死亡键按下 2~3 次（重复触发）。
                    // 只挑第一个可用宿主启一个协程；且用 Unity 重载的 != null
                    // 判断（?. 走的是引用判空，已销毁对象会漏过并抛 MissingReferenceException）。
                    MonoBehaviour host = ADOBase.controller != null ? ADOBase.controller
                        : ADOBase.editor != null ? ADOBase.editor
                        : ADOBase.customLevel;
                    if (host != null)
                        host.StartCoroutine(DelayedSendDeathKey());
                }
            }

            private static System.Collections.IEnumerator DelayedSendDeathKey()
            {
                yield return new WaitForSeconds(Main.Settings.DeathKeyDelay);

                if (Main.Settings.SimulateKeyPress && Main.Settings.SkyHookMode && InputSystem.IsInitialized)
                {
                    byte key = (byte)Main.Settings.DeathKeyCode;
                    // 走 AsyncInputManager.DirectPushKey：它内含"旧版原生 DLL 没有
                    // SendKeyDirect 导出时回退 PushKeyEvent"的逻辑。直接调
                    // InputSystem.SendKeyDirect 在旧 DLL 上会返回 -1 而静默不发键。
                    ADOFAIMacro.Macro.AsyncInputManager.DirectPushKey(key, true);
                    yield return new WaitForSeconds(0.05f);
                    ADOFAIMacro.Macro.AsyncInputManager.DirectPushKey(key, false);
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
        public static class Patch_scnEditor_Play
        {
            [HarmonyPostfix]
            public static void Postfix(scnEditor __instance)
            {
                if (Main.Settings.Macro)
                {
                    if (Main.Settings.ChangeJudementInPlay)
                        __instance.editorDifficultySelector.SetChangeable(true);
                    if (Main.Settings.ChangeNoFaillInPlay)
                        __instance.buttonNoFail.interactable = true;
                }
            }
        }

        [HarmonyPatch(typeof(scrConductor), "Update")]
        public static class __scrConductor
        {
            // 精确本地时间（与 skyhook 事件时间戳同域，实现见 PreciseNow 的域说明）
            public static unsafe long Update_1()
            {
                return PreciseNow.LocalTicks();
            }

            public static double Update_2() => DSPTimeSimulater.GetDSPTime();

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler_Update(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                if (!Main.Settings.HighPrecisionAsync)
                {
                    return instructions;
                }

                bool patch = false;
                int skip = 0;
                List<CodeInstruction> result = [];

                foreach (CodeInstruction ci in instructions)
                {
                    if (patch)
                    {
                        patch = false;
                        result.Add(new CodeInstruction(OpCodes.Call, typeof(__scrConductor).GetMethod(nameof(Update_1), AccessTools.all)));
                    }
                    if (skip > 0)
                    {
                        skip--;
                        continue;
                    }
                    if (ci.opcode == OpCodes.Call && ((MethodInfo)ci.operand).Name == "get_Now")
                    {
                        skip = 3;
                        patch = true;
                        continue;
                    }
                    if (ci.opcode == OpCodes.Call && ((MethodInfo)ci.operand).Name == "get_dspTime")
                    {
                        ci.operand = typeof(__scrConductor).GetMethod(nameof(Update_2), AccessTools.all);
                    }

                    result.Add(ci);
                }

                return result.AsEnumerable();
            }
        }

        // ─────────────────────────────────────────────
        //  判定误差探针：用游戏自己的换算复现 AddHit 的毫秒误差，
        //  连同 speed 一起记录——用于定位变速段偏移（数据说话）
        // ─────────────────────────────────────────────
        [HarmonyPatch(typeof(scrHitErrorMeter), nameof(scrHitErrorMeter.AddHit))]
        public static class Patch_HitErrorMeter_AddHit
        {
            [HarmonyPostfix]
            public static void Postfix(float angleDiff, float marginScale, scrPlanet planet, scrFloor hitFloor)
            {
                try
                {
                    var conductor = scrConductor.instance;
                    if (conductor == null) return;

                    // 2026-09 游戏 API 变更：AddHit 现在把自身参数【原地】换算成毫秒误差
                    //   angleDiff = angleDiff * -57.29578f;          // 弧度 → 度
                    //   angleDiff = angleDiff * (60.0 / boundary);   // 度 → ms
                    // Harmony 的 postfix 读到的是【改写后】的实参（已用 0Harmony 实测确认：
                    // 被 starg.s 改写的参数，postfix 看到的是新值），因此这里直接取用即可。
                    // 旧实现又乘一次 -57.29578 与 60/boundary，把量纲放大 50~115 倍并翻转
                    // 符号 → 每个样本饱和到 ±40ms → 闭环校准被推向 ±60ms 轨道。
                    float errMs = angleDiff;
                    float? spd = (hitFloor ?? planet?.player?.currFloor?.prevfloor)?.speed;

                    int floorId = hitFloor != null ? hitFloor.seqID
                        : planet?.player?.currFloor?.seqID ?? -1;
                    double nowSpeed = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 0.0;

                    // 方案7：闭环校准——把实测判定误差喂给宏
                    ADOFAIMacro.Macro.Macro.RecordJudgedError(errMs, (float)(spd ?? 1f));

                    // ⚠️ 这里在"每一次判定"上执行。旧实现无条件写一行 UMM 日志：
                    // 高密度谱面每局数千次文件 I/O，是主线程卡顿与日志膨胀的来源。
                    // 改为低频诊断（≥1s 一行，带窗口内样本数与均值），
                    // 与 Macro 中 [Macro-Diag] 的既有做法保持一致。
                    _judgeLogSum += errMs;
                    _judgeLogCount++;
                    int nowMs = Environment.TickCount;
                    if (unchecked(nowMs - _judgeLogLastMs) >= 1000)
                    {
                        double avg = _judgeLogCount > 0 ? _judgeLogSum / _judgeLogCount : 0.0;
                        Main.Mod?.Logger.Log(
                            $"[Macro-Judge] 近1s {_judgeLogCount} 次判定 | 平均误差 {avg:+0.00;-0.00}ms | " +
                            $"最近 floor={floorId} err={errMs:+0.0;-0.0}ms spdUsed={spd ?? 1f:F2} spdNow={nowSpeed:F2} marginScale={marginScale:F2}");
                        _judgeLogSum = 0;
                        _judgeLogCount = 0;
                        _judgeLogLastMs = nowMs;
                    }
                }
                catch { }
            }
        }

        // 解析按键字符串为KeyCode列表
        private static readonly Dictionary<string, KeyCode> KeyCodeAliasMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // 方向键
            {"UP",        KeyCode.UpArrow},
            {"DOWN",      KeyCode.DownArrow},
            {"LEFT",      KeyCode.LeftArrow},
            {"RIGHT",     KeyCode.RightArrow},
            // 修饰键
            {"CTRL",      KeyCode.LeftControl},
            {"LCTRL",     KeyCode.LeftControl},
            {"RCTRL",     KeyCode.RightControl},
            {"ALT",       KeyCode.LeftAlt},
            {"LALT",      KeyCode.LeftAlt},
            {"RALT",      KeyCode.RightAlt},
            {"SHIFT",     KeyCode.LeftShift},
            {"LSHIFT",    KeyCode.LeftShift},
            {"RSHIFT",    KeyCode.RightShift},
            {"WIN",       KeyCode.LeftWindows},
            {"LWIN",      KeyCode.LeftWindows},
            {"RWIN",      KeyCode.RightWindows},
            // 常用键
            {"SPACE",     KeyCode.Space},
            {"ENTER",     KeyCode.Return},
            {"RETURN",    KeyCode.Return},
            {"ESC",       KeyCode.Escape},
            {"ESCAPE",    KeyCode.Escape},
            {"TAB",       KeyCode.Tab},
            {"BACKSPACE", KeyCode.Backspace},
            {"DELETE",    KeyCode.Delete},
            {"DEL",       KeyCode.Delete},
            {"INSERT",    KeyCode.Insert},
            {"INS",       KeyCode.Insert},
            {"HOME",      KeyCode.Home},
            {"END",       KeyCode.End},
            {"PAGEUP",    KeyCode.PageUp},
            {"PGUP",      KeyCode.PageUp},
            {"PAGEDOWN",  KeyCode.PageDown},
            {"PGDN",      KeyCode.PageDown},
            {"CAPSLOCK",  KeyCode.CapsLock},
            {"CAPS",      KeyCode.CapsLock},
            {"NUMLOCK",   KeyCode.Numlock},
            {"SCROLLLOCK",KeyCode.ScrollLock},
            {"PRINTSCREEN",KeyCode.Print},
            {"PAUSE",     KeyCode.Pause},
            // 标点符号
            {";",         KeyCode.Semicolon},
            {"SEMICOLON", KeyCode.Semicolon},
            {"=",         KeyCode.Equals},
            {"EQUALS",    KeyCode.Equals},
            {",",         KeyCode.Comma},
            {"COMMA",     KeyCode.Comma},
            {"-",         KeyCode.Minus},
            {"MINUS",     KeyCode.Minus},
            {".",         KeyCode.Period},
            {"PERIOD",    KeyCode.Period},
            {"/",         KeyCode.Slash},
            {"SLASH",     KeyCode.Slash},
            {"`",         KeyCode.BackQuote},
            {"BACKQUOTE", KeyCode.BackQuote},
            {"[",         KeyCode.LeftBracket},
            {"LBRACKET",  KeyCode.LeftBracket},
            {"\\",        KeyCode.Backslash},
            {"BACKSLASH", KeyCode.Backslash},
            {"]",         KeyCode.RightBracket},
            {"RBRACKET",  KeyCode.RightBracket},
            {"'",         KeyCode.Quote},
            {"QUOTE",     KeyCode.Quote},
            // 小键盘
            {"NUM0",      KeyCode.Keypad0},
            {"NUM1",      KeyCode.Keypad1},
            {"NUM2",      KeyCode.Keypad2},
            {"NUM3",      KeyCode.Keypad3},
            {"NUM4",      KeyCode.Keypad4},
            {"NUM5",      KeyCode.Keypad5},
            {"NUM6",      KeyCode.Keypad6},
            {"NUM7",      KeyCode.Keypad7},
            {"NUM8",      KeyCode.Keypad8},
            {"NUM9",      KeyCode.Keypad9},
            {"NUM.",      KeyCode.KeypadPeriod},
            {"NUM/",      KeyCode.KeypadDivide},
            {"NUM*",      KeyCode.KeypadMultiply},
            {"NUM-",      KeyCode.KeypadMinus},
            {"NUM+",      KeyCode.KeypadPlus},
            {"NUMENTER",  KeyCode.KeypadEnter},
            {"NUM=",      KeyCode.KeypadEquals},
        };
        private static HashSet<KeyCode> ParseKeyCodes(string keyString)
        {
            var result = new HashSet<KeyCode>();
            if (string.IsNullOrEmpty(keyString)) return result;

            string[] keys = keyString.Split([','], StringSplitOptions.RemoveEmptyEntries);
            foreach (string key in keys)
            {
                string trimmedKey = key.Trim().ToUpper();

                // 优先查别名表（处理 UP/DOWN/LEFT/RIGHT 等）
                if (KeyCodeAliasMap.TryGetValue(trimmedKey, out KeyCode aliasCode))
                {
                    result.Add(aliasCode);
                }
                // 再尝试 Unity KeyCode 枚举名
                else if (Enum.TryParse<KeyCode>(trimmedKey, true, out KeyCode keyCode))
                {
                    result.Add(keyCode);
                }
                // 十六进制
                else if (trimmedKey.StartsWith("0X") && int.TryParse(trimmedKey.Substring(2),
                    System.Globalization.NumberStyles.HexNumber, null, out int hexCode))
                {
                    result.Add((KeyCode)hexCode);
                }
            }
            return result;
        }

        // 检查普通按键是否允许通过（位图优化版）
        private static bool IsKeyAllowed(KeyCode keyCode)
        {
            if (!Main.IsEnabled || !Main.Settings.Macro)
                return true; // 如果宏未启用，不过滤任何按键
            if (!Main.Settings.EnableKeyFilter) return true;

            // 需要重建位图？
            if (_keyFilterMap == null ||
                _lastFilteredKeysString != Main.Settings.FilteredKeys ||
                _lastEnableFilter != Main.Settings.EnableKeyFilter ||
                _lastFilterMode != Main.Settings.FilterMode)
            {
                BuildKeyFilterMap();
            }

            int idx = (int)keyCode;
            if (idx >= _keyFilterMap.Length) return true; // 超出范围默认放行

            bool inList = _keyFilterMap[idx];
            // 黑名单模式：在列表中则阻止；白名单模式：在列表中才允许
            return Main.Settings.FilterMode == 0 ? !inList : inList;
        }

        // 构建按键过滤位图（使用 bool[]，默认 false）
        private static void BuildKeyFilterMap()
        {
            _keyFilterMap = new bool[300];
            var parsed = ParseKeyCodes(Main.Settings.FilteredKeys);
            foreach (var kc in parsed)
            {
                int idx = (int)kc;
                if (idx < _keyFilterMap.Length) _keyFilterMap[idx] = true;
            }
            _lastFilteredKeysString = Main.Settings.FilteredKeys;
            _lastEnableFilter = Main.Settings.EnableKeyFilter;
            _lastFilterMode = Main.Settings.FilterMode;
        }

        [HarmonyPatch(typeof(scrController), "Awake")]
        private static class Patch_Awake
        {
            [HarmonyPostfix]
            public static void Postfix(scrController __instance)
            {
                CachedController = __instance;
            }
        }

        // 修改 CountValidKeysPressed 补丁添加黑白名单逻辑
        //
        // ⚠️ 旧实现用 Prefix 返回 false 整体替换了原方法，只重算按键数，
        // 结果丢掉了原方法的一堆副作用（反编译 Steam 版 Assembly-CSharp 逐条核对）：
        //   ① 触屏输入计数分支（touchEnabled / Switch）
        //   ② 联机分支（coopMode → RDInput.playerInputs[playerID]）
        //   ③ downKeysDuration 字典维护（含 0.5s 剪枝、keyLimiterOverCounter 超限计数）
        //   ④ scrController.maximumUsedKeys 更新
        //   ⑤ 仅在 States.PlayerControl 状态下才做记账
        // 这些状态被游戏其它逻辑读取，宏开启期间一直不更新会造成隐藏的行为差异；
        // 而且旧实现对 ADOBase.controller?.chosenPlanet?.player 直接解引用（NRE 风险）。
        //
        // 现在改为：Prefix 只统计"当前按下的键里有多少被过滤"，Postfix 从原方法
        // 的结果里减掉。过滤仍然生效，原方法的全部副作用原样保留。
        [HarmonyPatch(typeof(scrPlayer), "CountValidKeysPressed")]
        public static class scrPlayer_CountValidKeysPressed_Patch
        {
            [ThreadStatic]
            private static int _blockedKeyCount;

            [HarmonyPrefix]
            public static void Prefix()
            {
                _blockedKeyCount = 0;

                if (!Main.IsEnabled || !Main.Settings.Macro || !Main.Settings.EnableKeyFilter)
                    return;

                try
                {
                    foreach (AnyKeyCode anyKeyCode in RDInput.GetMainPressKeys())
                    {
                        object value = anyKeyCode.value;
                        if (value is KeyCode keyCode)
                        {
                            if (!IsKeyAllowed(keyCode))
                            {
                                _blockedKeyCount++;
                                Macro.Macro.Log($"Filtered Key: {keyCode} ({(Main.Settings.FilterMode == 0 ? "Blacklist" : "Whitelist")})");
                            }
                        }
                        else if (value is AsyncKeyCode asyncKeyCode)
                        {
                            if (!IsAsyncKeyAllowed(asyncKeyCode.key))  // 小写 key
                            {
                                _blockedKeyCount++;
                                Macro.Macro.Log($"Filtered Async Key in CountValidKeysPressed: {asyncKeyCode.key} (0x{asyncKeyCode.key:X2})");
                            }
                        }
                    }
                }
                catch { _blockedKeyCount = 0; }
            }

            [HarmonyPostfix]
            public static void Postfix(ref int __result)
            {
                if (_blockedKeyCount > 0)
                    __result = Math.Max(0, __result - _blockedKeyCount);
                _blockedKeyCount = 0;
            }
        }

        private static readonly Dictionary<string, ushort> AsyncKeyVKMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // 字母键
            {"A",0x41},{"B",0x42},{"C",0x43},{"D",0x44},{"E",0x45},{"F",0x46},
            {"G",0x47},{"H",0x48},{"I",0x49},{"J",0x4A},{"K",0x4B},{"L",0x4C},
            {"M",0x4D},{"N",0x4E},{"O",0x4F},{"P",0x50},{"Q",0x51},{"R",0x52},
            {"S",0x53},{"T",0x54},{"U",0x55},{"V",0x56},{"W",0x57},{"X",0x58},
            {"Y",0x59},{"Z",0x5A},
            // 数字键
            {"0",0x30},{"1",0x31},{"2",0x32},{"3",0x33},{"4",0x34},
            {"5",0x35},{"6",0x36},{"7",0x37},{"8",0x38},{"9",0x39},
            // 功能键
            {"F1",0x70},{"F2",0x71},{"F3",0x72},{"F4",0x73},{"F5",0x74},
            {"F6",0x75},{"F7",0x76},{"F8",0x77},{"F9",0x78},{"F10",0x79},
            {"F11",0x7A},{"F12",0x7B},{"F13",0x7C},{"F14",0x7D},{"F15",0x7E},
            {"F16",0x7F},{"F17",0x80},{"F18",0x81},{"F19",0x82},{"F20",0x83},
            {"F21",0x84},{"F22",0x85},{"F23",0x86},{"F24",0x87},
            // 方向键
            {"UP",0x26},{"DOWN",0x28},{"LEFT",0x25},{"RIGHT",0x27},
            // 常用键
            {"SPACE",     0x20},
            {"ENTER",     0x0D},{"RETURN",    0x0D},
            {"ESC",       0x1B},{"ESCAPE",    0x1B},
            {"TAB",       0x09},
            {"BACKSPACE", 0x08},
            {"DELETE",    0x2E},{"DEL",       0x2E},
            {"INSERT",    0x2D},{"INS",       0x2D},
            {"HOME",      0x24},
            {"END",       0x23},
            {"PAGEUP",    0x21},{"PGUP",      0x21},
            {"PAGEDOWN",  0x22},{"PGDN",      0x22},
            {"CAPSLOCK",  0x14},{"CAPS",      0x14},
            {"NUMLOCK",   0x90},
            {"SCROLLLOCK",0x91},
            {"PRINTSCREEN",0x2C},
            {"PAUSE",     0x13},
            // 修饰键
            {"SHIFT",     0x10},
            {"LSHIFT",    0xA0},{"RSHIFT",    0xA1},
            {"CTRL",      0x11},
            {"LCTRL",     0xA2},{"RCTRL",     0xA3},
            {"ALT",       0x12},
            {"LALT",      0xA4},{"RALT",      0xA5},
            {"WIN",       0x5B},{"LWIN",      0x5B},{"RWIN",      0x5C},
            // 标点符号
            {";",         0xBA},{"SEMICOLON", 0xBA},
            {"=",         0xBB},{"EQUALS",    0xBB},
            {",",         0xBC},{"COMMA",     0xBC},
            {"-",         0xBD},{"MINUS",     0xBD},
            {".",         0xBE},{"PERIOD",    0xBE},
            {"/",         0xBF},{"SLASH",     0xBF},
            {"`",         0xC0},{"BACKQUOTE", 0xC0},
            {"[",         0xDB},{"LBRACKET",  0xDB},
            {"\\",        0xDC},{"BACKSLASH", 0xDC},
            {"]",         0xDD},{"RBRACKET",  0xDD},
            {"'",         0xDE},{"QUOTE",     0xDE},
            // 小键盘
            {"NUM0",      0x60},{"NUM1",      0x61},{"NUM2",      0x62},
            {"NUM3",      0x63},{"NUM4",      0x64},{"NUM5",      0x65},
            {"NUM6",      0x66},{"NUM7",      0x67},{"NUM8",      0x68},
            {"NUM9",      0x69},
            {"NUM*",      0x6A},{"NUM+",      0x6B},{"NUM-",      0x6D},
            {"NUM.",      0x6E},{"NUM/",      0x6F},{"NUMENTER",  0x0D},
            // 媒体键
            {"MUTE",      0xAD},
            {"VOLUMEDOWN",0xAE},{"VOLDOWN",   0xAE},
            {"VOLUMEUP",  0xAF},{"VOLUP",     0xAF},
            {"MEDIANEXT", 0xB0},
            {"MEDIAPREV", 0xB1},
            {"MEDIASTOP", 0xB2},
            {"MEDIAPLAY", 0xB3},
        };

        private static HashSet<ushort> ParseAsyncKeyCodes(string keyString)
        {
            var result = new HashSet<ushort>();
            if (string.IsNullOrEmpty(keyString)) return result;

            string[] keys = keyString.Split([','], StringSplitOptions.RemoveEmptyEntries);
            foreach (string key in keys)
            {
                string trimmedKey = key.Trim().ToUpper();

                // 优先查 VK 映射表
                if (AsyncKeyVKMap.TryGetValue(trimmedKey, out ushort vkCode))
                {
                    result.Add(vkCode);
                    Macro.Macro.Log($"Parsed async key: {trimmedKey} -> VK 0x{vkCode:X2}");
                }
                // 十六进制 (0x26 格式)
                else if (trimmedKey.StartsWith("0X") && ushort.TryParse(trimmedKey.Substring(2),
                    System.Globalization.NumberStyles.HexNumber, null, out ushort hexCode))
                {
                    result.Add(hexCode);
                    Macro.Macro.Log($"Parsed async hex: {trimmedKey} -> 0x{hexCode:X2}");
                }
                // 纯数字
                else if (ushort.TryParse(trimmedKey, out ushort numCode))
                {
                    result.Add(numCode);
                    Macro.Macro.Log($"Parsed async number: {trimmedKey} -> 0x{numCode:X2}");
                }
                else
                {
                    Macro.Macro.Log($"Failed to parse async key: {trimmedKey}");
                }
            }
            return result;
        }

        // 检查异步按键是否允许通过（位图优化版）
        private static bool IsAsyncKeyAllowed(ushort keyCode)
        {
            if (!Main.IsEnabled || !Main.Settings.Macro)
                return true; // 如果宏未启用，不过滤任何按键
            if (!Main.Settings.EnableKeyFilter) return true;

            // 需要重建位图？
            if (_asyncKeyFilterMap == null ||
                _lastFilteredAsyncKeysString != Main.Settings.FilteredAsyncKeys ||
                _lastAsyncEnableFilter != Main.Settings.EnableKeyFilter ||
                _lastAsyncFilterMode != Main.Settings.FilterMode)
            {
                BuildAsyncKeyFilterMap();
            }

            if (keyCode >= 256) return true; // 超出范围默认放行
            bool inList = _asyncKeyFilterMap[keyCode];
            // 黑名单模式：在列表中则阻止；白名单模式：在列表中才允许
            return Main.Settings.FilterMode == 0 ? !inList : inList;
        }

        // 构建异步按键过滤位图（使用 bool[]，默认 false）
        private static void BuildAsyncKeyFilterMap()
        {
            _asyncKeyFilterMap = new bool[256];
            var parsed = ParseAsyncKeyCodes(Main.Settings.FilteredAsyncKeys);
            foreach (var k in parsed)
            {
                if (k < 256) _asyncKeyFilterMap[k] = true;
            }
            _lastFilteredAsyncKeysString = Main.Settings.FilteredAsyncKeys;
            _lastAsyncEnableFilter = Main.Settings.EnableKeyFilter;
            _lastAsyncFilterMode = Main.Settings.FilterMode;
        }

        // 修改 SkyHook 按键过滤补丁
        [HarmonyPatch(typeof(SkyHookManager), "HookCallback")]
        public static class SkyHookManager_HookCallback_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(SkyHookEvent ev)
            {
                // 0. 镜像回声丢弃：虚拟按键直喂成功后注入的显示用真实按键
                //    会经钩子回流，配额命中即丢弃（虚拟直喂走 KeyUpdated.Invoke，
                //    不经过这里，不受影响）——不丢会导致同一击打判定两次
                if (Macro.VirtualAsyncInput.ShouldDropMirrorEcho(ev.Key, ev.Type))
                    return false;

                // 1. 基本检查
                if (!Application.isPlaying) return true;

                // 2. 从弱引用安全获取控制器（防止销毁后访问）
                scrController ctrl = CachedController;
                if (ctrl == null) return true;          // 未初始化或已销毁

                // 3. 额外检查对象是否已销毁（双重保险）
                if (!ctrl || !ctrl.gameObject.activeInHierarchy) return true;

                // 4. 无条件放行
                if (ev.Type == SkyHook.EventType.KeyReleased || ev.Key == 27) return true;

                // 5. 功能过滤条件
                if (!Main.Settings.EnableKeyFilter) return true;
                if (ctrl.paused || !ctrl.gameworld) return true;

                // 6. 状态检查
                if (!(ctrl.stateMachine.GetState() is States s && s == States.PlayerControl))
                    return true;

                bool allowed = IsAsyncKeyAllowed(ev.Key);
                if (!allowed)
                    Macro.Macro.Log($"Filtered Async Key: {ev.Label} ({ev.Key}) - {(Main.Settings.FilterMode == 0 ? "Blacklist" : "Whitelist")}");

                return allowed;
            }
        }
    }
}