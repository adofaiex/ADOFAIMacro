// 手法模拟 A/B 对比 harness：同一输入同时喂 base(2669a94) 与 head 两版
// TechniqueSimulator.dll，dump 各自的分片纹理并量化差异。
//
// 关键建模：scrFloor.speed 就是该地板的局部 BPM 倍率，
// 所以 entryTime = Σ beatDur/speed[i]/subdiv —— 变速时音符本身会变密。
// 均匀等间隔（与 speed 无关）的谱是错的建模，会让分片循环几乎不切分。
//
// 编译：同目录 build_ab.bat
// 用法：ab.exe <baseDll> <headDll> [--dump]
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <string>
#include <algorithm>

using namespace std;

#pragma pack(push, 8)
struct HitEvent {
    double        TriggerTime;
    unsigned char KeyCode;
    unsigned char _pad0[3];
    BOOL          ReleaseOnly;
    BOOL          IsHoldRelated;
    unsigned char ReleaseKeyCode;
    unsigned char _pad1[3];
};

struct TechniqueSegment {
    int startFloor; int endFloor; double bpmLimit;
    unsigned char* leftKeys; int leftKeyCount;
    unsigned char* rightKeys; int rightKeyCount;
    int** leftKeyOrders; int* leftOrderLengths; int leftOrderCounts;
    int** rightKeyOrders; int* rightOrderLengths; int rightOrderCounts;
    double* leftPressTimes; double* rightPressTimes;
    BOOL hasKeyOverride;
};

struct TechniqueConfig {
    unsigned char* leftKeys; int leftKeyCount;
    unsigned char* rightKeys; int rightKeyCount;
    int** leftKeyOrders; int* leftOrderLengths; int leftOrderCounts;
    int** rightKeyOrders; int* rightOrderLengths; int rightOrderCounts;
    double* leftPressTimes; double* rightPressTimes;
    double bpmLimit; int handPreference;
    TechniqueSegment* segments; int segmentCount;
    double speedChangeTolerance;
};
#pragma pack(pop)

typedef void      (*FnSet)(TechniqueConfig*);
typedef HitEvent* (*FnBuildEx)(double*, int*, int*, double*, int, double, double, int*);
typedef void      (*FnFree)(HitEvent*);

struct Lib {
    HMODULE h = nullptr;
    FnSet    set = nullptr;
    FnBuildEx build = nullptr;
    FnFree   fre = nullptr;

    bool Load(const char* path) {
        h = LoadLibraryA(path);
        if (!h) { printf("  [FAIL] LoadLibrary %s err=%lu\n", path, GetLastError()); return false; }
        set   = (FnSet)    GetProcAddress(h, "SetTechniqueConfig");
        build = (FnBuildEx)GetProcAddress(h, "BuildTechniqueHitEventsEx");
        fre   = (FnFree)   GetProcAddress(h, "FreeHitEvents");
        if (!set || !build || !fre) { printf("  [FAIL] missing export in %s\n", path); return false; }
        return true;
    }
};

struct Chart {
    vector<double> evTime;
    vector<int>    evPress;
    vector<int>    evFloor;
    vector<double> evSpeed;
    double bpm = 400.0;
    double globalSpeed = 1.0;
};

static Chart MakeChart(const vector<double>& speedPerNote, double bpm, double subdiv) {
    Chart c;
    c.bpm = bpm;
    c.globalSpeed = speedPerNote.empty() ? 1.0 : speedPerNote[0];
    double beatDur = 60.0 / bpm;
    double t = 0.0;
    for (size_t i = 0; i < speedPerNote.size(); i++) {
        double sp = speedPerNote[i];
        if (sp < 1e-6) sp = 1e-6;
        c.evTime.push_back(t);
        c.evPress.push_back(1);
        c.evFloor.push_back((int)i);
        c.evSpeed.push_back(sp);
        t += beatDur / sp / subdiv;
    }
    return c;
}

static unsigned g_seed = 12345u;
static double rnd() { g_seed = g_seed * 1664525u + 1013904223u; return (double)((g_seed >> 8) & 0xFFFFFF) / 16777216.0; }
static void seed(unsigned s) { g_seed = s ? s : 1u; }

static bool isLeftKey(unsigned char kc) {
    return kc == 'D' || kc == 'F' || kc == 'S' || kc == 'A';
}

struct Press {
    double t;
    int    note;
    int    hand;
    unsigned char key;
};

struct Stats {
    int    nPress   = 0;
    int    handL    = 0, handR = 0;
    int    handRuns = 0;
    // ── 与游戏内 Player.log 的 [Macro-Tech] 手法表 一一对应 ──
    // Calibration.cs:LogTechniqueTableSummary 打印的就是这四个数，
    // 所以离线列必须用同样的算法，用户才能拿离线结论去预测游戏里会看到什么。
    int    frag     = 0;   // 单音碎块 = 长度为 1 的同手连段个数
    int    longest  = 0;   // 最长连击 = 最长同手连段长度
    double gapMean = 0, gapSD = 0, gapP50 = 0, gapMax = 0;
    int    jumps5 = 0, jumps20 = 0;
    double leadMeanAbs = 0, leadMaxAbs = 0;
    // 人手合理性：同一只手连续按多少下不换手。真实玩家几乎不连打 3 下以上，
    // 连续 5+ 说明分配算法在乱切 —— 这正是"突然变成左撇子"的量化形态。
    int    maxSameHandRun = 0, sameHandRun3 = 0, sameHandRun5 = 0;
    vector<Press> presses;
};

static Stats Analyze(const vector<HitEvent>& ev, const Chart& c) {
    Stats st;
    vector<double> ts;
    for (size_t i = 0; i < ev.size(); i++) {
        if (ev[i].ReleaseOnly) continue;
        ts.push_back(ev[i].TriggerTime);
        if (isLeftKey(ev[i].KeyCode)) st.handL++; else st.handR++;
    }
    st.nPress = (int)ts.size();
    if (ts.size() < 2) return st;

    int lastHand = -1;
    int curSame = 0;
    bool has = false;
    for (size_t i = 0; i < ev.size(); i++) {
        if (ev[i].ReleaseOnly) continue;
        int h = isLeftKey(ev[i].KeyCode) ? 0 : 1;
        if (!has) { curSame = 1; has = true; }
        else if (h == lastHand) curSame++;
        else {
            // 收尾上一段（照抄 Calibration.cs:214-229 的写法）
            if (curSame == 1) st.frag++;
            if (curSame > st.longest) st.longest = curSame;
            st.handRuns++; curSame = 1;
        }
        lastHand = h;
        if (curSame > st.maxSameHandRun) st.maxSameHandRun = curSame;
        if (curSame >= 3) st.sameHandRun3++;
        if (curSame >= 5) st.sameHandRun5++;
        size_t lo = 0, hi = c.evTime.size();
        while (lo < hi) { size_t mid = (lo + hi) / 2; if (c.evTime[mid] < ev[i].TriggerTime) lo = mid + 1; else hi = mid; }
        size_t best = 0;
        if (lo < c.evTime.size()) best = lo;
        else if (!c.evTime.empty()) best = c.evTime.size() - 1;
        if (best > 0 && (lo >= c.evTime.size() ||
            fabs(c.evTime[best] - ev[i].TriggerTime) > fabs(c.evTime[best - 1] - ev[i].TriggerTime)))
            best--;
        Press p{ ev[i].TriggerTime, (int)best, h, ev[i].KeyCode };
        st.presses.push_back(p);
        double lead = fabs(ev[i].TriggerTime - c.evTime[best]) * 1000.0;
        st.leadMeanAbs += lead;
        if (lead > st.leadMaxAbs) st.leadMaxAbs = lead;
    }
    st.leadMeanAbs /= st.presses.size();
    if (curSame > 0) {
        if (curSame == 1) st.frag++;
        if (curSame > st.longest) st.longest = curSame;
    }

    vector<double> gaps;
    for (size_t i = 1; i < ts.size(); i++) gaps.push_back(ts[i] - ts[i - 1]);
    vector<double> sorted = gaps;
    sort(sorted.begin(), sorted.end());
    st.gapMax = sorted.back();
    st.gapP50 = sorted[sorted.size() / 2];
    double sum = 0; for (size_t i = 0; i < gaps.size(); i++) sum += gaps[i];
    st.gapMean = sum / gaps.size();
    double v = 0; for (size_t i = 0; i < gaps.size(); i++) v += (gaps[i] - st.gapMean) * (gaps[i] - st.gapMean);
    st.gapSD = sqrt(v / gaps.size());
    for (size_t i = 1; i < gaps.size(); i++) {
        double a = gaps[i - 1]; if (a < 1e-6) continue;
        double rel = fabs(gaps[i] / a - 1.0);
        if (rel > 0.05) st.jumps5++;
        if (rel > 0.20) st.jumps20++;
    }
    return st;
}

struct Case {
    const char* name;
    Chart       chart;
    double      bpmLimit;
    int         handPref;
    double      tol;
};

// ── 单次构建：固定键位配置，只变 tol ──
static vector<HitEvent> BuildOne(Lib& lib, const Chart& ch, double bpmLimit,
                                 int handPref, double tol) {
    unsigned char lk[2] = { 'D', 'F' };
    unsigned char rk[2] = { 'J', 'K' };
    double lt[2] = { 0.8, 0.8 }, rt[2] = { 0.8, 0.8 };
    TechniqueConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.leftKeys = lk;  cfg.leftKeyCount = 2;
    cfg.rightKeys = rk; cfg.rightKeyCount = 2;
    cfg.leftPressTimes = lt; cfg.rightPressTimes = rt;
    cfg.bpmLimit = bpmLimit;
    cfg.handPreference = handPref;
    cfg.speedChangeTolerance = tol;
    lib.set(&cfg);
    int outCount = 0;
    int n = (int)ch.evTime.size();
    // 导出函数原型是非 const 指针，但它只读这些数组（见 cpp 的 evTime/evPress/…）。
    Chart& m = const_cast<Chart&>(ch);
    HitEvent* r = lib.build(m.evTime.data(), m.evPress.data(),
                            m.evFloor.data(), m.evSpeed.data(),
                            n, m.bpm, m.globalSpeed, &outCount);
    vector<HitEvent> out;
    if (r && outCount > 0) out.assign(r, r + outCount);
    if (r) lib.fre(r);
    return out;
}

// ── 按音符索引配对：同一个音符，A 打什么手/键 vs B 打什么。
//    不看事件顺序，纯粹衡量"纹理分歧"，排除整体相位差。──
static void PerNoteDiff(const Stats& A, const Stats& B,
                        int& pairs, int& handDiff, int& keyDiff) {
    vector<int> hA, kA, hB, kB;
    for (size_t i = 0; i < A.presses.size(); i++) {
        int n = A.presses[i].note; if (n < 0) continue;
        if ((int)hA.size() <= n) { hA.resize(n + 1, -1); kA.resize(n + 1, -1); }
        hA[n] = A.presses[i].hand; kA[n] = (int)A.presses[i].key;
    }
    for (size_t i = 0; i < B.presses.size(); i++) {
        int n = B.presses[i].note; if (n < 0) continue;
        if ((int)hB.size() <= n) { hB.resize(n + 1, -1); kB.resize(n + 1, -1); }
        hB[n] = B.presses[i].hand; kB[n] = (int)B.presses[i].key;
    }
    pairs = handDiff = keyDiff = 0;
    size_t m = (size_t)min(hA.size(), hB.size());
    for (size_t n = 0; n < m; n++) {
        if (hA[n] < 0 || hB[n] < 0) continue;
        pairs++;
        if (hA[n] != hB[n]) handDiff++;
        if (kA[n] != kB[n]) keyDiff++;
    }
}

// ── 变速容差扫描 ──────────────────────────────────────────────
//  参考 = base(2669a94)：没有速率容差，只有"下一片稀疏就把 pLen 拉长"的几何修补。
//  候选 = head(96551cf B+C)：多了一个滑动窗口基准 + 速率死区。
//
//  ★ speedChangeTolerance 同时控制两件事，扫 tol 时两个开关一起动，必须拆开：
//    (1) 速率死区  deadZone = (tol > 0 ? tol : 0.50)   cpp:271-272
//        ← tol=0 反而给最大死区 0.50，这就是用户"必须开到最大"的来源
//    (2) 几何修补  `tol > 0.0` 才生效                  cpp:409
//  参考固定用 base@tol=0.5（几何修补开），这样 tol 列的变化只来自速率容差。
//  再用 v_deadonly（死区恒 0）复扫一遍，单独量几何修补。
static void RunSweep(Lib& refLib, Lib& candLib, Lib* dzOff, const vector<Case>& cases) {
    const double tols[] = { 0.0, 0.02, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50 };
    const int NT = (int)(sizeof(tols) / sizeof(tols[0]));

    printf("\n########## SWEEP: speed-change tolerance ##########\n");
    printf("reference = base(2669a94) @ tol=0.5   (no rate deadzone; geometric patch ON)\n");
    printf("candidate = head(B+C)                deadZone = (tol>0 ? tol : 0.0)\n");
    if (dzOff)
        printf("dz-off    = v_deadonly (deadZone=0)  isolates the geometric patch\n\n");

    for (size_t ci = 0; ci < cases.size(); ci++) {
        const Case& cs = cases[ci];
        vector<HitEvent> refEv = BuildOne(refLib, cs.chart, cs.bpmLimit, cs.handPref, 0.5);
        if (refEv.empty()) { printf("[%-12s] reference produced no events, skip\n", cs.name); continue; }
        Stats R = Analyze(refEv, cs.chart);

        printf("=== [%s] notes=%d bpmLimit=%.0f\n", cs.name, (int)cs.chart.evTime.size(), cs.bpmLimit);
        printf("  base@0.5: press=%d runs=%d frag=%d longest=%d L=%d R=%d r3+=%d r5+=%d\n",
               R.nPress, R.handRuns, R.frag, R.longest, R.handL, R.handR,
               R.sameHandRun3, R.sameHandRun5);
        printf("  %5s %6s | %6s %6s %6s %6s %6s %6s %5s %5s %6s %5s\n",
               "tol", "dZone", "press", "runs", "frag", "long", "dFrag", "dLong",
               "L", "R", "hand%", "r3+");
        for (int t = 0; t < NT; t++) {
            double tol = tols[t];
            // head 的映射（cpp:271-272，2026-10-03 已改为 0 = 关闭死区）
            double dz = (tol > 0.0) ? tol : 0.0;
            vector<HitEvent> candEv = BuildOne(candLib, cs.chart, cs.bpmLimit, cs.handPref, tol);
            if (candEv.empty()) { printf("  %5.2f %6.2f |  (no events)\n", tol, dz); continue; }
            Stats C = Analyze(candEv, cs.chart);
            int pairs = 0, hd = 0, kd = 0;
            PerNoteDiff(R, C, pairs, hd, kd);
            printf("  %5.2f %6.2f | %6d %6d %6d %6d %+6d %+6d %5d %5d %5.1f%% %5d\n",
                   tol, dz, C.nPress, C.handRuns, C.frag, C.longest,
                   C.frag - R.frag, C.longest - R.longest,
                   C.handL, C.handR,
                   pairs > 0 ? 100.0 * hd / pairs : 0.0,
                   C.sameHandRun3);
        }
        if (dzOff) {
            printf("  --- deadZone forced 0 -> isolates the geometric patch ---\n");
            for (int t = 0; t < NT; t++) {
                double tol = tols[t];
                vector<HitEvent> candEv = BuildOne(*dzOff, cs.chart, cs.bpmLimit, cs.handPref, tol);
                if (candEv.empty()) { printf("  %5.2f %6.2f |  (no events)\n", tol, 0.0); continue; }
                Stats C = Analyze(candEv, cs.chart);
                printf("  %5.2f %6.2f | %6d %6d %6d %6d %+6d %+6d %5d %5d %5s %5d\n",
                       tol, 0.0, C.nPress, C.handRuns, C.frag, C.longest,
                       C.frag - R.frag, C.longest - R.longest,
                       C.handL, C.handR, "-", C.sameHandRun3);
            }
        }
        printf("\n");
    }
}

static void RunCase(Case& cs, Lib& base, Lib& head, bool dump) {
    unsigned char lk[2] = { 'D', 'F' };
    unsigned char rk[2] = { 'J', 'K' };
    double lt[2] = { 0.8, 0.8 }, rt[2] = { 0.8, 0.8 };

    TechniqueConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.leftKeys = lk;  cfg.leftKeyCount = 2;
    cfg.rightKeys = rk; cfg.rightKeyCount = 2;
    cfg.leftPressTimes = lt; cfg.rightPressTimes = rt;
    cfg.bpmLimit = cs.bpmLimit;
    cfg.handPreference = cs.handPref;
    cfg.speedChangeTolerance = cs.tol;

    int n = (int)cs.chart.evTime.size();
    const char* tag[2] = { "base", "head" };
    Lib* libs[2] = { &base, &head };
    vector<HitEvent> out[2];

    double span = (n > 0) ? cs.chart.evTime[n - 1] : 0.0;
    printf("\n=== [%s] notes=%d span=%.1fs bpmLimit=%.0f handPref=%d tol=%.2f\n",
           cs.name, n, span, cs.bpmLimit, cs.handPref, cs.tol);

    for (int k = 0; k < 2; k++) {
        libs[k]->set(&cfg);
        int outCount = 0;
        HitEvent* r = libs[k]->build(cs.chart.evTime.data(), cs.chart.evPress.data(),
                                     cs.chart.evFloor.data(), cs.chart.evSpeed.data(),
                                     n, cs.chart.bpm, cs.chart.globalSpeed, &outCount);
        if (r && outCount > 0) out[k].assign(r, r + outCount); else out[k].clear();
        if (r) libs[k]->fre(r);
    }

    Stats ss[2] = { Analyze(out[0], cs.chart), Analyze(out[1], cs.chart) };
    printf("  %-5s %6s %8s %6s %8s %7s %7s %6s %6s %8s\n",
           "ver", "press", "L/R", "runs", "gapMean", "gapSD", "gapP50", "jmp5", "jmp20", "leadAvg");
    for (int k = 0; k < 2; k++) {
        const Stats& s = ss[k];
        printf("  %-5s %6d %3d/%-3d %6d %8.2f %7.2f %7.2f %6d %6d %8.2f  | sameHand max=%d  n3+=%d n5+=%d\n",
               tag[k], s.nPress, s.handL, s.handR, s.handRuns,
               s.gapMean, s.gapSD, s.gapP50, s.jumps5, s.jumps20, s.leadMeanAbs,
               s.maxSameHandRun, s.sameHandRun3, s.sameHandRun5);
    }

    int common = (int)min(ss[0].presses.size(), ss[1].presses.size());
    int handDiff = 0, keyDiff = 0, noteDiff = 0;
    for (int i = 0; i < common; i++) {
        if (ss[0].presses[i].note != ss[1].presses[i].note) noteDiff++;
        if (ss[0].presses[i].hand != ss[1].presses[i].hand) handDiff++;
        if (ss[0].presses[i].key  != ss[1].presses[i].key)  keyDiff++;
    }
    double dMean = ss[0].gapMean > 1e-9 ? (ss[1].gapMean / ss[0].gapMean - 1.0) * 100.0 : 0.0;
    printf("  D: mean=%+.2f%%  press=%+d  runs=%+d\n",
           dMean, ss[1].nPress - ss[0].nPress, ss[1].handRuns - ss[0].handRuns);
    if (common > 0)
        printf("  ** misalign: note %d/%d(%.1f%%)  hand %d/%d(%.1f%%)  key %d/%d(%.1f%%)\n",
               noteDiff, common, 100.0 * noteDiff / common,
               handDiff, common, 100.0 * handDiff / common,
               keyDiff,  common, 100.0 * keyDiff / common);

    // ── 对齐控制：全局错开 s 个事件后，两版是否其实是同一条纹理 ──
    // 只错 1 个事件会让 hand 差异率虚高到 ~100%，必须排除这种"相位差"。
    {
        const vector<Press>& A = ss[0].presses;
        const vector<Press>& B = ss[1].presses;
        int bestS = 0; double bestErr = 1e18;
        for (int s = -4; s <= 4; s++) {
            int cmp = 0, err = 0;
            for (size_t i = 0; i < A.size(); i++) {
                long long j = (long long)i + s;
                if (j < 0 || j >= (long long)B.size()) continue;
                cmp++;
                if (A[i].note != B[(size_t)j].note || A[i].key != B[(size_t)j].key) err++;
            }
            if (cmp > 0 && (double)err / cmp < bestErr) { bestErr = (double)err / cmp; bestS = s; }
        }
        // 按"同一个音符"配对（不看顺序），衡量真正的纹理分歧
        // 用音符索引做键：base 的音符 i 被打成什么键/手 vs head 的同一音符
        vector<int> handA, handB;
        for (size_t i = 0; i < A.size(); i++) { if (A[i].note >= 0) { if ((int)handA.size() <= A[i].note) handA.resize(A[i].note + 1, -1); handA[A[i].note] = A[i].hand; } }
        for (size_t i = 0; i < B.size(); i++) { if (B[i].note >= 0) { if ((int)handB.size() <= B[i].note) handB.resize(B[i].note + 1, -1); handB[B[i].note] = B[i].hand; } }
        int pairs = 0, handNoteDiff = 0, keyNoteDiff = 0;
        for (size_t i = 0; i < A.size(); i++) {
            int nt = A[i].note;
            if (nt < 0 || nt >= (int)handB.size()) continue;
            if (handB[nt] < 0) continue;   // head 没打这个音符
            pairs++;
            if (handB[nt] != A[i].hand) handNoteDiff++;
            if (B.empty()) continue;
        }
        (void)keyNoteDiff;
        printf("  -- align: best shift=%+d err=%.2f%%   same-note hand mismatch %d/%d (%.1f%%)\n",
               bestS, 100.0 * bestErr,
               handNoteDiff, pairs, pairs > 0 ? 100.0 * handNoteDiff / pairs : 0.0);

        // 失配的分布形态：连续的"整段翻转" vs 零散的"逐音抖动"
        // 数一下 handNoteDiff 的落点有多少个"翻转簇"，以及每簇平均长度。
        {
            int flips = 0, inFlip = 0, longRuns = 0;
            double lastFlipAt = -1e18;
            for (size_t n = 0; n < handB.size(); n++) {
                if (n >= handA.size() || handA[n] < 0 || handB[n] < 0) continue;
                bool bad = (handA[n] != handB[n]);
                if (bad) {
                    if (!inFlip) { flips++; inFlip = 1; }
                    // 记下每簇的起点与终点
                } else inFlip = 0;
                (void)lastFlipAt; (void)longRuns;
            }
            // 统计每簇长度
            int cur = 0, maxRun = 0;
            for (size_t n = 0; n < handB.size(); n++) {
                if (n >= handA.size() || handA[n] < 0 || handB[n] < 0) { cur = 0; continue; }
                if (handA[n] != handB[n]) { cur++; if (cur > maxRun) maxRun = cur; }
                else cur = 0;
            }
            // 音符总数（两版共同覆盖）
            int covered = 0;
            for (size_t n = 0; n < handB.size(); n++)
                if (n < handA.size() && handA[n] >= 0 && handB[n] >= 0) covered++;
            printf("  -- flips: %d clusters over %d notes (%.1f notes/flip), max cluster=%d notes (%.1f%% of chart)\n",
                   flips, covered,
                   flips > 0 ? (double)covered / flips : 0.0,
                   maxRun,
                   covered > 0 ? 100.0 * maxRun / covered : 0.0);
        }
    }

    if (dump) {
        for (int k = 0; k < 2; k++) {
            char path[512];
            snprintf(path, sizeof(path), "tools/out/ab_%s_%s.crpl", cs.name, tag[k]);
            FILE* f = fopen(path, "w");
            if (!f) continue;
            for (size_t i = 0; i < out[k].size(); i++) {
                const HitEvent& e = out[k][i];
                fprintf(f, "%.6f,%d,%d,%d,%d\n", e.TriggerTime, e.KeyCode,
                        (int)e.ReleaseOnly, (int)e.IsHoldRelated, e.ReleaseKeyCode);
            }
            fclose(f);
            printf("  wrote %s (%d events)\n", path, (int)out[k].size());
        }
    }
}

