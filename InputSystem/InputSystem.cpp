#include <windows.h>
#include <queue>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <memory>
#include <chrono>
#include <vector>
#include <set>
#include <array>
#include <cstdint>

#pragma comment(lib, "user32.lib")
#include "InputSystem.h"

// 确保 INPUT_API 导出
#ifndef INPUT_API
#define INPUT_API __declspec(dllexport)
#endif

// ─────────────────────────────────────────────
//  函数指针
// ─────────────────────────────────────────────
typedef UINT(NTAPI* NtUserInjectKeyboardInput_t)(KEYBDINPUT* pInputs, UINT nInputs);
typedef UINT(WINAPI* NtUserSendInput_t)(UINT cInputs, LPINPUT pInputs, int cbSize);

static NtUserInjectKeyboardInput_t pNtUserInjectKeyboardInput = nullptr;
static NtUserSendInput_t           pNtUserSendInput = nullptr;

// ─────────────────────────────────────────────
//  扫描码缓存（256 项，初始化一次）
// ─────────────────────────────────────────────
static std::array<WORD, 256> g_scanCache = {};

static void buildScanCache() {
    for (int i = 0; i < 256; i++)
        g_scanCache[i] = static_cast<WORD>(MapVirtualKey(i, MAPVK_VK_TO_VSC));
}

// ─────────────────────────────────────────────
//  扩展键表
// ─────────────────────────────────────────────
static bool g_extTableBuilt = false;
static bool g_extTable[256] = {};

static void buildExtTable() {
    if (g_extTableBuilt) return;
    const BYTE ext[] = {
        VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN,
        VK_HOME, VK_END, VK_PRIOR, VK_NEXT,
        VK_INSERT, VK_DELETE,
        VK_RCONTROL, VK_RMENU,
        VK_NUMLOCK, VK_SNAPSHOT
    };
    for (BYTE k : ext) g_extTable[k] = true;
    g_extTableBuilt = true;
}

// ─────────────────────────────────────────────
//  按键事件结构
// ─────────────────────────────────────────────
struct KeyEvent {
    BYTE  keyCode;
    BOOL  isDown;
    DWORD delayMs;

    KeyEvent() : keyCode(0), isDown(FALSE), delayMs(0) {}
    KeyEvent(BYTE k, BOOL d, DWORD ms) : keyCode(k), isDown(d), delayMs(ms) {}
};

// ─────────────────────────────────────────────
//  环形缓冲区（生产者互斥 + 单消费者）
//
//  发布顺序修正：旧实现 CAS 占位后再写槽内数据，
//  消费者 acquire 读到新 tail 时槽数据可能尚未写完（可见性窗口）。
//  现在由 producerMutex_ 保证多生产者互斥，先写槽数据、
//  再 release 发布 tail——消费者 acquire 读到新 tail 时数据必然可见。
//  队列现为冷路径（C# 宏热路径走 SendKeyDirect 同步直发），
//  用一把锁换取正确性，性能无关紧要。
// ─────────────────────────────────────────────
template<typename T, size_t MaxSize>
class RingBuffer {
    static_assert((MaxSize& (MaxSize - 1)) == 0, "MaxSize must be power of two");
    T buffer_[MaxSize];
    alignas(64) std::atomic<size_t> head_{ 0 };   // 读索引（消费者独占）
    alignas(64) std::atomic<size_t> tail_{ 0 };   // 写索引（生产者持锁递增）
    std::mutex producerMutex_;

public:
    bool push(const T& item) {
        std::lock_guard<std::mutex> lock(producerMutex_);
        size_t t = tail_.load(std::memory_order_relaxed);
        size_t h = head_.load(std::memory_order_acquire);
        if (t - h >= MaxSize)
            return false;                       // 队列满

        buffer_[t & (MaxSize - 1)] = item;
        tail_.store(t + 1, std::memory_order_release);
        return true;
    }

