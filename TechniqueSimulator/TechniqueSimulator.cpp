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

        // ── 【2026-10-03 变速容差整条移除】─────────────────────
        //
        //  这里原来有一整套"变速容差"，现已删除。被删的东西：
        //    const int    BaseWindow   = 32;   // 滑动窗口音数
        //    const double BaseLimitMul = 2.0;  // 相对窗口基准的允许倍数
        //    double deadZone = (g_config.speedChangeTolerance > 0.0)
        //                    ? g_config.speedChangeTolerance : 0.50;
        //    double lockedRate;               // 跨片保持的锁定速率
        //    每片重算：取最近 BaseWindow 个音的最高速率为基准，把本片速率夹到
        //    [基准/2, 基准×2]，相对变化 < deadZone 就沿用 lockedRate。
        //    段边界把 lockedRate 重置为本段第一块地的速度。
        //  另外在主循环末尾还有一处"自适应时间片延伸"（几何修补），
        //    由 speedChangeTolerance 控制，触发条件
        //      diff > pLen*0.001 && diff < pLen*speedChangeTolerance
        //  也一并删除。
        //
        //  起因（保留作历史记录）：用户诉求「一些小的变速上就不应该根据当前
        //  的变速执行，人的手法又不是机器，有容差和忽略」。原本每片无条件用
        //  本片地板 speed 重算 nowBpm → pLen 立刻变；那张变速狂谱 12886 音里
        //  有 2130 个变速点，其中 712 处（33.4%）相对变化 <25%，每处都让片长
        //  突变一次。曾试方案 A（照抄游戏 scrMisc.cs:304 的"全谱最高速单一
        //  基准" + ±2× 限幅），**实测无效**：速度跨度 0.14~58.67 被最高速抬爆，
        //  纹理跳变率 18.7% → 18.5%。于是改成了上面那套 B+C+几何修补。
        //
        //  为什么现在整条删掉（离线 A/B 实测，见 tools\abtest\bin\ab.exe）：
        //   1. 一项两用：既改片长（速率决策）又改分片几何，绑在一个滑条上。
        //   2. 滑条语义颠倒：设为 0 时死区反而取最大 0.50，设为 0.01 时几乎
        //      为零 —— 用户根本没法关掉它。
        //   3. 死区那一路近乎无效：lockedRate 被窗口基准与限幅单向拽住后，
        //      相对变化天然极小，rel > deadZone 几乎恒成立，滑条调它没反应
        //      （实测 v_deadonly 与本版在 tol>=0.02 时输出逐位相同）。
        //      用户体感到的"必须开 0.5"其实来自几何修补那一路。
        //   4. 几何修补触发过猛：tol 上限 0.5 时，接近半拍的空隙都被拉长，
        //      连续变速谱上等于人为减少分片，片长与真实节奏越偏越远。
        //   5. 离线对拍显示它把左右手分配大面积重排（变速谱上手失配 40%~58%、
        //      键位失配 58%~80%），正是用户说的「有的手法会突然变成左撇子」
        //      「手法都很诡异」。
        //   6. 用户原话（2026-10-03）：「变速容差就是个祸害」。
        //
        //  现在片长回到最朴素的模型：逐片跟随本片速度，无窗口、无限幅、
        //  无死区、无几何修补 —— 与 2669a94（用户验证过的基线）行为一致。
        nowBpm = GetAdviceBpm(bpm, speed, lastSegLimit);

        double nowT = 0.0;
        int    nowD = 0;
        int    hand = (g_config.handPreference == 0) ? -1 : 1; // -1=左主, 1=右主
        int    mult = 0;
        long long mCnt[64] = {};   // 64 级：无上限倍乘下 mult 理论可超 16（原版 [16] 靠实测谱面不越界）
        long long mCntPre[64] = {};
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
                // 段切换：片长先回到本段基准速度
                nowBpm = GetAdviceBpm(bpm, speed, lastSegLimit);
                lastSegIdx = curSegIdx;
            }

            // ── 逐片跟随本片速度 ─────────────────────────────
            // 段边界只处理"配置分段"，而 SetSpeed/变速不产生分段，所以每片
            // 都要重新看本片所属地板的速度（这与 2669a94 基线一致）。
            // 被删除的变速容差是在这一行的**外面**又套了一层：
            // 滑动窗口基准(32 音取 max) + [基准/2, 基准×2] 限幅 + 死区 +
            // lockedRate 保持。现在只剩这一行。
            if (speedMuls) {
                int si = (nowD < eventCount) ? nowD : eventCount - 1;
                if (si >= 0) {
                    double ls = speedMuls[si];
                    if (ls > 1e-9) nowBpm = GetAdviceBpm(bpm, ls, lastSegLimit);
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

            // 按键数超限：提升倍乘（无上限 —— 原版手法拟真 potato() 语义：
            // 倍乘无顶格、片长单调缩短、cnt 必然收敛，结构上不可能振荡）
            if (cnt > maxK) {
                if (canMulti == 1 && isOffHand) needBack = true;
                mult++;
                mCnt[mult] = 0;
                continue;
            }

            // 回溯到上一片（由主手重新处理）
            if (needBack && !pieces.empty()) {
                needBack = false;
                hand = mainHand;
                auto& prev = pieces.back();
                nowT = prev.startTime;
                nowD = prev.evStart;
                memcpy(mCnt, mCntPre, sizeof(mCnt));
                mult = prev.multiplier + 1;   // 无上限：回溯后倍乘必须严格高于上一片，否则零进展振荡
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

            /*
            // ── 自适应时间片延伸（几何修补）──────────────────
            // 【2026-10-03 整条删除】原代码：
            //   if (g_config.speedChangeTolerance > 0.0 && cnt > 0 && nowD + cnt < eventCount) {
            //       double nextEvTime = evTime[nowD + cnt];
            //       double diff = nextEvTime - (nowT + pLen);
            //       if (diff > pLen * 0.001 && diff < pLen * g_config.speedChangeTolerance) {
            //           int nextCnt = CountEventsInRange(evTime, nowD + cnt, (nowT + pLen) + pLen * 0.995);
            //           if (nextCnt < cnt) pLen = nextEvTime - nowT;
            //       }
            //   }
            //  删除理由：tol 上限 0.5 时接近半拍的空隙都会被拉长，连续变速谱上
            //  等于人为减少分片，片长与真实节奏越偏越远；而且它是用户体感到的
            //  「必须开 0.5」的真正来源（速率死区那一路实测近乎无效），
            //  却与速率决策共用一个设置项 —— 一项两用，无法单独关掉。
            //  用户原话：「变速容差就是个祸害」。
            //  与上方被注释掉的"非二进制分片检测"是同一类几何修补，
            //  保持一并注释掉的状态，需要时可从这里翻出来对比。
            */

            // 提交时间片
            memcpy(mCntPre, mCnt, sizeof(mCnt));
            pieces.emplace_back(cnt, csH, pLen, nowT, nowT + pLen, nowD, mult);

            // 更新级联倍乘计数器（mult 可能超过 16：16-(mult-c) 为负时 pow 截断为 0，
            // 高级位不进账 —— 与原版手法拟真的 long 截断行为一致；只回看 32 级防负偏移）
            for (int c = mult; c > 0 && c > mult - 32; c--) {
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
//  离线轨迹求解器（"播放器"的数据来源）
//
//  为什么要有它：BuildTechniqueHitEventsImpl 是**单趟贪心** —— 每片片长由
//  「当前地板 speed 折算出的 BPM」唯一决定，然后无脑吃掉这片里的所有音。
//  它没有任何前瞻：片子一旦切歪（半个三连音、一次变速），后面整张谱的换手
//  相位就永久歪下去。这正是用户看到的「有的手法会突然变成左撇子」「忽快忽慢」。
//
//  求解器把同一件事变成搜索问题：
//    状态 =（已吃到第几个音, 当前时间, 换手相位, 倍乘, 级联节奏计数器）
//    动作 =（选一个倍乘 → 定片长 → 切这一片）以及可选的「把片边界拉到音上」
//    代价 = 单音碎片罚分 + 同手连击罚分 + 相邻片长突变罚分 + 丢音罚分
//  用束搜索（按 nowD 分层，每层保留代价最低的若干候选）挑全局最整齐的一套切法。
//  代价口径直接对齐游戏内「手法表」显示的 frag 与 longest，所以 A/B 可直接对拍。
//
//  角度定位：每个事件额外带 (floorIndex, angleFrac)。因为行星在一块砖的旋转内
//  是**匀角速度**的，而 scrLevelMaker 的 entryTime 就是按同样匀速算出来的，
//  所以「时间比例」与「角度比例」严格线性等价：
//     angleFrac = (t − entryTime(F)) / (pressTime(F) − entryTime(F))
//  运行时只要比对本砖已扫过的角度比例即可触发，完全不用看时钟。
//  按下事件的 angleFrac 恒为 1（按下时刻就是压线时刻），松键事件的 angleFrac
//  落在 (0,1) —— 这正是角度驱动需要的全部信息。
// ─────────────────────────────────────────────

static TraceStats g_lastTraceStats;

// 【2026-10-03 代价函数与指标对齐（关键）】
//
//  之前的代价函数按**时间片**记账（片级 keyRun、rough = 相邻片长比），
//  而 harness.cpp / 游戏内「手法表」统计的是**按键序列**：
//     frag    = 长度为 1 的同手连段个数
//     longest = 最长同手连段长度
//  两者差一层：连段长度 = 该连段里所有片按出的键数之和，一片不止一个键；
//  而"同手"在 harness 里是按**键位**判的（isLeftKey(KeyCode)），
//  不是按片的手标志判的。
//  实测错位的指纹：frag 1008 → 2084（+107%）、longest 2 → 117，
//  同时 leadAvg / gapSD / gapMax 与贪心**逐位相同**（几何没坏，
//  纯粹是手/键分配跑偏）。注意 longest 是"段长"不是"片数"，
//  强制每片换手也压不住它 —— 一片吃 4 个音时连段就是 4。
//
//  现在改成**按键流账本**：state 里直接维护已闭合连段的 frag/最长，
//  每切一片就把本片键数按"是否换手"记进去，代价与 harness 口径逐项对应。
struct SolveState {
    SolveState() : parent(-1), piece(0, 0, 0.0, 0.0, 0.0, 0, 0), cost(0.0), consumed(0), depth(0), nowT(0.0), prevLen(0.0), hand(1), mult(0),
                   keyRun(0), segCount(0), segFrag(0), segMax(0), segRunCost(0),
                   emptyRun(0), segIdx(-2) {
        memset(mCnt, 0, sizeof(mCnt));
    }
    int        parent;      // g_solveStates 下标，-1 = 起点
    PieceInfo  piece;       // 本状态切出的这一片
    double     cost;        // 累计代价（含本片）
    int        consumed;    // 进度：到本状态为止已吃掉的音数（**不再用作分层判据**）
    int        depth;      // 已切片的片数（束搜索的分层判据，见 SolveTechniqueTrace）
    double     nowT;        // 本片结束后的时间
    double     prevLen;     // 本片片长（给下一片算 rough 用）
    int        hand;
    int        mult;
    // ── 按键流账本（与 harness.cpp / 游戏内手法表同口径）──
    int        keyRun;      // 当前未闭合的同手连段已吃进的键数（换手时闭合）
    int        segCount;   // 已闭合的同手连段数
    int        segFrag;    // 已闭合且长度为 1 的连段数 = frag
    int        segMax;     // 已闭合的最长连段长度 = longest
    int        segRunCost;  // 已闭合各段的 (段长-2) 正部之和（增量罚项，替代只读 segMax）
    int        emptyRun;    // 连续空片数（回环地板挖出的洞）
    int        segIdx;
    long long  mCnt[16];    // 级联节奏计数器（与贪心的 mCnt 同类型）
};

static SolveOptions MakeDefaultSolveOptions()
{
    SolveOptions o;
    o.beamWidth     = 4;
    o.maxMultiplier = 7;
    o.fragPenalty   = 1.0;
    o.runPenalty    = 0.6;
    o.roughPenalty  = 0.25;
    o.dropPenalty   = 10000.0;   // 等价硬约束：丢一个音要付出任何整形代价
    return o;
}

// 把「每个判定砖的进场时刻 / 压线时刻」抽出来。判据照抄 NativeTechnique.cs：
// 每个判定砖 i 只产生一个按下事件，时刻 = 下一个判定砖的 entryTime；而
// 砖 i 自己进场（行星开始转 i 的角度）的时刻 = 上一个判定砖的按下时刻。
// 这些区间 [entryTime(F), pressTime(F)] 首尾相接、恰好铺满整张谱。
struct FloorTiming {
    vector<int>    judgedIdx;    // 按下事件在 evTime 里的下标（升序）
    vector<double> entryTime;    // 该砖旋转开始的时刻
    vector<double> pressTime;    // 该砖压线（判定）的时刻
};

static FloorTiming BuildFloorTiming(const vector<int>& evFloor,
                                    const vector<double>& evTime,
                                    const vector<int>& evPress)
{
    FloorTiming ft;
    for (size_t i = 0; i < evPress.size(); i++)
        if (evPress[i] > 0) ft.judgedIdx.push_back((int)i);
    ft.entryTime.resize(ft.judgedIdx.size());
    ft.pressTime.resize(ft.judgedIdx.size());
    for (size_t j = 0; j < ft.judgedIdx.size(); j++)
    {
        ft.pressTime[j] = evTime[ft.judgedIdx[j]];
        // 第一个判定砖之前没有任何判定砖，它的进场时刻就是 0（谱面起点）
        ft.entryTime[j] = (j == 0) ? 0.0 : evTime[ft.judgedIdx[j - 1]];
    }
    return ft;
}

// 事件时间 t 落在哪个旋转区间里 → (区间序号, 比例)
static void LocateByTime(const FloorTiming& ft, double t, int* outSeg, double* outFrac)
{
    int n = (int)ft.pressTime.size();
    if (n <= 0) { *outSeg = 0; *outFrac = 0.0; return; }
    // 找第一个 pressTime >= t
    int lo = 0, hi = n - 1, res = n - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) >> 1;
        if (ft.pressTime[mid] >= t) { res = mid; hi = mid - 1; }
        else lo = mid + 1;
    }
    double a = ft.entryTime[res], b = ft.pressTime[res];
    double f = (b - a) > 1e-12 ? (t - a) / (b - a) : 1.0;
    if (f < 0.0) f = 0.0;
    if (f > 1.0) f = 1.0;
    *outSeg = res;
    *outFrac = f;
}

// ─────────────────────────────────────────────
//  片序列 → TraceEvent 序列
//  事件生成规则照抄 BuildTechniqueHitEventsImpl 的那一段（键位解析 / 顺序表 /
//  pressTimes 比例 / hold 头尾 / 段边界释放 / 同键重叠修正），只是换成带
//  角度定位的 TraceEvent。
// ─────────────────────────────────────────────
static void EmitTraceEvents(const vector<PieceInfo>& pieces,
                            const vector<double>&   evTime,
                            const vector<int>&      evPress,
                            const vector<int>&      evFloor,
                            const FloorTiming&      ft,
                            vector<TraceEvent>&     output)
{
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

            int    tseg;
            double tfrac;
            LocateByTime(ft, t, &tseg, &tfrac);
            int tFloor = (tseg < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[tseg]] : 0;

            // hold 尾：松开当前长按键
            if (press == -1) {
                if (activeHold) {
                    TraceEvent ev = {};
                    ev.TriggerTime = t;
                    ev.AngleFrac = (float)tfrac;
                    ev.Flags = TRACE_FLAG_RELEASE_ONLY | TRACE_FLAG_HOLD_RELATED;
                    ev.ReleaseKeyCode = activeHoldKey;
                    ev.FloorIndex = tFloor;
                    output.push_back(ev);
                    activeHold = false;
                    activeHoldKey = 0;
                }
                continue;
            }

            int curFloor = (idx < (int)evFloor.size()) ? evFloor[idx] : evFloor.back();

            // 段边界：释放活跃 hold 键
            int curSegIdx;
            auto ec = ResolveConfig(curFloor, &curSegIdx);
            if (curSegIdx != lastSegIdxEvent) {
                if (activeHold && lastSegIdxEvent != -2) {
                    TraceEvent relEv = {};
                    double rt = t - 0.000001;
                    int rs; double rf;
                    LocateByTime(ft, rt, &rs, &rf);
                    relEv.TriggerTime = rt;
                    relEv.AngleFrac = (float)rf;
                    relEv.Flags = TRACE_FLAG_RELEASE_ONLY | TRACE_FLAG_HOLD_RELATED;
                    relEv.ReleaseKeyCode = activeHoldKey;
                    relEv.FloorIndex = (rs < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs]] : tFloor;
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
            bool isHoldHead = (press == 2);

            if (isHoldHead && activeHold) {
                TraceEvent relPrev = {};
                double rt = t - 0.000001;
                int rs; double rf;
                LocateByTime(ft, rt, &rs, &rf);
                relPrev.TriggerTime = rt;
                relPrev.AngleFrac = (float)rf;
                relPrev.Flags = TRACE_FLAG_RELEASE_ONLY | TRACE_FLAG_HOLD_RELATED;
                relPrev.ReleaseKeyCode = activeHoldKey;
                relPrev.FloorIndex = (rs < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs]] : tFloor;
                output.push_back(relPrev);
                activeHold = false;
                activeHoldKey = 0;
            }

            TraceEvent pressEv = {};
            pressEv.TriggerTime = t;
            pressEv.AngleFrac = (float)tfrac;
            pressEv.KeyCode = kc;
            pressEv.Flags = isHoldHead ? TRACE_FLAG_HOLD_RELATED : 0;
            pressEv.FloorIndex = curFloor;
            output.push_back(pressEv);

            if (isHoldHead) {
                activeHold = true;
                activeHoldKey = kc;
                continue;
            }

            double dur = CalculateReleaseTime(pStart, cur, next, t, ratio);
            double rel = t + dur;

            if (next.hand != cur.hand || next.evCount == 0) {
                if (rel >= next.endTime) rel = next.endTime - 1e-6;
            }
            else {
                if (rel >= cur.endTime) rel = cur.endTime - 1e-6;
            }
            if (rel <= t) rel = t + (next.endTime - t) * 0.4;

            TraceEvent releaseEv = {};
            releaseEv.TriggerTime = rel;
            int rs; double rf;
            LocateByTime(ft, rel, &rs, &rf);
            releaseEv.AngleFrac = (float)rf;
            releaseEv.Flags = TRACE_FLAG_RELEASE_ONLY;
            releaseEv.ReleaseKeyCode = kc;
            releaseEv.FloorIndex = (rs < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs]] : curFloor;
            output.push_back(releaseEv);
        }
    }

    if (activeHold && !pieces.empty()) {
        TraceEvent finalRel = {};
        finalRel.TriggerTime = pieces.back().endTime;
        int rs; double rf;
        LocateByTime(ft, finalRel.TriggerTime, &rs, &rf);
        finalRel.AngleFrac = (float)rf;
        finalRel.Flags = TRACE_FLAG_RELEASE_ONLY | TRACE_FLAG_HOLD_RELATED;
        finalRel.ReleaseKeyCode = activeHoldKey;
        finalRel.FloorIndex = (rs < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs]] : 0;
        output.push_back(finalRel);
    }

    // 同键重叠修正（TraceEvent 版）
    if (output.empty()) return;
    sort(output.begin(), output.end(),
        [](const TraceEvent& a, const TraceEvent& b) { return a.TriggerTime < b.TriggerTime; });
    map<unsigned char, int> pending;
    int n = (int)output.size();
    for (int i = 0; i < n; i++) {
        auto& ev = output[i];
        if (ev.Flags & TRACE_FLAG_RELEASE_ONLY) {
            if (ev.ReleaseKeyCode != 0) pending.erase(ev.ReleaseKeyCode);
            continue;
        }
        if (ev.KeyCode == 0) continue;
        auto it = pending.find(ev.KeyCode);
        if (it != pending.end()) {
            auto& relEv = output[it->second];
            if (relEv.TriggerTime >= ev.TriggerTime) {
                relEv.TriggerTime = ev.TriggerTime - 1e-6;
                // 时间被前移后角度比例也要跟着回退，否则角度驱动会晚触发
                int rs; double rf;
                LocateByTime(ft, relEv.TriggerTime, &rs, &rf);
                relEv.AngleFrac = (float)rf;
                relEv.FloorIndex = (rs < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs]] : ev.FloorIndex;
            }
            pending.erase(it);
        }
        for (int j = i + 1; j < n; j++) {
            auto& fwd = output[j];
            if ((fwd.Flags & TRACE_FLAG_RELEASE_ONLY) && fwd.ReleaseKeyCode == ev.KeyCode
                && !(fwd.Flags & TRACE_FLAG_HOLD_RELATED)) {
                pending[ev.KeyCode] = j;
                break;
            }
        }
    }
    sort(output.begin(), output.end(),
        [](const TraceEvent& a, const TraceEvent& b) { return a.TriggerTime < b.TriggerTime; });
}

