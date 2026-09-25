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
    double speedChangeTolerance;
    // 按压时长风格：0=跟随音符片长（新），1=旧版 1.3.0.30 折叠片长
    int    pressDurationMode;
    // 多押按键均分：0=关（主手取满 maxK 后余数给另一手），1=开（对半均分到两手）
    int    multiChordBalance;
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

    // 新版接口：逐事件速度倍率（scrFloor.speed，相对基准 BPM）
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
}