    bool pop(T& item) {
        size_t h = head_.load(std::memory_order_relaxed);
        size_t t = tail_.load(std::memory_order_acquire);
        if (h == t)
            return false;                            // 队列空

        item = buffer_[h & (MaxSize - 1)];
        head_.store(h + 1, std::memory_order_release);
        return true;
    }

    size_t size() const {
        // 注意：此处返回值可能因并发而不精确，但用于判断空/满足够安全
        return tail_.load(std::memory_order_acquire) - head_.load(std::memory_order_acquire);
    }

    bool empty() const {
        return size() == 0;
    }

    // 清空所有现有事件（返回清空的元素个数）
    size_t drain() {
        size_t cleared = 0;
        T dummy;
        size_t currentTail = tail_.load(std::memory_order_acquire);
        size_t h = head_.load(std::memory_order_relaxed);
        while (h < currentTail) {
            if (pop(dummy))
                ++cleared;
            h = head_.load(std::memory_order_relaxed);
        }
        return cleared;
    }
};

// ─────────────────────────────────────────────
//  InputSystem（优化版）
// ─────────────────────────────────────────────
class InputSystem {
private:
    // 环形缓冲区（容量 1024，2 的幂）
    static constexpr size_t QUEUE_CAPACITY = 1024;
    RingBuffer<KeyEvent, QUEUE_CAPACITY> ringBuffer_;

    // 用于条件变量等待的互斥锁（仅用于 wait/notify）
    std::mutex              queueMutex_;
    std::condition_variable queueCond_;

    std::unique_ptr<std::thread> workerThread_;
    std::atomic<bool> running_{ false };
    std::atomic<bool> processing_{ true };
    // 暂停确认：clearQueue 等待工作线程真正停下（替代 sleep_for(1ms) 赌时序）。
    // workerParked_ = 工作线程当前停靠在条件变量上；pauseEpoch_ 每次停靠自增，
    // 用于区分“新一次停靠”和“上一次的旧状态”。
    std::atomic<bool>     workerParked_{ false };
    std::atomic<unsigned> pauseEpoch_{ 0 };
    std::mutex              pauseMutex_;
    std::condition_variable pauseCond_;

    std::atomic<int>  processedCount_{ 0 };
    std::atomic<size_t> approxQueueSize_{ 0 };   // 近似的队列大小，用于外部查询

    // 当前输入模式及有效模式缓存
    std::atomic<int> requestedMode_{ (int)InputMode::Auto };
    std::atomic<InputMode> effectiveMode_{ InputMode::SendInput };   // 解析后的实际模式

    // 按下的键集合（256 位图，避免 std::set 每次按键事件的堆分配/释放）
    std::array<bool, 256> pressedKeys_{};
    int pressedKeyCount_ = 0;
    mutable std::mutex     pressedKeysMutex_;

    HMODULE hWin32u_ = nullptr;

    // ─────────────────────────────────────────
    InputSystem() {
        buildScanCache();
        buildExtTable();
        loadNtFunctions();
        // 初始化有效模式
        effectiveMode_.store(resolveMode(), std::memory_order_relaxed);
    }

    ~InputSystem() {
        shutdown();
        if (hWin32u_) FreeLibrary(hWin32u_);
    }

    InputSystem(const InputSystem&) = delete;
    InputSystem& operator=(const InputSystem&) = delete;

    // ── 加载底层函数 ──────────────────────────
    bool loadNtFunctions() {
        hWin32u_ = LoadLibraryA("win32u.dll");
        if (!hWin32u_) return false;

        pNtUserInjectKeyboardInput = reinterpret_cast<NtUserInjectKeyboardInput_t>(
            GetProcAddress(hWin32u_, "NtUserInjectKeyboardInput"));

        pNtUserSendInput = reinterpret_cast<NtUserSendInput_t>(
            GetProcAddress(hWin32u_, "NtUserSendInput"));

        return (pNtUserInjectKeyboardInput != nullptr || pNtUserSendInput != nullptr);
    }

