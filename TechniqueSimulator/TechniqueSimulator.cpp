#define TECHNIQUE_SIMULATOR_EXPORTS
#include "TechniqueSimulator.h"
#include <vector>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <map>
#include <objbase.h>

using namespace std;

// ─────────────────────────────────────────────
//  全局配置
// ─────────────────────────────────────────────
static TechniqueConfig g_config;

// ─────────────────────────────────────────────
//  时间片信息
// ─────────────────────────────────────────────
struct PieceInfo {
    int    evCount;      // 本片覆盖的事件数（含长按尾 press==-1）
    int    realCount;    // 本片真正要按的键数（长按尾不占键位）
    int    hand;        // 0=左, 1=右
    double pieceLen;
    double startTime;
    double endTime;
    int    evStart;
    int    multiplier;

    PieceInfo(int ec, int h, double pl, double st, double et, int es, int mult = 0, int rc = -1)
        : evCount(ec), realCount(rc < 0 ? ec : rc), hand(h), pieceLen(pl),
          startTime(st), endTime(et), evStart(es), multiplier(mult) {
    }
};
// 按住时长上下限（秒）。与分片模型无关，是纯物理约束：
//   下限 20ms —— 人手能做出的最短点按，低于此值判定窗口会消失；
//   上限 120ms —— 参照 ADOFAI_Macro_for_Publish 的 MaxPressSpeed
//                 （BPM_to_Time(MinPressBPM=500) = 0.12s）。
// 慢速段（speed 很小）片长会很长，按片几何算出来的松键能到几百毫秒甚至
// 几秒，人手不会那么按着，所以要封顶。
static const double MinPressSeconds = 0.02;
static const double MaxPressSeconds = 0.12;

// ─────────────────────────────────────────────
//  有效配置（全局 or 分段覆盖）
// ─────────────────────────────────────────────
struct EffectiveConfig {
    const unsigned char* leftKeys;   int leftKeyCount;
    const unsigned char* rightKeys;  int rightKeyCount;
    int** leftKeyOrders;  int* leftOrderLengths;  int leftOrderCounts;
    int** rightKeyOrders; int* rightOrderLengths; int rightOrderCounts;
    const double* leftPressTimes;
    const double* rightPressTimes;
    double bpmLimit;
};

static EffectiveConfig ResolveConfig(int floorIdx, int* outSegIdx = nullptr)
{
    EffectiveConfig ec{};
    // 默认：全局配置
    ec.leftKeys = g_config.leftKeys;
    ec.leftKeyCount = g_config.leftKeyCount;
    ec.rightKeys = g_config.rightKeys;
    ec.rightKeyCount = g_config.rightKeyCount;
    ec.leftKeyOrders = g_config.leftKeyOrders;
    ec.leftOrderLengths = g_config.leftOrderLengths;
    ec.leftOrderCounts = g_config.leftOrderCounts;
    ec.rightKeyOrders = g_config.rightKeyOrders;
    ec.rightOrderLengths = g_config.rightOrderLengths;
    ec.rightOrderCounts = g_config.rightOrderCounts;
    ec.leftPressTimes = g_config.leftPressTimes;
    ec.rightPressTimes = g_config.rightPressTimes;
    ec.bpmLimit = g_config.bpmLimit;

    int found = -1;
    for (int i = 0; i < g_config.segmentCount; i++) {
        auto& seg = g_config.segments[i];
        if (floorIdx >= seg.startFloor && floorIdx <= seg.endFloor) {
            ec.bpmLimit = seg.bpmLimit;
            if (seg.hasKeyOverride) {
                // 左手覆盖（仅当实际提供了按键时）
                if (seg.leftKeys && seg.leftKeyCount > 0) {
                    ec.leftKeys = seg.leftKeys;
                    ec.leftKeyCount = seg.leftKeyCount;
                    ec.leftKeyOrders = seg.leftKeyOrders;
                    ec.leftOrderLengths = seg.leftOrderLengths;
                    ec.leftOrderCounts = seg.leftOrderCounts;
                    ec.leftPressTimes = seg.leftPressTimes;
                }
                // 右手覆盖
                if (seg.rightKeys && seg.rightKeyCount > 0) {
                    ec.rightKeys = seg.rightKeys;
                    ec.rightKeyCount = seg.rightKeyCount;
                    ec.rightKeyOrders = seg.rightKeyOrders;
                    ec.rightOrderLengths = seg.rightOrderLengths;
                    ec.rightOrderCounts = seg.rightOrderCounts;
                    ec.rightPressTimes = seg.rightPressTimes;
                }
            }
            found = i;
            break;
        }
    }
    if (outSegIdx) *outSegIdx = found;
    return ec;
}