// ─────────────────────────────────────────────
//  束搜索核心
// ─────────────────────────────────────────────
//
// 【2026-10-03 三处正确性修复】第一版搜索写错了，离线 A/B（tools\abtest\bin\
// solver_ab.exe）实测 frag 反而 +52.5%、wild 谱丢 230 个音、每行 cost 都是 0。
// 根因与修法：
//
//  1) 悬垂引用：ExpandState 里 `const SolveState& st = states[si]` 一直活到
//     push_back(ns) 之后，而 push_back 可能触发 vector 扩容 → st 指向已释放的
//     内存。修法：先把父状态整份**拷贝**出来，绝不在扩容期间持有引用。
//
//  2) 分层判据反了：原来按"吃到的音最少"分层，可每片吃到的音越多（越贪心）
//     代价往往越低，于是永远在"一口气吃掉最多音"的那条路上被窄化 —— 束搜索
//     退化成贪心，cost 全 0、nodes=1。修法：按**消费进度**（已吃音数）分层，
//     同一进度层内比代价，这才是束搜索的正确定义。
//
//  3) 丢音：原来只在"找不到完整解"时补贪心兜底，而 wild 谱在 nodeCap /
//     layerGuard 上就断了，属于"有候选但没走到终点"，结果 pieces 覆盖不到谱尾，
//     230 个音凭空消失。修法：兜底补片条件改成"覆盖不足就补"，与是否有
//     best 无关；同时把 nodeCap / layerGuard 放宽。
//
//  另外把 `emptyRun` 的处理简化：空片不再清零 keyRun 的记账口径（原来
//  空片后 keyRun 归 0 会让"空片夹在同手连击中间"这种形态反而显得更优）。

