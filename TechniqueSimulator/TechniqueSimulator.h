#pragma once
#include <windows.h>

#ifdef TECHNIQUE_SIMULATOR_EXPORTS
#define TECH_API __declspec(dllexport)
#else
#define TECH_API __declspec(dllimport)
#endif

#pragma pack(push, 8)

// 必须与 C# NativeHitEvent (Pack=8) 完全对齐
struct HitEvent {
    double        TriggerTime;
    unsigned char KeyCode;
    unsigned char _pad0[3];
    BOOL          ReleaseOnly;
    BOOL          IsHoldRelated;
    unsigned char ReleaseKeyCode;
    unsigned char _pad1[3];
};

// 每个变速分段的配置（含可选按键覆盖）
// 必须与 C# NativeTechniqueSegment (Pack=8) 完全对齐
struct TechniqueSegment {
    int    startFloor;           // offset 0
    int    endFloor;             // offset 4
    double bpmLimit;             // offset 8

    // 可选按键覆盖（nullptr = 使用全局配置）
    unsigned char* leftKeys;     // offset 16
    int            leftKeyCount; // offset 24
    // 4 bytes padding           // offset 28
    unsigned char* rightKeys;    // offset 32
    int            rightKeyCount;// offset 40
    // 4 bytes padding           // offset 44

    int** leftKeyOrders;         // offset 48
    int* leftOrderLengths;      // offset 56
    int   leftOrderCounts;       // offset 64
    // 4 bytes padding           // offset 68
    int** rightKeyOrders;        // offset 72
    int* rightOrderLengths;     // offset 80
    int   rightOrderCounts;      // offset 88
    // 4 bytes padding           // offset 92

    double* leftPressTimes;      // offset 96
    double* rightPressTimes;     // offset 104

    BOOL hasKeyOverride;         // offset 112
    // 4 bytes padding           // offset 116
    // sizeof = 120
};

// ── 离线轨迹事件（"播放器"用；比 HitEvent 多带角度定位信息）─────
//
// 与 HitEvent 的区别：HitEvent 只描述"什么时候按哪个键"（时间轴驱动够用），
// 角度驱动还需要知道"这是第几块砖的旋转里的第几拍"。floorIndex + angleFrac
// 这两个字段让回放完全不依赖时钟 —— 只比对行星当前转过的角度比例。
//
// angleFrac = 该按键时刻在"第 floorIndex 块砖的旋转"里已扫过的比例 ∈[0,1]。
//   0 = 刚进这块砖（行星落后整块砖的角度距离）
//   1 = 转到该砖的目标角（正好压在判定线上）
// 求解器是纯时间轴的（只认 entryTime），所以它算得出 angleFrac 的分子分母，
// 但 angleLength/isCW 属于游戏运行期数据 —— 留给托管层在加载轨迹时乘上去，
// 这样缓存文件不绑定运行期几何，回放时用当次运行的权威值。
struct TraceEvent {
    double        TriggerTime;      // offset 0
    float         AngleFrac;        // offset 8
    unsigned char KeyCode;          // offset 12
    unsigned char Flags;            // offset 13  bit0=ReleaseOnly bit1=IsHoldRelated
    unsigned char ReleaseKeyCode;   // offset 14
    unsigned char _pad0;            // offset 15
    int           FloorIndex;       // offset 16
    // 4 bytes padding               // offset 20
    // sizeof = 24
};

// TraceEvent.Flags 位定义
#define TRACE_FLAG_RELEASE_ONLY   0x01
#define TRACE_FLAG_HOLD_RELATED   0x02

// 离线求解器的搜索参数（决定"最优方案"的取舍口径）
struct SolveOptions {
    int    beamWidth;        // 每一层保留的候选数（越小越快、越大越可能找到更好的）
    int    maxMultiplier;    // 倍乘上限（对应贪心里的 mult < 7）
    double fragPenalty;      // 单音碎片片的罚分权重（游戏内"手法表"里的 frag）
    double runPenalty;       // 同手连击超出 2 后的罚分权重（游戏内"手法表"里的 longest）
    double roughPenalty;     // 相邻片长突变的罚分权重（"忽快忽慢"的观感）
    double dropPenalty;      // 丢音罚分（应始终远大于其它项，等价硬约束）
};

