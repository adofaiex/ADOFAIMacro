using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using UnityEngine;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 分片算法（C# 实现）：把事件流切成「时间片」，一片交给一只手，
    /// 用 mCnt 级联计数器决定倍乘层，产出接近真人的按键分布。
    ///
    /// 只在 DEBUG 构建启用，作为原生 TechniqueSimulator.dll 不可用时的回退。
    /// 与原生实现保持同步 —— 改这里也必须同步改 C++，否则两条路径手法不同。
    ///
    /// ⚠️ 手法最敏感的部分：B+C 速率容差（滑动窗口基准 + 死区）、
    ///    逐地板速度跟随、needBack 回溯都在这里。
    /// </summary>
    internal static partial class MacroEngine
    {
#if DEBUG
        // ═══════════════════════════════════════════════════════════════
        //  C# 回退路径（Release 模式下也作为 DLL 加载失败的备份）
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildCSHarpTechniqueHitEvents()
        {
            ParseTechniqueConfig();

            var  floors  = cachedFloors!;
            bool sim     = Main.Settings.SimulateKeyPress;

            // 使用对象池，避免分配
            _evTimeRecycle.Clear();
            _evPressRecycle.Clear();
            _evFloorRecycle.Clear();
            _evSpeedRecycle.Clear();
            var evSpeed = _evSpeedRecycle;

            // 终点砖（最后一块）也要按键 —— 通关条件是
            // GCS.checkpointNum >= listFloors.Count（scrConductor.cs:411）。
            for (int i = 0; i < floors.Length; i++)
            {
                var fl = floors[i];
                if (fl == null) continue;
                if (fl.auto) continue;

                // 找"下一块真正要判定的砖"：照抄游戏 scrPlayer.cs:411-415
                // （跳过中旋再判 holdLength）。中旋自己可以带长按
                // （scnGame.cs:1174 把 Hold 的 duration 写在这块砖上），
                // 只有"不带长按的中旋"才跳过。
                int ni = i + 1;
                while (ni < floors.Length - 1)
                {
                    var cf = floors[ni];
                    if (cf == null) { ni++; continue; }
                    if (cf.midSpin) { if (cf.holdLength > -1) break; ni++; continue; }
                    if (cf.auto) { ni++; continue; }
                    break;
                }

                bool isLastFloor = (i == floors.Length - 1);

                // 后面全是 auto/中旋、且当前砖不是终点砖：长按仍要发松键尾事件
                // （否则键一直按着，strictHolds 判中途没松 → 长按失败）。
                if (ni >= floors.Length - 1 && !isLastFloor)
                {
                    if (sim && fl.holdLength > -1 && !fl.midSpin)
                    {
                        double te = floors[floors.Length - 1]?.entryTime ?? double.MaxValue;
                        _evTimeRecycle.Add(te); _evPressRecycle.Add(-1); _evFloorRecycle.Add(i);
                        evSpeed.Add(fl.speed);
                    }
                    // continue 而非 break：处理完还要继续后面的砖（含终点砖）
                    continue;
                }

                if (fl.midSpin && fl.holdLength <= -1) continue;

                // 终点砖：普通按键，无长按头/尾
                if (isLastFloor)
                {
                    _evTimeRecycle.Add(fl.entryTime); _evPressRecycle.Add(1); _evFloorRecycle.Add(i);
                    evSpeed.Add(fl.speed);
                    continue;
                }

                var    nf = floors[ni];
                double t  = nf?.entryTime ?? double.MaxValue;

                if (sim && fl.holdLength > -1 && nf != null && nf.holdLength == -1)
                {
                    _evTimeRecycle.Add(t); _evPressRecycle.Add(-1); _evFloorRecycle.Add(i);
                    evSpeed.Add(fl.speed);
                    continue;
                }

                bool isHoldHead = sim && nf != null && nf.holdLength > -1;
                _evTimeRecycle.Add(t);
                _evPressRecycle.Add(isHoldHead ? 2 : 1);
                _evFloorRecycle.Add(i);
                evSpeed.Add(fl.speed);
            }

            int total = _evTimeRecycle.Count;
            if (total == 0) { _hitEvents = []; _hitEventCount = 0; return; }

            _piecesRecycle.Clear();
            BuildPieces(_evTimeRecycle, _evPressRecycle, _evFloorRecycle, total, _piecesRecycle, evSpeed);

            if (_piecesRecycle.Count > 0)
            {
                var lp = _piecesRecycle[_piecesRecycle.Count - 1];
                _piecesRecycle.Add(new PieceInfo(0, 1 - lp.Hand, lp.PieceLen,
                                         lp.EndTime, lp.EndTime + lp.PieceLen, total));
            }

            var output = GenerateHitEventsFromPieces(_evTimeRecycle, _evPressRecycle, _evFloorRecycle, _piecesRecycle, sim);
            FixSameKeyOverlaps(output);

            // 使用对象池
            if (output.Count > _hitEventPool.Length)
            {
                _hitEvents = output.ToArray(); // Fallback for overflow
            }
            else
            {
                for (int i = 0; i < output.Count; i++)
                    _hitEventPool[i] = output[i];
                _hitEvents = _hitEventPool.AsSpan(0, output.Count).ToArray();
            }

            _hitEventCount = _hitEvents.Length;
            Log($"[Macro-Main] C# 手法模拟完成：{_hitEventCount} 事件，{_piecesRecycle.Count} 时间片");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindSegmentIndex(int floorIdx)
        {
            if (_currentSegments == null) return -1;
            for (int i = 0; i < _currentSegments.Count; i++)
            {
                var seg = _currentSegments[i];
                if (floorIdx >= seg.startFloor && floorIdx <= seg.endFloor)
                    return i;
            }
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            int total, List<PieceInfo> pieces,
            List<double>? evSpeed = null)   // 逐地板速度；为 null 时退化成全局速度
        {
            double nowT    = 0.0;
            int    nowD    = 0;
            var    _levelTechHandPref = LevelTechniqueManager.GetCurrentLevelConfig()?.handPreference ?? Main.Settings.TechniqueHandPreference;
            int    cHand   = (_levelTechHandPref == 0) ? -1 : 1;
            int    mult    = 0;

            var mCnt    = new long[16];
            var mCntPre = new long[16];
            int  canMulti  = 0;
            bool needBack  = false;

            // ── 速率容差：滑动窗口基准 + 死区（与原生 TechniqueSimulator.cpp 同步）──
            const int    BaseWindow   = 32;    // 滑动窗口音数
            const double BaseLimitMul = 2.0;   // 局部速率相对窗口基准的允许倍数
            double deadZone = (Main.Settings.SpeedChangeTolerance > 0.0)
                            ? Main.Settings.SpeedChangeTolerance : 0.50;
            if (deadZone < 0.0) deadZone = 0.0;
            if (deadZone > 0.9) deadZone = 0.9;

            double lockedRate = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 1.0;
            if (evSpeed != null && evSpeed.Count > 0 && evSpeed[0] > 1e-9) lockedRate = evSpeed[0];

            float  lastSegLimit = GetSegmentBpmLimit(evFloor[0]);
            double nowBpm       = GetAdviceBpm(lastSegLimit, lockedRate);
            int    lastSegIdx   = FindSegmentIndex(evFloor[0]);

            while (nowD < total)
            {
                int   curFloorIdx = evFloor[nowD];
                int   curSegIdx   = FindSegmentIndex(curFloorIdx);
                float curSegLimit = GetSegmentBpmLimit(curFloorIdx);

                if (curSegIdx != lastSegIdx)
                {
                    cHand   = (_levelTechHandPref == 0) ? -1 : 1;
                    mult    = 0;
                    Array.Clear(mCnt,    0, mCnt.Length);
                    Array.Clear(mCntPre, 0, mCntPre.Length);
                    canMulti  = 0;
                    needBack  = false;
                    lastSegLimit = curSegLimit;
                    // 段切换：锁定速率回到本段第一块地的速度（死区状态清零）
                    lockedRate = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 1.0;
                    if (evSpeed != null && curFloorIdx < evSpeed.Count && evSpeed[curFloorIdx] > 1e-9)
                        lockedRate = evSpeed[curFloorIdx];
                    nowBpm       = GetAdviceBpm(lastSegLimit, lockedRate);
                    lastSegIdx   = curSegIdx;
                }

                // ── 逐片速率容差 ──────────────────────────────
                // 与原生一致：滑动窗口基准 → ±BaseLimitMul 限幅 → 死区。
                // 小于死区的变速不改变片长（人的手法有容差和忽略）。
                if (evSpeed != null && evSpeed.Count > 0)
                {
                    int si = (nowD < evSpeed.Count) ? nowD : evSpeed.Count - 1;
                    if (si >= 0)
                    {
                        double baseRate = 0.0;
                        int w0 = si - BaseWindow + 1; if (w0 < 0) w0 = 0;
                        for (int w = w0; w <= si; w++)
                            if (evSpeed[w] > baseRate) baseRate = evSpeed[w];
                        if (baseRate <= 1e-9) baseRate = evSpeed[si];
                        if (baseRate <= 1e-9) baseRate = lockedRate;

                        double lo = baseRate / BaseLimitMul;
                        double hi = baseRate * BaseLimitMul;

                        double ls = evSpeed[si];
                        if (ls > 1e-9)
                        {
                            if (ls < lo) ls = lo;
                            if (ls > hi) ls = hi;
                            double rel = (lockedRate > 1e-9)
                                       ? Math.Abs(ls / lockedRate - 1.0) : 1.0;
                            if (rel > deadZone) lockedRate = ls;
                            nowBpm = GetAdviceBpm(lastSegLimit, lockedRate);
                        }
                    }
                }

                if (pieces.Count > total * 64) break;

                double pLen = 60.0 / (nowBpm * Math.Pow(2, mult)) / 2.0;
                if (pLen < 1e-9) pLen = 1e-9;

                int cnt   = CountEventsInRange(evTime, nowD, nowT + pLen * 0.995);
                int csH   = (cHand == 1) ? 1 : 0;

                // 使用分段有效配置来确定当前手的最大按键数
                var   ec   = GetEffectiveConfig(curFloorIdx);
                int   maxK = (csH == 0) ? ec.LeftKeys.Length : ec.RightKeys.Length;

                int  mainHand  = (_levelTechHandPref == 0) ? -1 : 1;
                bool isOffHand = (cHand != mainHand);

                if (cnt > maxK)
                {
                    if (canMulti == 1 && isOffHand) needBack = true;
                    if (mult < 7) { mult++; mCnt[mult] = 0; continue; }
                    else           cnt = maxK;
                }

                if (needBack && pieces.Count > 0)
                {
                    needBack = false;
                    cHand    = mainHand;
                    var prev = pieces[pieces.Count - 1];
                    nowT = prev.StartTime;
                    nowD = prev.EvStart;
                    Array.Copy(mCntPre, mCnt, 16);
                    mult = prev.Multiplier + 1;
                    if (mult > 7) mult = 7;
                    pieces.RemoveAt(pieces.Count - 1);
                    canMulti = 0;
                    continue;
                }

                // ── 自适应时间片延伸（仅在下一片更稀疏时合并）────
                float speedChangeTolerance = LevelTechniqueManager.GetCurrentLevelConfig()?.speedChangeTolerance
                    ?? Main.Settings.SpeedChangeTolerance;
                if (speedChangeTolerance > 0f && cnt > 0 && nowD + cnt < total)
                {
                    double nextEvTime = evTime[nowD + cnt];
                    double diff = nextEvTime - (nowT + pLen);
                    if (diff > pLen * 0.001 && diff < pLen * speedChangeTolerance)
                    {
                        int nextCnt = CountEventsInRange(evTime, nowD + cnt, (nowT + pLen) + pLen * 0.995);
                        if (nextCnt < cnt)
                        {
                            pLen = nextEvTime - nowT;
                        }
                    }
                }

                Array.Copy(mCnt, mCntPre, 16);
                pieces.Add(new PieceInfo(cnt, csH, pLen, nowT, nowT + pLen, nowD, mult));

                for (int c = mult; c > 0; c--)
                {
                    mCnt[c] += (long)Math.Pow(2, 16 - (mult - c));
                    mCnt[c] %= (1L << 18);
                }
                while (mult > 0 && mCnt[mult] == 0) mult--;

                nowD += cnt;
                nowT += pLen;
                cHand = -cHand;
                canMulti = 1;

                if (nowD < total && Math.Abs(evTime[nowD] - nowT) < pLen * 0.01)
                    nowT = evTime[nowD];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<HitEvent> GenerateHitEventsFromPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            List<PieceInfo> pieces, bool sim)
        {
            int total  = evTime.Count;
            var output = new List<HitEvent>(total * 2);

            bool          activeHold    = false;
            byte          activeHoldKey = 0;
            int           lastSegIdxEvent = -2;

            for (int pcnt = 0; pcnt < pieces.Count - 1; pcnt++)
            {
                var    cur    = pieces[pcnt];
                var    next   = pieces[pcnt + 1];
                double pStart = (pcnt > 0) ? pieces[pcnt - 1].EndTime : 0.0;

                for (int i = 0; i < cur.EvCount; i++)
                {
                    int    idx   = cur.EvStart + i;
                    int    press = evPress[idx];
                    double t     = evTime[idx];

                    if (press == -1)
                    {
                        if (activeHold)
                        {
                            output.Add(new HitEvent(t, 0, releaseOnly: true,
                                isHoldRelated: true, releaseKeyCode: activeHoldKey));
                            activeHold = false; activeHoldKey = 0;
                        }
                        continue;
                    }

                    // 按当前事件的地板索引解析有效键位
                    int curFloor = (idx < evFloor.Count) ? evFloor[idx] : evFloor[evFloor.Count - 1];

                    // 段边界：释放活跃 hold 键
                    int curSegIdx = FindSegmentIndex(curFloor);
                    if (curSegIdx != lastSegIdxEvent)
                    {
                        if (activeHold && lastSegIdxEvent != -2)
                        {
                            output.Add(new HitEvent(t - 0.000001, 0, releaseOnly: true,
                                isHoldRelated: true, releaseKeyCode: activeHoldKey));
                            activeHold = false;
                            activeHoldKey = 0;
                        }
                        lastSegIdxEvent = curSegIdx;
                    }

                    var ec = GetEffectiveConfig(curFloor);

                    byte[]   hK = (cur.Hand == 0) ? ec.LeftKeys        : ec.RightKeys;
                    int[][]  hO = (cur.Hand == 0) ? ec.LeftOrders       : ec.RightOrders;
                    double[] hT = (cur.Hand == 0) ? ec.LeftPressTimes   : ec.RightPressTimes;

                    int oi = Math.Min(cur.EvCount - 1, hK.Length - 1);
                    int ki = (i < hO[oi].Length) ? hO[oi][i] : (i % hK.Length);
                    ki = Mathf.Clamp(ki, 0, hK.Length - 1);

                    byte   kc          = hK[ki];
                    double ratio       = (ki < hT.Length) ? hT[ki] : 0.8;
                    bool   isHoldHead  = (press == 2);

                    if (isHoldHead && activeHold)
                    {
                        output.Add(new HitEvent(t - 0.000001, 0, releaseOnly: true,
                            isHoldRelated: true, releaseKeyCode: activeHoldKey));
                        activeHold = false; activeHoldKey = 0;
                    }

                    output.Add(new HitEvent(t, kc, false, isHoldHead));

                    if (isHoldHead) { activeHold = true; activeHoldKey = kc; }
                    if (!sim || isHoldHead) continue;

                    // 计算松键时间
                    double dur;
                    if (next.PieceLen > cur.PieceLen + 5e-6)
                    {
                        dur = (pStart + cur.PieceLen > cur.EndTime + 5e-6)
                            ? (next.EndTime - t) * ratio / 2.0
                            : (pStart + cur.PieceLen * 2.0 - t) * ratio / 2.0;
                    }
                    else
                    {
                        dur = (pStart + cur.PieceLen + 5e-6 < cur.EndTime)
                            ? (pStart + cur.PieceLen + next.PieceLen - t) * ratio / 2.0
                            : (next.EndTime - t) * ratio / 2.0;
                    }

                    double rel = t + dur;

                    if (next.Hand != cur.Hand || next.EvCount == 0)
                        { if (rel >= next.EndTime) rel = next.EndTime - 1e-6; }
                    else
                        { if (rel >= cur.EndTime)  rel = cur.EndTime  - 1e-6; }

                    if (rel <= t) rel = t + (next.EndTime - t) * 0.4;

                    output.Add(new HitEvent(rel, 0, true, false, releaseKeyCode: kc));
                }
            }

            if (activeHold && pieces.Count > 0)
            {
                double lastTime = pieces[pieces.Count - 1].EndTime;
                output.Add(new HitEvent(lastTime, 0, releaseOnly: true,
                    isHoldRelated: true, releaseKeyCode: activeHoldKey));
            }

            return output;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FixSameKeyOverlaps(List<HitEvent> events)
        {
            events.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));

            int n = events.Count;
            var pending = new Dictionary<byte, int>(8);

            for (int i = 0; i < n; i++)
            {
                var ev = events[i];

                if (ev.ReleaseOnly)
                {
                    if (ev.ReleaseKeyCode != 0) pending.Remove(ev.ReleaseKeyCode);
                    continue;
                }

                byte kc = ev.KeyCode;
                if (kc == 0) continue;

                if (pending.TryGetValue(kc, out int relIdx))
                {
                    var relEv = events[relIdx];
                    if (relEv.TriggerTime >= ev.TriggerTime)
                    {
                        events[relIdx] = new HitEvent(ev.TriggerTime - 1e-6,
                            relEv.KeyCode, releaseOnly: true,
                            isHoldRelated: relEv.IsHoldRelated,
                            releaseKeyCode: relEv.ReleaseKeyCode);
                    }
                    pending.Remove(kc);
                }

                for (int j = i + 1; j < n; j++)
                {
                    var fwd = events[j];
                    if (fwd.ReleaseOnly && fwd.ReleaseKeyCode == kc && !fwd.IsHoldRelated)
                    {
                        pending[kc] = j;
                        break;
                    }
                }
            }

            events.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountEventsInRange(List<double> times, int start, double endTime)
        {
            if (start >= times.Count) return 0;
            int left = start, right = times.Count - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (times[mid] < endTime) left  = mid + 1;
                else                      right = mid - 1;
            }
            return left - start;
        }
#endif
    }
}