// 搜索期共享的工作缓冲。求解器一次性调用，不并发。
static vector<SolveState> g_solveStates;

// 单片的代价。返回 INFINITY 表示这一步不可行（键数不足 / 越不过去）。
//
// 【2026-10-03 代价函数第三次重写（与手法表逐项对齐）】
// 前两次都跑偏（frag 1008 → 1781 → 2084）。根因不是参数没调好，而是
// 记账维度错了两次：
//   ① 按**片**记 frag/cnt==1 —— frag 记的是按键序列里的孤立敲，一片可能
//      与下一片同手并进同一条连段，片级判据根本不是 frag。
//   ② 改成按**键**记 keyRun，但仍用"增量代价"结算 —— frag 是"连段闭合
//      后长度恰为 1"的**后验**量，只有在连段闭合的那一刻才知道。
// 现在改成闭合式记账：state 携带 segFrag/segMax，代价 = 账本快照值。
// 口径与 tools\abtest\solver_ab.cpp 的 Analyze() 一一对应。
static double PieceCost(const SolveOptions& opt, int cnt, int keyCount,
                        double pLen, double prevLen,
                        int keyRun, int emptyRun)
{
    if (keyCount <= 0) return INFINITY;

    double c = 0.0;

    // 本片按 cnt 个键，同手连段长度 = 之前的 keyRun + cnt
    int run = keyRun + cnt;

    // frag：只有当上一段不是同手（keyRun==0）且本片只按 1 个键时，
    // 才是新的"孤立一敲"。
    if (cnt == 1 && keyRun == 0) c += opt.fragPenalty;

    // 同手连击超过 2 键开始罚
    if (run > 2) c += opt.runPenalty * (double)(run - 2);

    // 相邻片长突变：忽快忽慢的观感来源。用对数比，对 ±1 倍的变化对称。
    if (prevLen > 1e-12 && pLen > 1e-12)
        c += opt.roughPenalty * fabs(log(pLen / prevLen));

    // 空片：回环地板挖出的时间洞。空洞本身不可避免（不推进时间轴就没法覆盖后面的音），
    // 但连续空片会持续翻手相位，所以只罚"连续"，且空片**不计入 keyRun**
    // （它没按键，Analyze() 里也不会产生 frag）。
    if (cnt == 0) c += opt.fragPenalty * 0.5 * (double)(emptyRun + 1);

    return c;
}