// ─────────────────────────────────────────────
//  工具函数
// ─────────────────────────────────────────────

// 二分统计 [start, endTime) 区间内的事件数
static int CountEventsInRange(const vector<double>& times, int start, double endTime)
{
    if (start >= (int)times.size()) return 0;
    int left = start, right = (int)times.size() - 1, result = start;
    while (left <= right) {
        int mid = (left + right) >> 1;
        if (times[mid] < endTime) { result = mid + 1; left = mid + 1; }
        else { right = mid - 1; }
    }
    return result - start;
}

// 将实际 BPM 折叠到 (limit/2, limit] 区间
static double GetAdviceBpm(double bpm, double speed, double limit)
{
    // 防御死循环：limit ≤ 0 时 `r <= limit/2` 对 r==0 恒真 → 无限循环。
    // limit 来自关卡配置（磁盘 JSON）或全局设置，可能被手工改成 0/负数。
    if (limit <= 0.0) limit = 500.0;
    double r = bpm * speed;
    if (r <= 0.0) return limit;
    while (r > limit)      r /= 2.0;
    while (r <= limit / 2.0) r *= 2.0;
    return r;
}

// 计算松键时刻偏移量
static double CalculateReleaseTime(double pStart, const PieceInfo& cur, const PieceInfo& next,
    double t, double ratio)
{
    if (next.pieceLen > cur.pieceLen + 5e-6) {
        if (pStart + cur.pieceLen > cur.endTime + 5e-6)
            return (next.endTime - t) * ratio / 2.0;
        else
            return (pStart + cur.pieceLen * 2.0 - t) * ratio / 2.0;
    }
    else {
        if (pStart + cur.pieceLen + 5e-6 < cur.endTime)
            return (pStart + cur.pieceLen + next.pieceLen - t) * ratio / 2.0;
        else
            return (next.endTime - t) * ratio / 2.0;
    }
}

// 修正同键重叠（按下前必须先松开上一次）
static void FixSameKeyOverlaps(vector<HitEvent>& events)
{
    if (events.empty()) return;

    sort(events.begin(), events.end(),
        [](const HitEvent& a, const HitEvent& b) { return a.TriggerTime < b.TriggerTime; });

    map<unsigned char, int> pending;
    int n = (int)events.size();

    for (int i = 0; i < n; i++) {
        auto& ev = events[i];

        if (ev.ReleaseOnly) {
            if (ev.ReleaseKeyCode != 0) pending.erase(ev.ReleaseKeyCode);
            continue;
        }

        unsigned char kc = ev.KeyCode;
        if (kc == 0) continue;

        auto it = pending.find(kc);
        if (it != pending.end()) {
            auto& relEv = events[it->second];
            if (relEv.TriggerTime >= ev.TriggerTime) {
                // 同键重叠必须消掉（否则一次按键被吃），但**不能把松键推到按下
                // 时刻之前**：那等于"按下即松开"，判定窗口直接消失（实测 0ms）。
                // 音间隔本来就短，所以至少留 MinPressSeconds。
                double minRel = events[i].TriggerTime + MinPressSeconds;
                double limit  = ev.TriggerTime - 1e-6;
                relEv.TriggerTime = (minRel < limit) ? minRel : limit;
            }
            pending.erase(it);
        }

        for (int j = i + 1; j < n; j++) {
            auto& fwd = events[j];
            // 长按的松键（IsHoldRelated）**也要登记**。原实现用
            // `!fwd.IsHoldRelated` 排除了它，于是长按键按下后没有配对的松键，
            // 下一次同键按下会往前错配到别的松键上，把长按时长压到 0。
            if (fwd.ReleaseOnly && fwd.ReleaseKeyCode == kc) {
                pending[kc] = j;
                break;
            }
        }
    }

    sort(events.begin(), events.end(),
        [](const HitEvent& a, const HitEvent& b) { return a.TriggerTime < b.TriggerTime; });
}

// ─────────────────────────────────────────────
//  导出函数：SetTechniqueConfig
// ─────────────────────────────────────────────
void SetTechniqueConfig(TechniqueConfig* config)
{
    if (config) g_config = *config;
}