    // ── 解析 Auto 模式下的实际模式 ────────────
    InputMode resolveMode() const {
        int m = requestedMode_.load(std::memory_order_relaxed);
        if (m == (int)InputMode::Auto) {
            if (pNtUserInjectKeyboardInput) return InputMode::NtUserInjectKeyboard;
            if (pNtUserSendInput)           return InputMode::NtUserSendInput;
            return InputMode::SendInput;
        }
        if (m == (int)InputMode::NtUserInjectKeyboard && !pNtUserInjectKeyboardInput) {
            if (pNtUserSendInput) return InputMode::NtUserSendInput;
            return InputMode::SendInput;
        }
        if (m == (int)InputMode::NtUserSendInput && !pNtUserSendInput)
            return InputMode::SendInput;

        return static_cast<InputMode>(m);
    }

    // ── 按键状态追踪 ──────────────────────────
    // 须持有 pressedKeysMutex_ 时调用
    void applyKeyStateLocked(BYTE keyCode, BOOL isDown) {
        if (isDown) {
            if (!pressedKeys_[keyCode]) { pressedKeys_[keyCode] = true; ++pressedKeyCount_; }
        }
        else {
            if (pressedKeys_[keyCode]) { pressedKeys_[keyCode] = false; --pressedKeyCount_; }
        }
    }

    void updateKeyState(BYTE keyCode, BOOL isDown) {
        std::lock_guard<std::mutex> lock(pressedKeysMutex_);
        applyKeyStateLocked(keyCode, isDown);
    }

    void releaseAllPressedKeys() {
        BYTE keys[256];
        int n = 0;
        {
            std::lock_guard<std::mutex> lock(pressedKeysMutex_);
            for (int k = 0; k < 256; ++k)
                if (pressedKeys_[k]) keys[n++] = static_cast<BYTE>(k);
            pressedKeys_.fill(false);
            pressedKeyCount_ = 0;
        }
        for (int i = 0; i < n; i++) {
            sendKeyCore(keys[i], FALSE);
            Sleep(5);
        }
    }

    // ── 核心发送（使用有效模式缓存）─────────────
    void sendKeyCore(BYTE keyCode, BOOL isDown) {
        WORD scan = g_scanCache[keyCode];
        DWORD flags = isDown ? 0 : KEYEVENTF_KEYUP;
        if (g_extTable[keyCode]) flags |= KEYEVENTF_EXTENDEDKEY;

        KEYBDINPUT ki = {};
        ki.wVk = keyCode;
        ki.wScan = scan;
        ki.dwFlags = flags;

        InputMode mode = effectiveMode_.load(std::memory_order_relaxed);

        switch (mode) {
        case InputMode::NtUserInjectKeyboard:
            pNtUserInjectKeyboardInput(&ki, 1);
            break;

        case InputMode::NtUserSendInput: {
            INPUT inp = {};
            inp.type = INPUT_KEYBOARD;
            inp.ki = ki;
            pNtUserSendInput(1, &inp, sizeof(INPUT));
            break;
        }

        case InputMode::SendInput:
        default: {
            INPUT inp = {};
            inp.type = INPUT_KEYBOARD;
            inp.ki = ki;
            ::SendInput(1, &inp, sizeof(INPUT));
            break;
        }
        }
    }