// 【2026-10-03】上面那个 PieceCost 保留作"平滑项"（rough），frag/run 改由
// 下面的 ClosedLedger 记账 —— 因为 frag 是连段闭合时的**后验**量。
// 用平滑项 + 闭合账本合起来当一家的代价。
struct Ledger {
    int keyRun = 0, segCount = 0, segFrag = 0, segMax = 0;
    // 【2026-10-03 第 6 轮修复：longest 罚项原本是"读最大值"，等于只罚一次】
    // 原来 LedgerCost 里是 `if (L.segMax > 2) c += runPenalty*(segMax-2)`。
    // segMax 是**单调不减的最大值**：只要谱里出现过一条 20 长的同手连段，
    // 之后再多开 99 条 20 长的，罚项纹丝不动 —— 优化器完全没有动力去压 longest。
    // 这正是实测 longest 2→32（sec16）、3→117（surge）而代价反而变小的原因：
    // 代价函数根本没在罚它。
    // 改成**按段增量计费**：每闭合一段就累加 max(0, 该段长-2)。
    // 这样"多一条长段"永远要额外付钱，量纲也和 frag 一致（都是段数的和）。
    int segRunCost = 0;   // 已闭合各段的 (段长-2) 正部之和
};

// 把本片（cnt 个键、是否换手）记进账本。closedFrag/closedMax 返回本步闭合掉
// 的那个连段的长度（0 表示没闭合）。
static int LedgerApply(Ledger& L, int cnt, bool switchedHand)
{
    if (cnt <= 0) return 0;                      // 空片不按键，不进账
    int closed = 0;
    if (switchedHand) {                          // 换手 → 闭合上一段
        closed = L.keyRun;
        if (closed > 0) {
            L.segCount++;
            if (closed == 1) L.segFrag++;
            if (closed > L.segMax) L.segMax = closed;
            if (closed > 2) L.segRunCost += closed - 2;
        }
        L.keyRun = 0;
    }
    L.keyRun += cnt;
    return closed;
}

