// ─────────────────────────────────────────────
//  solver_ab.cpp —— 离线求解器 vs 现有贪心的 A/B
//
//  目的：证明 SolveTechniqueTrace（束搜索）产出的手法纹理真的比
//  BuildTechniqueHitEventsEx（贪心基线）好，而且不丢音、不改判定时刻。
//
//  用法：  solver_ab.exe <dll路径> [--beam N] [--dump]
//
//  判定口径（与游戏内 [Macro-Tech] 手法表 / Calibration.cs 一致）：
//    frag      单音碎块 = 长度为 1 的同手连段个数
//    longest   最长同手连段
//    maxSame   最长同手连段（逐事件累计口径）
//    r3+/r5+   连续 3 下 / 5 下以上同手的次数 —— "突然变成左撇子"的量化形态
//    gapMean/SD/gapMax  按键间隔统计
//    jumps5/20  相邻间隔相对跳变 >5% / >20% 的次数（忽快忽慢）
//    pieceVar  片长方差（求解器 TraceStats 提供）
//    dropped   丢音数（必须恒为 0）
//    leadMs    |按键时刻 − 该音 entryTime| 的均值/最大（判定时刻不得漂移）
// ─────────────────────────────────────────────
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <vector>
#include <algorithm>
#include <objbase.h>

#include "../../TechniqueSimulator/TechniqueSimulator.h"

using namespace std;

typedef TechniqueConfig*  (*FnSet)(TechniqueConfig*);
typedef HitEvent*         (*FnBuildEx)(double*, int*, int*, double*, int, double, double, int*);
typedef void              (*FnFree)(HitEvent*);
typedef TraceEvent*       (*FnSolve)(double*, int*, int*, double*, int, double, double,
                                      SolveOptions*, int*);
typedef TraceStats*       (*FnStats)();
typedef void              (*FnFreeTrace)(TraceEvent*);

struct Lib {
    HMODULE   h = nullptr;
    FnSet     set = nullptr;
    FnBuildEx build = nullptr;
    FnFree    fre = nullptr;
    FnSolve   solve = nullptr;
    FnStats   stats = nullptr;
    FnFreeTrace freTrace = nullptr;

    bool Load(const char* path) {
        h = LoadLibraryA(path);
        if (!h) { printf("[FAIL] LoadLibrary %s err=%lu\n", path, GetLastError()); return false; }
        set   = (FnSet)       GetProcAddress(h, "SetTechniqueConfig");
        build = (FnBuildEx)   GetProcAddress(h, "BuildTechniqueHitEventsEx");
        fre   = (FnFree)      GetProcAddress(h, "FreeHitEvents");
        solve = (FnSolve)     GetProcAddress(h, "SolveTechniqueTrace");
        stats = (FnStats)     GetProcAddress(h, "GetLastTraceStats");
        freTrace = (FnFreeTrace)GetProcAddress(h, "FreeTraceEvents");
        if (!set || !build || !fre) { printf("[FAIL] missing base export in %s\n", path); return false; }
        if (!solve || !stats || !freTrace) { printf("[FAIL] missing solver export in %s\n", path); return false; }
        return true;
    }
};

static unsigned g_seed = 12345u;
static double rnd() { g_seed = g_seed * 1664525u + 1013904223u; return (double)((g_seed >> 8) & 0xFFFFFF) / 16777216.0; }
static void seed(unsigned s) { g_seed = s ? s : 1u; }

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

static bool isLeftKey(unsigned char kc) {
    return kc == 'D' || kc == 'F' || kc == 'S' || kc == 'A';
}

struct Press { double t; int note; int hand; unsigned char key; };

struct Stats {
    int    nPress = 0, handL = 0, handR = 0, handRuns = 0;
    int    frag = 0, longest = 0;
    int    maxSame = 0, r3 = 0, r5 = 0;
    double gapMean = 0, gapSD = 0, gapMax = 0;
    int    jumps5 = 0, jumps20 = 0;
    double leadMean = 0, leadMax = 0;
    vector<Press> presses;
};

