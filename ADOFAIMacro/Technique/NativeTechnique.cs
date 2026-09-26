using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using Settings = ADOFAIMacro.Settings.Settings;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 原生手法事件表构建：把整张谱的地板交给 TechniqueSimulator.dll 分片，
    /// 拿回按键事件流。
    ///
    /// 地板取舍照抄游戏做法 —— 终点砖、auto 砖、中旋长按的处理见各处源码引用。
    /// DLL 不可用时由 BuildHitEvents 回退到 C# 实现（PieceBuilder.cs）。
    /// </summary>
    internal static partial class MacroEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildTechniqueHitEvents()
        {
            ParseTechniqueConfig();

            var floors = cachedFloors!;
            bool sim = Main.Settings.SimulateKeyPress;

            _evTimeRecycle.Clear();
            _evPressRecycle.Clear();
            _evFloorRecycle.Clear();
            _evSpeedRecycle.Clear();

            var evTime = _evTimeRecycle;
            var evPress = _evPressRecycle;
            var evFloor = _evFloorRecycle;
            var evSpeed = _evSpeedRecycle;

            // 终点砖（最后一块）**也要按键**。通关条件是
            // GCS.checkpointNum >= listFloors.Count（scrConductor.cs:411），
            // 必须一路命中到最后一块。scrLevelMaker 里那些
            // `i < listFloors.Count - 1` 的循环只是**不算终点砖的时间**，
            // 不代表它不判定 —— 曾被这点误导漏掉了终点砖的按键。
            for (int i = 0; i < floors.Length; i++)
            {
                var fl = floors[i];
                if (fl == null) continue;
                if (fl.auto) continue;

                // ── 找"下一块真正要判定的砖"（照抄游戏 scrPlayer.cs:411-415）──
                //   scrFloor nextfloor = currFloor.nextfloor;
                //   while (nextfloor.midSpin && nextfloor.nextfloor) nextfloor = nextfloor.nextfloor;
                //   __nextTileIsHoldCached = nextfloor.holdLength > -1;
                // 游戏跳过中旋再判长按 —— 中旋的 holdLength 语义不同，直接取
                // floors[i+1] 会误判长按头/尾。
                //
                // 中旋**自己**仍可能带长按：scnGame.cs:1171-1174 的 Hold 事件
                //   floor7.holdLength = (floor7.nextfloor && duration >= 0) ? duration : -1;
                // 直接写在这块砖上，这块砖可以是中旋。所以中旋不能无条件跳过 ——
                // 只有"不带长按的中旋"才跳过（那才是纯装饰、不需要按键）。
                int ni = i + 1;
                while (ni < floors.Length - 1)
                {
                    var cf = floors[ni];
                    if (cf == null) { ni++; continue; }
                    if (cf.midSpin)
                    {
                        // 中旋自己带长按 → 它就是要按的那一块，停下
                        if (cf.holdLength > -1) break;
                        ni++; continue;                  // 纯中旋，跳过
                    }
                    if (cf.auto) { ni++; continue; }
                    break;
                }

                // 找不到下一块可判定的砖。分两种情况：
                //  ① 当前砖就是**终点砖**（i == floors.Length-1）→ 它自己
                //     仍要按键（通关条件 checkpointNum >= listFloors.Count，
                //     scrConductor.cs:411）。没有"下一块"就没有长按头/尾，
                //     按普通音处理。
                //  ② 当前砖是长按、后面全是 auto/中旋 → 必须发松键尾事件，
                //     否则键一直按着，strictHolds 判中途没松 → 长按失败
                //     （用户报的「长按了没用」）。松键时刻取谱尾那块砖。
                bool isLastFloor = (i == floors.Length - 1);
                if (ni >= floors.Length - 1 && !isLastFloor)
                {
                    if (sim && fl.holdLength > -1 && !fl.midSpin)
                    {
                        double te = floors[floors.Length - 1]?.entryTime ?? double.MaxValue;
                        evTime.Add(te); evPress.Add(-1); evFloor.Add(i); evSpeed.Add(fl.speed);
                    }
                    // 用 continue 不是 break：i=9 这类「长按在倒数第二」的情况
                    // 处理完还要**继续处理后面的砖**（尤其是终点砖）。
                    // 原来写 break 导致 i=9 之后整个循环结束，终点砖 i=10 从头
                    // 到尾都没被处理 —— 实测 10 个地板只判定 9 个
                    // （谱尾诊断：i=8 有事件、i=9 和 i=10 都无事件）。
                    continue;
                }

                // 终点砖的按键时刻：它没有"下一块"，用**它自己的 entryTime**
                // 作为判定时刻（玩家在它进场时就得按下去）。
                scrFloor nf;
                double t;
                if (isLastFloor)
                {
                    nf = fl;
                    t = fl.entryTime;
                }
                else
                {
                    nf = floors[ni];
                    t  = nf?.entryTime ?? double.MaxValue;
                }

                // 纯中旋（无长按）不产生按键
                if (fl.midSpin && fl.holdLength <= -1) continue;

                // 终点砖没有"下一块"，不该产生长按头/尾（nf 被赋成了自己，
                // 判 holdLength 会把自己当长按头 → 永远等不到尾 → 键不松）。
                if (isLastFloor)
                {
                    evTime.Add(t); evPress.Add(1);
                    evFloor.Add(i); evSpeed.Add(fl.speed);
                    continue;
                }

                if (sim && fl.holdLength > -1 && nf != null && nf.holdLength == -1)
                {
                    evTime.Add(t); evPress.Add(-1); evFloor.Add(i); evSpeed.Add(fl.speed);
                    continue;
                }

                bool isHoldHead = sim && nf != null && nf.holdLength > -1;
                evTime.Add(t);
                evPress.Add(isHoldHead ? 2 : 1);
                evFloor.Add(i);
                evSpeed.Add(fl.speed);
            }

            int total = evTime.Count;
            if (total == 0) { _hitEvents = []; _hitEventCount = 0; return; }

            // ── 谱尾诊断（临时，可由日志总开关关闭）──────────────
            // 用户报「最后一格子还是没有按键」「10个地板只判定9个」。
            // 终点砖的判定时刻用 fl.entryTime，但如果它同时是 midSpin / auto /
            // hold，就会被前面的 continue 分支吃掉。落盘谱尾 6 块砖的真实状态，
            // 以及每块砖是否生成了事件。
            if (Main.LoggingEnabled)
            try
            {
                var sbT = new System.Text.StringBuilder();
                int last = floors.Length - 1;
                sbT.AppendLine($"floors.Length={floors.Length}  事件数={total}  谱尾索引={last}");
                int emitted = 0;
                for (int k = 0; k < evTime.Count; k++)
                    if (evPress[k] != -1) emitted++;
                sbT.AppendLine($"按下事件={emitted}");
                for (int k = Math.Max(0, last - 5); k < floors.Length; k++)
                {
                    var fk = floors[k];
                    bool got = false;
                    for (int q = 0; q < evFloor.Count; q++)
                        if (evFloor[q] == k) { got = true; break; }
                    sbT.AppendLine(
                        $"  i={k} seqID={fk?.seqID} entryTime={fk?.entryTime:F3} hold={fk?.holdLength} " +
                        $"midSpin={fk?.midSpin} auto={fk?.auto} freeroam={fk?.freeroam} " +
                        $"next={(fk?.nextfloor == null ? "null" : $"seqID={fk.nextfloor.seqID} auto={fk.nextfloor.auto} mid={fk.nextfloor.midSpin}")} " +
                        $"→{(got ? "有事件" : "无事件")}");
                }
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "ADOFAIMacro-谱尾诊断.txt"),
                    sbT.ToString());
            }
            catch { }