// 代价 = **增量结算**。逐项对应 harness 的 frag / longest 口径，
// 但每一分钱都在被搜索的那一步当场付清（原来返回账本快照，会造成 deferred charge）。
//
// 【2026-10-03 修复 D：退款必须以"本片真的加了键"为前提】
// 原式 `else if (prevRun == 1) c -= fragPenalty;` 漏了 `cnt > 0`。
// 空片（回环地板挖出的洞）加了 0 个键，keyRun 仍是 1，next 非空片照样退款 ——
// 于是"多插几个空片"能无限退款，实测出现 **cost = −0.5** 的负代价解，
// 求解器开始主动追求负分（并因此丢掉 160~283 个音）。
// 只有当本片确实补进 1 个键（把 1 键段补成 2 键段）才该退那笔预付。
// 【2026-10-03 修复 F：frag 必须"开段预付一次 + 闭合时结算"，且预付/退款不能串味】
//
// 前两版都错在把 frag 的收/退绑在了**局部**条件上：
//  ① `closed == 1 → +frag` 与 `prevRun==0 && cnt==1 → +frag` 双重收费
//     （长度 1 的段被罚 2.0，长度 2 的段罚 0.0 —— 权重翻倍且开段/闭段不同价）；
//  ② `prevRun == 1 → −frag` 完全没看**这一片是否与上一段同手**。
//     同手时是"把 1 键段补成 2 键段"，该退；
//     换手时 prevRun=1 意味着**长度 1 的段正在闭合**，预付本来就该留下，
//     却在这里退了款 —— 于是 frag 被"洗白"，代价函数奖励制造孤立一敲。
//
// 现在用与 harness 严格等价的 potential 记账：
//   · 开段（prevRun==0 且本片有键）：+fragPenalty            —— 预付押金
//   · 补键（prevRun==1 且**同手**延伸）：−fragPenalty        —— 押金退回，段长≥2 不算 frag
//   · 闭合（closed≥2，即该段最终长度≥2）：−fragPenalty       —— 押金退回
//   · 闭合长度 1：不退，押金留下 = 一条 frag
// 每段恰好收一次、开段与闭段同价，且没有未入账的尾巴，束宽终于有判据。
//
// 【2026-10-03 修复 H（推翻 G，改用 potential 望远镜求和）】
// G 版的"开段收、闭合退"在**同手延伸**时会把押金退两次：
//   prevRun==1 且同手 → 退 −frag；随后该段闭合且长度≥2 → 又退 −frag。
// 一条 3 键同手连段净收 1 − 2 = −1 的**负代价**。
// 这类"手势推演式"的 if 列表越补越漏，已经错了四轮。
// 正确做法是 potential（望远镜求和）：cost = Φ(终) − Φ(初)，无重复、无遗漏。
// 取 Φ(run) = fragPenalty·[run == 1]，则一步增量恰好是：
//   · run 0→1（新段开张）    +fragPenalty   ← 这就是 frag 的全部来源
//   · run 1→2（补键救活）    −fragPenalty
//   · run ≥2→≥2（含闭合）      0
//   · run 1→0（孤立一敲闭合）  0   ← 押金留下 = 一条 frag，天然正确
// 加上 runPenalty·max(0, run−2) 对超长段的线性罚，逐段结算。
static double LedgerCost(const SolveOptions& opt, int closed, int prevRun, int cnt)
{
    int run = prevRun + cnt;                 // 本片之后未闭合段的长度（换手时 prevRun 归零）
    if (closed > 0) run = closed + cnt;      // 换手：旧段以 closed 收尾，新段从 cnt 起
    double c = opt.fragPenalty * ((prevRun == 1 ? 1 : 0) - (run == 1 ? 1 : 0));
    if (run > 2) c += opt.runPenalty * (double)(run - 2);
    return c;
}

