using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ADOFAIMacro.Core;
using ADOFAIMacro.Input;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>Parses input settings and snapshots the effective technique configuration.</summary>
    internal static partial class MacroEngine
    {
        // ─────────────────────────────────────────────
        //  按键名称 → VK 映射
        // ─────────────────────────────────────────────

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

        private static string? Inherit(string? value, string? fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static TechniqueSource CaptureTechniqueSource(scrFloor[] floors, scrConductor conductor)
        {
            var settings = Main.Settings;
            var level = LevelTechniqueManager.GetCurrentLevelConfig();
            var techniqueOptions = level ?? settings.CurrentTechniqueProfile;
            var segments = level != null ? level.techniqueSegments : settings.CurrentTechniqueProfile?.techniqueSegments;
            int preference = level?.handPreference ?? settings.TechniqueHandPreference;
            string strategyId = TechniquePlanner.NormalizeStrategyId(techniqueOptions?.groupingStrategyId);
            string handPolicyId = TechniquePlanner.NormalizeHandPolicyId(techniqueOptions?.runHandPolicyId);
            double singleFingerKps = TechniquePlanner.NormalizeSingleFingerKps(techniqueOptions?.singleFingerKps ?? 6.5);
            var groupingStrategy = TechniquePlanner.CreateGroupingStrategy(strategyId, singleFingerKps, handPolicyId);
            bool lazyRelease = techniqueOptions?.lazyRelease ?? false;
            double limit = settings.TechniqueBpmLimit;
            string? leftKeys = level == null ? settings.TechLeftHandKeys : level.leftHandKeys;
            string? rightKeys = level == null ? settings.TechRightHandKeys : level.rightHandKeys;
            string? leftOrders = level == null ? settings.TechLeftHandOrders : level.leftHandOrders;
            string? rightOrders = level == null ? settings.TechRightHandOrders : level.rightHandOrders;
            string? leftTimes = level == null ? settings.TechLeftHandPressTimes : level.leftHandPressTimes;
            string? rightTimes = level == null ? settings.TechRightHandPressTimes : level.rightHandPressTimes;

            var left = ParseLayout(leftKeys, leftOrders, leftTimes, 0x46);
            var right = ParseLayout(rightKeys, rightOrders, rightTimes, 0x4A);
            var global = new TechniqueProfile(limit, left, right);
            var resolved = new List<ConfiguredSegment>();
            if (segments != null)
                foreach (var segment in segments)
                {
                    if (segment == null || segment.endFloor < segment.startFloor) continue;
                    var lp = ParseLayout(Inherit(segment.leftHandKeys, leftKeys), Inherit(segment.leftHandOrders, leftOrders),
                                         Inherit(segment.leftHandPressTimes, leftTimes), 0x46);
                    var rp = ParseLayout(Inherit(segment.rightHandKeys, rightKeys), Inherit(segment.rightHandOrders, rightOrders),
                                         Inherit(segment.rightHandPressTimes, rightTimes), 0x4A);
                    double segmentLimit = TechniqueTimeline.IsFinite(segment.bpmLimit) && segment.bpmLimit > 0 ? segment.bpmLimit : limit;
                    resolved.Add(new ConfiguredSegment(segment.startFloor, segment.endFloor, new TechniqueProfile(segmentLimit, lp, rp)));
                }

            var tiles = new TechniqueTile?[floors.Length];
            for (int i = 0; i < floors.Length; i++)
                if (floors[i] != null)
                {
                    var f = floors[i];
                    double turn = Math.Abs(f.angleLength) / Math.PI;
                    tiles[i] = new TechniqueTile(f.entryTime, f.speed, f.auto, f.midSpin, f.holdLength > -1,
                        TechniqueTimeline.IsFinite(turn) ? turn : 0);
                }
            double speed = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 1.0;
            var hash = new TechniqueHash();
            bool usesTempoInputs = groupingStrategy.UsesTempoInputs;
            hash.Add(TechniqueTimeline.Fingerprint(tiles, conductor.bpm, speed,
                includeTempo: usesTempoInputs, includeTileSpeed: usesTempoInputs, includeTurns: strategyId == TechniquePlanner.BeatGridStrategyId));
            hash.Add(preference != 0 ? 1 : 0);
            hash.Add(TechniquePlanner.GroupingStrategyFingerprint(strategyId, singleFingerKps, handPolicyId));
            if (lazyRelease) hash.Add(0x4C5A);
            TechniquePlanner.AddProfileFingerprint(ref hash, global, strategyId);
            hash.Add(resolved.Count);
            foreach (var segment in resolved)
            {
                hash.Add(segment.Start);
                hash.Add(segment.End);
                TechniquePlanner.AddProfileFingerprint(ref hash, segment.Profile, strategyId);
            }
            return new TechniqueSource(tiles, conductor.bpm, speed, preference != 0, groupingStrategy, lazyRelease,
                global, resolved.ToArray(), hash.Value);
        }

        private static HandLayout ParseLayout(string? keyText, string? orderText, string? ratioText, byte fallback)
        {
            var keys = new List<byte>();
            if (!string.IsNullOrWhiteSpace(keyText))
                foreach (var part in keyText!.Split(','))
                    if (KeyMap.TryParse(part, out byte code) && code != 0) keys.Add(code);
            if (keys.Count == 0) keys.Add(fallback);
            // An input layout can't contain more positions than there are usable VK codes.
            if (keys.Count > 255) keys.RemoveRange(255, keys.Count - 255);

            string[] groups = (orderText ?? string.Empty).Split('|');
            var orders = new int[keys.Count][];
            for (int row = 0; row < orders.Length; row++)
            {
                var indices = new List<int>();
                foreach (var part in groups[Math.Min(row, groups.Length - 1)].Split(','))
                    if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    {
                        indices.Add(Math.Max(1, Math.Min(value, keys.Count)) - 1);
                        if (indices.Count == keys.Count) break;
                    }
                orders[row] = indices.ToArray();
            }
            string[] parts = (ratioText ?? string.Empty).Split(',');
            var ratios = new double[keys.Count];
            for (int i = 0; i < ratios.Length; i++)
            {
                if (i >= parts.Length || !double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out ratios[i]))
                    ratios[i] = 0.8;
            }
            return new HandLayout(keys.ToArray(), orders, ratios);
        }
    }
}