    // ── 工作线程（批量处理零延迟事件）──────────
    void workerProc() {
        while (running_) {
            // 1. 检查是否暂停处理
            if (!processing_.load(std::memory_order_relaxed)) {
                std::unique_lock<std::mutex> lock(queueMutex_);
                {
                    std::lock_guard<std::mutex> pauseLock(pauseMutex_);
                    workerParked_.store(true, std::memory_order_release);
                    pauseEpoch_.fetch_add(1, std::memory_order_release);
                }
                pauseCond_.notify_all();
                queueCond_.wait(lock, [this] {
                    return processing_.load(std::memory_order_relaxed) || !running_;
                    });
                workerParked_.store(false, std::memory_order_release);
                if (!running_) break;
                continue;
            }

            // 2. 取一个事件
            KeyEvent evt;
            if (!ringBuffer_.pop(evt)) {
                // 队列空，等待
                std::unique_lock<std::mutex> lock(queueMutex_);
                queueCond_.wait(lock, [this] {
                    return !ringBuffer_.empty() || !running_;
                    });
                continue;
            }

            // 3. 处理事件
            if (evt.delayMs > 0) {
                // 有延迟：单次处理
                sendKeyCore(evt.keyCode, evt.isDown);
                updateKeyState(evt.keyCode, evt.isDown);
                ++processedCount_;
                std::this_thread::sleep_for(std::chrono::milliseconds(evt.delayMs));
            }
            else {
                // 零延迟：批量收集后续零延迟事件
                const size_t BATCH_MAX = 64;
                std::array<KeyEvent, BATCH_MAX> batch;
                size_t batchCount = 0;
                batch[batchCount++] = evt;

                // 延迟事件暂存：不能在已收集的零延迟事件之前发送（会乱序），
                // 也不能丢弃 delayMs（旧实现两者都犯）。先记下，等本批按序发完
                // 再单独按单事件语义处理。
                bool hasDelayed = false;
                KeyEvent delayedEvt;

                while (batchCount < BATCH_MAX) {
                    KeyEvent next;
                    if (!ringBuffer_.pop(next)) break;   // 队列空，停止收集
                    if (next.delayMs > 0) {
                        delayedEvt = next;
                        hasDelayed = true;
                        break;
                    }
                    batch[batchCount++] = next;
                }

                // 批量发送零延迟事件
                for (size_t i = 0; i < batchCount; ++i) {
                    sendKeyCore(batch[i].keyCode, batch[i].isDown);
                }

                // 一次性更新按键状态（减少锁竞争）
                {
                    std::lock_guard<std::mutex> lock(pressedKeysMutex_);
                    for (size_t i = 0; i < batchCount; ++i)
                        applyKeyStateLocked(batch[i].keyCode, batch[i].isDown);
                }

                processedCount_ += static_cast<int>(batchCount);

                // 延迟事件：发送后按 delayMs 休眠，保持队列顺序与延迟语义
                if (hasDelayed) {
                    sendKeyCore(delayedEvt.keyCode, delayedEvt.isDown);
                    updateKeyState(delayedEvt.keyCode, delayedEvt.isDown);
                    ++processedCount_;
                    std::this_thread::sleep_for(std::chrono::milliseconds(delayedEvt.delayMs));
                }
            }

            // 更新近似队列大小（用于外部查询）
            approxQueueSize_.store(ringBuffer_.size(), std::memory_order_relaxed);
        }
    }

public:
    static InputSystem& getInstance() {
        static InputSystem instance;
        return instance;
    }

    // ── 模式控制 ──────────────────────────────
    int setInputMode(int mode) {
        if (mode < (int)InputMode::Auto || mode >(int)InputMode::SendInput)
            return -1;

        requestedMode_.store(mode, std::memory_order_relaxed);
        InputMode effective = resolveMode();
        effectiveMode_.store(effective, std::memory_order_relaxed);
        return (int)effective;
    }

    int getInputMode() const {
        return (int)effectiveMode_.load(std::memory_order_relaxed);
    }

    int getAvailableModes() const {
        int mask = (1 << (int)InputMode::Auto) | (1 << (int)InputMode::SendInput);
        if (pNtUserInjectKeyboardInput) mask |= (1 << (int)InputMode::NtUserInjectKeyboard);
        if (pNtUserSendInput)           mask |= (1 << (int)InputMode::NtUserSendInput);
        return mask;
    }

    // ── 初始化 ────────────────────────────────
    int initialize(int /*maxSize*/) {
        if (running_) return 0;   // 已运行

        running_ = true;
        processing_ = true;
        processedCount_ = 0;
        approxQueueSize_ = 0;
        {
            std::lock_guard<std::mutex> lock(pressedKeysMutex_);
            pressedKeys_.fill(false);
            pressedKeyCount_ = 0;
        }

        workerThread_ = std::make_unique<std::thread>(&InputSystem::workerProc, this);
        return 0;
    }