static vector<double> Flat(int n, double s) { return vector<double>(n, s); }

static vector<double> Steppy(int n, int period, double lo, double hi, bool jitter) {
    vector<double> v; v.reserve(n);
    double cur = lo;
    for (int i = 0; i < n; i++) {
        if (i % period == 0) cur = lo + (hi - lo) * rnd();
        double x = cur;
        if (jitter) x *= (0.90 + 0.20 * rnd());
        v.push_back(x);
    }
    return v;
}

static vector<double> Ramp(int n, double lo, double hi, double wobble) {
    vector<double> v; v.reserve(n);
    for (int i = 0; i < n; i++) {
        double t = (double)i / (n > 1 ? n - 1 : 1);
        v.push_back((lo + (hi - lo) * t) * (1.0 + wobble * (rnd() - 0.5)));
    }
    return v;
}

static vector<double> Sections(int n, int sectionLen, double lo, double hi) {
    vector<double> v; v.reserve(n);
    double cur = lo;
    for (int i = 0; i < n; i++) {
        if (i % sectionLen == 0) cur = lo + (hi - lo) * rnd();
        v.push_back(cur * (0.97 + 0.06 * rnd()));
    }
    return v;
}

int main(int argc, char** argv) {
    const char* basePath = (argc > 1) ? argv[1] : "tools/abtest/bin/base.dll";
    const char* headPath = (argc > 2) ? argv[2] : "tools/abtest/bin/head.dll";
    bool dump = false, sweep = false;
    for (int i = 3; i < argc; i++) {
        if (strcmp(argv[i], "--dump") == 0) dump = true;
        if (strcmp(argv[i], "--sweep") == 0) sweep = true;
    }

    Lib base, head;
    printf("base: %s\nhead: %s\n", basePath, headPath);
    if (!base.Load(basePath)) { printf("base load failed\n"); return 1; }
    if (!head.Load(headPath)) { printf("head load failed\n"); return 1; }
    printf("both DLLs loaded OK\n");

    const double BPM = 400.0;
    const double SUB = 4.0;

    vector<Case> cases;
    auto add = [&](const char* nm, const vector<double>& sp, double tol) {
        Case c; c.name = nm; c.chart = MakeChart(sp, BPM, SUB);
        c.bpmLimit = 500; c.handPref = 1; c.tol = tol; cases.push_back(c);
    };
    // 同样的谱，但 bpmLimit 放宽到 5000 —— GetAdviceBpm 的倍频折叠
    // (limit/2, limit] 在这个区间几乎不生效，能看出 B+C 到底改没改片长。
    auto addNoFold = [&](const char* nm, const vector<double>& sp, double tol) {
        Case c; c.name = nm; c.chart = MakeChart(sp, BPM, SUB);
        c.bpmLimit = 5000; c.handPref = 1; c.tol = tol; cases.push_back(c);
    };

    seed(1);  add("flat1x",         Flat(400, 1.0), 0.0);
    seed(1);  add("flat1x_tol5",    Flat(400, 1.0), 0.5);
    seed(3);  add("step8_jit",      Steppy(600, 8, 0.6, 1.8, true), 0.0);
    seed(3);  add("step8_tol5",     Steppy(600, 8, 0.6, 1.8, true), 0.5);
    seed(5);  add("ramp_jit",       Ramp(600, 0.5, 3.0, 0.30), 0.0);
    seed(5);  add("ramp_tol5",      Ramp(600, 0.5, 3.0, 0.30), 0.5);
    seed(7);  add("sec16",          Sections(800, 64, 0.4, 2.5), 0.0);
    seed(7);  add("sec16_tol5",     Sections(800, 64, 0.4, 2.5), 0.5);
    seed(9);  add("wild",           Steppy(1000, 4, 0.14, 58.67, true), 0.0);
    seed(9);  add("wild_tol5",      Steppy(1000, 4, 0.14, 58.67, true), 0.5);
    seed(11); add("slow_jit",       Steppy(600, 16, 0.3, 1.2, true), 0.0);
    seed(11); add("slow_jit_tol5",  Steppy(600, 16, 0.3, 1.2, true), 0.5);
    seed(13); add("surge",          Steppy(800, 32, 0.5, 4.0, true), 0.0);
    seed(13); add("surge_tol5",     Steppy(800, 32, 0.5, 4.0, true), 0.5);

    seed(3);  addNoFold("NF_step8_jit",  Steppy(600, 8, 0.6, 1.8, true), 0.0);
    seed(3);  addNoFold("NF_step8_tol5", Steppy(600, 8, 0.6, 1.8, true), 0.5);
    seed(5);  addNoFold("NF_ramp_jit",   Ramp(600, 0.5, 3.0, 0.30), 0.0);
    seed(5);  addNoFold("NF_ramp_tol5",  Ramp(600, 0.5, 3.0, 0.30), 0.5);
    seed(7);  addNoFold("NF_sec16",      Sections(800, 64, 0.4, 2.5), 0.0);
    seed(13); addNoFold("NF_surge",      Steppy(800, 32, 0.5, 4.0, true), 0.0);

    if (sweep) {
        // v_deadonly = head 的 deadZone 恒 0，用来把"几何修补"从"速率死区"里剥出来。
        Lib deadOnly;
        bool haveDzOff = deadOnly.Load("tools/abtest/bin/v_deadonly.dll");
        if (!haveDzOff) printf("  (v_deadonly.dll not found, geometric-patch rows skipped)\n");
        // sweep 自己管 tol，只保留每种速度形态的**一个** tol=0 条目，
        // tol 在 RunSweep 里被扫掉，Case::tol 不参与。
        vector<Case> sc;
        auto addS = [&](const char* nm, const vector<double>& sp) {
            Case c; c.name = nm; c.chart = MakeChart(sp, BPM, SUB);
            c.bpmLimit = 500; c.handPref = 1; c.tol = 0.0; sc.push_back(c);
        };
        seed(1);  addS("flat1x",     Flat(400, 1.0));
        seed(3);  addS("step8_jit",  Steppy(600, 8, 0.6, 1.8, true));
        seed(5);  addS("ramp_jit",   Ramp(600, 0.5, 3.0, 0.30));
        seed(7);  addS("sec16",      Sections(800, 64, 0.4, 2.5));
        seed(9);  addS("wild",       Steppy(1000, 4, 0.14, 58.67, true));
        seed(11); addS("slow_jit",   Steppy(600, 16, 0.3, 1.2, true));
        seed(13); addS("surge",      Steppy(800, 32, 0.5, 4.0, true));
        RunSweep(base, head, haveDzOff ? &deadOnly : nullptr, sc);
        printf("\nDONE\n");
        return 0;
    }

    for (size_t i = 0; i < cases.size(); i++)
        RunCase(cases[i], base, head, dump);

    printf("\nDONE\n");
    return 0;
}