// ─────────────────────────────────────────────
//  内部实现：可选「逐地板速度倍率」
//  speedMuls[i] = 第 i 个事件所属地板的 scrFloor.speed（相对基准 BPM 的倍率）。
//  为 nullptr 时全图使用全局 speed —— 与历史版本逐事件完全一致。
//  用途：SetSpeed/BPM 事件会改变局部音符速率，而建表时只能拿到"进关那一刻"
//  的全局速度。若不逐地板取速率，变速谱面的快段仍按旧速率切片，换手相位与
//  片长全错（表现为速度一变手法就乱）。
// ─────────────────────────────────────────────
static HitEvent* BuildTechniqueHitEventsImpl(
    double* entryTimes,
    int* pressTypes,
    int* floorIndices,
    const double* speedMuls,
    int     eventCount,
    double  bpm,
    double  speed,
    int* outEventCount)
{
    *outEventCount = 0;
    if (eventCount == 0 || !entryTimes || !pressTypes || !floorIndices)
        return nullptr;

    try {
        vector<double> evTime(entryTimes, entryTimes + eventCount);
        vector<int>    evPress(pressTypes, pressTypes + eventCount);
        vector<int>    evFloor(floorIndices, floorIndices + eventCount);

        // ── 初始阈值（取第一个事件所属分段）────────────────────
        double lastSegLimit = g_config.bpmLimit;
        int    lastSegIdx   = -2;  // -2 = 未初始化
        if (g_config.segmentCount > 0 && eventCount > 0) {
            int segIdx;
            auto ec0 = ResolveConfig(evFloor[0], &segIdx);
            lastSegLimit = ec0.bpmLimit;
            lastSegIdx   = segIdx;     // 首次不触发边界重置
        }
        double nowBpm = GetAdviceBpm(bpm, speed, lastSegLimit);
        (void)nowBpm;   // 新分片模型不再使用（时间基准改为游戏给的 entryTime）

        vector<PieceInfo> pieces;
        pieces.reserve(static_cast<std::vector<PieceInfo, std::allocator<PieceInfo>>::size_type>(eventCount / 4) + 4);

        // ══════════════════════════════════════════════════════════════
        //  按拍分组（不再自造时间网格）
        // ══════════════════════════════════════════════════════════════
        //
        //  旧做法错在哪（读了游戏源码才明白）：
        //  游戏的时间是**角度驱动**的 —— scrMisc.GetTimeBetweenAngles：
        //      t = mod(exitangle - entryangle, 2π) / π × (60/bpm) / speed
        //  scrLevelMaker.CalculateFloorEntryTimes 把它逐块累加成
        //  scrFloor.entryTime。宏拿到的 entryTime 已经是**游戏算好的真实时刻**，
        //  角度公式早已应用过了。
        //
        //  但旧代码丢掉 entryTime，重新用 pLen = 60/(bpm×speed)/2 自造一个
        //  "半拍网格"去切，等于**假设每块砖固定转 2π 角度**。实际每块砖转
        //  多少角度随谱面而变（角度差 = 节奏），所以这个网格和真实节奏毫无
        //  关系 → 切出来的"片"和音符错位 → 手序乱、时长乱。
        //
        //  新做法：
        //  · 每个音用游戏给的 entryTime（已是真实时刻）；
        //  · "一拍" = 该音所在砖的本地拍长 60/(bpm×speed_of_floor)；
        //    逐块砖取，所以变速段自动按各自速度算拍长；
        //  · 同一拍内的音归同一只手（拍内同手），跨拍换手（拍间换手）；
        //  · 一片最多装该手的按键数（8 键预算），超出就切到下一拍。
        //  这样"片"永远由**音符和拍**决定，不会有"切在音中间"。
        int mainHandI2 = (g_config.handPreference == 0) ? -1 : 1;
        int nowD = 0;
        {
            int mainHandI = mainHandI2;
            int curHand   = mainHandI;
            int lastSegIdxLocal = lastSegIdx;
            double beatEnd = 0.0;      // 当前拍的结束时刻
            int    segIdxLocal = -2;
            bool   segInit = false;

            while (nowD < eventCount) {

                // 解析分段（配置分段：左右手键位/顺序/手性等）
                int sIdx;
                auto ecSeg = ResolveConfig(evFloor[nowD], &sIdx);
                if (!segInit || sIdx != segIdxLocal) {
                    // 段切换：重置为该段的主手，与旧行为一致
                    curHand = mainHandI;
                    segIdxLocal = sIdx;
                    segInit = true;
                }

                // 本地拍长：这块砖的 speed 决定一拍有多长
                double localSpeed = speed;
                if (speedMuls) {
                    int si = (nowD < eventCount) ? nowD : eventCount - 1;
                    if (si >= 0 && speedMuls[si] > 1e-9) localSpeed = speedMuls[si];
                }
                if (localSpeed < 1e-9) localSpeed = 1.0;
                double beatLen = 60.0 / (bpm * localSpeed);
                if (beatLen < 1e-9) beatLen = 1e-9;

                double t0 = evTime[nowD];
                // 新的一拍：从第一个音开始
                if (!segInit || t0 >= beatEnd - 1e-9) beatEnd = t0 + beatLen;

                int csH = (curHand == 1) ? 1 : 0;
                int maxK = (csH == 0) ? ecSeg.leftKeyCount : ecSeg.rightKeyCount;
                if (maxK < 1) maxK = 1;

                // 收集本拍内的音：不跨拍，且不超按键预算
                int cnt = 0, realCnt = 0;
                double pieceEnd = beatEnd;
                while (nowD + cnt < eventCount) {
                    double te = evTime[nowD + cnt];
                    if (te >= beatEnd - 1e-9 && cnt > 0) break;   // 跨到下一拍
                    if (realCnt + 1 > maxK) break;                 // 超按键预算
                    if (evPress[nowD + cnt] != -1) realCnt++;
                    cnt++;
                }
                if (cnt <= 0) { nowD++; continue; }   // 保险：不让死循环

                double pLen = pieceEnd - t0;
                if (pLen < 1e-9) pLen = 1e-9;

                pieces.emplace_back(cnt, csH, pLen, t0, pieceEnd, nowD, 0, realCnt);
                nowD += cnt;

                // 本拍结束 → 换手（拍内同手，拍间换手）
                if (nowD >= eventCount || evTime[nowD] >= beatEnd - 1e-9) {
                    curHand = -curHand;
                    beatEnd = (nowD < eventCount) ? evTime[nowD] + beatLen : beatEnd;
                }
            }
        }

        // 哨兵片
        if (!pieces.empty()) {
            auto& lp = pieces.back();
            pieces.emplace_back(0, 1 - lp.hand, lp.pieceLen,
                lp.endTime, lp.endTime + lp.pieceLen, nowD);
        }

        // ── 生成 HitEvent 列表 ────────────────────────────────
        vector<HitEvent> output;
        output.reserve(static_cast<std::vector<HitEvent, std::allocator<HitEvent>>::size_type>(eventCount) * 2);

        bool          activeHold = false;
        unsigned char activeHoldKey = 0;
        int           lastSegIdxEvent = -2;
        // 同键防抖状态（按手各一份）
        double        lastKeyTime[2] = { -1.0, -1.0 };
        unsigned char lastKeyUsed[2] = { 0, 0 };
        int           lastKeyHand = -1;

        for (size_t pcnt = 0; pcnt + 1 < pieces.size(); pcnt++) {
            auto& cur = pieces[pcnt];
            auto& next = pieces[pcnt + 1];
            double pStart = (pcnt > 0) ? pieces[pcnt - 1].endTime : 0.0;
            int    pieceRealIdx = 0;   // 本片内真实按键序号（长按尾不占位）

            for (int i = 0; i < cur.evCount; i++) {
                int    idx = cur.evStart + i;
                int    press = evPress[idx];
                double t = evTime[idx];

                // hold 尾：松开当前长按键
                if (press == -1) {
                    if (activeHold) {
                        HitEvent ev = {};
                        ev.TriggerTime = t;
                        ev.KeyCode = 0;
                        ev.ReleaseOnly = TRUE;
                        ev.IsHoldRelated = TRUE;
                        ev.ReleaseKeyCode = activeHoldKey;
                        output.push_back(ev);
                        activeHold = false;
                        activeHoldKey = 0;
                    }
                    continue;
                }

                // 轮指序号：只按**真实按键**递增。长按尾(press==-1)不按任何键，
                // 不占键位，用 i 会让后面的键序号顶偏一位（选错键）。
                int realIdx = pieceRealIdx++;

                // ── 按当前地板解析有效键位配置 ──────────────────
                int curFloor = (idx < (int)evFloor.size()) ? evFloor[idx] : evFloor.back();

                // ── 段边界：释放活跃 hold 键 ──
                int curSegIdx;
                auto ec = ResolveConfig(curFloor, &curSegIdx);
                if (curSegIdx != lastSegIdxEvent) {
                    if (activeHold && lastSegIdxEvent != -2) {
                        HitEvent relEv = {};
                        relEv.TriggerTime = t - 0.000001;
                        relEv.KeyCode = 0;
                        relEv.ReleaseOnly = TRUE;
                        relEv.IsHoldRelated = TRUE;
                        relEv.ReleaseKeyCode = activeHoldKey;
                        output.push_back(relEv);
                        activeHold = false;
                        activeHoldKey = 0;
                    }
                    lastSegIdxEvent = curSegIdx;
                }

                const unsigned char* keys = (cur.hand == 0) ? ec.leftKeys : ec.rightKeys;
                int                  keyCount = (cur.hand == 0) ? ec.leftKeyCount : ec.rightKeyCount;
                int** orders = (cur.hand == 0) ? ec.leftKeyOrders : ec.rightKeyOrders;
                int* orderLens = (cur.hand == 0) ? ec.leftOrderLengths : ec.rightOrderLengths;
                int                  orderCounts = (cur.hand == 0) ? ec.leftOrderCounts : ec.rightOrderCounts;
                const double* pressTimes = (cur.hand == 0) ? ec.leftPressTimes : ec.rightPressTimes;

                // 保护：若 keyCount 为 0，跳过
                if (!keys || keyCount <= 0) continue;

                // 轮指顺序：用户配置形如 "1|2,1|3,2,1|3,2,1,5|4,3,2,1|..."
                // 语义是「这一片要按几个音」→ 选第几张表；表内第几个 → 选哪个键。
                //   这一片按 1 个音 → 表[0] = [1]        → 键1（P / R）
                //   这一片按 2 个音 → 表[1] = [2,1]      → 键2、键1
                //   这一片按 3 个音 → 表[2] = [3,2,1]    → 键3、键2、键1
                // 所以 oi 由**真实按键数**决定（不是 evCount，长按尾不占位），
                // 表内位置用 realIdx（真实按键序号）。
                int oi = min(cur.realCount - 1, keyCount - 1);
                int ki;
                if (oi < orderCounts && orders && orders[oi] && realIdx < orderLens[oi])
                    ki = orders[oi][realIdx];
                else
                    ki = realIdx % keyCount;
                ki = max(0, min(ki, keyCount - 1));

                // 同键防抖：一个键刚按过，MinPressSeconds 内任何一只手都不能再按
                // 同一个键（否则会被 FixSameKeyOverlaps 压成 0ms 按压，判定窗口
                // 直接消失）。所以查**所有手**最近的按键，不只查本手 —— 实测有
                // 4 处 0.05ms 就是跨手的。
                // 注意必须**先试 ki 本身**：之前写成从 ki+1 起找，导致哪怕键1
                // 空闲也被无条件推到下一个键，右手 P 变 =、左手 R 变 3。
                if (keyCount > 1) {
                    bool busy = true;
                    for (int kk = 0; kk < keyCount && busy; kk++) {
                        int cand = (ki + kk) % keyCount;
                        unsigned char ck = keys[cand];
                        bool conflict = false;
                        for (int h2 = 0; h2 < 2; h2++) {
                            if (lastKeyTime[h2] <= 0.0) continue;
                            if (t - lastKeyTime[h2] < MinPressSeconds &&
                                lastKeyUsed[h2] == ck) { conflict = true; break; }
                        }
                        if (!conflict) { ki = cand; busy = false; }
                    }
                }
                lastKeyHand = cur.hand;
                lastKeyTime[cur.hand] = t;
                lastKeyUsed[cur.hand] = keys[ki];

                unsigned char kc = keys[ki];
                double        ratio = (pressTimes && ki < keyCount) ? pressTimes[ki] : 0.8;
                BOOL isHoldHead = (press == 2) ? TRUE : FALSE;

                // 若已有长按键且新事件是 hold 头，先强制释放
                if (isHoldHead && activeHold) {
                    HitEvent relPrev = {};
                    relPrev.TriggerTime = t - 0.000001;
                    relPrev.KeyCode = 0;
                    relPrev.ReleaseOnly = TRUE;
                    relPrev.IsHoldRelated = TRUE;
                    relPrev.ReleaseKeyCode = activeHoldKey;
                    output.push_back(relPrev);
                    activeHold = false;
                    activeHoldKey = 0;
                }

                // 按下事件
                HitEvent pressEv = {};
                pressEv.TriggerTime = t;
                pressEv.KeyCode = kc;
                pressEv.ReleaseOnly = FALSE;
                pressEv.IsHoldRelated = isHoldHead;
                pressEv.ReleaseKeyCode = 0;
                output.push_back(pressEv);

                if (isHoldHead) {
                    activeHold = true;
                    activeHoldKey = kc;
                    continue; // hold 头不插入定时松键，等待 hold 尾事件
                }

                // ── 计算松键时刻 ──────────────────────────────────
                double dur = CalculateReleaseTime(pStart, cur, next, t, ratio);
                if (dur < MinPressSeconds) dur = MinPressSeconds;   // 下限
                if (dur > MaxPressSeconds) dur = MaxPressSeconds;   // 上限
                double rel = t + dur;

                // 夹到片边界（换手前不能按住超过这片，否则会和下一片撞键）
                if (next.hand != cur.hand || next.evCount == 0) {
                    if (rel >= next.endTime) rel = next.endTime - 1e-6;
                }
                else {
                    if (rel >= cur.endTime) rel = cur.endTime - 1e-6;
                }
                if (rel <= t) rel = t + MinPressSeconds;

                HitEvent releaseEv = {};
                releaseEv.TriggerTime = rel;
                releaseEv.KeyCode = 0;
                releaseEv.ReleaseOnly = TRUE;
                releaseEv.IsHoldRelated = FALSE;
                releaseEv.ReleaseKeyCode = kc;
                output.push_back(releaseEv);
            }
        }

        // 确保最后的长按键被释放
        if (activeHold && !pieces.empty()) {
            HitEvent finalRel = {};
            finalRel.TriggerTime = pieces.back().endTime;
            finalRel.KeyCode = 0;
            finalRel.ReleaseOnly = TRUE;
            finalRel.IsHoldRelated = TRUE;
            finalRel.ReleaseKeyCode = activeHoldKey;
            output.push_back(finalRel);
        }

        FixSameKeyOverlaps(output);

        // ── 分配 CoTaskMem 并返回 ─────────────────────────────
        size_t   byteSize = output.size() * sizeof(HitEvent);
        HitEvent* result = (HitEvent*)CoTaskMemAlloc(byteSize);
        if (!result) { *outEventCount = 0; return nullptr; }
        memcpy(result, output.data(), byteSize);
        *outEventCount = (int)output.size();
        return result;

    }
    catch (...) {
        *outEventCount = 0;
        return nullptr;
    }
}