    // ── 入队按键事件 ──────────────────────────
    int pushKeyEvent(BYTE keyCode, BOOL isDown, DWORD delayMs) {
        if (!running_) return -1;

        if (!ringBuffer_.push(KeyEvent(keyCode, isDown, delayMs)))
            return -2;   // 队列满

        approxQueueSize_.store(ringBuffer_.size(), std::memory_order_relaxed);

        // 无条件在锁内 notify：旧实现仅 wasEmpty 时唤醒，但 wasEmpty 基于
        // 可能过期的 head_ —— 消费者刚清空队列正要进入 wait、生产者读到旧
        // head 误判"非空"而跳过 notify 的窗口里，事件会滞留无人处理。
        // 队列已是冷路径，每次一把锁 + 一次 notify 的代价无关紧要。
        {
            std::lock_guard<std::mutex> lock(queueMutex_);
            queueCond_.notify_one();
        }
        return 0;
    }

    // ── 直接发送（绕过队列）────────────────────
    int sendKeyDirect(BYTE keyCode, BOOL isDown) {
        if (!running_) return -1;
        sendKeyCore(keyCode, isDown);
        updateKeyState(keyCode, isDown);
        return 0;
    }

    int sendKeyCombination(const std::vector<BYTE>& keys, DWORD delayMs = 50) {
        if (!running_ || keys.empty()) return -1;
        for (BYTE k : keys) { sendKeyCore(k, TRUE);  updateKeyState(k, TRUE);  Sleep(delayMs); }
        for (auto it = keys.rbegin(); it != keys.rend(); ++it) {
            sendKeyCore(*it, FALSE); updateKeyState(*it, FALSE); Sleep(delayMs);
        }
        return 0;
    }

    int sendText(const char* text) {
        if (!running_ || !text) return -1;
        while (*text) {
            char c = *text++;
            SHORT vk = VkKeyScanA(c);
            if (vk == -1) continue;

            BYTE keyCode = LOBYTE(vk);
            BYTE shiftState = HIBYTE(vk);

            if (shiftState & 1) { sendKeyCore(VK_SHIFT, TRUE);  updateKeyState(VK_SHIFT, TRUE); }
            sendKeyCore(keyCode, TRUE);
            sendKeyCore(keyCode, FALSE);
            updateKeyState(keyCode, TRUE);
            updateKeyState(keyCode, FALSE);
            if (shiftState & 1) { sendKeyCore(VK_SHIFT, FALSE); updateKeyState(VK_SHIFT, FALSE); }
            Sleep(10);
        }
        return 0;
    }

    int startProcessing() {
        processing_.store(true, std::memory_order_relaxed);
        std::lock_guard<std::mutex> lock(queueMutex_);
        queueCond_.notify_one();
        return 0;
    }

    int stopProcessing() {
        processing_.store(false, std::memory_order_relaxed);
        return 0;
    }

    void clearQueue() {
        // 暂停处理
        processing_.store(false, std::memory_order_relaxed);

        // 等待工作线程确认暂停（带超时：工作线程可能正在 delayMs 睡眠，
        // 超时后按原语义继续清空，不会永久阻塞调用线程）
        if (!workerParked_.load(std::memory_order_acquire)) {
            unsigned epoch = pauseEpoch_.load(std::memory_order_acquire);
            std::unique_lock<std::mutex> pauseLock(pauseMutex_);
            pauseCond_.wait_for(pauseLock, std::chrono::milliseconds(100), [this, epoch] {
                return pauseEpoch_.load(std::memory_order_acquire) != epoch || !running_;
                });
        }

        // 清空现有事件（只清空已入队的，新事件保留）
        size_t drained = ringBuffer_.drain();
        approxQueueSize_.store(ringBuffer_.size(), std::memory_order_relaxed);

        // 释放所有按下的键
        releaseAllPressedKeys();

        // 恢复处理
        processing_.store(true, std::memory_order_relaxed);
        std::lock_guard<std::mutex> lock(queueMutex_);
        queueCond_.notify_one();
    }

