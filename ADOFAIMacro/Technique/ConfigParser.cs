using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Input;
using Settings = ADOFAIMacro.Settings.Settings;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 配置解析：把设置里写的字符串（"R,3,2,Q"、"1|2,1|3,2"、"0.8,1,1.2"）
    /// 解析成数组，按「分段配置」算出某块地板实际生效的按键/顺序/时长，
    /// 并把速率折叠成建议 BPM。
    ///
    /// 纯解析，不涉及任何出手时序。
    /// </summary>
    internal static partial class MacroEngine
    {
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
                // 统一解析：单字符 / 键名 / 十六进制 0xNN（旧实现丢弃 0xNN）
                if (KeyMap.TryParse(part, out byte code)) result.Add(code);
            }
            return result.Count == 0 ? [0x4A] : [.. result];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && _keyCodesSnapshot.Length > 0) return;

            lastKeysSetting = keysSetting;
            var newList = new List<byte>(4);
            foreach (string part in keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                // KeyMap.TryParse 统一处理 单字符 / 键名 / 十六进制 0xNN
                if (KeyMap.TryParse(part, out byte code)) newList.Add(code);
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

        /// <summary>
        /// 把 requireHolding 还给游戏自身语义。
        /// 游戏在 scrController.SetupImportantVariables（进关）与 UpdateSetting（改设置）
        /// 里都写入 `Persistence.holdBehavior &lt; HoldBehavior.NoHoldNeeded`；
        /// 而 ApplyHoldBehavior 在"直接判定"模式下会把它强制为 false。
        /// 旧实现关闭宏时从不还原 —— 后果是长按地板在整个关卡里一直保持
        /// "不需要按住"，与玩家设置的长按判定不符，直到下次进关或改设置。
        /// </summary>
        internal static void RestoreHoldBehavior()
        {
            var controller = ADOBase.controller;
            if (controller == null) return;
            try
            {
                bool gameValue = Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
                if (controller.requireHolding != gameValue) controller.requireHolding = gameValue;
            }
            catch { }
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
            // 防御死循环：limit ≤ 0 时下面第二个折叠循环 `bpm <= limit/2` 恒真
            // （bpm 被反复乘 2 仍是 0）；speed 缺失（controller 未就绪）时 bpm 折叠到 0
            // 同样死循环。关卡配置是磁盘上的 JSON，bpmLimit 可能被手工改成 0 或负数。
            if (limit <= 0.0) limit = 500.0;
            double speed = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 0.0;
            double bpm = (double)(conductor!.bpm * speed);
            if (bpm <= 0.0) return limit;
            while (bpm > limit) bpm /= 2.0;
            while (bpm <= limit / 2.0) bpm *= 2.0;
            return bpm;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetAdviceBpm() => GetAdviceBpm(Main.Settings.TechniqueBpmLimit);

        /// <summary>
        /// 按**指定速率倍率**折叠建议 BPM —— 对应原生的 GetAdviceBpm(bpm, rate, limit)。
        /// 把 bpm×rate 按 2 的幂折叠到 (limit/2, limit]。
        /// 上面那个重载只能用全局 speed，等价于 rate = planetarySystem.speed。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetAdviceBpm(double limit, double rate)
        {
            if (limit <= 0.0) limit = 500.0;
            double bpm = (double)(conductor!.bpm * rate);
            if (bpm <= 0.0) return limit;
            while (bpm > limit) bpm /= 2.0;
            while (bpm <= limit / 2.0) bpm *= 2.0;
            return bpm;
        }

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
    }
}