// 用与 harness.cpp 完全相同的算法统计（口径必须一致，否则没法对比）
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

    int lastHand = -1, curSame = 0; bool has = false;
    for (size_t i = 0; i < ev.size(); i++) {
        if (ev[i].ReleaseOnly) continue;
        int h = isLeftKey(ev[i].KeyCode) ? 0 : 1;
        if (!has) { curSame = 1; has = true; }
        else if (h == lastHand) curSame++;
        else {
            if (curSame == 1) st.frag++;
            if (curSame > st.longest) st.longest = curSame;
            st.handRuns++; curSame = 1;
        }
        lastHand = h;
        if (curSame > st.maxSame) st.maxSame = curSame;
        if (curSame >= 3) st.r3++;
        if (curSame >= 5) st.r5++;

        size_t lo = 0, hi = c.evTime.size();
        while (lo < hi) { size_t mid = (lo + hi) / 2; if (c.evTime[mid] < ev[i].TriggerTime) lo = mid + 1; else hi = mid; }
        size_t best = (lo < c.evTime.size()) ? lo : (c.evTime.empty() ? 0 : c.evTime.size() - 1);
        if (best > 0 && (lo >= c.evTime.size() ||
            fabs(c.evTime[best] - ev[i].TriggerTime) > fabs(c.evTime[best - 1] - ev[i].TriggerTime)))
            best--;
        Press p{ ev[i].TriggerTime, (int)best, h, ev[i].KeyCode };
        st.presses.push_back(p);
        double lead = fabs(ev[i].TriggerTime - c.evTime[best]) * 1000.0;
        st.leadMean += lead;
        if (lead > st.leadMax) st.leadMax = lead;
    }
    st.leadMean /= (double)st.presses.size();
    if (curSame > 0) {
        if (curSame == 1) st.frag++;
        if (curSame > st.longest) st.longest = curSame;
    }

    vector<double> gaps;
    for (size_t i = 1; i < ts.size(); i++) gaps.push_back(ts[i] - ts[i - 1]);
    if (gaps.empty()) return st;
    vector<double> sorted = gaps;
    sort(sorted.begin(), sorted.end());
    st.gapMax = sorted.back();
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

static unsigned char g_lk[2] = { 'D', 'F' };
static unsigned char g_rk[2] = { 'J', 'K' };
static double g_lt[2] = { 0.8, 0.8 };
static double g_rt[2] = { 0.8, 0.8 };

static void SetCfg(Lib& lib, double bpmLimit, int handPref) {
    TechniqueConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.leftKeys = g_lk; cfg.leftKeyCount = 2;
    cfg.rightKeys = g_rk; cfg.rightKeyCount = 2;
    cfg.leftPressTimes = g_lt; cfg.rightPressTimes = g_rt;
    cfg.bpmLimit = bpmLimit;
    cfg.handPreference = handPref;
    lib.set(&cfg);
}

static vector<HitEvent> BuildGreedy(Lib& lib, Chart& ch, double bpmLimit, int handPref) {
    SetCfg(lib, bpmLimit, handPref);
    int outCount = 0, n = (int)ch.evTime.size();
    HitEvent* r = lib.build(ch.evTime.data(), ch.evPress.data(), ch.evFloor.data(),
                            ch.evSpeed.data(), n, ch.bpm, ch.globalSpeed, &outCount);
    vector<HitEvent> out;
    if (r && outCount > 0) out.assign(r, r + outCount);
    if (r) lib.fre(r);
    return out;
}

// 求解器返回 TraceEvent，转成 HitEvent 以复用同一套 Analyze 口径
static vector<HitEvent> BuildSolved(Lib& lib, Chart& ch, double bpmLimit, int handPref,
                                    SolveOptions opt, TraceStats* outStats) {
    SetCfg(lib, bpmLimit, handPref);
    int outCount = 0, n = (int)ch.evTime.size();
    TraceEvent* r = lib.solve(ch.evTime.data(), ch.evPress.data(), ch.evFloor.data(),
                              ch.evSpeed.data(), n, ch.bpm, ch.globalSpeed, &opt, &outCount);
    vector<HitEvent> out;
    if (r && outCount > 0) {
        for (int i = 0; i < outCount; i++) {
            HitEvent h = {};
            h.TriggerTime = r[i].TriggerTime;
            h.KeyCode = r[i].KeyCode;
            h.ReleaseOnly = (r[i].Flags & TRACE_FLAG_RELEASE_ONLY) ? TRUE : FALSE;
            h.IsHoldRelated = (r[i].Flags & TRACE_FLAG_HOLD_RELATED) ? TRUE : FALSE;
            h.ReleaseKeyCode = r[i].ReleaseKeyCode;
            out.push_back(h);
        }
    }
    if (r) lib.freTrace(r);
    if (outStats) {
        TraceStats* s = lib.stats();
        if (s) { *outStats = *s; CoTaskMemFree(s); }
    }
    return out;
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

struct Case { const char* name; vector<double> sp; double bpmLimit; };

int main(int argc, char** argv) {
    const char* dll = (argc > 1) ? argv[1] : "TechniqueSimulator/x64/Release/TechniqueSimulator.dll";
    int beam = 4;
    double fragP = 1.0, runP = 0.6, roughP = 0.25;
    for (int i = 2; i < argc; i++) {
        if (strcmp(argv[i], "--beam") == 0 && i + 1 < argc) beam = atoi(argv[++i]);
        else if (strcmp(argv[i], "--frag") == 0 && i + 1 < argc) fragP = atof(argv[++i]);
        else if (strcmp(argv[i], "--run") == 0 && i + 1 < argc) runP = atof(argv[++i]);
        else if (strcmp(argv[i], "--rough") == 0 && i + 1 < argc) roughP = atof(argv[++i]);
    }
    Lib lib;
    printf("dll: %s   beam=%d  frag=%.2f run=%.2f rough=%.2f\n\n", dll, beam, fragP, runP, roughP);
    if (!lib.Load(dll)) return 1;
    printf("[OK] exports resolved (base 4 + solver 3)\n\n");

    const double BPM = 400.0, SUB = 4.0;
    vector<Case> cases;
    auto add = [&](const char* nm, const vector<double>& sp, double lim) {
        Case c; c.name = nm; c.sp = sp; c.bpmLimit = lim; cases.push_back(c);
    };
    seed(1);  add("flat1x",     Flat(400, 1.0), 500);
    seed(3);  add("step8_jit",  Steppy(600, 8, 0.6, 1.8, true), 500);
    seed(5);  add("ramp_jit",   Ramp(600, 0.5, 3.0, 0.30), 500);
    seed(7);  add("sec16",      Sections(800, 64, 0.4, 2.5), 500);
    seed(9);  add("wild",       Steppy(1000, 4, 0.14, 58.67, true), 500);
    seed(11); add("slow_jit",   Steppy(600, 16, 0.3, 1.2, true), 500);
    seed(13); add("surge",      Steppy(800, 32, 0.5, 4.0, true), 500);
    // 密集：8 分音符连打（bpmLimit 折到 8 分之下，逼出倍乘路径）
    seed(17); add("dense16",    Flat(600, 1.0), 200);
    // 极稀疏：音间隔远超一拍，考验空片/翻手相位
    seed(19); add("sparse",     Flat(120, 0.25), 500);

    SolveOptions opt;
    opt.beamWidth = beam;
    opt.maxMultiplier = 7;
    opt.fragPenalty = fragP;
    opt.runPenalty = runP;
    opt.roughPenalty = roughP;
    opt.dropPenalty = 10000.0;

    printf("%-11s %-7s | %6s %6s | %6s %6s | %5s %5s | %6s %6s | %7s %7s | %5s\n",
           "case", "algo", "press", "runs", "frag", "long", "maxS", "r3+",
           "leadAvg", "leadMax", "gapSD", "gapMax", "drop");
    printf("-------------------------------------------------------------------------------------------------------------\n");

    int totalFragG = 0, totalFragS = 0, worstDrop = 0, worseCases = 0;
    for (size_t i = 0; i < cases.size(); i++) {
        Chart ch = MakeChart(cases[i].sp, BPM, SUB);
        vector<HitEvent> g = BuildGreedy(lib, ch, cases[i].bpmLimit, 1);
        TraceStats ts; memset(&ts, 0, sizeof(ts));
        vector<HitEvent> s = BuildSolved(lib, ch, cases[i].bpmLimit, 1, opt, &ts);

        Stats G = Analyze(g, ch);
        Stats S = Analyze(s, ch);

        // 零丢音是硬约束：按键数必须等于谱面音数（贪心与求解器都应如此）
        int notes = (int)ch.evTime.size();
        int dropG = notes - G.nPress, dropS = notes - S.nPress;
        if (dropS < 0) dropS = 0;
        if (dropS > worstDrop) worstDrop = dropS;

        printf("%-11s %-7s | %6d %6d | %6d %6d | %5d %5d | %6.2f %6.2f | %7.3f %7.3f | %5d\n",
               cases[i].name, "greedy", G.nPress, G.handRuns, G.frag, G.longest,
               G.maxSame, G.r3, G.leadMean, G.leadMax, G.gapSD, G.gapMax, dropG);
        printf("%-11s %-7s | %6d %6d | %6d %6d | %5d %5d | %6.2f %6.2f | %7.3f %7.3f | %5d",
               "", "solve", S.nPress, S.handRuns, S.frag, S.longest,
               S.maxSame, S.r3, S.leadMean, S.leadMax, S.gapSD, S.gapMax, dropS);
        if (dropS == 0 && S.frag <= G.frag) printf("  OK");
        else printf("  !!");
        if (S.frag > G.frag) worseCases++;
        totalFragG += G.frag; totalFragS += S.frag;
        printf("   [frag %+d, long %+d, jumps5 %+d, jumps20 %+d, pieceVar %.2e, nodes %d, cost %.1f]\n",
               S.frag - G.frag, S.longest - G.longest, S.jumps5 - G.jumps5, S.jumps20 - G.jumps20,
               ts.pieceLenVariance, ts.solverNodes, ts.totalCost);
    }

    printf("\n---- 汇总 ----\n");
    printf("frag 合计：贪心 %d -> 求解器 %d (%+.1f%%)\n", totalFragG, totalFragS,
           totalFragG ? 100.0 * (totalFragS - totalFragG) / totalFragG : 0.0);
    printf("frag 变差的谱例数：%d\n", worseCases);
    printf("最大丢音数：%d  %s\n", worstDrop, worstDrop == 0 ? "(零丢音，硬约束满足)" : "(!! 有丢音)");
    printf("\nDONE\n");
    return 0;
}