// 全局手法配置
// 必须与 C# NativeTechniqueConfig (Pack=8) 完全对齐
struct TechniqueConfig {
    unsigned char* leftKeys;
    int            leftKeyCount;
    // 4 bytes padding
    unsigned char* rightKeys;
    int            rightKeyCount;
    // 4 bytes padding

    int** leftKeyOrders;
    int* leftOrderLengths;
    int   leftOrderCounts;
    // 4 bytes padding
    int** rightKeyOrders;
    int* rightOrderLengths;
    int   rightOrderCounts;
    // 4 bytes padding

    double* leftPressTimes;
    double* rightPressTimes;

    double bpmLimit;
    int    handPreference;
    // 4 bytes padding

    TechniqueSegment* segments;
    int               segmentCount;
    // 4 bytes padding
    // 【2026-10-03】原末尾的 double speedChangeTolerance 已删除（变速容差整条
    // 移除）。它是被删除的速率死区 + 几何修补的唯一入口，删除后 sizeof 不变
    // （本结构体末尾已有 4 bytes padding，字段被 padding 吸收），故 ABI 无变化。
};

#pragma pack(pop)

extern "C" {
    TECH_API void SetTechniqueConfig(TechniqueConfig* config);

    TECH_API HitEvent* BuildTechniqueHitEvents(
        double* entryTimes,
        int* pressTypes,
        int* floorIndices,
        int     eventCount,
        double  bpm,
        double  speed,
        int* outEventCount);

    // 逐地板速度倍率版（变速谱面）：speedMuls[i] = 第 i 个事件所属地板的
    // scrFloor.speed；传 nullptr 等价于旧接口，行为逐事件一致。
    TECH_API HitEvent* BuildTechniqueHitEventsEx(
        double* entryTimes,
        int* pressTypes,
        int* floorIndices,
        double* speedMuls,
        int     eventCount,
        double  bpm,
        double  speed,
        int* outEventCount);

    TECH_API void FreeHitEvents(HitEvent* events);

    // ─────────────────────────────────────────
    //  离线求解器（"播放器"的数据来源）
    //
    //  与 BuildTechniqueHitEventsEx 的区别：那个是**单趟贪心**（每片只按当前
    //  片长贪心吃音，片长由 bpmLimit/speed 决定），从头到尾没有任何前瞻；
    //  这个是**束搜索**：在"片长 × 手上/主手 × 倍乘"的分层状态图上做前瞻，
    //  用 SolveOptions 里的罚分口径挑一套全局更整齐的切法。同一份输入两边都跑，
    //  离线 A/B 对比 frag / longest / 片长方差即可量化收益。
    //
    //  不改动既有 4 个导出的行为，纯增量。
    // ─────────────────────────────────────────

    // 求解：返回 TraceEvent 数组（CoTaskMemAlloc，用 FreeTraceEvents 释放）
    //   entryTimes/pressTypes/floorIndices/speedMuls 与 BuildTechniqueHitEventsEx 同义
    //   opts 传 nullptr = 用默认口径（等价于一次束宽 1 的搜索，即接近贪心基线）
    TECH_API TraceEvent* SolveTechniqueTrace(
        double* entryTimes,
        int*    pressTypes,
        int*    floorIndices,
        double* speedMuls,
        int     eventCount,
        double  bpm,
        double  speed,
        SolveOptions* opts,
        int* outEventCount);

    // 诊断：求解器内部的代价分解（frag 数 / 最长同手连击 / 丢音数 / 片长方差）
    //   传 nullptr = 不导出。调用方负责释放（CoTaskMemAlloc）。
    struct TraceStats {
        int    fragmentCount;      // 单音碎片片数（frag）
        int    longestSameHand;    // 最长同手连击（longest）
        int    droppedNotes;       // 未能覆盖的音数（应恒为 0）
        int    pieceCount;         // 片总数
        double pieceLenVariance;   // 片长方差（越小越匀）
        double totalCost;          // 搜索目标值
        double fragCost;
        double runCost;
        double roughCost;
        int    solverNodes;        // 展开的状态数（性能观测）
    };
    TECH_API TraceStats* GetLastTraceStats();

    TECH_API void FreeTraceEvents(TraceEvent* events);
}