// ─────────────────────────────────────────────
//  导出函数：BuildTechniqueHitEvents（旧接口，全图单一速度）
// ─────────────────────────────────────────────
HitEvent* BuildTechniqueHitEvents(
    double* entryTimes,
    int* pressTypes,
    int* floorIndices,
    int     eventCount,
    double  bpm,
    double  speed,
    int* outEventCount)
{
    return BuildTechniqueHitEventsImpl(entryTimes, pressTypes, floorIndices,
                                       nullptr, eventCount, bpm, speed, outEventCount);
}

// ─────────────────────────────────────────────
//  导出函数：BuildTechniqueHitEventsEx（逐地板速度倍率，变速谱面用）
// ─────────────────────────────────────────────
HitEvent* BuildTechniqueHitEventsEx(
    double* entryTimes,
    int* pressTypes,
    int* floorIndices,
    double* speedMuls,
    int     eventCount,
    double  bpm,
    double  speed,
    int* outEventCount)
{
    return BuildTechniqueHitEventsImpl(entryTimes, pressTypes, floorIndices,
                                       speedMuls, eventCount, bpm, speed, outEventCount);
}

// ─────────────────────────────────────────────
//  导出函数：FreeHitEvents
// ─────────────────────────────────────────────
void FreeHitEvents(HitEvent* events)
{
    if (events) CoTaskMemFree(events);
}

// ─────────────────────────────────────────────
//  DLL 入口
// ─────────────────────────────────────────────
BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }