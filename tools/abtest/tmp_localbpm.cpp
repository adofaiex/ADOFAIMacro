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
    int    evCount;
    int    hand;        // 0=左, 1=右
    double pieceLen;
    double startTime;
    double endTime;
    int    evStart;
    int    multiplier;

    PieceInfo(int ec, int h, double pl, double st, double et, int es, int mult = 0)
        : evCount(ec), hand(h), pieceLen(pl), startTime(st), endTime(et), evStart(es), multiplier(mult) {
    }
};

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

// 将**已经是 BPM 值**的速率折叠到 (limit/2, limit] 区间
// 【2026-10-03】新增：片长改用实时 BPM（逐音符实际间隔反推）后，
// nowBpm 的来源已是绝对 BPM，不再是 chartBpm × speed 的乘积，
// 所以折叠也必须直接吃绝对值，否则会多乘一次 chartBpm。
static double FoldBpm(double absBpm, double limit)
{
    // 防御死循环：limit ≤ 0 时 `r <= limit/2` 对 r==0 恒真 → 无限循环。
    // limit 来自关卡配置（磁盘 JSON）或全局设置，可能被手工改成 0/负数。
    if (limit <= 0.0) limit = 500.0;
    double r = absBpm;
    if (r <= 0.0) return limit;
    while (r > limit)      r /= 2.0;
    while (r <= limit / 2.0) r *= 2.0;
    return r;
}

// 将 chart BPM × 速率倍率折叠到 (limit/2, limit]
static double GetAdviceBpm(double bpm, double speed, double limit)
{
    return FoldBpm(bpm * speed, limit);
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
            if (relEv.TriggerTime >= ev.TriggerTime)
                relEv.TriggerTime = ev.TriggerTime - 1e-6;
            pending.erase(it);
        }

        for (int j = i + 1; j < n; j++) {
            auto& fwd = events[j];
            if (fwd.ReleaseOnly && fwd.ReleaseKeyCode == kc && !fwd.IsHoldRelated) {
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
        double nowBpm = FoldBpm(bpm, lastSegLimit);

        // ══════════════════════════════════════════════════════════════
        //  实时 BPM = 逐音符实际间隔反推
        // ══════════════════════════════════════════════════════════════
        //
        //  【2026-10-03 重写】用户原话：「我认为手法模拟的计算不应该只看当前的BPM
        //   还有同时看实时BPM 比如有的关卡会变当前的BPM和轨道进行计算从而导致
        //   实时BPM不会改变」。
        //
        //  先厘清一个事实：scrConductor.bpm 在整局游戏里**从头到尾不变**
        //  （全 Assembly-CSharp 只有 scrConductor.cs:380 `bpm = levelData.bpm;`
        //  和 scrLevelMaker.cs:132 `obj.bpm = bpm_forUnityUseOnly;` 两处赋值）。
        //  所以"实时 BPM"从来不是 conductor.bpm 在变。真正让玩家感到"这段变快/
        //  变慢"的东西，是 scrLevelMaker.cs:607-669 CalculateFloorEntryTimes
        //  算出来的**实际音符间隔**，其中 bpm 只是 GetTimeBetweenAngles
        //  （scrMisc.cs:396-400）里的一个除数，轨道几何全部直接乘了进去：
        //  角度跨度、中旋(midSpin→num4=0)、extraBeats、holdLength*2。
        //
        //  典型错例 —— 回环地板：CalculateFloorEntryTimes 里当
        //  `angleMoved >= 6.2831` 时走 `num4 = (midSpin ? 0.0 : 2.0*timeBetweenAngles)`
        //  （scrLevelMaker.cs:640-643），间隔直接翻倍 → **实际 BPM 是
        //  bpm*speed/2**。而旧实现仍按满速算 pLen = 60/(bpm*speed)/2，片长短了
        //  一半 → 一个手位塞不下 → 触发 mult++ 倍乘细分 → 换手翻倍、单音碎块暴涨，
        //  这正是用户报的"有的手法会突然变成左撇子"。
        //
        //  修法：**能测量就别推导**。真实间隔 entryTime 已经算完并摆在手边，
        //  不必照抄游戏公式反推 BPM（那样多行星/中旋/回环/长按/extraBeats 的
        //  组合逻辑要抄十几行，漏一处又是新 bug）。直接用实际间隔反推：
        //      localBpm[i] = 60 / (entryTime[i] - entryTime[i-1])
        //  再开短窗口压掉单点尖刺。speed（scrFloor.speed）自动被吸收，
        //  speedMuls 参数在位长计算里不再需要。
        //
        //  注意：这**不是**变速容差。变速容差（speedChangeTolerance）已于
        //  2026-10-03 整条删除 —— 见下方"已删除：变速容差"说明。

        // 逐音符实际间隔 → 局部 BPM（开窗取中位数，压掉单点尖刺）
        vector<double> localBpm(eventCount, 0.0);
        if (eventCount >= 2) {
            for (int i = 1; i < eventCount; i++) {
                double gap = evTime[i] - evTime[i - 1];
                localBpm[i] = (gap > 1e-9) ? (60.0 / gap) : 0.0;
            }
            localBpm[0] = localBpm[1];
        }

        //  片长直接由**实际音符间隔**决定（实时 BPM = 60 / 相邻间隔中位数），
        //  speedMuls 仅在 speedMuls==nullptr 的老接口里作为兜底来源。

        double nowT = 0.0;
        int    nowD = 0;
        int    hand = (g_config.handPreference == 0) ? -1 : 1; // -1=左主, 1=右主
        int    mult = 0;
        long long mCnt[16] = {};
        long long mCntPre[16] = {};
        int  canMulti = 0;
        bool needBack = false;

        vector<PieceInfo> pieces;
        pieces.reserve(static_cast<std::vector<PieceInfo, std::allocator<PieceInfo>>::size_type>(eventCount / 4) + 4);

        // ── 时间片划分 ────────────────────────────────────────
        while (nowD < eventCount) {

            // 根据当前地板索引解析有效配置及段索引
            int curSegIdx;
            auto ec = ResolveConfig(evFloor[nowD], &curSegIdx);

            // 段边界：重置所有连续状态（手交替·倍乘·回溯·BPM）
            if (curSegIdx != lastSegIdx) {
                hand = (g_config.handPreference == 0) ? -1 : 1;
                mult = 0;
                memset(mCnt, 0, sizeof(mCnt));
                memset(mCntPre, 0, sizeof(mCntPre));
                canMulti = 0;
                needBack = false;
                lastSegLimit = ec.bpmLimit;
                nowBpm = FoldBpm(bpm, lastSegLimit);
                lastSegIdx = curSegIdx;
            }

            // ── 实时 BPM：取当前事件附近的局部速率 ──────────────
            // 用实际音符间隔反推，不照抄游戏公式。窗口取中位数（BaseWindow/4
            // 个邻居）压掉单点尖刺 —— 回环/多行星/长按造成的极端间隔会自己
            // 被中位数滤掉一半影响，且不会像滑条那样改变片长模型。
            //
            // 【已删除：变速容差】原先这里有 C 滑动窗口取 max + ±2× 限幅 +
            // B 死区，以及底部一段"自适应时间片延伸"几何修补，全部由
            // speedChangeTolerance 控制。实测那个滑条一项两用、语义反转，
            // 且几何修补过于激进（tol=0.5 时接近半拍的空隙都被拉长），
            // 是手感不稳的主要来源之一，故 2026-10-03 整条移除。
            {
                const int LocalWin = 8;   // 中位数窗口（邻居数）
                int si = (nowD < eventCount) ? nowD : eventCount - 1;
                if (si >= 0) {
                    double lb = 0.0;
                    int w0 = si - LocalWin; if (w0 < 0) w0 = 0;
                    double buf[16];
                    int nb = 0;
                    for (int w = w0; w <= si; w++) {
                        if (localBpm[w] > 1e-9) buf[nb++] = localBpm[w];
                    }
                    if (nb > 0) {
                        // 小规模插入排序取中位数
                        for (int a = 1; a < nb; a++) {
                            double key = buf[a]; int b = a - 1;
                            while (b >= 0 && buf[b] > key) { buf[b + 1] = buf[b]; b--; }
                            buf[b + 1] = key;
                        }
                        lb = buf[nb / 2];
                    }
                    if (lb > 1e-9) nowBpm = FoldBpm(lb, lastSegLimit);
                }
            }

            // 防止死循环
            if (pieces.size() > (size_t)eventCount * 64) break;

            double pLen = 60.0 / (nowBpm * pow(2.0, mult)) / 2.0;
            if (pLen < 1e-9) pLen = 1e-9;

            int cnt = CountEventsInRange(evTime, nowD, nowT + pLen * 0.995);
            int csH = (hand == 1) ? 1 : 0;
            int maxK = (csH == 0) ? ec.leftKeyCount : ec.rightKeyCount;
            int mainHand = (g_config.handPreference == 0) ? -1 : 1;
            bool isOffHand = (hand != mainHand);

            // 按键数超限：提升倍乘
            if (cnt > maxK) {
                if (canMulti == 1 && isOffHand) needBack = true;
                if (mult < 7) { mult++; mCnt[mult] = 0; continue; }
                else { cnt = maxK; }
            }

            // 回溯到上一片（由主手重新处理）
            if (needBack && !pieces.empty()) {
                needBack = false;
                hand = mainHand;
                auto& prev = pieces.back();
                nowT = prev.startTime;
                nowD = prev.evStart;
                memcpy(mCnt, mCntPre, sizeof(mCnt));
                mult = prev.multiplier + 1;
                if (mult > 7) mult = 7;
                pieces.pop_back();
                canMulti = 0;
                continue;
            }

            /*
            // ── 非二进制分片检测（三连音/五连音自适应）──
            if (cnt > 0 && nowD + cnt < eventCount) {
                double nextEvTime = evTime[nowD + cnt];
                double boundary     = nowT + pLen;
                double diff         = nextEvTime - boundary;
                if (diff > pLen * 0.001 && diff < pLen * 0.50) {
                    // 预测不调整时下一个分片的事件数
                    // 注意 0.995 只乘在 pLen 上（与下次循环的计数范围一致）
                    int nextCnt = CountEventsInRange(evTime, nowD + cnt, boundary + pLen * 0.995);
                    // 只有当下一个分片不满（手分配不均）时才调整
                    if (nextCnt < cnt) {
                        pLen = nextEvTime - nowT;
                    }
                }
            }
            */

            // ── 已删除：自适应时间片延伸（几何修补）──────────────
            //
            // 【2026-10-03 整条移除】原代码：
            //   if (g_config.speedChangeTolerance > 0.0 && cnt > 0 && nowD+cnt < eventCount) {
            //       double nextEvTime = evTime[nowD + cnt];
            //       double diff = nextEvTime - (nowT + pLen);
            //       if (diff > pLen*0.001 && diff < pLen*g_config.speedChangeTolerance) {
            //           int nextCnt = CountEventsInRange(..., nowT+pLen+pLen*0.995);
            //           if (nextCnt < cnt) pLen = nextEvTime - nowT;
            //       }
            //   }
            //
            //  删除理由（实测，见 tools\abtest\ab.exe --sweep）：
            //   1. 这与"速率容差"语义无关 —— 它纯粹是把片长拉长去凑下一个事件，
            //      是一个**几何**修补，却和速率死区共用一个滑条（一项两用）。
            //   2. 触发条件过于激进：tol 最大 0.5 时，接近半拍的空隙都会被拉长，
            //      连续变速谱上等于人为把分片变少 → 换手变少、单音碎块变少，
            //      但同时片长与真实节奏脱节，越到后面偏移越大。
            //   3. 它的效果无法与"死区"分离：用户拖滑条调的是这一路，可这一路
            //      恰恰是"变速就换个手"的根源之一。
            //   4. 用户明确要求删除（2026-10-03：「变速容差就是个祸害」）。
            //  现在片长完全由实时 BPM（局部间隔中位数）决定，逻辑单一可解释。

            // 提交时间片
            memcpy(mCntPre, mCnt, sizeof(mCnt));
            pieces.emplace_back(cnt, csH, pLen, nowT, nowT + pLen, nowD, mult);

            // 更新级联倍乘计数器
            for (int c = mult; c > 0; c--) {
                mCnt[c] += (long long)pow(2, 16 - (mult - c));
                mCnt[c] %= (1LL << 18);
            }
            while (mult > 0 && mCnt[mult] == 0) mult--;

            nowD += cnt;
            nowT += pLen;
            hand = -hand;
            canMulti = 1;

            // 微误差矫正
            if (nowD < eventCount && fabs(evTime[nowD] - nowT) < pLen * 0.01)
                nowT = evTime[nowD];
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

        for (size_t pcnt = 0; pcnt + 1 < pieces.size(); pcnt++) {
            auto& cur = pieces[pcnt];
            auto& next = pieces[pcnt + 1];
            double pStart = (pcnt > 0) ? pieces[pcnt - 1].endTime : 0.0;

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

                int oi = min(cur.evCount - 1, keyCount - 1);
                int ki;
                if (oi < orderCounts && orders && orders[oi] && i < orderLens[oi])
                    ki = orders[oi][i];
                else
                    ki = i % keyCount;
                ki = max(0, min(ki, keyCount - 1));

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
                double rel = t + dur;

                if (next.hand != cur.hand || next.evCount == 0) {
                    if (rel >= next.endTime) rel = next.endTime - 1e-6;
                }
                else {
                    if (rel >= cur.endTime) rel = cur.endTime - 1e-6;
                }
                if (rel <= t) rel = t + (next.endTime - t) * 0.4;

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