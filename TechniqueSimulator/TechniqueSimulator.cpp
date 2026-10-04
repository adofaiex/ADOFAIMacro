#define TECHNIQUE_SIMULATOR_EXPORTS
#include "TechniqueSimulator.h"
#include <vector>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <map>
#include <objbase.h>
#include <cstdio>

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

    // 【2026-10-04 全抄对拍修正】stable_sort：与 EmitTraceEvents 同理，
    // 保持发射顺序，使同时刻事件不换位（详见 EmitTraceEvents 内的说明）。
    // 【2026-10-04 全抄对拍】同刻 tie-break：**按下在前、松键在后**。
    // 原版 check() 的强行弹起写的是 time1[R][0]-1（减 1 微秒），存进 long 后
    // 该音的松键时刻与下一音的按下时刻**恰好相等**，于是 out_() 的输出里两条
    // 落在同一微秒 —— 观测原版 main.crpl：t=10.492295 处正是
    // 「k=51 按下」在前、「k=187 松开」在后（按下优先）。
    // 本移植的 push 顺序是 press 先、release 后，稳定排序能保它；这里再写死
    // 一遍 tie-break 是为了让次序不依赖 push 顺序，语义更贴近原版发射规则。
    stable_sort(events.begin(), events.end(),
        [](const HitEvent& a, const HitEvent& b) {
            if (a.TriggerTime != b.TriggerTime) return a.TriggerTime < b.TriggerTime;
            const bool aRel = a.ReleaseOnly != 0;
            const bool bRel = b.ReleaseOnly != 0;
            return aRel != bRel ? bRel : false;
        });
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

    // 【2026-10-04 全抄对拍修正】stable_sort：与 EmitTraceEvents 同理，
    // 保持发射顺序，使同时刻事件不换位（详见 EmitTraceEvents 内的说明）。
    stable_sort(events.begin(), events.end(),
        [](const HitEvent& a, const HitEvent& b) {
            if (a.TriggerTime != b.TriggerTime) return a.TriggerTime < b.TriggerTime;
            const bool aRel = a.ReleaseOnly != 0;
            const bool bRel = b.ReleaseOnly != 0;
            return aRel != bRel ? bRel : false;
        });
}

// ─────────────────────────────────────────────
//  导出函数：SetTechniqueConfig
// ─────────────────────────────────────────────
void SetTechniqueConfig(TechniqueConfig* config)
{
    if (config) g_config = *config;
}

// ============================================================
//  忠实移植：原版「手法拟真」potato()
//  来源：D:\Projects\adofai macro\手法拟真\main.cpp:213-340
// ============================================================
//
//  【2026-10-04 全抄重构】按原版逐行重写，替换两套自研分片器
//  （单趟贪心 + 10-03 束搜索）。原版是唯一算法权威。
//
//  单位换算（原版微秒 → 本移植秒，凡阈值 5 → 5e-6）：
//    timedata[i][0] 判定时刻        → evTime[i]
//    timedata[i][1] 1/2/-1 按键语义  → evPress[i]（C# 组装层产出）
//    timedata[i][2] 轨道序号        → evFloor[i]
//    restart[i][0]  非0=重开时间片  → RestartMark（变速点）
//    multiple_counter[16]           → mCnt[64]
//    piece[psize][7] 时间片         → PieceInfo
//
//  ── 考证 A：原版 change_speed[] 恒为 0 ──────────────────────────
//  bpm_in() 只在 speeddata 行有第 4 段时写 change_speed[]（main.cpp:197-202），
//  而 timedata生成 只写 3 段（main.cpp:341-352）。所以真实运行里恒为 0：
//    · 窗口系数 (0.995-change_speed) 恒为 0.995
//    · main.cpp:264「符合变速条件」分支恒假（abs<piece_time*0，piece_time>0）
//  那段"微小变速"在原版从不执行 —— 这才是「变速容差是祸害」的正确依据。
//  本移植删掉该分支（保留 0.995），与原版逐位一致。
//
//  ── 考证 B：真正在跑的是 restart 分支（main.cpp:223-250）─────────
//  每个变速点：重置 mult、重置级联计数器、保持手性、按 speeddata 的绝对
//  bpm 重设 now_bpm、now_time 直接跳到该事件、禁止向前回溯、重设上一片
//  终止时间。此前移植版**没有这一支**，改成「每片按本片 speed 重算 pLen」
//  —— 这就是「忽快忽慢」「突然变左撇子」的根源。
//
//  ── 三处刻意偏差（修 UB / 防死循环，正常谱面结果不变）─────────────
//  1. 原版回溯恢复计数器 for(c=0;c<32;...) 而数组只有 [16] → 越界读写相邻
//     全局。本移植恢复全部 64 级。
//  2. 原版级联循环无上界，mult 可越界 long 数组 [16]。本移植 mult 上限 60
//     （mCnt 仍 64 级）。
//  3. 原版主循环无退出保护：若该手 keys_num==0 则 cnt>0 恒成立 → 死循环。
//     本移植加 pieces 上限兜底。
//