    int getStatus(int* outQueueSize, int* outProcessedCount) {
        if (outQueueSize)      *outQueueSize = static_cast<int>(approxQueueSize_.load(std::memory_order_relaxed));
        if (outProcessedCount) *outProcessedCount = processedCount_.load(std::memory_order_relaxed);
        return 0;
    }

    void shutdown() {
        running_ = false;
        {
            std::lock_guard<std::mutex> lock(queueMutex_);
            queueCond_.notify_all();
        }
        if (workerThread_ && workerThread_->joinable()) {
            workerThread_->join();
            workerThread_.reset();
        }
        releaseAllPressedKeys();
    }

    void emergencyStop() {
        // 快速清空所有事件（包括新事件）
        KeyEvent dummy;
        while (ringBuffer_.pop(dummy)) {}
        approxQueueSize_.store(0, std::memory_order_relaxed);
        releaseAllPressedKeys();
    }

    bool isUsingNtFunctions() const {
        return pNtUserInjectKeyboardInput != nullptr || pNtUserSendInput != nullptr;
    }

    int getPressedKeysCount() const {
        std::lock_guard<std::mutex> lock(pressedKeysMutex_);
        return pressedKeyCount_;
    }
};

// ─────────────────────────────────────────────
//  DLL 入口
// ─────────────────────────────────────────────
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH: DisableThreadLibraryCalls(hModule); break;
    case DLL_PROCESS_DETACH: InputSystem::getInstance().shutdown(); break;
    }
    return TRUE;
}

// ─────────────────────────────────────────────
//  导出函数（接口完全不变）
// ─────────────────────────────────────────────
extern "C" {
    INPUT_API int  __stdcall Initialize(int maxQueueSize) { return InputSystem::getInstance().initialize(maxQueueSize); }
    INPUT_API int  __stdcall PushKeyEvent(BYTE keyCode, BOOL isDown, DWORD delayMs) { return InputSystem::getInstance().pushKeyEvent(keyCode, isDown, delayMs); }
    INPUT_API int  __stdcall SendKeyDirect(BYTE keyCode, BOOL isDown) { return InputSystem::getInstance().sendKeyDirect(keyCode, isDown); }
    INPUT_API int  __stdcall SendKeyCombination(BYTE* keys, int keyCount, DWORD delayMs) {
        if (!keys || keyCount <= 0) return -1;
        return InputSystem::getInstance().sendKeyCombination(std::vector<BYTE>(keys, keys + keyCount), delayMs);
    }
    INPUT_API int  __stdcall SendText(const char* text) { return InputSystem::getInstance().sendText(text); }
    INPUT_API int  __stdcall StartProcessing() { return InputSystem::getInstance().startProcessing(); }
    INPUT_API int  __stdcall StopProcessing() { return InputSystem::getInstance().stopProcessing(); }
    INPUT_API void __stdcall ClearQueue() { InputSystem::getInstance().clearQueue(); }
    INPUT_API int  __stdcall GetInputQueueStatus(int* queueSize, int* processedCount) { return InputSystem::getInstance().getStatus(queueSize, processedCount); }
    INPUT_API void __stdcall Shutdown() { InputSystem::getInstance().shutdown(); }
    INPUT_API void __stdcall EmergencyStop() { InputSystem::getInstance().emergencyStop(); }
    INPUT_API BOOL __stdcall IsUsingNtFunctions() { return InputSystem::getInstance().isUsingNtFunctions() ? TRUE : FALSE; }
    INPUT_API int  __stdcall GetPressedKeysCount() { return InputSystem::getInstance().getPressedKeysCount(); }

    // 模式控制
    INPUT_API int  __stdcall SetInputMode(int mode) { return InputSystem::getInstance().setInputMode(mode); }
    INPUT_API int  __stdcall GetInputMode() { return InputSystem::getInstance().getInputMode(); }
    INPUT_API int  __stdcall GetAvailableModes() { return InputSystem::getInstance().getAvailableModes(); }
}