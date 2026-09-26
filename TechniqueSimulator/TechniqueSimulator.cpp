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
        double nowBpm = GetAdviceBpm(bpm, speed, lastSegLimit);

        // ══════════════════════════════════════════════════════════════
        //  速率容差：滑动窗口基准 + 死区
        // ══════════════════════════════════════════════════════════════
        //
        //  用户诉求：「一些小的变速上就不应该根据当前的变速执行，人的手法又不是
        //  机器，有容差和忽略」。
        //
        //  现状问题（原本每片无条件用本片地板 speed 重算 nowBpm → pLen 立刻
        //  变）：实测那张变速狂谱 12886 音里有 2130 个变速点，其中 712 处
        //  （33.4%）相对变化 <25%，每一处都让片长突变一次 → 手法纹理在小
        //  变速点上抖动，不像人。
        //
        //  试过的方案 A（照抄游戏 scrMisc.cs:304 的"全谱最高速单一基准"
        //  + ±2× 限幅）**实测无效**：那张谱速度跨度 0.14~58.67，全谱最高
        //  58.67 把基准抬得极高，±2× 限幅几乎不起作用，纹理跳变率
        //  18.7% → 18.5%，基本没动。
        //
        //  改为 B + C 组合（都零调参）：
        //   C. 滑动窗口基准 —— 基准取**最近 Window 个音的最高速率**，而不是
        //      全谱最高，局部慢的段落不会被远处的 58.67× 绑架；
        //   B. 死区 —— 候选速率相对**锁定速率**的相对变化在 DeadZone 内时
        //      直接无视，沿用锁定速率。人手不会为微小变化改指法。
        //      DeadZone < 1 时复用已有的 SpeedChangeTolerance（0 = 关闭）。
        //
        //  两者都作用在 nowBpm（片长）上，是速率决策；原来的
        //  speedChangeTolerance 只做"下一片稀疏时把 pLen 拉长"，那是几何
        //  修补，不是速率容差，两回事。
        const int    BaseWindow   = 32;    // 滑动窗口音数
        const double BaseLimitMul = 2.0;   // 局部速率相对窗口基准的允许倍数
        double deadZone = (g_config.speedChangeTolerance > 0.0)
                        ? g_config.speedChangeTolerance : 0.10;
        if (deadZone < 0.0) deadZone = 0.0;
        if (deadZone > 0.9) deadZone = 0.9;

        // 锁定速率（speed 倍率），决定片长
        double lockedRate = speed;
        if (speedMuls && eventCount > 0 && speedMuls[0] > 1e-9) lockedRate = speedMuls[0];
        nowBpm = GetAdviceBpm(bpm, lockedRate, lastSegLimit);

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
                // 段切换：锁定速率回到本段第一块地的速度（死区状态清零）
                lockedRate = speed;
                if (speedMuls && nowD < eventCount && speedMuls[nowD] > 1e-9)
                    lockedRate = speedMuls[nowD];
                nowBpm = GetAdviceBpm(bpm, lockedRate, lastSegLimit);
                lastSegIdx = curSegIdx;
            }

            // ── 速率容差：滑动窗口基准 + 死区 ──────────────────
            // 段边界只处理"配置分段"，而 SetSpeed/BPM 变化不产生分段。
            //
            // 每片不再无条件跟随本片速度，而是：
            //   1) C 滑动窗口基准：取最近 BaseWindow 个音的最高速率。
            //      局部段落不会被远处的极高速绑架（方案 A 失败的原因）。
            //   2) 限幅：局部速率夹在 [基准/2, 基准×2]。
            //   3) B 死区：候选相对 lockedRate 的变化在 deadZone 内 → 无视，
            //      沿用锁定速率。人手不会为微小变化改指法。
            if (speedMuls) {
                int si = (nowD < eventCount) ? nowD : eventCount - 1;
                if (si >= 0) {
                    // 1) 滑动窗口基准
                    double base = 0.0;
                    int w0 = si - BaseWindow + 1; if (w0 < 0) w0 = 0;
                    for (int w = w0; w <= si; w++) {
                        if (speedMuls[w] > base) base = speedMuls[w];
                    }
                    if (base <= 1e-9) base = speedMuls[si];
                    if (base <= 1e-9) base = speed;
                    double lo = base / BaseLimitMul;
                    double hi = base * BaseLimitMul;

                    double ls = speedMuls[si];
                    if (ls > 1e-9) {
                        // 2) 限幅
                        if (ls < lo) ls = lo;
                        if (ls > hi) ls = hi;
                        // 3) 死区：变化太小就不动
                        double rel = (lockedRate > 1e-9)
                                   ? fabs(ls / lockedRate - 1.0) : 1.0;
                        if (rel > deadZone) {
                            lockedRate = ls;
                        }
                        nowBpm = GetAdviceBpm(bpm, lockedRate, lastSegLimit);
                    }
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

            // ── 自适应时间片延伸（仅在下一片更稀疏时合并）────
            if (g_config.speedChangeTolerance > 0.0 && cnt > 0 && nowD + cnt < eventCount) {
                double nextEvTime = evTime[nowD + cnt];
                double diff = nextEvTime - (nowT + pLen);
                if (diff > pLen * 0.001 && diff < pLen * g_config.speedChangeTolerance) {
                    int nextCnt = CountEventsInRange(evTime, nowD + cnt, (nowT + pLen) + pLen * 0.995);
                    if (nextCnt < cnt) {
                        pLen = nextEvTime - nowT;
                    }
                }
            }

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