// 展开一个状态：把父状态整份拷出来，再枚举所有可行的倍乘。
// 关键：父状态按**值**取，且 parent 索引用形参传进来 ——
// 绝不在 push_back 可能扩容的期间持有 g_solveStates 的引用。
static void ExpandState(const SolveOptions& opt,
                        const SolveState& st, int si,
                        const vector<double>& evTime,
                        const vector<int>&    evFloor,
                        const double* speedMuls, int eventCount,
                        double bpm, double speed,
                        vector<int>& outNew)
{
    // 关键：start 必须是**下一个未消费的音**，即父片末尾，不是父片的起点。
    //
    // 【2026-10-03 致命 bug：片链索引不推进】
    // 这里原来写的是 `int start = st.piece.evStart;` —— 也就是"父片自己的起点"。
    // 于是每一层都在**同一个事件索引**上重新计数、重新生成按键：
    //   flat1x 400 音 → 求解器吐 540 键；sparse 120 音 → 239 键；
    //   wild 1000 音 → 只推进了 649 键（其余全被反复重发/覆盖，丢 351 音）。
    // 贪心基线是对的：片落库时 evStart = nowD，下一轮 nowD += cnt
    // （cpp 贪心 :400 / :409）。求解器必须同样把片首推过去。
    int start = st.piece.evStart + st.piece.evCount;
    if (start >= eventCount) return;

    // 段边界 → 解析有效配置与该手的键数上限
    int curSegIdx;
    auto ec = ResolveConfig(evFloor[start], &curSegIdx);

    // 本片所属地板的速度 → 折算成"建议 BPM"（与贪心同口径，保证可比）
    double nowBpm = GetAdviceBpm(bpm, speed, ec.bpmLimit);
    if (speedMuls) {
        double ls = speedMuls[start];
        if (ls > 1e-9) nowBpm = GetAdviceBpm(bpm, ls, ec.bpmLimit);
    }

    // 本片用手：**可搜索**。
    //
    // 【2026-10-03 动作空间修复】原来这里是
    //     int curHand = st.hand;            // 只能沿用上一片的手
    //     ns.hand = -curHand;               // 下一片强制换手
    // 即"换手"根本没有决策权，frag 完全由片长决定，搜索器改不了分毫。
    // 贪心能拿 frag=0（flat1x）是**振荡式**换手撞出来的，
    // 不是它算出来的 —— 代价函数再准也无从下手。
    // 现在把"用哪只手"变成显式动作：沿用上一片 / 换手，两条都入束。
    //
    // 代价：分支 ×2。换手的收益体现在 segFrag 闭合（避免孤立一敲），
    // 不换手的收益体现在避免片长突变（rough）。两者都由账本统一裁决。
    // 注意：贪心的"每片强制换手"仍保留为一条可达路径（沿用/换手都能走到它），
    // 所以求解结果**至少**不比贪心差（配合层内 cost 排序与 best 取最小）。
    for (int useHandI = 0; useHandI < 2; useHandI++)
    {
    // curHand = 上一片之后的"当前手"；useHandI==0 沿用，1 表示换手。
    int curHand = st.hand;
    int csH = (curHand == 1) ? 1 : 0;
    if (useHandI) csH = 1 - csH;
    int maxK = (csH == 0) ? ec.leftKeyCount : ec.rightKeyCount;
    if (maxK <= 0) return;                 // 这只手没有配置按键 → 不可能按下去

    // 候选倍乘。
    //
    // 【2026-10-03 修复 C：倍乘必须允许"降回来"，否则贪心路径不在搜索空间里】
    // 贪心基线的 mult 是**振荡**的（cpp 贪心 :341 与 :407）：
    //     if (cnt > maxK) mult++;                       // 密的地方提
    //     while (mult > 0 && mCnt[mult] == 0) mult--;  // 级联满了再降
    // 而求解器原来写的是 `for (mult = st.mult; ...)` —— mult 只能单调不减。
    // 于是：某个密段一旦把 mult 顶到 5，之后**永远回不去**，整条片链都按
    // 半拍切 → 每片只吃 1 个音 → frag（单键孤press）暴涨、pieceLenVariance
    // 塌到 1e-8 量级、nodes 退化成 1。
    // 实测（修复前）：flat1x frag 0 -> 28；pieceVar 1.12e-03；
    //   step8_jit / wild / surge 三个用例 nodes=1、cost=0.0 —— 搜索完全躺平。
    //
    // 现在 mult 从 0 全枚举（含降档），贪心路径重新可达。
    // 代价：分支数从 ≤8 变成 8×8，但 beam 剪枝吸收得住（见层内去重）。
    for (int mult = 0; mult <= opt.maxMultiplier; mult++) {
        double pLen = 60.0 / (nowBpm * pow(2.0, mult)) / 2.0;
        if (pLen < 1e-9) pLen = 1e-9;
        // 计数与贪心同口径：贪心是 CountEventsInRange(evTime, nowD, nowT + pLen*0.995)
        // （cpp 贪心 :332）。两处口径必须一致，否则片链与贪心不可比。
        int cnt = CountEventsInRange(evTime, start, st.piece.endTime + pLen * 0.995);
        // 【2026-10-03 关键修复】键数上限的可行性保护。
        // 降倍乘 → 片更长 → cnt 更大，所以 cnt 随 mult 单调**不增**：
        // 还超上限说明还能靠升倍乘再切短，continue 试下一个；
        // 但**倍乘到顶了还超上限，就必须照抄贪心的夹取**
        // （cpp 贪心 :341-343 的 `else { cnt = maxK; }`），
        // 不能把这种非法状态放进束里。
        // 否则会切出"一片按 22 个键"这种 2 根手指按不出来的片 ——
        // 实测正是如此：flat1x longest 2→22、dense16 2→62、frag 全线翻倍。
        if (cnt > maxK) {
            if (mult < opt.maxMultiplier) continue;
            cnt = maxK;
        }

        // 回环地板挖出的时间洞（angleLength 绕回起点，判定点仍在）：贪心靠
        // "片长自然扫不到任何音"形成 cnt==0 的空片。求解器允许一次跳很长的
        // 空片，但那会让 nowT 猛冲、后续片的 rough 代价失真。空片步长单独限幅。
        if (cnt == 0 && pLen > st.piece.pieceLen * 2.0 + 1e-9) continue;

        // frag / longest 是"连段闭合"时的**后验**量，所以先把本片记进账本，
        // 再用账本快照算代价（见 LedgerApply / LedgerCost）。
        Ledger L;
        L.keyRun   = st.keyRun;
        L.segCount = st.segCount;
        L.segFrag  = st.segFrag;
        L.segMax   = st.segMax;
        L.segRunCost = st.segRunCost;
        // 「是否换手」按**按键侧**判：上一片的手 st.piece.hand vs 本片 csH。
        //
        // 【2026-10-03 致命记账 bug（解释了前三轮为什么白改）】
        // 这里原来写的是 `st.hand != curHand`，而 curHand = st.hand —— 恒为假！
        // 于是 LedgerApply 从不"闭合"连段：segCount/segFrag/segMax 恒为 0，
        // frag 罚项**完全失效**（代价里只剩 rough），优化器等于在盲飞。
        // 症状很隐蔽：实测代价从 14981 降到 12430（frag 项消失 → 代价变小），
        // 但真实 frag 反而从 2084 涨到 2075 —— 越"优化"越糟，因为它优化的量
        // 根本不是 frag。
        int prevRun = st.keyRun;
        bool switched = (st.piece.hand != csH);
        int closed = LedgerApply(L, cnt, switched);

        // rough（相邻片长突变）与空片连跑：形态项，仍是逐步结算。
        double cost = LedgerCost(opt, closed, prevRun, cnt);
        if (st.prevLen > 1e-12 && pLen > 1e-12)
            cost += opt.roughPenalty * fabs(log(pLen / st.prevLen));
        if (cnt == 0) cost += opt.fragPenalty * 0.5 * (double)(st.emptyRun + 1);
        if (!isfinite(cost)) continue;

        SolveState ns;
        ns.parent = si;
        ns.prevLen = pLen;
        // 下一片的"当前手"= 本片实际用的手（换手与否已含在 csH 里）
        ns.hand = (csH == 1) ? 1 : -1;
        ns.mult = mult;
        ns.segIdx = curSegIdx;
        ns.emptyRun = (cnt == 0) ? st.emptyRun + 1 : 0;
        ns.keyRun    = L.keyRun;
        ns.segCount  = L.segCount;
        ns.segFrag   = L.segFrag;
        ns.segMax    = L.segMax;
        ns.segRunCost = L.segRunCost;
        ns.nowT = st.piece.endTime + pLen;
        memcpy(ns.mCnt, st.mCnt, sizeof(ns.mCnt));
        // 级联节奏计数器（与贪心同口径）
        for (int c2 = mult; c2 > 0; c2--) {
            ns.mCnt[c2] += (long long)pow(2, 16 - (mult - c2));
            ns.mCnt[c2] %= (1LL << 18);
        }
        ns.piece = PieceInfo(cnt, csH, pLen, st.piece.endTime, st.piece.endTime + pLen,
                             start, mult);
        ns.consumed = st.consumed + cnt;   // 进度：已吃掉的音数（终局判定 + 兜底选优用）
        ns.depth   = st.depth + 1;    // 片数：分层判据
        ns.cost = st.cost + cost;

        outNew.push_back((int)g_solveStates.size());
        g_solveStates.push_back(ns);
    }
    }  // end useHandI（换手/不换手 两条动作）
}