#if DEBUG
            bool useCppVersion = Main.Settings.UseCppTechniqueInDebug;
#else
            bool useCppVersion = true;
#endif
            if (useCppVersion)
            {
                try
                {
                    var levelConfig = LevelTechniqueManager.GetCurrentLevelConfig();
                    Settings.TechniqueSegment[] segments;
                    int handPref;
                    if (levelConfig != null)
                    {
                        segments = levelConfig.techniqueSegments?.ToArray() ?? [];
                        handPref = levelConfig.handPreference;
                    }
                    else
                    {
                        var currentProfile = Main.Settings.CurrentTechniqueProfile;
                        segments = currentProfile?.techniqueSegments?.ToArray() ?? [];
                        handPref = Main.Settings.TechniqueHandPreference;
                    }

                    double speedChangeTolerance = levelConfig?.speedChangeTolerance
                        ?? Main.Settings.SpeedChangeTolerance;
                    TechniqueSimulator.UpdateConfig(
                        _techLeftKeys, _techRightKeys,
                        _techKeyOrders[0], _techKeyOrders[1],
                        _techPressDur[0], _techPressDur[1],
                        Main.Settings.TechniqueBpmLimit,
                        handPref,
                        speedChangeTolerance,
                        segments);

                    if (TechniqueSimulator.BuildHitEvents(
                            [.. evTime], [.. evPress], [.. evFloor], [.. evSpeed],
                            total,
                            conductor!.bpm, ADOBase.controller.playerOne.planetarySystem.speed,
                            out var nativeEvents))
                    {
                        _hitEvents = nativeEvents;
                        _hitEventCount = nativeEvents!.Length;
                        Log($"[Macro-Main] C++ 手法模拟（原生分段）完成：{_hitEventCount} 事件");
                        LogTechniqueTableSummary(nativeEvents!, evSpeed);
                        return;
                    }
                }
                catch (Exception ex) { Log($"[Macro-Main] C++ 手法模拟异常: {ex.Message}，回退 C# 版本"); }
            }

#if DEBUG
            // Debug 构建保留 C# 复刻实现作为回退
            BuildCSHarpTechniqueHitEvents();
#else
            // Release 没有 C# 回退：原生路径失败时必须显式清空事件表。
            // 否则 _hitEvents 仍是上一张谱面的数组，而 Initialize() 依旧会置
            // initialized=true 并发布锚点 → 工作线程按旧谱时间戳乱按键。
            _hitEvents = [];
            _hitEventCount = 0;
            Main.Log("[Macro-Main] C++ 手法模拟未产出事件，事件表已清空（Release 无 C# 回退）");
#endif
        }
    }
}
