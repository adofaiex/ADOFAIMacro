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

            var floors = cachedFloors!;
            bool sim = Main.Settings.SimulateKeyPress;

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

                var nf = floors[ni];
                double t = nf?.entryTime ?? double.MaxValue;

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

        // ── 【2026-10-11 与 C++ TimeSlicePartition 同步】──────────────
        //  以下三个成员与原生 TechniqueSimulator.cpp 的 BuildRestartMarks/
        //  RestartMark/markAt/adviceBpm 一一对应。C# 回退路径此前没有
        //  「变速点重启」机制（只重看分段配置），变速谱面会一片吃穿变速点；
        //  现按原生语义重建：建议 BPM 变化处 = 变速点 = 重开时间片。
        //
        //  原生 BuildRestartMarks 的三个要点照抄：
        //  · 索引 0 永远是一个 mark（kind=1 指定主副手）；
        //  · 建议 BPM 未变（差 ≤1e-9）不算变速点；
        //  · 变速点 mark 的手性乘数恒为 1（"*1" ⇒ hand 不变），
        //    且 adviceBpm 恒 >0 ⇒ restart 时走「绝对赋值」分支，
        //    乘法分支（adviceBpm<0）在本移植的 mark 生成规则下不可达，
        //    故 C# 只实现绝对分支。
        private struct RestartMark { public int Idx; public int Kind; public int Hand; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildRestartMarks(
            List<double> evTime, List<int> evFloor, List<double> evSpeed,
            bool hasSpeed, double speedFallback,
            double bpm, double bpmLimit, int mainHand,
            List<RestartMark> marks, int[] markAt, double[] adviceBpm)
        {
            int n = evTime.Count;
            marks.Clear();
            for (int i = 0; i < n; i++) { markAt[i] = -1; adviceBpm[i] = 0.0; }
            if (n <= 0) return;

            for (int i = 0; i < n; i++)
            {
                double s = (hasSpeed && i < evSpeed.Count) ? evSpeed[i] : speedFallback;
                if (!(s > 1e-9)) s = (speedFallback > 1e-9) ? speedFallback : 1.0;
                adviceBpm[i] = GetAdviceBpm(bpmLimit, s);
            }

            // 第 1 行：索引 0，永远重开 + 指定主副手（原生 :313-314）
            marks.Add(new RestartMark { Idx = 0, Kind = 1, Hand = mainHand });
            markAt[0] = 0;

            // 变速点判定：建议 BPM 变化处（原生 :331-340）。
            // 手性乘数恒 1（kind=2）⇒ hand *= 1 ⇒ 手性不变。
            for (int i = 1; i < n; i++)
            {
                if (Math.Abs(adviceBpm[i] - adviceBpm[i - 1]) <= 1e-9) continue;
                marks.Add(new RestartMark { Idx = i, Kind = 2, Hand = 1 });
                markAt[i] = marks.Count - 1;
            }
        }

        // 片界守卫窗口（一帧 ≈ 16.7ms），与原生 kBoundaryGuardSec 相同
        private const double BoundaryGuardSec = 0.0167;

        // 瞬双窗口（2026-11-13，与原生 kBoundaryTinySec 同步）：gap < 3ms 的跨手片界
        // 音视为「同一瞬间落下」的伪双押（INVERSION 雨段实测 2.679ms）。
        private const double BoundaryTinySec = 0.003;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            int total, List<PieceInfo> pieces,
            List<double>? evSpeed = null)   // 逐地板速度；为 null 时退化成全局速度
        {
            double nowT = 0.0;
            int nowD = 0;
            var _levelTechHandPref = LevelTechniqueManager.GetCurrentLevelConfig()?.handPreference ?? Main.Settings.TechniqueHandPreference;
            int cHand = (_levelTechHandPref == 0) ? -1 : 1;
            int mult = 0;

            var mCnt = new long[64];   // 64 级：无上限倍乘（与 cpp 同步扩容）
            var mCntPre = new long[64];
            int canMulti = 0;
            bool needBack = false;
            // 槽位手号表：与 pieces 同索引，回溯时**不弹出**（见 maxK 处注释）
            var slotHand = new List<int>();

            // ── 【2026-10-03 变速容差整条移除】─────────────────────
            //  原来这里的 BaseWindow(32 音取 max 基准) + BaseLimitMul(±2× 限幅) +
            //  deadZone + lockedRate 保持，以及底部的"自适应时间片延伸"
            //  （几何修补），已随原生 TechniqueSimulator.cpp 一并删除。
            //  理由与实测数据见 TechniqueSimulator.cpp 内同名注释块。
            //
            // ── 【2026-10-11 与 C++ TimeSlicePartition 同步】────────
            //  片长模型从「逐片跟随本片速度 + 分段切换重置」整体改为原生的
            //  「变速点重启（RestartMark）驱动」：
            //    · nowBpm 只在 restart 时按绝对 bpm 重设（C++ :416），
            //      片与片之间保持不变（mult 除外）；
            //    · 变速点 = 建议 BPM 变化处（由 BuildRestartMarks 预先算好）；
            //    · restart 时：mult=0、级联计数器清零、nowT 跳到该事件、
            //      上一片终止时刻对齐、canMulti=0（C++ :405-426）；
            //    · 窗口计数在变速点处断开（C++ :450-453）。
            //  旧的「逐片跟随本片速度」是 10-04 重构前单趟贪心的遗留，
            //  与原生行为不符；「段切换重置」在原生里也不存在 ——
            //  分段配置只影响 ResolveConfig 等价的键表/上限查找，
            //  不触发任何算法状态重置。回退路径现已与两条原生路径
            //  （在线贪心 / 离线求解）共享同一套分片语义。
            double speedFallback = ADOBase.controller?.playerOne?.planetarySystem?.speed ?? 1.0;
            bool hasSpeed = evSpeed != null && evSpeed.Count > 0;

            // 初始 bpmLimit：取第一个事件所属分段（C++ :369-375）
            double limit0 = Main.Settings.TechniqueBpmLimit;
            if (_currentSegments is { Count: > 0 })
            {
                int s0 = FindSegmentIndex(evFloor[0]);
                if (s0 >= 0) limit0 = _currentSegments[s0].bpmLimit;
            }

            var marks = new List<RestartMark>();
            var markAt = new int[total];
            var adviceBpm = new double[total];
            BuildRestartMarks(evTime, evFloor, evSpeed ?? [], hasSpeed,
                              speedFallback, (double)conductor!.bpm, limit0,
                              (_levelTechHandPref == 0) ? -1 : 1, marks, markAt, adviceBpm);

            double nowBpm = adviceBpm.Length > 0 ? adviceBpm[0] : 500.0;
            if (nowBpm <= 0.0) nowBpm = 500.0;

            while (nowD < total)
            {
                int psize = pieces.Count;   // 本片提交前的片数

                // ── 变速点：重开时间片（C++ :403-426）────────────
                if (markAt[nowD] >= 0)
                {
                    RestartMark m = marks[markAt[nowD]];
                    mult = 0;
                    Array.Clear(mCnt, 0, mCnt.Length);
                    Array.Clear(mCntPre, 0, mCntPre.Length);
                    if (m.Kind == 1) cHand = m.Hand;
                    else cHand *= m.Hand;
                    nowT = evTime[nowD];
                    if (adviceBpm[nowD] > 0.0) nowBpm = adviceBpm[nowD];
                    else nowBpm *= -1.0 * adviceBpm[nowD];
                    // 原生 restart 后自清标记（C++ :418-422）：
                    // nowT 已对齐 evTime[nowD]，差值恒 0，不自清会永久自旋。
                    markAt[nowD] = -1;
                    if (pieces.Count > 0)
                    {
                        var last = pieces[pieces.Count - 1];
                        pieces[pieces.Count - 1] = new PieceInfo(last.EvCount, last.Hand,
                            last.PieceLen, last.StartTime, nowT, last.EvStart, last.Multiplier);
                    }
                    canMulti = 0;
                    needBack = false;
                    continue;
                }

                // ── 片长（C++ :429）────────────────────────────
                double pLen = 60.0 / (nowBpm * Math.Pow(2, mult)) / 2.0;
                // 与 cpp 的 kMaxMult=60 对齐：mult 本身无上限，而 mCnt 是
                // long[16]，越界即 UB；60 级已远超任何真实谱面。
                if (mult > 60) mult = 60;
                if (pLen < 1e-9) pLen = 1e-9;

                if (pieces.Count > total * 64 + 4096) break;   // 防死循环兜底（C++ pieceCap）

                // ── 本片可纳入事件数（C++ :446-455）────────────
                // 窗口系数 0.995；遇变速点立即断开。
                int cnt = 0;
                {
                    double w = nowT + pLen * 0.995;
                    int j = nowD;
                    while (j < total && evTime[j] < w)
                    {
                        if (markAt[j] >= 0) break;
                        j++;
                    }
                    cnt = j - nowD;
                }

                int curFloorIdx = evFloor[nowD];
                int csH = (cHand == 1) ? 1 : 0;

                // 使用分段有效配置来确定当前手的最大按键数
                var ec = GetEffectiveConfig(curFloorIdx);
                // 复刻一个真实怪癖（不是笔误）：读 piece[psize][1] 时该槽尚未写入
                // （写入发生在 :308 提交之后），因此恒为**左手**键数；只有
                // 回溯复用的槽才带旧手号。slotHand 与 pieces 同索引，
                // 回溯时只弹 pieces、**不弹 slotHand**，以此精确保留该行为。
                int slotH = slotHand.Count > psize ? slotHand[psize] : 0;
                int maxK = (slotH == 0) ? ec.LeftKeys.Length : ec.RightKeys.Length;
                int mainHand = (_levelTechHandPref == 0) ? -1 : 1;
                bool isOffHand = (cHand != mainHand);

                // 无上限倍乘 —— 与时间片划分语义一致（cpp 已同步）
                if (cnt > maxK)
                {
                    if (canMulti == 1 && isOffHand) needBack = true;
                    mult++;
                    continue;
                }

                if (needBack && pieces.Count > 0)
                {
                    needBack = false;
                    cHand = mainHand;
                    var prev = pieces[pieces.Count - 1];
                    nowT = prev.StartTime;
                    nowD = prev.EvStart;
                    Array.Copy(mCntPre, mCnt, 64);
                    mult = prev.Multiplier + 1;   // 无上限：回溯后倍乘必须严格高于上一片（与 cpp 同步）
                    pieces.RemoveAt(pieces.Count - 1);
                    // 回溯只回退 psize，slotHand（piece[][1] 的槽）**保留旧手号**，
                    // 下一轮重新提交时该槽会被新手号覆盖 —— 故这里也不弹 slotHand。
                    canMulti = 0;
                    continue;
                }

                /*
                // ── 自适应时间片延伸（几何修补）──────────────────
                // 【2026-10-03 整条删除】随变速容差一并移除。
                // 原代码：
                //   float speedChangeTolerance = LevelTechniqueManager.GetCurrentLevelConfig()?.speedChangeTolerance
                //       ?? Main.Settings.SpeedChangeTolerance;
                //   if (speedChangeTolerance > 0f && cnt > 0 && nowD + cnt < total) {
                //       double nextEvTime = evTime[nowD + cnt];
                //       double diff = nextEvTime - (nowT + pLen);
                //       if (diff > pLen * 0.001 && diff < pLen * speedChangeTolerance) {
                //           int nextCnt = CountEventsInRange(evTime, nowD + cnt, (nowT + pLen) + pLen * 0.995);
                //           if (nextCnt < cnt) pLen = nextEvTime - nowT;
                //       }
                //   }
                // 删除理由见 TechniqueSimulator.cpp 同名注释：tol 上限 0.5 时
                // 接近半拍的空隙都被拉长，且与速率决策共用一个设置项（一项两用）。
                */

                // 【2026-10-04 对拍】piece[psize][2]=piece_time 存进
                // **long** → 截断到整数微秒。startTime/endTime 用未截断的
                // nowT/pLen（now_time 也是 double，窗口判定必须精确），
                // 故只有 PieceLen 截断。三者全截断 / 全不截断都会让对拍分叉
                //（实测首个差异分别落在 #139 / #269 片）。
                double pLenStored = (double)(long)(pLen * 1e6) / 1e6;
                pieces.Add(new PieceInfo(cnt, csH, pLenStored, nowT, nowT + pLen, nowD, mult));
                // 手号写进槽位。maxK 处读的是本片提交**前**的槽值，
                // 故 Add 的顺序不影响当轮判定。
                slotHand.Add(csH);

                // 【2026-10-11 同步 C++ :514】提交片前快照级联计数器。
                // 此前 C# 从未做过这次复制（mCntPre 只在段切换时被清零），
                // 回溯时 Array.Copy(mCntPre, mCnt) 恢复的是零/过期数组 —— 回溯后
                // 的倍乘节拍与原生完全不同。现在与原生一致：mCntPre = 上一片
                // 提交前的 mCnt，回溯用它恢复。
                Array.Copy(mCnt, mCntPre, 64);

                // 全层级（long[16] 数组被 for(c=0;c<32) 越界写，属 UB；cpp 已修成全 64 级）
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

            // ── 片界防伪双押后处理（2026-10-11 与 C++ :588-645 同步）────
            // 放在分片循环之后、追加哨兵片之前（哨兵由调用方追加）。
            //
            // 【为什么是后处理，而不是在分片循环里 cnt++】（照抄原生注释）
            // 第一版把吞并写在循环里（cnt++ 后再提交），结果撞出**相位漂移**：
            // 手性靠 if (cnt > 0) hand = -hand 累积，被吞的那片一旦变空就不再翻手，
            // 于是后续每片手性按奇偶整体对调。这里改为：分片循环照常跑完
            //（片边界、mult、nowT 与基线逐项相同），再把「跨手片界上、
            // 间隔 < 一帧」的音从下一片**搬**到上一片。每片的 hand 是显式存进
            // PieceInfo 的（不是运行期奇偶），搬音不改变任何一片的 hand ⇒ 相位零漂移。
            //
            // 约束（与原生一致）：
            //   ① 只处理跨手片界（同手界不是伪双押）；
            //   ② 搬后本片音数不得超过该手键数上限（超过会退化成同键重叠）；
            //   ③ 遇变速点不搬（BPM 层级切换语义必须保留）；
            //   ④ 不许把下一片掏空，且必须保住它的多音性（瞬双档搬后 >=1 / 其余 >=2）——
            //     掏空会让它变成空片（空片也翻手 ⇒ 后续片与更后面片同手）；
            //     只保 ≥1 音还不够：下一片 2 音被搬走 1 音就掉成 1 音，
            //     oi = min(evCount-1, keyCount-1) 从 1 掉到 0，order 表掉行，
            //     该片的重音样式（强弱拍）被削。`> 2` ⇒ 行数不掉 ⇒ 强弱拍保住。
            //  实测（原生，ofnr 谱）：多音段 3389→3552（连守卫关的 3406 都超过），
            //  跨手同帧 564→679（守卫关 859）。三谱全面不劣。
            for (int i = 0; i + 1 < pieces.Count; i++)
            {
                if (pieces[i].Hand == pieces[i + 1].Hand) continue;
                if (pieces[i].EvCount <= 0 || pieces[i + 1].EvCount <= 0) continue;

                int maxK;
                {
                    var ec = GetEffectiveConfig(evFloor[pieces[i].EvStart]);
                    maxK = (pieces[i].Hand == 0) ? ec.LeftKeys.Length : ec.RightKeys.Length;
                }
                if (maxK <= 0) continue;

                while (pieces[i].EvCount < maxK && pieces[i + 1].EvCount > 0)
                {
                    int lastIdx = pieces[i].EvStart + pieces[i].EvCount - 1;
                    int nextIdx = pieces[i + 1].EvStart;
                    if (nextIdx != lastIdx + 1) break;              // 非相邻（防御）
                    double gap = evTime[nextIdx] - evTime[lastIdx];
                    if (!(gap > 1e-9) || gap >= BoundaryGuardSec) break;
                    // [2026-11-14 实验版 C·实机测试] donorFloor=1：均匀 flam 同手化，
                    // 离线指标 伪双押 134→39、长单手段 5 处。实机手感待验。
                    int donorFloor = 1;
                    if (pieces[i + 1].EvCount <= donorFloor) break;
                    if (markAt[nextIdx] >= 0) break;                // 变速点不搬
                    // 【2026-11-13 改善守卫，与原生同步】搬后新界 gap 必须**严格大于**搬前
                    // 界 gap 才搬：INVERSION 雨段 14.285ms 健康交替界曾被搬成 2.678ms
                    // 瞬双界（f1395→f1396 报告对根因：捐赠自伤）。不再改善即停，
                    // 原始分界保留。
                    if (nextIdx + 1 >= evTime.Count) break;         // 防御（donorFloor 保证一般不可达）
                    double newGap = evTime[nextIdx + 1] - evTime[nextIdx];
                    if (!(newGap > gap)) break;
                    pieces[i] = new PieceInfo(pieces[i].EvCount + 1, pieces[i].Hand,
                        pieces[i].PieceLen, pieces[i].StartTime, pieces[i].EndTime,
                        pieces[i].EvStart, pieces[i].Multiplier);
                    pieces[i + 1] = new PieceInfo(pieces[i + 1].EvCount - 1, pieces[i + 1].Hand,
                        pieces[i + 1].PieceLen, pieces[i + 1].StartTime, pieces[i + 1].EndTime,
                        pieces[i + 1].EvStart + 1, pieces[i + 1].Multiplier);
                }
            }

            // ── 合并兜底（2026-11-13，与原生 TechniqueSimulator.cpp 同步）──
            // 搬音要求下一片 > 2 音（保住强弱拍）；超密段一片一音时恒不满足，
            // 守卫空转 → 相邻砖跨手同帧 = 双手同步伪双押。搬不动就整片并入本片
            //（同手），发键段 order 表随片内音数加深 → 同手换指。
            // 相位零漂移：各片 Hand 显式存储，合并只减片数、不改后续片的 Hand。
            // 阈值用瞬双窗口 BoundaryTinySec（3ms）：只并「同一瞬间落下」的片界，
            // 交替段（>=3ms）不动 —— 用守卫窗 16.7ms 会把快速连打并成整段单手。
            for (int i = 0; i + 1 < pieces.Count; )
            {
                if (pieces[i].Hand == pieces[i + 1].Hand) { i++; continue; }
                if (pieces[i].EvCount <= 0 || pieces[i + 1].EvCount <= 0) { i++; continue; }

                int maxK2;
                {
                    var ec2 = GetEffectiveConfig(evFloor[pieces[i].EvStart]);
                    maxK2 = (pieces[i].Hand == 0) ? ec2.LeftKeys.Length : ec2.RightKeys.Length;
                }
                if (maxK2 <= 0) { i++; continue; }

                int mLast = pieces[i].EvStart + pieces[i].EvCount - 1;
                int mNext = pieces[i + 1].EvStart;
                if (mNext != mLast + 1) { i++; continue; }
                double mGap = evTime[mNext] - evTime[mLast];
                if (!(mGap > 1e-9) || mGap >= BoundaryTinySec) { i++; continue; }
                if (pieces[i].EvCount + pieces[i + 1].EvCount > maxK2) { i++; continue; }

                pieces[i] = new PieceInfo(pieces[i].EvCount + pieces[i + 1].EvCount, pieces[i].Hand,
                    pieces[i].PieceLen, pieces[i].StartTime, pieces[i].EndTime,
                    pieces[i].EvStart, pieces[i].Multiplier);
                pieces.RemoveAt(i + 1);
            }

            // ── 反向捐赠（2026-11-13，与原生 TechniqueSimulator.cpp 同步）──
            // 前向捐赠要求本片有键位余量、合并兜底要求两片音数和 <= 本片键数；
            // 两片都已满员时（超密段相邻片各顶满键数）瞬双界只能原样保留。
            // 反向捐赠换方向：把**本片末音**搬进下一片（接收方通常更空），
            // 搬完新界 gap = 本片内部相邻间隔。守卫：只动 gap < BoundaryTinySec
            // 的瞬双界；本片搬后仍 >=1 音；接收方收下后 <= 其键数上限；
            // 被搬事件不是变速点；新界 gap 严格大于搬前（不再改善就停）。
            // 相位零漂移：只改 EvCount/EvStart，不动任何一片的 Hand。
            //（与原生的 gap 判据差一个 1e-9 下界：本文件既有惯例，gap==0 事件
            //  实际不存在于 entryTime 序列。）
            for (int i = 0; i + 1 < pieces.Count; i++)
            {
                if (pieces[i].Hand == pieces[i + 1].Hand) continue;
                if (pieces[i].EvCount <= 0 || pieces[i + 1].EvCount <= 0) continue;

                int maxKNext;
                {
                    var ecN = GetEffectiveConfig(evFloor[pieces[i + 1].EvStart]);
                    maxKNext = (pieces[i + 1].Hand == 0) ? ecN.LeftKeys.Length : ecN.RightKeys.Length;
                }
                if (maxKNext <= 0) continue;

                while (true)
                {
                    int rLast = pieces[i].EvStart + pieces[i].EvCount - 1;
                    int rNext = pieces[i + 1].EvStart;
                    if (rNext != rLast + 1) break;              // 非相邻（防御）
                    double rGap = evTime[rNext] - evTime[rLast];
                    // [2026-11-14] 频带扩展到守卫窗：[3ms,16.7ms) 的 flam 界同样反向捐。
                    // 改善守卫照旧把关，均速连打必被拦；不减少片数 ⇒ 交替结构不动。
                    if (!(rGap > 1e-9) || rGap >= BoundaryGuardSec) break;
                    if (pieces[i].EvCount <= 1) break;          // 本片至少留 1 音
                    if (pieces[i + 1].EvCount + 1 > maxKNext) break;    // 接收方键数上限
                    if (markAt[rLast] >= 0) break;              // 被搬的是变速点不搬
                    double rNewGap = evTime[rLast] - evTime[rLast - 1];
                    if (!(rNewGap > rGap)) break;               // 改善守卫：不改善就停
                    pieces[i] = new PieceInfo(pieces[i].EvCount - 1, pieces[i].Hand,
                        pieces[i].PieceLen, pieces[i].StartTime, pieces[i].EndTime,
                        pieces[i].EvStart, pieces[i].Multiplier);
                    pieces[i + 1] = new PieceInfo(pieces[i + 1].EvCount + 1, pieces[i + 1].Hand,
                        pieces[i + 1].PieceLen, pieces[i + 1].StartTime, pieces[i + 1].EndTime,
                        pieces[i + 1].EvStart - 1, pieces[i + 1].Multiplier);
                }
            }

            // ── flam 界合并（2026-11-14，与原生 TechniqueSimulator.cpp 同步）──
            // main.adofai f2155|f2156 实测：变速 mark 强制片界落在 14.71ms 的 flam
            // 对上，翻手后 = 跨手伪双押（左手 R 键 + 右手 P 键）。捐赠在 mark 处
            // 门禁且接收片 1 音不可掏空；合并/反向捐赠只认 <3ms —— 全管不到。
            // 本通道只并「flam 形状」的界：gap < BoundaryGuardSec(16.7ms) 且
            // 后间隔 > 2×gap（贴身一对、下一拍远离）。均速连打的后间隔 ≈ gap，
            // 被排除 —— 不产生整段单手，也不碰均匀流（INVERSION 雨段不受影响）。
            // 合并 = 右片整片并入左片（同手多指），音数和受键数上限约束；
            // 相位零漂移：只减片数，不改任何一片的 Hand。
            for (int i = 0; i + 1 < pieces.Count; )
            {
                if (pieces[i].Hand == pieces[i + 1].Hand) { i++; continue; }
                if (pieces[i].EvCount <= 0 || pieces[i + 1].EvCount <= 0) { i++; continue; }

                int maxKF;
                {
                    var ecF = GetEffectiveConfig(evFloor[pieces[i].EvStart]);
                    maxKF = (pieces[i].Hand == 0) ? ecF.LeftKeys.Length : ecF.RightKeys.Length;
                }
                if (maxKF <= 0) { i++; continue; }

                int fLast = pieces[i].EvStart + pieces[i].EvCount - 1;
                int fNext = pieces[i + 1].EvStart;
                if (fNext != fLast + 1) { i++; continue; }      // 非相邻（防御）
                double fGap = evTime[fNext] - evTime[fLast];
                if (!(fGap > 1e-9) || fGap >= BoundaryGuardSec) { i++; continue; }
                // [2026-11-14] 和弦簇判定（取代单一后间隔，与原生同步）：边界所在
                // <16.7ms 簇总音数 ≤4 且簇两端间隔 ≥2×界 gap 才是「和弦」；长簇 =
                // 连打流（雨段），交替手才是对的，不并。
                int fc = 1;
                while (fNext + fc < evTime.Count && evTime[fNext + fc] - evTime[fNext + fc - 1] < BoundaryGuardSec) fc++;
                int bc = 1;
                while (fLast - bc >= 0 && evTime[fLast - bc + 1] - evTime[fLast - bc] < BoundaryGuardSec) bc++;
                // [2026-11-14 只退一点] 簇必须恰好 3-4 音（真和弦）：2 音簇是普通
                // flam 对，整片吸收会链式吃掉交替槽，一律不并。
                if (fc + bc < 3 || fc + bc > 4) { i++; continue; }
                int cFirst = fLast - (bc - 1);
                int cLast = fNext + (fc - 1);
                if (cFirst - 1 >= 0 && evTime[cFirst] - evTime[cFirst - 1] < 2.0 * fGap) { i++; continue; }
                if (cLast + 1 < evTime.Count && evTime[cLast + 1] - evTime[cLast] < 2.0 * fGap) { i++; continue; }
                if (pieces[i].EvCount + pieces[i + 1].EvCount > maxKF) { i++; continue; }

                // 窗口扩展：被吞并片的时长归本片（否则被并音符溢出窗外，松键上限
                // 把 flam 首音按住时长压到边界差，实测 41.2ms→14.7ms、70.6ms→0.0ms）。
                double fNewEnd = pieces[i + 1].EndTime;
                pieces[i] = new PieceInfo(pieces[i].EvCount + pieces[i + 1].EvCount, pieces[i].Hand,
                    fNewEnd - pieces[i].StartTime, pieces[i].StartTime, fNewEnd,
                    pieces[i].EvStart, pieces[i].Multiplier);
                pieces.RemoveAt(i + 1);                         // i 不前进：连续 flam 界继续并
            }

            // 【2026-11-14 已回滚】60° 扫角组归手：扫角反推依赖 sm（逐事件速度），
            // 而 speeddata/谱面携带速度与游戏内有效速度存在系统性偏差，判定不可靠
            // 且在其他 60° 段误触发造成深键回归。待速度映射问题解决后再议。
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<HitEvent> GenerateHitEventsFromPieces(
            List<double> evTime, List<int> evPress, List<int> evFloor,
            List<PieceInfo> pieces, bool sim)
        {
            int total = evTime.Count;
            var output = new List<HitEvent>(total * 2);

            bool activeHold = false;
            byte activeHoldKey = 0;
            int lastSegIdxEvent = -2;

            for (int pcnt = 0; pcnt < pieces.Count - 1; pcnt++)
            {
                var cur = pieces[pcnt];
                var next = pieces[pcnt + 1];
                double pStart = (pcnt > 0) ? pieces[pcnt - 1].EndTime : 0.0;

                for (int i = 0; i < cur.EvCount; i++)
                {
                    int idx = cur.EvStart + i;
                    int press = evPress[idx];
                    double t = evTime[idx];

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

                    byte[] hK = (cur.Hand == 0) ? ec.LeftKeys : ec.RightKeys;
                    int[][] hO = (cur.Hand == 0) ? ec.LeftOrders : ec.RightOrders;
                    double[] hT = (cur.Hand == 0) ? ec.LeftPressTimes : ec.RightPressTimes;

                    int oi = Math.Min(cur.EvCount - 1, hK.Length - 1);
                    int ki = (i < hO[oi].Length) ? hO[oi][i] : (i % hK.Length);
                    ki = Mathf.Clamp(ki, 0, hK.Length - 1);

                    byte kc = hK[ki];
                    double ratio = (ki < hT.Length) ? hT[ki] : 0.8;
                    bool isHoldHead = (press == 2);

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
                    { if (rel >= cur.EndTime) rel = cur.EndTime - 1e-6; }

                    if (rel <= t) rel = t + (next.EndTime - t) * 0.4;
                    // 【2026-10-04 对拍】out_event[][0] 是 long（整数微秒），
                    // :377/:381 的夹取也在整数上算 → 补最后一次截断。
                    // 不截断会出现 43.434117019 这类亚微秒值，与上游的
                    // 43.434116000 不同，进而影响同刻事件的先后判定。
                    rel = (double)(long)(rel * 1e6) / 1e6;

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
            // 【2026-10-04 对拍】同刻 tie-break：按下在前、松键在后。
            // 强行弹起写的是 time1[R][0]-1（减 1 微秒），存进 long 后
            // long 后与下一音的按下时刻**恰好相等**，于是落在同一微秒 ——
            // 观测参考产物：t=10.492295 处「k=51 按下」在前、
            // 「k=187 松开」在后。List.Sort 不稳定，故显式写死 tie-break。
            events.Sort((a, b) =>
            {
                if (a.TriggerTime != b.TriggerTime) return a.TriggerTime.CompareTo(b.TriggerTime);
                return a.ReleaseOnly.CompareTo(b.ReleaseOnly);
            });

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

            // 【2026-10-04 对拍】同刻 tie-break：按下在前、松键在后。
            // 强行弹起写的是 time1[R][0]-1（减 1 微秒），存进 long 后
            // long 后与下一音的按下时刻**恰好相等**，于是落在同一微秒 ——
            // 观测参考产物：t=10.492295 处「k=51 按下」在前、
            // 「k=187 松开」在后。List.Sort 不稳定，故显式写死 tie-break。
            events.Sort((a, b) =>
            {
                if (a.TriggerTime != b.TriggerTime) return a.TriggerTime.CompareTo(b.TriggerTime);
                return a.ReleaseOnly.CompareTo(b.ReleaseOnly);
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountEventsInRange(List<double> times, int start, double endTime)
        {
            if (start >= times.Count) return 0;
            int left = start, right = times.Count - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (times[mid] < endTime) left = mid + 1;
                else right = mid - 1;
            }
            return left - start;
        }
#endif
    }
}