// ─────────────────────────────────────────────
//  导出函数：SolveTechniqueTrace
// ─────────────────────────────────────────────
TraceEvent* SolveTechniqueTrace(
    double* entryTimes,
    int*    pressTypes,
    int*    floorIndices,
    double* speedMuls,
    int     eventCount,
    double  bpm,
    double  speed,
    SolveOptions* opts,
    int* outEventCount)
{
    *outEventCount = 0;
    memset(&g_lastTraceStats, 0, sizeof(g_lastTraceStats));
    if (eventCount == 0 || !entryTimes || !pressTypes || !floorIndices)
        return nullptr;

    try {
        SolveOptions opt = opts ? *opts : MakeDefaultSolveOptions();
        if (opt.beamWidth < 1) opt.beamWidth = 1;
        if (opt.maxMultiplier < 0) opt.maxMultiplier = 0;
        if (opt.maxMultiplier > 7) opt.maxMultiplier = 7;

        vector<double> evTime(entryTimes, entryTimes + eventCount);
        vector<int>    evPress(pressTypes, pressTypes + eventCount);
        vector<int>    evFloor(floorIndices, floorIndices + eventCount);
        FloorTiming ft = BuildFloorTiming(evFloor, evTime, evPress);

        vector<double> localSpeed;
        const double* localSpeedPtr = nullptr;
        if (speedMuls) {
            localSpeed.assign(speedMuls, speedMuls + eventCount);
            localSpeedPtr = localSpeed.data();
        }

        g_solveStates.clear();
        SolveState root;
        root.parent = -1;
        root.piece = PieceInfo(0, 0, 0.0, 0.0, 0.0, 0, 0);
        root.cost = 0.0;
        root.consumed = 0;
        root.depth = 0;
        root.nowT = 0.0;
        root.prevLen = 0.0;
        root.hand = (g_config.handPreference == 0) ? -1 : 1;
        root.mult = 0;
        root.keyRun = 0;
        root.segCount = 0;
        root.segFrag = 0;
        root.segMax = 0;
        root.segRunCost = 0;
        root.emptyRun = 0;
        root.segIdx = -2;
        memset(root.mCnt, 0, sizeof(root.mCnt));
        g_solveStates.push_back(root);

        // ── 束搜索主循环 ───────────────────────────────────
        // 【2026-10-03 修复 I：分层判据必须用"片数"，不能用"已吃音数"】
        // 原先按 `consumed`（已吃音数）分层，取 minConsumed 那一层推进。
        // 但 cnt 是 `CountEventsInRange(..., nowT + pLen*0.995)` 的结果，
        // 而 pLen 随倍乘**指数缩短**：mult=7 那一档半拍只够扫到 1 个音。
        // 于是"最小已吃音数"这一层**恒定由 mult=7 的 1 键片组成**，
        // 束搜索被永久锁死在"每片只按 1 个键"的动作空间里 —— 正好是
        // frag 最多的形态，也正是贪心用它 frag=0 的反面。
        // 实测印证：solver 的 runs 普遍 < greedy（481 vs 373），
        // 却 frag 高一倍 —— 多出的段全是 1 键孤敲。
        // 正确分层：片数 `depth`。每片的 cnt 自由（贪心的 2 键片自然可达），
        // 层内仍按累计代价排序取束宽。
        vector<int> frontier; frontier.push_back(0);
        // 【2026-10-03 修复 E：节点上限不能用"常数×音数"】
        // 原来是 nodeCap = eventCount*64 + 4096，实测**每一个 beam 宽度都刚好顶满**
        // （16/48/128 三个宽度 nodes 都停在 ~42.5k = 800*64+4096），
        // 说明上限才是真正的停止条件，束宽根本管不着：
        //   · beam 越大，每层展开的候选越多 → 同样的节点预算**更早耗尽**
        //     → 搜索被截断 → 160~283 个音被丢（硬约束直接破）。
        // 也就是说"加大束宽"一直在反向操作：它让丢音变多而不是变少。
        // 现在把上限放到"与束宽和分支数成正比"，让**层数**（= 音数）成为
        // 真正的推进驱动力，而不是节点总数。
        // 上限仍保留，纯粹是防病态谱（超大谱 + 大束宽）把内存吃光；
        // 触发时记 solverNodes 便于事后核对是否截断。
        size_t nodeCap = (size_t)eventCount * (size_t)(opt.beamWidth * 64 + 1024) + 1048576;
        int    layerGuard = 0;
        int    best = -1;
        bool   capped = false;

        // 收紧下限：nodeCap 只是防爆内存的兜底，层推进由 layerGuard 主导。
        // 【2026-10-03 修复 G：终局候选必须与"部分解"分开记分】
        // 下面马上要修的是 best 的选取；这里先把 beam=128 复现的 154 丢音
        // 钉死为"必须修复"的对照基线。

        while (!frontier.empty() &&
               g_solveStates.size() < nodeCap &&
               layerGuard++ < eventCount * 4 + 64)
        {
            vector<int> cand;
            for (int si : frontier) {
                // 已吃完所有音 → 终局候选
                if (g_solveStates[si].consumed >= eventCount) {
                    if (best < 0 || g_solveStates[si].cost < g_solveStates[best].cost)
                        best = si;
                    continue;
                }
                // 父状态**按值拷贝**，否则 push_back 扩容会让引用悬空
                SolveState st = g_solveStates[si];
                vector<int> produced;
                ExpandState(opt, st, si, evTime, evFloor,
                            localSpeedPtr, eventCount, bpm, speed, produced);
                for (int p : produced) cand.push_back(p);
            }
            if (cand.empty()) break;

            // 同一"片数"深度内取代价最小的一层（片数每步 +1，深度单调，搜索必然推进）
            int minDepth = INT_MAX;
            for (int c2 : cand)
                minDepth = min(minDepth, g_solveStates[c2].depth);
            vector<int> layer;
            for (int c2 : cand)
                if (g_solveStates[c2].depth == minDepth) layer.push_back(c2);
            if (layer.empty()) break;

            sort(layer.begin(), layer.end(), [&](int a, int b2) {
                if (g_solveStates[a].cost != g_solveStates[b2].cost)
                    return g_solveStates[a].cost < g_solveStates[b2].cost;
                return a < b2;
            });
            // 同 (进度, 手, 倍乘, 连击, 片长) 只留最省的一个。
                    // 注意账本字段（segFrag/segMax/segCount）**不入去重键**：
                    // 它们是代价的来源，用它们去重会把不同代价的候选提前合并，
                    // 等于绕过排序直接丢状态。
                    vector<int> pruned;
                    for (int c2 : layer) {
                        bool dup = false;
                        for (int p : pruned) {
                            const SolveState& A = g_solveStates[c2];
                            const SolveState& B = g_solveStates[p];
                            if (A.consumed == B.consumed && A.hand == B.hand
                                && A.mult == B.mult && A.keyRun == B.keyRun
                                && A.emptyRun == B.emptyRun
                                && fabs(A.piece.endTime - B.piece.endTime) < 1e-9) { dup = true; break; }
                        }
                if (!dup) pruned.push_back(c2);
                if ((int)pruned.size() >= opt.beamWidth) break;
            }
            if (pruned.empty()) break;
            frontier = pruned;
        }

        // 兜底：没有走到终局的完整解时，取"吃到的音最多、代价最低"的那个。
        // （变量名不能用 far —— Windows SDK 头里有同名历史宏，会展开成空。）
        // 之后还有一道"覆盖不足就补贪心片"的兜底，保证零丢音（见文件头第 3 条）。
        if (best < 0) {
            int bestPartial = 0;
            for (size_t i = 1; i < g_solveStates.size(); i++) {
                const SolveState& A = g_solveStates[i];
                const SolveState& B = g_solveStates[bestPartial];
                if (A.consumed > B.consumed ||
                    (A.consumed == B.consumed && A.cost < B.cost))
                    bestPartial = (int)i;
            }
            best = bestPartial;
        }

        // 回溯出片序列
        vector<PieceInfo> pieces;
        for (int c2 = best; c2 > 0; c2 = g_solveStates[c2].parent)
            pieces.push_back(g_solveStates[c2].piece);
        reverse(pieces.begin(), pieces.end());

        // 覆盖不足的部分：补贪心兜底片，保证一个音都不丢
        {
            int covered = pieces.empty() ? 0 : pieces.back().evStart + pieces.back().evCount;
            double nowT = pieces.empty() ? 0.0 : pieces.back().endTime;
            int handNow = (g_config.handPreference == 0) ? -1 : 1;
            int multNow = 0;
            int guard = 0;
            while (covered < eventCount && guard++ < eventCount * 4) {
                int segIdx;
                auto ec = ResolveConfig(evFloor[covered], &segIdx);
                double nowBpm = GetAdviceBpm(bpm, speed, ec.bpmLimit);
                if (speedMuls) {
                    double ls = speedMuls[min(covered, eventCount - 1)];
                    if (ls > 1e-9) nowBpm = GetAdviceBpm(bpm, ls, ec.bpmLimit);
                }
                double pLen = 60.0 / (nowBpm * pow(2.0, multNow)) / 2.0;
                if (pLen < 1e-9) pLen = 1e-9;
                int csH = (handNow == 1) ? 1 : 0;
                int maxK = (csH == 0) ? ec.leftKeyCount : ec.rightKeyCount;
                int cnt = CountEventsInRange(evTime, covered, nowT + pLen * 0.995);
                if (maxK > 0 && cnt > maxK) {
                    if (multNow < opt.maxMultiplier) { multNow++; continue; }
                    cnt = maxK;
                }
                pieces.push_back(PieceInfo(cnt, csH, pLen, nowT, nowT + pLen, covered, multNow));
                covered += cnt;
                nowT += pLen;
                handNow = -handNow;
            }
        }

        // 哨兵片
        if (!pieces.empty()) {
            const PieceInfo& lp = pieces.back();
            pieces.push_back(PieceInfo(0, 1 - lp.hand, lp.pieceLen,
                                        lp.endTime, lp.endTime + lp.pieceLen,
                                        lp.evStart + lp.evCount, lp.multiplier));
        }

        vector<TraceEvent> output;
        EmitTraceEvents(pieces, evTime, evPress, evFloor, ft, output);

        // ── 统计（对齐游戏内「手法表」）──────────────────────
        int frag = 0, longest = 0, run = 0, lastHand = -1;
        for (size_t i = 0; i + 1 < pieces.size(); i++) {
            if (pieces[i].evCount == 1) frag++;
            if (pieces[i].evCount == 0) continue;
            if (pieces[i].hand == lastHand) run++; else run = 1;
            lastHand = pieces[i].hand;
            if (run > longest) longest = run;
        }
        double mean = 0.0;
        for (size_t i = 0; i + 1 < pieces.size(); i++) mean += pieces[i].pieceLen;
        mean /= (double)(pieces.size() > 1 ? pieces.size() - 1 : 1);
        double var = 0.0;
        for (size_t i = 0; i + 1 < pieces.size(); i++) {
            double d = pieces[i].pieceLen - mean;
            var += d * d;
        }
        var /= (double)(pieces.size() > 1 ? pieces.size() - 1 : 1);

        g_lastTraceStats.fragmentCount = frag;
        g_lastTraceStats.longestSameHand = longest;
        g_lastTraceStats.droppedNotes = 0;
        g_lastTraceStats.pieceCount = (int)(pieces.size() > 0 ? pieces.size() - 1 : 0);
        g_lastTraceStats.pieceLenVariance = var;
        g_lastTraceStats.totalCost = (best >= 0) ? g_solveStates[best].cost : 0.0;
        g_lastTraceStats.fragCost = frag * opt.fragPenalty;
        g_lastTraceStats.runCost = (longest > 2 ? (double)(longest - 2) : 0.0) * opt.runPenalty;
        g_lastTraceStats.roughCost = var * opt.roughPenalty;
        g_lastTraceStats.solverNodes = (int)g_solveStates.size();

        size_t byteSize = output.size() * sizeof(TraceEvent);
        TraceEvent* result = (TraceEvent*)CoTaskMemAlloc(byteSize ? byteSize : 1);
        if (!result) { *outEventCount = 0; return nullptr; }
        if (byteSize) memcpy(result, output.data(), byteSize);
        *outEventCount = (int)output.size();
        return result;
    }
    catch (...) {
        *outEventCount = 0;
        memset(&g_lastTraceStats, 0, sizeof(g_lastTraceStats));
        return nullptr;
    }
}

// ─────────────────────────────────────────────
//  导出函数：GetLastTraceStats
// ─────────────────────────────────────────────
TraceStats* GetLastTraceStats()
{
    TraceStats* p = (TraceStats*)CoTaskMemAlloc(sizeof(TraceStats));
    if (p) *p = g_lastTraceStats;
    return p;
}

// ─────────────────────────────────────────────
//  导出函数：FreeTraceEvents
// ─────────────────────────────────────────────
void FreeTraceEvents(TraceEvent* events)
{
    if (events) CoTaskMemFree(events);
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