using ADOFAIMacro.Platform;
using ADOFAIMacro.Technique;
using ADOFAIMacro.Input;
using ADOFAIMacro.Timing;
using HarmonyLib;
using SkyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Settings = global::ADOFAIMacro.Settings.Settings;

namespace ADOFAIMacro.Core
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

        // ─────────────────────────────────────────────
        //  补丁入口异常防护
        // ─────────────────────────────────────────────
        // Harmony 前缀/后缀直接插在游戏方法上，异常一旦逃出去就会抛进
        // scrController 自己的调用栈 —— PlayerControl_Update 是**每帧**跑的，
        // 一个没兜住的 NRE 会变成每帧一条日志的刷屏，而且可能连带打断游戏
        // 这一帧的其余逻辑。
        //
        // 所以补丁入口一律走 Guarded()：捕获 + 记日志 + 吞掉。
        // 宏自己的状态在异常时通常已经自洽（事件表没建成就没建，initialized
        // 仍是 false，下一帧重试），吞掉比让游戏炸掉安全。
        //
        // 日志要节流：同一个异常每帧都发生时，只记前 3 条，之后每 10 秒记一条
        // 「仍在发生」，否则防护本身又成了刷屏源。
        private const int GuardLogHead = 3;      // 前 N 条逐条记
        private const double GuardLogEverySec = 10; // 之后每 N 秒记一条
        private static readonly Dictionary<string, int> _guardCount = new();
        private static readonly Dictionary<string, int> _guardLastMs = new();

        private static void Guarded(string tag, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    int n = _guardCount.TryGetValue(tag, out int c) ? c + 1 : 1;
                    _guardCount[tag] = n;

                    bool log = n <= GuardLogHead;
                    if (!log)
                    {
                        int now = Environment.TickCount;
                        if (_guardLastMs.TryGetValue(tag, out int last) &&
                            unchecked(now - last) < (int)(GuardLogEverySec * 1000))
                        {
                            return;
                        }
                        _guardLastMs[tag] = now;
                    }
                    Main.Log($"[Patches] {tag} 异常（第 {n} 次，已拦截）: {ex.GetType().Name}: {ex.Message}");
                }
                catch
                {
                    // 连记日志都失败就彻底放弃，绝不把异常抛回游戏
                }
            }
        }

        /// <summary>重置异常计数（关卡切换/宏启停时调用，避免旧计数掩盖新问题）。</summary>
        public static void ResetGuardCounters()
        {
            _guardCount.Clear();
            _guardLastMs.Clear();
        }

        // ── 【2026-10-12 MacroPatch 第二层防护 · 内联 catch 上报口】──
        // 历史：MacroPatchHub 原计划用 Reflection.Emit 生成 try/catch 包装（TryingCatch），
        // 但 Unity Mono 运行时对 DynamicMethod 的反射面太窄，两层实现（直挂/补丁工厂）
        // 都在实机被拒——详见 MacroPatchHub「异常兜底」注释块。
        // 现方案：防护内联在补丁方法体自身（本文件各 Patch_* 方法的外层 try/catch），
        // catch 里调 ReportToHub 上报 → Hub 的 30s 窗口限流 + DisableAfterExceptions 自动卸载。
        // ★ 上报自身绝不允许抛（否则防护变凶器）。
        private static void ReportToHub(string patchMethodName, string targetName, Exception ex)
        {
            try
            {
                MacroPatchHub.NoteGuardException("MacroPatch:Patches." + patchMethodName,
                    nameof(Patches), patchMethodName, targetName, ex);
            }
            catch { /* 上报失败就放弃，绝不抛回游戏 */ }
        }

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

        // ── 【2026-10-11 MacroPatch 迁移】13 个补丁全部改为 [MacroPatch] 声明，──
        //  由 MacroPatchHub 统一扫描/解析/挂载（候选链/版本门控/异常兜底/探针）。
        //  迁移对照：
        //    [HarmonyPatch(typeof(X), "M")] + [HarmonyPrefix] void Prefix(...)
        //  ⇒ [MacroPatch(typeof(X), "M", PatchType.Prefix)] static void Prefix(...)
        //  裸字符串目标全部加了 MethodNames/ClassNames 候选链（新名在前旧名兜底）；
        //  原有的 Guarded() 调用保留（方法体内首个防护层），MacroPatchHub 的
        //  TryingCatch 是第二层（补丁体抛出的一切异常，含 Guarded 自身的）。
        //  挂载入口从 Harmony.PatchAll 改为 Main.OnToggle 里的
        //  MacroPatchHub.Initialize + ApplyAll。

        [MacroPatch(typeof(scrController), "PlayerControl_Update", PatchType.Prefix,
            MethodNames = new[] { "PlayerControl_Update" })]
        public static void Patch_PlayerControl_Update_Prefix(scrController __instance)
        {
            try
            {
                Guarded("PlayerControl_Update", () => MacroEngine.Update(__instance));
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_PlayerControl_Update_Prefix), "scrController.PlayerControl_Update", ex); }
        }

        [MacroPatch(typeof(scrController), nameof(scrController.Awake_Rewind), PatchType.Postfix)]
        public static void Patch_Awake_Rewind_Postfix(scrController __instance)
        {
            try
            {
                Guarded("Awake_Rewind", () =>
                {
                    MacroEngine.Reset(__instance);
                    // 关卡重置时，尝试加载关卡特定配置
                    if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                    {
                        LevelTechniqueManager.ResetCheckState();
                        LevelTechniqueManager.CheckAndLoadLevelConfig();
                    }
                });
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_Awake_Rewind_Postfix), "scrController.Awake_Rewind", ex); }
        }

        [MacroPatch(typeof(scrController), nameof(scrController.Restart), PatchType.Prefix)]
        public static void Patch_Restart_Prefix(scrController __instance)
        {
            try
            {
                Guarded("Restart", () =>
                {
                    MacroEngine.Reset(__instance);
                    // 关卡重启时，尝试加载关卡特定配置
                    if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                    {
                        LevelTechniqueManager.ResetCheckState();
                        LevelTechniqueManager.CheckAndLoadLevelConfig();
                    }
                });
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_Restart_Prefix), "scrController.Restart", ex); }
        }

        // 关卡加载完成时也检测一次
        [MacroPatch(typeof(scnGame), "LoadAndPlayLevel", PatchType.Postfix,
            ParameterTypes = new[] { typeof(bool), typeof(string) })]
        public static void Patch_scnGame_LoadAndPlayLevel_Postfix(bool __result, string levelPath)
        {
            try
            {
                if (!__result) return;

                if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation && Main.Settings.LevelConfigAutoLoad)
                {
                    LevelTechniqueManager.ResetCheckState();
                    LevelTechniqueManager.CheckAndLoadLevelConfig();
                }

                // 轨迹播放器：加载期预热（21:20 前的功能，回退时被误删，已恢复）。
                // 此刻（scnGame.LoadAndPlayLevel 尾部）整首谱的地板与每块砖的
                // entryTime 都已经算好（MakeLevel → ApplyEventsToFloors →
                // CalculateFloorEntryTimes），而 conductor 尚未 Start()。
                // 所以这里就能把最优轨迹求解出来并落盘 —— 进关按下开始之前
                // 就已就绪，不必"先玩一次"。
                if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation)
                {
                    Guarded("TracePrewarm", () => MacroEngine.PrewarmTrace());
                }
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_scnGame_LoadAndPlayLevel_Postfix), "scnGame.LoadAndPlayLevel", ex); }
        }

        [MacroPatch(typeof(scnEditor), "Start", PatchType.Postfix)]
        public static void Patch_scnEditor_Start_Postfix(scnEditor __instance)
        {
            try
            {
                if (Main.Settings.LockLevelEditor)
                    __instance.LockPathEditing(true);
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_scnEditor_Start_Postfix), "scnEditor.Start", ex); }
        }

        [MacroPatch(typeof(scrController), "Fail2Action", PatchType.Postfix)]
        public static void Patch_FailAction_Postfix()
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
                // 同时登记过滤放行配额（该注入同样会回流到 HookCallback）。
                if (Main.Settings.EnableKeyFilter) VirtualAsyncInput.RegisterInjectedKey(key, true);
                Input.AsyncInputManager.DirectPushKey(key, true);
                yield return new WaitForSeconds(0.05f);
                if (Main.Settings.EnableKeyFilter) VirtualAsyncInput.RegisterInjectedKey(key, false);
                Input.AsyncInputManager.DirectPushKey(key, false);
            }
        }

        [MacroPatch(typeof(scnEditor), nameof(scnEditor.Play), PatchType.Postfix)]
        public static void Patch_scnEditor_Play_Postfix(scnEditor __instance)
        {
            try
            {
                if (Main.Settings.Macro)
                {
                    if (Main.Settings.ChangeJudementInPlay)
                        __instance.editorDifficultySelector.SetChangeable(true);
                    if (Main.Settings.ChangeNoFaillInPlay)
                        __instance.buttonNoFail.interactable = true;
                }

                // 轨迹播放器预热（编辑器试玩路径）（21:20 前的功能，回退时被误删，已恢复）。
                // 编辑器试玩不走 scnGame.LoadAndPlayLevel，所以上面那个补丁挂不上。
                // 但 scnEditor.Play() 内部会跑 customLevel.Play(num) →
                // FinishCustomLevelLoading() → ApplyEventsToFloors()（试玩时 remakeFloors=true），
                // 返回到这里时地板与 entryTime 已就绪，同样是"按下开始之前"。
                if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation)
                {
                    Guarded("TracePrewarmEditor", () => MacroEngine.PrewarmTrace());
                }
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_scnEditor_Play_Postfix), "scnEditor.Play", ex); }
        }

        // Transpiler 不能走 [MacroPatch]（其方法签名是 instructions 流，不是游戏方法；
        // MacroPatch 的重载消歧/守卫包装对它无意义）。保留原生 [HarmonyPatch]，
        // 但把两个加固做上：①"get_Now"/"get_dspTime" 按属性 getter 精确引用匹配
        //（不再按名字裸匹配——"get_Now" 会命中任何类的 DateTime.Now）；
        // ②替换数为 0 时打探针日志（IL 形状变了 → skip=3 假设失效）。
        [HarmonyPatch(typeof(scrConductor), "Update")]
        public static class __scrConductor
        {
            // 精确本地时间（与 skyhook 事件时间戳同域，实现见 PreciseNow 的域说明）
            public static unsafe long Update_1()
            {
                return PreciseNow.LocalTicks();
            }

            public static double Update_2() => DSPTimeSimulater.GetDSPTime();

            private static readonly MethodInfo DateTimeNowGetter =
                AccessTools.PropertyGetter(typeof(DateTime), nameof(DateTime.Now));
            private static readonly MethodInfo AudioDspTimeGetter =
                AccessTools.PropertyGetter(typeof(AudioSettings), nameof(AudioSettings.dspTime));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler_Update(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                if (!Main.Settings.HighPrecisionAsync)
                {
                    return instructions;
                }

                bool patch = false;
                int skip = 0;
                int replacedNow = 0;
                int replacedDsp = 0;
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
                    if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo m1 &&
                        (m1 == DateTimeNowGetter || (m1.DeclaringType == typeof(DateTime) && m1.Name == "get_Now")))
                    {
                        skip = 3;
                        patch = true;
                        replacedNow++;
                        continue;
                    }
                    if (ci.opcode == OpCodes.Call && ci.operand is MethodInfo m2 &&
                        (m2 == AudioDspTimeGetter || (m2.DeclaringType == typeof(AudioSettings) && m2.Name == "get_dspTime")))
                    {
                        ci.operand = typeof(__scrConductor).GetMethod(nameof(Update_2), AccessTools.all);
                        replacedDsp++;
                    }

                    result.Add(ci);
                }

                // 探针：替换数为 0 = 游戏 Update 的 IL 形状变了（skip=3 假设失效），
                // HighPrecisionAsync 将静默不生效 —— 必须报出来而不是无声跳过。
                if (replacedNow == 0 && replacedDsp == 0)
                    Main.Log("[MacroPatch] scrConductor.Update Transpiler 未找到任何时间源调用"
                        + "（游戏 Update 可能已变更，[实验性]启用高精度异步将不生效）");
                else
                    Main.Log($"[MacroPatch] scrConductor.Update Transpiler: get_Now×{replacedNow}, get_dspTime×{replacedDsp}");

                return result.AsEnumerable();
            }
        }

        // ─────────────────────────────────────────────
        //  判定误差探针：用游戏自己的换算复现 AddHit 的毫秒误差，
        //  连同 speed 一起记录——用于定位变速段偏移（数据说话）
        // ─────────────────────────────────────────────
        // r150 的 AddHit(原改写后实参直接可用)——参数名匹配消歧（按名绑定）。
        [MacroPatch(typeof(scrHitErrorMeter), nameof(scrHitErrorMeter.AddHit), PatchType.Postfix,
            ParameterTypes = new[] { typeof(float), typeof(HitMargin), typeof(float), typeof(scrPlanet), typeof(scrFloor) })]
        public static void Patch_HitErrorMeter_AddHit_Postfix(float angleDiff, float marginScale, scrPlanet planet, scrFloor hitFloor)
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
                ADOFAIMacro.Technique.MacroEngine.RecordJudgedError(errMs, (float)(spd ?? 1f));

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
                    Main.Log(
                        $"[Macro-Judge] 近1s {_judgeLogCount} 次判定 | 平均误差 {avg:+0.00;-0.00}ms | " +
                        $"最近 floor={floorId} err={errMs:+0.0;-0.0}ms spdUsed={spd ?? 1f:F2} spdNow={nowSpeed:F2} marginScale={marginScale:F2}");
                    _judgeLogSum = 0;
                    _judgeLogCount = 0;
                    _judgeLogLastMs = nowMs;
                }
            }
            catch (Exception ex)
            {
                // 内层 catch 原本就吞；这里补上报（低频：AddHit 每次判定都跑，靠 Hub 的 30s 窗口限流压日志）
                ReportToHub(nameof(Patch_HitErrorMeter_AddHit_Postfix), "scrHitErrorMeter.AddHit", ex);
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

        [MacroPatch(typeof(scrController), "Awake", PatchType.Postfix)]
        public static void Patch_Awake_Postfix(scrController __instance)
        {
            try
            {
                CachedController = __instance;
            }
            catch (Exception ex) { ReportToHub(nameof(Patch_Awake_Postfix), "scrController.Awake", ex); }
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
        [MacroPatch(typeof(scrPlayer), "CountValidKeysPressed", PatchType.Prefix)]
        public static void scrPlayer_CountValidKeysPressed_Prefix()
        {
            _blockedKeyCount = 0;

            if (!Main.IsEnabled || !Main.Settings.Macro || !Main.Settings.EnableKeyFilter)
                return;

            try
            {
                // 游戏 2026-10-19 更新后签名变成 void GetMainPressKeys(List<AnyKeyCode> keys)：
                // 由「返回新建 List」改为「填充调用方传入的 List」（省掉每次分配）。
                // 两版签名都保留：运行时按实际存在的方法走，r149 及以下用无参版，
                // r150+ 用填充版。这里复用线程静态缓冲，避免在每帧的 Prefix 里分配。
                List<AnyKeyCode> pressKeys = GetMainPressKeysCompat();

                foreach (AnyKeyCode anyKeyCode in pressKeys)
                {
                    object value = anyKeyCode.value;
                    if (value is KeyCode keyCode)
                    {
                        if (!IsKeyAllowed(keyCode))
                        {
                            _blockedKeyCount++;
                            MacroEngine.Log($"Filtered Key: {keyCode} ({(Main.Settings.FilterMode == 0 ? "Blacklist" : "Whitelist")})");
                        }
                    }
                    else if (value is AsyncKeyCode asyncKeyCode)
                    {
                        if (!IsAsyncKeyAllowed(asyncKeyCode.key))  // 小写 key
                        {
                            _blockedKeyCount++;
                            MacroEngine.Log($"Filtered Async Key in CountValidKeysPressed: {asyncKeyCode.key} (0x{asyncKeyCode.key:X2})");
                        }
                    }
                }
            }
            catch { _blockedKeyCount = 0; }
        }

        [MacroPatch(typeof(scrPlayer), "CountValidKeysPressed", PatchType.Postfix)]
        public static void scrPlayer_CountValidKeysPressed_Postfix(ref int __result)
        {
            try
            {
                if (_blockedKeyCount > 0)
                    __result = Math.Max(0, __result - _blockedKeyCount);
                _blockedKeyCount = 0;
            }
            catch (Exception ex) { ReportToHub(nameof(scrPlayer_CountValidKeysPressed_Postfix), "scrPlayer.CountValidKeysPressed", ex); }
        }

        [ThreadStatic]
        private static int _blockedKeyCount;

        // CountValidKeysPressed 的 Prefix 每帧复用，避免每次调用都分配 List。
        [ThreadStatic]
        private static List<AnyKeyCode> _pressKeysBuffer;

        // 跨版本兼容层：游戏 2026-10-19 把 RDInput.GetMainPressKeys 从
        //   List<AnyKeyCode> GetMainPressKeys()
        // 改成
        //   void GetMainPressKeys(List<AnyKeyCode> keys)
        // 两版 releaseNumber 都报 150，没法用版本号门控，只能运行时按方法形态分发。
        // 反射只解析一次，之后缓存委托，热路径零反射。
        private static Func<List<AnyKeyCode>> _getMainPressKeysLegacy;
        private static Action<List<AnyKeyCode>> _getMainPressKeysFill;
        private static bool _getMainPressKeysResolved;

        private static List<AnyKeyCode> GetMainPressKeysCompat()
        {
            if (!_getMainPressKeysResolved)
            {
                _getMainPressKeysResolved = true;
                try
                {
                    MethodInfo fill = typeof(RDInput).GetMethod("GetMainPressKeys",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(List<AnyKeyCode>) }, null);
                    if (fill != null)
                    {
                        _getMainPressKeysFill = (Action<List<AnyKeyCode>>)Delegate.CreateDelegate(
                            typeof(Action<List<AnyKeyCode>>), fill);
                    }
                    MethodInfo legacy = typeof(RDInput).GetMethod("GetMainPressKeys",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (legacy != null)
                        _getMainPressKeysLegacy = (Func<List<AnyKeyCode>>)Delegate.CreateDelegate(
                            typeof(Func<List<AnyKeyCode>>), legacy);
                }
                catch (Exception ex)
                {
                    ReportToHub(nameof(GetMainPressKeysCompat), "RDInput.GetMainPressKeys", ex);
                }
            }

            if (_getMainPressKeysFill != null)
            {
                List<AnyKeyCode> buffer = _pressKeysBuffer ?? (_pressKeysBuffer = new List<AnyKeyCode>(16));
                buffer.Clear();
                _getMainPressKeysFill(buffer);
                return buffer;
            }
            return _getMainPressKeysLegacy?.Invoke() ?? new List<AnyKeyCode>();
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
                    MacroEngine.Log($"Parsed async key: {trimmedKey} -> VK 0x{vkCode:X2}");
                }
                // 十六进制 (0x26 格式)
                else if (trimmedKey.StartsWith("0X") && ushort.TryParse(trimmedKey.Substring(2),
                    System.Globalization.NumberStyles.HexNumber, null, out ushort hexCode))
                {
                    result.Add(hexCode);
                    MacroEngine.Log($"Parsed async hex: {trimmedKey} -> 0x{hexCode:X2}");
                }
                // 纯数字
                else if (ushort.TryParse(trimmedKey, out ushort numCode))
                {
                    result.Add(numCode);
                    MacroEngine.Log($"Parsed async number: {trimmedKey} -> 0x{numCode:X2}");
                }
                else
                {
                    MacroEngine.Log($"Failed to parse async key: {trimmedKey}");
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
        // ★ 此前本方法完全裸奔（每个按键事件都会执行，一帧 NRE = 每帧刷日志）——
        //   防护 = 方法体内联 try/catch（2026-10-12 起：Unity Mono 不支持 DynamicMethod
        //   参数反射，Hub 的 TryingCatch 包装路线废弃，改由各补丁自带 catch）。
        //   ★ 本补丁是 Prefix：异常时返回 true = 放行按键（宁多判定不丢键）。
        [MacroPatch(typeof(SkyHookManager), "HookCallback", PatchType.Prefix,
            ParameterTypes = new[] { typeof(SkyHookEvent) })]
        public static bool SkyHookManager_HookCallback_Prefix(SkyHookEvent ev)
        {
            try
            {
                return SkyHookManager_HookCallback_Impl(ev);
            }
            catch (Exception ex)
            {
                ReportToHub(nameof(SkyHookManager_HookCallback_Prefix), "SkyHookManager.HookCallback", ex);
                return true; // 异常 = 不过滤，放行（漏过滤比丢键安全）
            }
        }

        private static bool SkyHookManager_HookCallback_Impl(SkyHookEvent ev)
        {
            // 0. 镜像回声丢弃：虚拟按键直喂成功后注入的显示用真实按键
            //    会经钩子回流，配额命中即丢弃（虚拟直喂走 KeyUpdated.Invoke，
            //    不经过这里，不受影响）——不丢会导致同一击打判定两次
            if (VirtualAsyncInput.ShouldDropMirrorEcho(ev.Key, ev.Type))
                return false;

            // 0.5 宏自己注入的键：放行并跳过按键过滤。过滤的语义是拦玩家输入，
            //     不是拦宏的输出 —— 白名单模式下否则会把宏用的键全部过滤掉。
            if (VirtualAsyncInput.ConsumeInjectedKey(ev.Key, ev.Type))
                return true;

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
                MacroEngine.Log($"Filtered Async Key: {ev.Label} ({ev.Key}) - {(Main.Settings.FilterMode == 0 ? "Blacklist" : "Whitelist")}");

            return allowed;
        }

        // ─────────────────────────────────────────────
        //  放行游戏硬编码过滤掉的四个特殊按键
        // ─────────────────────────────────────────────
        // RDInputType_AsyncKeyboard.GetSpecialInput() 无条件把 SpecialKeys
        // （PrintScreen / F12 / LAlt / Super）里当前按下的键收进"特殊输入"表，
        // Main(ButtonState.WentDown) 随后用这张表把这些键从本帧的普通按键
        // 候选里 RemoveWhere 掉（RDInputType_AsyncKeyboard.cs:216-222）。
        // 结果：这四个键在异步输入下**永远进不了判定** —— 宏拿其中之一当
        // 触发键时表现为"按了没反应"。
        //
        // 这四个键在游戏里没有任何玩法用途（LAlt 只被读作 LShift，
        // PrintScreen / F12 / Super 无绑定），直接放行；其余特殊键
        // （Escape / 暂停键 / 方向键 / 关卡选择 / CLS）一律保持原样。
        // 目标是**私有**方法 —— typeof 直引不检查可见性，Harmony 可挂私有。
        //
        // 版本分叉（r149 为界）：
        //   r149 及以下：List<AsyncKeyCode> GetSpecialInput()  —— 无参，Postfix 取 __result
        //   r150 及以上：void GetSpecialInput(List<AsyncKeyCode> keys) —— 填充式，处理入参
        // 两条补丁按 MinVersion/MaxVersion 互斥，只有一条会挂载。
        [MacroPatch(typeof(RDInputType_AsyncKeyboard), "GetSpecialInput", PatchType.Postfix,
            ParameterTypes = new Type[0], MaxVersion = 149)]
        public static void AsyncKeyboard_GetSpecialInput_Legacy_Postfix(List<AsyncKeyCode> __result)
        {
            try
            {
                if (__result == null || __result.Count == 0) return;
                __result.RemoveAll(k => Array.IndexOf(PassThroughKeys, k.label) >= 0);
            }
            catch (Exception ex) { ReportToHub(nameof(AsyncKeyboard_GetSpecialInput_Legacy_Postfix), "RDInputType_AsyncKeyboard.GetSpecialInput", ex); }
        }

        // r150+：方法变成 void 之后 Postfix 拿不到 __result（Harmony 会拒绝挂载：
        // "Cannot get result from void method"），改为直接处理入参列表；
        // ParameterTypes 必须写出该签名，否则重载消歧拿不到唯一目标。
        [MacroPatch(typeof(RDInputType_AsyncKeyboard), "GetSpecialInput", PatchType.Postfix,
            ParameterTypes = new[] { typeof(List<AsyncKeyCode>) }, MinVersion = 150)]
        public static void AsyncKeyboard_GetSpecialInput_Postfix(List<AsyncKeyCode> keys)
        {
            try
            {
                if (keys == null || keys.Count == 0) return;
                keys.RemoveAll(k => Array.IndexOf(PassThroughKeys, k.label) >= 0);
            }
            catch (Exception ex) { ReportToHub(nameof(AsyncKeyboard_GetSpecialInput_Postfix), "RDInputType_AsyncKeyboard.GetSpecialInput", ex); }
        }

        private static readonly KeyLabel[] PassThroughKeys =
        {
            KeyLabel.PrintScreen, KeyLabel.F12, KeyLabel.LAlt, KeyLabel.Super
        };
    }
}