static const int kCounterLevel = 64;   // 级联计数器级数（承接无上界倍乘）
static const int kMaxMult      = 60;   // 倍乘层上限（防越界）

// 一个变速点（对应原版 restart[i]）
struct RestartMark
{
    int  idx;    // 事件下标
    int  kind;   // 1 = "=hand" 指定主副手；2 = "*k" 手性乘 k
    int  hand;   // kind==1 绝对手（1=右主 -1=左主）；kind==2 乘数（*1=不变）
};
// ── 由逐地板速度重建 speeddata 的「建议变速点」─────────────────────────
//
// 照抄 timedata生成/main.cpp:341-352 的筛选，只保留 3 段格式：
//   第 1 行 "0,=建议bpm,=1"      → 总是重开时间片 + 指定主副手
//   第 i 行 "tile,=建议bpm,*1"    → 重开时间片 + 手性不变
// 筛选（i>=1 且 i+1<n）：
//   · 绝对 bpm 与前一点互为整数倍 → 不算变速点（fmod<=0.0001）
//   · 相邻时间差互为整数倍（余 <5 微秒）→ 不算变速点
// 建议 bpm = 折进 (limit/2, limit]（同 GetAdviceBpm 口径）。
static void BuildRestartMarks(const vector<double>& evTime,
                              const vector<int>&    evFloor,
                              const vector<double>& evSpeedMuls,
                              bool hasSpeedMuls,
                              double speedFallback,
                              double bpm, double bpmLimit, int mainHand,
                              vector<RestartMark>& marks, vector<int>& markAt,
                              vector<double>&       adviceBpm)
{
    const int n = (int)evTime.size();
    marks.clear();
    markAt.assign(n > 0 ? n : 0, -1);
    adviceBpm.assign(n > 0 ? n : 0, 0.0);
    if (n <= 0) return;

    vector<double> rawBpm(n);
    for (int i = 0; i < n; i++)
    {
        double s = (hasSpeedMuls && i < (int)evSpeedMuls.size()) ? evSpeedMuls[i] : speedFallback;
        if (!(s > 1e-9)) s = (speedFallback > 1e-9) ? speedFallback : 1.0;
        rawBpm[i]    = bpm * s;
        adviceBpm[i] = GetAdviceBpm(bpm, s, bpmLimit);
    }

    // 第 1 行：索引 0，永远重开 + 指定主副手
    marks.push_back(RestartMark{ 0, 1, mainHand });
    markAt[0] = 0;

    for (int i = 1; i + 1 < n; i++)
    {
        double a = rawBpm[i], b = rawBpm[i - 1];
        if (a <= 0.0 || b <= 0.0) continue;
        if (fmod(a, b) <= 0.0001 || fmod(b, a) <= 0.0001) continue;

        double d0 = (evTime[i] - evTime[i - 1]) * 1e6;      // 微秒
        double d1 = (evTime[i + 1] - evTime[i]) * 1e6;
        if (d0 <= 0.0 || d1 <= 0.0) continue;   // 除零保护：原版此处是 NaN，!NaN 恒真
        if (fmod(d0, d1) < 5.0 || fmod(d1, d0) < 5.0) continue;

        // speeddata 的 tile 序号 → 第一个 floor 更大的事件下标
        // （原版 bpm_in:169-172 的 while(timedata[i][2]<data_cnt) 是"向前找"；
//  这里数据已是事件序列，直接取该点自身下标）
        marks.push_back(RestartMark{ i, 2, 1 });            // "*1" = 手性不变
        markAt[i] = (int)marks.size() - 1;
    }
}
// ── potato()：时间片划分（原版 main.cpp:213-340）──────────────────────────
//
// 忠实保留原版一个真实怪癖（不是笔误）：判按键数上限取的是 piece[psize][1]，
// 而该槽在提交前从未被写过（数组整体清零）→ **恒为左手键数**；只有回溯复用的
// 槽才带上被弹出那片的旧手号。证据：piece[psize][1]=hand 在 main.cpp:308，
// 即该片**提交之后**；而判上限在 main.cpp:277，psize 尚未自增。
// 原项目双手键数相同（options.txt "8,8"），所以该怪癖在其内部也不可观测。
static bool PotatoPartition(const vector<double>& evTime,
                            const vector<int>&    evFloor,
                            const double* speedMuls, int speedCount,
                            double bpm, double speedFallback,
                            vector<PieceInfo>& pieces)
{
    const int n = (int)evTime.size();
    if (n <= 0) return false;
    const int mainHand = (g_config.handPreference == 0) ? -1 : 1;

    vector<double> speedMulsVec;
    if (speedMuls && speedCount > 0) {
        speedMulsVec.assign(speedMuls, speedMuls + speedCount);
    }
    const bool hasSpeed = !speedMulsVec.empty();

    vector<RestartMark> marks;
    vector<int>  markAt;
    vector<double> adviceBpm;
    {
        double limit = g_config.bpmLimit;
        if (g_config.segmentCount > 0) {
            int sIdx;
            auto ec = ResolveConfig(evFloor[0], &sIdx);
            limit = ec.bpmLimit;
        }
        BuildRestartMarks(evTime, evFloor, speedMulsVec, hasSpeed, speedFallback,
                          bpm, limit, mainHand, marks, markAt, adviceBpm);
    }

    // 初值（原版 main.cpp:214-220）
    long double nowTime = 0.0L;
    int    nowData = 0;
    int    hand    = mainHand;
    double nowBpm  = adviceBpm.empty() ? 500.0 : adviceBpm[0];
    if (nowBpm <= 0.0) nowBpm = 500.0;
    double pieceTime = 30.0 / nowBpm;        // 原版 30000000/basic_bpm[0] 微秒
    int    mult      = 0;
    long long mCnt[kCounterLevel]   = {};
    long long mCntPre[kCounterLevel] = {};
    int    canMulti  = 0;
    int    back      = 0;      // 是否需要回溯（原版 main.cpp:220）

    vector<int> slotHand;
    pieces.clear();
    pieces.reserve(static_cast<std::vector<PieceInfo, std::allocator<PieceInfo>>::size_type>(n / 2) + 8);
    const size_t pieceCap = static_cast<size_t>(n) * 64 + 4096;

    while (nowData < n)
    {
        const int psize = (int)pieces.size();

        // ── 变速点：重开时间片（原版 main.cpp:223-250）────────────
        // pieceTime 取的是**上一轮**的旧值（原版 piece_time 声明在循环外），忠实保留。
        if (markAt[nowData] >= 0 &&
            (evTime[nowData] - (double)nowTime) < pieceTime * 0.99)
        {
            const RestartMark& m = marks[markAt[nowData]];
            mult = 0;
            memset(mCnt,    0, sizeof(mCnt));
            memset(mCntPre, 0, sizeof(mCntPre));
            if (m.kind == 1) hand = m.hand;
            else             hand *= m.hand;
            nowTime = evTime[nowData];
            // 原版：basic_bpm>0 → 绝对赋值；<0 → now_bpm *= -1*系数
            if (adviceBpm[nowData] > 0.0) nowBpm = adviceBpm[nowData];
            else                          nowBpm *= -1.0 * adviceBpm[nowData];
            // 原版 main.cpp:244 restart[now_data][0]=0; —— restart 后**自清标记**。
            // 漏掉这行会导致 markAt[nowData] 永远 >= 0：restart 把 nowTime 对齐到
            // evTime[nowData] 后，差值恒为 0 < pieceTime*0.99 → 每轮都重新 restart
            // → 永久自旋（本移植首版实测：3585 事件的原版谱直接挂死在此）。
            markAt[nowData] = -1;
            if (!pieces.empty()) pieces.back().endTime = (double)nowTime;
            canMulti = 0;
            continue;
        }

        // ── 片长（原版 main.cpp:251）────────────────────────────
        pieceTime = 60.0 / (nowBpm * pow(2.0, (double)mult)) / 2.0;  // 原 60000000/(...)/2
        if (pieceTime < 1e-9) pieceTime = 1e-9;
        if (mult > kMaxMult) mult = kMaxMult;

        // ── 本片可纳入事件数（原版 main.cpp:254-260 的 try_data）──
        // 窗口系数 0.995（change_speed 恒 0，见考证 A）；遇变速点立即断开。
        int cnt = 0;
        {
            const double limit = (double)nowTime + pieceTime * 0.995;
            int j = nowData;
            while (j < n && evTime[j] < limit) {
                if (markAt[j] >= 0) break;
                j++;
            }
            cnt = j - nowData;
        }

        // ── 按键数超限 → 提升倍乘（原版 main.cpp:277-286）─────────
        {
            int slotH = (psize < (int)slotHand.size()) ? slotHand[psize] : 0;
            int maxK;
            {
                int sIdx;
                auto ec = ResolveConfig(evFloor[nowData], &sIdx);
                maxK = (slotH == 0) ? ec.leftKeyCount : ec.rightKeyCount;
            }
            if (maxK < 0) maxK = 0;

            if (cnt > maxK) {
                if (canMulti == 1 && hand == -1) back = 1;
                mult++;
                continue;
            }
        }

        // ── 回溯：由主手重切上一片（原版 main.cpp:287-301）──────────
        if (back == 1)
        {
            back = 0;
            if (psize > 0) {
                hand = mainHand;
                nowTime = pieces[psize - 1].startTime;
                nowData = pieces[psize - 1].evStart;
                memcpy(mCnt, mCntPre, sizeof(mCnt));
                mult = pieces[psize - 1].multiplier + 1;
                pieces.pop_back();
                canMulti = 0;
                continue;
            }
        }

        // ── 提交时间片（原版 main.cpp:304-334）─────────────────────
        {
            const int csH = (hand == 1) ? 1 : 0;
            // ── 【2026-10-04 全抄对拍：microsecond 取整】───────────────
            // 原版 piece[] 声明为 long，赋值即**截断到整数微秒**：
            //   main.cpp:326  piece[psize][2]=piece_time   → 片长截断
            //   main.cpp:327  piece[psize][3]=now_time      → 终止时刻截断
            // turn_to() 随后读到的全是这些整数值，所以"本片是否被强行拉长/缩短"
            // （turn_to:361 piece_start+piece[pcnt][2]>piece[pcnt][3]）与松键夹取
            // 全部按整数微秒判定。
            //
            // 对拍结论（tools\potatoab，逐片对拍 ref_pieces.txt vs dll_pieces.txt）：
            //   len 截断 + start/end 不截断 → 首个差异在第 #269 片（最优）
            //   三者全截断        → 首个差异提前到第 #139 片
            //   三者全不截断      → 首个差异提前到第 #3 片
            // 原因：窗口上界用的是**未截断**的 pieceTime（逐片判定必须精确），
            // 而 turn_to 的几何比较用的是**已截断**的存值。两者来源不同，不能混用。
            const double pieceLenStored  = (double)(long long)(pieceTime * 1e6) / 1e6;
            const double pieceStart = (double)nowTime;
            const double pieceEnd   = (double)nowTime + pieceTime;
            pieces.push_back(PieceInfo(cnt, csH, pieceLenStored, pieceStart, pieceEnd, nowData, mult));
            slotHand.push_back(csH);

            // 级联倍乘计数器（原版 main.cpp:313-319）
            memcpy(mCntPre, mCnt, sizeof(mCnt));
            for (int c = mult; c > 0; c--) {
                mCnt[c] += (long long)pow(2.0, 16 - (mult - c));
                mCnt[c] %= (1LL << 18);
            }
            // 倍乘结束条件（原版 main.cpp:321-324）
            while (mult > 0 && mCnt[mult] == 0) mult--;

            nowData += cnt;
            nowTime += pieceTime;
            hand     = -hand;
            canMulti = 1;

            // 微误差矫正（原版 main.cpp:332-334）
            if (nowData < n && fabs(evTime[nowData] - (double)nowTime) < pieceTime * 0.01)
                nowTime = evTime[nowData];
            // 【2026-10-04 全抄对拍修正】原版这里**没有**任何"空片强制推进"保护。
            // 曾加过一个 emptyRun 守卫（连续 5 个空片就把 nowTime 拉到下一个事件
            // 时刻），实测它正是片序列在第 #269 片分叉的根因：原版允许连续 4 个
            // 空片让 nowTime 一小步一小步逼近事件（13876728→13936105→13995481→
            // 14054858，每步 +59376），守卫却在第 5 片直接把时间跳到 14173611，
            // 于是 cnt 从原版的 0 变成 3，整盘错位。已删除，仅保留 pieceCap 兜底。
        }

        if (pieces.size() > pieceCap) break;    // 偏差 3：防死循环兜底
    }

    // 哨兵片（原版 main.cpp:336-339）：turn_to/Emit 需要"下一片"几何。
    // 原版哨兵 hand 写 0-piece[psize-1][1]，但该字段只被 main.cpp:376 读到，
    // 而那里第二项 `piece[pcnt+1][0]==0` 恒真 → 取值无影响。这里写 1-lastHand。
    if (!pieces.empty()) {
        const PieceInfo& lp = pieces.back();
        pieces.push_back(PieceInfo(0, 1 - lp.hand, lp.pieceLen,
                                   lp.endTime, lp.endTime + lp.pieceLen, n, lp.multiplier));
    }
    return true;
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
// ─────────────────────────────────────────────────────────────────────────────


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

        // 【2026-10-04 全抄重构】原先此处是「逐片跟随本片速度」的单趟贪心分区器，
        // 其上方的 nowBpm / nowT / nowD / hand / mult / mCnt 等局部状态连同
        // 「变速容差」的历史注释块一并删除：片长、换手相位、倍乘节拍现在全部
        // 由 PotatoPartition（忠实移植原版 potato()）内部维护，外部不再持有。


        // ── 时间片划分：改走忠实移植的 potato() ──────────────────────
        // 【2026-10-04 全抄重构】原来这里是单趟贪心（每片按本片 speed 重算 pLen、
        // 无变速点概念）。现在与 SolveTechniqueTrace 共用同一份忠实移植核心
        // （见 PotatoPartition，原版手法拟真 main.cpp:213-340）。
        vector<PieceInfo> pieces;
        if (!PotatoPartition(evTime, evFloor, speedMuls, eventCount, bpm, speed, pieces))
            return nullptr;

        // PotatoPartition 内部已追加哨兵片，这里不要再追加。


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

                // 【2026-10-04 全抄对拍】rel 的最终值在原版是 out_event[][0]（long，
                // 整数微秒）；:377/:381 的夹取也在整数上算。本移植全程用 double，
                // 故在此补最后一次截断。实测：不截断会出现 43.434117019 这类亚微秒值，
                // 与原版的 43.434116000 不同，进而影响同刻事件的先后判定。
                rel = (double)(long long)(rel * 1e6) / 1e6;
                
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

//  角度定位：每个事件额外带 (floorIndex, angleFrac)。因为行星在一块砖的旋转内
//  是**匀角速度**的，而 scrLevelMaker 的 entryTime 就是按同样匀速算出来的，
//  所以「时间比例」与「角度比例」严格线性等价：
//     angleFrac = (t − entryTime(F)) / (pressTime(F) − entryTime(F))
//  运行时只要比对本砖已扫过的角度比例即可触发，完全不用看时钟。
//  按下事件的 angleFrac 恒为 1（按下时刻就是压线时刻），松键事件的 angleFrac
//  落在 (0,1) —— 这正是角度驱动需要的全部信息。
// ─────────────────────────────────────────────

static TraceStats g_lastTraceStats;

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

                // 【2026-10-04 全抄对拍】rel 的最终值在原版是 out_event[][0]（long，
                // 整数微秒）；:377/:381 的夹取也在整数上算。本移植全程用 double，
                // 故在此补最后一次截断。实测：不截断会出现 43.434117019 这类亚微秒值，
                // 与原版的 43.434116000 不同，进而影响同刻事件的先后判定。
                rel = (double)(long long)(rel * 1e6) / 1e6;
                
                TraceEvent releaseEv = {};
                releaseEv.TriggerTime = rel;
                int rs2; double rf2;
                LocateByTime(ft, rel, &rs2, &rf2);
                releaseEv.AngleFrac = (float)rf2;
                releaseEv.Flags = TRACE_FLAG_RELEASE_ONLY;
                releaseEv.ReleaseKeyCode = kc;
                releaseEv.FloorIndex = (rs2 < (int)ft.judgedIdx.size()) ? evFloor[ft.judgedIdx[rs2]] : curFloor;
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
    // 【2026-10-04 全抄对拍修正】用 stable_sort 而非 sort：原版 out_() 是**顺序发射**
    // （逐音：先 check() 补松键、再补按下），同一时刻多条事件的相对次序由
    // 发射顺序决定。std::sort 不稳定 → 51/187 这类"同时刻不同键"的对偶事件
    // 会被随机换位（实测与原版 main.crpl 逐条比对：前 409 条完全一致，
    // 之后全是这种同刻换位，diff 4654）。stable_sort 保持发射序即可复原。
    stable_sort(output.begin(), output.end(),
        [](const TraceEvent& a, const TraceEvent& b) {
            if (a.TriggerTime != b.TriggerTime) return a.TriggerTime < b.TriggerTime;
            const bool aRel = (a.Flags & TRACE_FLAG_RELEASE_ONLY) != 0;
            const bool bRel = (b.Flags & TRACE_FLAG_RELEASE_ONLY) != 0;
            return aRel != bRel ? bRel : false;   // 同刻：按下在前、松键在后
        });
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
    // 【2026-10-04 全抄对拍修正】用 stable_sort 而非 sort：原版 out_() 是**顺序发射**
    // （逐音：先 check() 补松键、再补按下），同一时刻多条事件的相对次序由
    // 发射顺序决定。std::sort 不稳定 → 51/187 这类"同时刻不同键"的对偶事件
    // 会被随机换位（实测与原版 main.crpl 逐条比对：前 409 条完全一致，
    // 之后全是这种同刻换位，diff 4654）。stable_sort 保持发射序即可复原。
    stable_sort(output.begin(), output.end(),
        [](const TraceEvent& a, const TraceEvent& b) {
            if (a.TriggerTime != b.TriggerTime) return a.TriggerTime < b.TriggerTime;
            const bool aRel = (a.Flags & TRACE_FLAG_RELEASE_ONLY) != 0;
            const bool bRel = (b.Flags & TRACE_FLAG_RELEASE_ONLY) != 0;
            return aRel != bRel ? bRel : false;   // 同刻：按下在前、松键在后
        });
}
// ─────────────────────────────────────────────────────────────────────────────
//  （原 2026-10-03 束搜索求解器整段移除：ExpandState / PieceCost / Ledger /
//   LedgerApply / LedgerCost / SolveState / g_solveStates）
//
//  分片逻辑现全部由上方 PotatoPartition（忠实移植原版 potato()）承担，
//  BuildTechniqueHitEventsImpl 与 SolveTechniqueTrace 共用同一份核心，
//  两条路径（时间驱动 / 角度驱动）产出的片序列与键流因此天然一致。
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────
//  导出函数：SolveTechniqueTrace
// ─────────────────────────────────────────────
// ─────────────────────────────────────────────────────────────────────────────
//  导出函数：SolveTechniqueTrace
//
//  【2026-10-04 全抄重构】原先这里是 10-03 自研的**束搜索求解器**（beam width 4、
//  frag/run/rough 代价函数、Ledger 闭式记账、nodeCap/layerGuard 兜底）。
//  现整条删除，改为与 BuildTechniqueHitEventsImpl 共用同一份忠实移植核心
//  PotatoPartition（原版「手法拟真」main.cpp:213-340）。
//
//  理由：
//    1. 原版算法是唯一权威。用户明确要求「把手法模拟全抄过来」，自研求解器
//       偏离原版语义（它甚至改变了「换手」的定义：原版每片强制换手是硬规则，
//       求解器把它当成可选动作放进搜索空间）。
//    2. 束搜索的代价函数经 10-03 三轮重写仍与游戏内「手法表」口径对不齐，
//       且实测出现「越优化 frag 越高」（优化器优化的量不是 frag）。
//    3. 复杂度/耗时（nodeCap 百万级）对实时建表不利，而原版是一趟 O(n)。
//
//  ABI 不变：SolveOptions* opts 参数保留但**不再使用**（传 nullptr 即可），
//  7 个导出、TraceEvent/HitEvent/TraceStats 结构全部保持原样，托管层无需改动。
// ─────────────────────────────────────────────────────────────────────────────
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
    (void)opts;   // 束搜索已移除；保留形参只为 ABI 兼容

    try {
        vector<double> evTime(entryTimes, entryTimes + eventCount);
        vector<int>    evPress(pressTypes, pressTypes + eventCount);
        vector<int>    evFloor(floorIndices, floorIndices + eventCount);
        FloorTiming ft = BuildFloorTiming(evFloor, evTime, evPress);

        vector<PieceInfo> pieces;
        if (!PotatoPartition(evTime, evFloor, speedMuls, eventCount, bpm, speed, pieces))
            return nullptr;

        vector<TraceEvent> output;
        EmitTraceEvents(pieces, evTime, evPress, evFloor, ft, output);

        // ── 统计（ABI 保留：字段口径沿用旧实现，仅作诊断展示）────────
        int frag = 0, longest = 0, run = 0, lastHand = -1;
        for (size_t i = 0; i + 1 < pieces.size(); i++) {
            if (pieces[i].evCount == 1) frag++;
            if (pieces[i].evCount == 0) continue;
            if (pieces[i].hand == lastHand) run++; else run = 1;
            lastHand = pieces[i].hand;
            if (run > longest) longest = run;
        }
        double mean = 0.0;
        const int pc = (int)(pieces.size() > 0 ? pieces.size() - 1 : 0);
        for (int i = 0; i < pc; i++) mean += pieces[i].pieceLen;
        mean /= (double)(pc > 0 ? pc : 1);
        double var = 0.0;
        for (int i = 0; i < pc; i++) {
            double d = pieces[i].pieceLen - mean;
            var += d * d;
        }
        var /= (double)(pc > 0 ? pc : 1);

        g_lastTraceStats.fragmentCount    = frag;
        g_lastTraceStats.longestSameHand  = longest;
        g_lastTraceStats.droppedNotes     = 0;
        g_lastTraceStats.pieceCount       = pc;
        g_lastTraceStats.pieceLenVariance = var;
        g_lastTraceStats.totalCost        = 0.0;      // 无搜索 → 无目标值
        g_lastTraceStats.fragCost         = 0.0;
        g_lastTraceStats.runCost          = 0.0;
        g_lastTraceStats.roughCost        = 0.0;
        g_lastTraceStats.solverNodes      = 0;        // 无搜索 → 无节点数

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