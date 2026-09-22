# ADOFAIMacro

[![License](https://img.shields.io/github/license/adofaiex/ADOFAIMacro?color=blue)](LICENSE.txt)
[![Downloads](https://img.shields.io/github/downloads/adofaiex/ADOFAIMacro/total)](https://github.com/adofaiex/ADOFAIMacro/releases)
[![C# 12.0](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET Framework 4.8.1](https://img.shields.io/badge/.NET%20Framework-4.8.1-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework)

[English README](README.en.md)

**ADOFAIMacro** 是《A Dance of Fire and Ice》(ADOFAI) 的 UnityModManager 模组：解析谱面地板时间戳，由独立高优先级线程微秒级定时发送按键。从"直接判定触发"到"系统级按键模拟"再到"左右手手法模拟"，覆盖纯自动过谱到拟真手法的各种场景。

> ⚠️ **两条红线，先说清楚：**
> - **不要修改 `Info.json`** —— 模组带防篡改检测，改动后启用时会直接退出游戏。
> - **与 BaseMacro 不兼容** —— 检测到会直接退出游戏。

---

## 目录

- [功能一览](#功能一览)
- [快速上手](#快速上手)
- [触发模式与输入链路](#触发模式与输入链路)
- [设置速查](#设置速查)
- [手法模拟指南](#手法模拟指南)
- [游戏内快捷键](#游戏内快捷键)
- [常见问题](#常见问题)
- [构建（开发者）](#构建开发者)
- [项目结构](#项目结构)
- [许可证与相关项目](#许可证与相关项目)

---

## 功能一览

- **自动触发**：谱面时间戳 → 高精度定时按键；双缓冲时间锚点同步主线程与工作线程，无锁零等待。
- **四种输入路径**：直接判定 / SendInput / SkyHook 底层注入（NtInject / NtSendInput）/ 虚拟异步键盘直喂（跳过系统注入，亚微秒抖动）。
- **手法模拟**：左右手交替、BPM 阈值自动细分时间片（单手多指连打）、可配置按键 / 顺序 / 按压时长、变速分段覆盖、每关独立配置；核心算法在 C++ 原生 DLL 中运行。
- **时间微调**：±100 ms 偏移，游戏内方向键实时调节；判定误差自动校准（闭环，自动消除变速段偏移）。
- **按键过滤**：黑 / 白名单，同步（KeyCode）与异步（VK 码）独立过滤。
- **其他**：死亡后自动按键、游玩期 GC 停顿抑制、窗口失焦拦截、中英双语 UI。

---

## 快速上手

**前置**：已安装 ADOFAI 和 [UnityModManager](https://www.nexusmods.com/site/mods/21)（≥ 0.27.0）。

1. 从 [Releases](https://github.com/adofaiex/ADOFAIMacro/releases) 下载压缩包，用 UMM 安装；或手动解压到游戏的 `Mods/ADOFAIMacro/` 目录。
   - 目录内应包含：`ADOFAIMacro.dll`、`InputSystem.dll`、`TechniqueSimulator.dll`、`Localization/`（zh-CN / en-US）。
2. 启动游戏，打开 UMM 模组窗口，启用 **ADOFAIMacro**。
3. 「宏」选项卡 → 勾选 **启用宏**。
4. 默认配置（按键模拟关闭 = 直接判定）即可自动过谱；判定整体偏早/偏晚时，到「延迟设置」调整 **延迟 (ms)**，或游戏内按 <kbd>←</kbd>/<kbd>→</kbd> 微调。

就这么多。更多玩法往下看。

---

## 触发模式与输入链路

「按键设置」中的三个开关组合出四条链路，按需选择：

| 你想要什么 | 配置 |
|---|---|
| 只要能过谱，链路最短 | 按键模拟 **关** |
| 需要真实系统按键（连打观感、按键音效、第三方工具读键盘） | 按键模拟 **开** + 使用高级输入 **关**（走 SendInput） |
| 高频谱面 / 多软件并行 / SendInput 被拦截 | 按键模拟 **开** + 使用高级输入 **开** + 输入模式 **自动** |
| 极致同步精度 | 上一行配置 + **虚拟异步键盘直喂** 开（默认已开） |

各链路说明：

1. **直接判定**（`SimulateKeyPress = false`）：工作线程计数 → 主线程调用游戏 `Hit()`。零系统副作用、延迟最可控，但游戏将其视为自动命中，不产生真实按键。
2. **SendInput**：标准 Win32 注入，兼容性最好。
3. **SkyHook 高级输入**：通过 `InputSystem.dll` 走更底层路径。输入模式四选一：
   - **自动**：优先选择可用的最底层方式（建议从这里开始）；
   - **NtInject**：最底层，直接注入原始输入流；
   - **NtSendInput ★**：内核边界注入；
   - **SendInput**：同标准 Win32。
4. **虚拟异步键盘直喂**：合成按键事件直接送进游戏自身的输入队列（与真实键盘钩子同一入口），跳过整个系统注入链路，时间戳本地生成、亚微秒抖动，且无窗口焦点依赖。不可用（如按键无映射、布局校验失败）时**自动回退**注入路径，无需手动干预。

---

## 设置速查

按 UMM 面板的选项卡组织。「内部名」对应配置文件中的字段。

### 宏

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 启用宏 | `Macro` | 关 | 宏总开关。 |

### 按键设置

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 按键序列 (逗号分隔) | `MacroKeys` | `D,F,J,K` | 简单轮转模式的按键序列。 |
| 按键模拟 | `SimulateKeyPress` | 关 | 关 = 直接判定；开 = 系统按键模拟。 |
| 使用高级输入 | `SkyHookMode` | 关 | 开启后走 SkyHook 路径，否则 SendInput。 |
| 虚拟异步键盘直喂 | `UseVirtualAsyncInput` | 开 | 见上文链路 4；不可用自动回退。 |
| 虚拟按键同步系统输入 | `MirrorVirtualKeys` | 开 | 直喂成功后同步注入一份真实按键，让按键显示器等第三方工具看见虚拟按键；注入回声自动丢弃，不会双判定。 |
| Win API 输入模式 | `InputMode` | 自动 | 自动 / NtInject / NtSendInput ★ / SendInput。 |

### 延迟设置

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 延迟 (ms) | `TimeOffset` | 0 | 宏触发时间偏移，范围 −100 ~ 100。 |
| 调整步长 | `AdjustStep` | 1 | 游戏内方向键每次调整的偏移量，0.1 ~ 10。 |
| 允许左右键调整延迟(游戏中) | `EnableArrowTimeAdjust` | 开 | 游戏内 <kbd>←</kbd>/<kbd>→</kbd> 直接调偏移。 |
| 允许Ctrl+左右键调整步长偏移(游戏中) | `EnableKeyAdjust` | 开 | 游戏内 <kbd>Ctrl</kbd>+<kbd>←</kbd>/<kbd>→</kbd> 调步长。 |
| 启用高精度时间（提高同步精度） | `HighPrecisionTime` | 关 | 切换更精确的时钟源。 |
| [实验性] 启用高精度异步 | `HighPrecisionAsync` | 关 | 实验特性，不确定有问题再动。 |
| 判定误差自动校准 | `AutoCalibrateJudgement` | 开 | 闭环：用实际判定误差反馈自动补偿偏移（含变速段），每局自动收敛。 |

### 按键过滤

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 启用按键过滤 | `EnableKeyFilter` | 关 | 过滤系统总开关。 |
| 过滤模式 | `FilterMode` | 黑名单 | 黑名单 = 阻止列表内按键；白名单 = 仅放行列表内按键。 |
| 按键列表 (逗号分隔) | `FilteredKeys` | `F1,F2,F3,F4` | 同步输入过滤。 |
| 异步按键列表 (逗号分隔) | `FilteredAsyncKeys` | 空 | 异步输入过滤（需高级输入 / SkyHook 模式）。 |

### 其他选项

| 面板项 | 内部名 | 默认 | 说明 |
|---|---|---|---|
| 游玩期抑制GC停顿 | `SuppressGcPauses` | 关 | 高密度图消除 GC 造成的误差尖峰（GC 在加载期集中发生）。 |
| 死亡后自动按键 | `EnableDeathKey` | 关 | **仅高级输入（SkyHook）模式生效**。 |
| 延迟秒数 | `DeathKeyDelay` | 5 | 死亡后延迟多少秒按键，0.1 ~ 30。 |
| 按键 | `DeathKeyInput` | `R` | 死亡后按的键，支持键名（SPACE、ENTER…）或虚拟键码（0x52）。 |
| 游戏允许切换到失败模式 | `ChangeNoFaillInPlay` | 关 | 游玩中解锁 NoFail 切换。 |
| 游戏中允许切换判定 | `ChangeJudementInPlay` | 关 | 游玩中解锁判定模式切换。 |
| 锁定关卡编辑器 | `LockLevelEditor` | 关 | 防误操作。 |
| 窗口未激活时阻止按键输入 | `BlockInputWhenUnfocused` | 开 | 失焦时不发送按键（工作线程不暂停，回到前台继续）。 |

### 按键书写格式（通用）

- 支持键名：`A`–`Z`、`0`–`9`、`F1`–`F12`、`SPACE`、`ENTER`、`ESC`、`TAB`、`SHIFT`、`CTRL`、`ALT`、方向键（`UP`/`DOWN`/`LEFT`/`RIGHT`）等；
- 支持十六进制虚拟键码：如 `0x41`；
- 多个按键用英文逗号分隔，如 `J,K,L`。

---

## 手法模拟指南

开启后（需同时开启 **按键模拟**），宏不再简单轮转按键序列，而是模拟真人双手打谱。时间按“片”划分：未超过 **速度阈值** 时，每音一片并保持 **起始手（主手）** 连续敲击；超过阈值时自动细分，由左右手交替承担。快段结束后进入慢段，会自动回到主手。

> 实际速率按音符间隔折算（音符/分钟），不是砖 BPM：直线砖 ×1，90° 砖 = 半拍 ×2，45° 砖 ×4；SetSpeed 变速同样按实际间隔体现。

### 基本参数

| 面板项 | 默认 | 说明 |
|---|---|---|
| 启用手法模拟（左右手交替） | 关 | 总开关，需同时开启 **按键模拟**。 |
| 起始手 | 右手 | 慢段 / 每片的主手。 |
| 全局·速度阈值 (BPM) | 500 | 超过此实际音符速率时细分时间片（50 ~ 2000）。 |
| 左/右手按键 | `D,F` / `J,K` | 每只手可用键，内置预设 DF/JK、DS/JK、ASDF/JKL。 |
| 左/右手顺序 | 空 | 见下方格式说明，留空 = 默认轮转。 |
| 左/右手时长 | `0.8,0.8` | 按压时长比例（0 ~ 1）。 |
| 变速容差 | 0 | 自动微调 BPM 让时间片对齐事件时序。0 = 关闭，0.2 = 适中，0.5 = 激进。 |
| 多押按键均分 | 关 | 多押默认主手取满、余数给另一手；开启后对半均分。 |

**按键顺序格式**：用 `|` 分隔不同按键数的片，逗号分隔 1-based 键序号。例如 `1,2 | 1,2 | 1,2,1`：单键片按第 1、2 键轮转，三键片按 1→2→1。留空 = 默认顺序。

### 分片规则

- 片长以**实际音符间隔**为基准：跳过同刻双押/和弦，到下一个音符的时间差；切点吸附到事件间隙。
- 不超过阈值：每音一片，该手单指连打。
- 超过阈值：按“单指敲击上限 = 阈值”计算手指数 `n = ceil(音符速率 / 阈值)`。每片由一只手负责 `n` 个音中的一半：`n` 为偶数时两手各 `n/2` 交替；`n` 为奇数时主手多一指。片内事件数超过该手按键数时取满全部按键。
- 多押：同一时刻按键数超过单手按键数时，默认主手取满、余数交给另一手；开启 **多押按键均分** 后对半均分到两手，单数多的一键给主手。
- “同一时刻”自适应识别：相邻事件间隔 ≤12ms 且整簇跨度 ≤35ms 视为一次多押。

### 按住时长

按住时长 = `max(该音实际音符间隔 × 左/右手时长比例, 50ms)`，并以“本片音符跨度 + 最小内部间隔（跳过 ≤12ms 簇内间隙）”为上限。密簇内退到簇外间隔，同刻双押/和弦共享同一基准，且不短于一帧。

### 配置（Profiles）

可保存多套完整手法参数（按键、顺序、时长、起始手、变速容差、分段），面板上一键新建 / 删除 / 切换。

### 变速分段

在某个 floor 区间内覆盖全局设置：每段可独立指定 **BPM 阈值** 与 **左右手按键 / 顺序 / 时长**（留空字段继承全局）。段边界处自动重置手序、释放跨段长按键。

### 关卡特定配置

每张图可以保存独立配置：

- 文件与关卡同目录，命名 `关卡名.adofaimacro.json`；
- 进关自动加载（“自动从关卡目录加载”开关，默认开）；
- 面板底部可手动 加载 / 保存到关卡目录 / 删除，并显示当前关卡的配置状态。

适合给高 BPM 段落配更小的按键组合、为特定手癖设计键序、按图精调参数。

### 注意

- **刚进游戏需要先死亡一次来校准时间**（面板同款提示）。
- 核心算法在原生 `TechniqueSimulator.dll` 中运行，请确保它位于 `Mods/ADOFAIMacro/` 目录。**Release 版本没有 DLL 就没有手法模拟输出**（调试版可回退 C# 实现）。

---

## 游戏内快捷键

| 按键 | 作用 | 生效条件 |
|---|---|---|
| <kbd>←</kbd> / <kbd>→</kbd> | 按当前步长增减延迟 | 允许左右键调整延迟 开 |
| <kbd>Ctrl</kbd> + <kbd>←</kbd> / <kbd>→</kbd> | 步长 ±0.1 | 允许Ctrl+左右键调整步长 开 |

建议先在短图测试，确认稳定后再上长图 / 高密谱。

---

## 常见问题

**Q1：宏开了但没反应**
按顺序检查：UMM 中模组已启用 → 启用宏已勾选 → 按键序列格式正确（英文逗号分隔）→ 若开了"窗口未激活时阻止按键输入"，切出游戏时按键会被拦截（回到前台即恢复）。

**Q2：时准时不准、偶尔漏键**
先用方向键微调延迟（每次 1 ms）；高频谱面开高级输入 + 输入模式自动；高密度图开"游玩期抑制GC停顿"；仍冲突就开按键过滤隔离干扰源。

**Q3：变速谱面偏移大**
确认"判定误差自动校准"开启（默认开）；手法模拟下可调高"变速容差"，或用分段给变速段单独设 BPM 阈值。

**Q4：死亡后自动按键不生效**
仅高级输入（SkyHook）模式生效 → 确认已开启；确认死亡按键已启用、键名 / 键码有效；必要时增大延迟秒数。

**Q5：按键显示器看不到宏按键**
虚拟异步键盘直喂不经过系统输入流，第三方工具读不到 → 开启"虚拟按键同步系统输入"（默认开）。

**Q6：按键过滤"没有作用"**
确认过滤已启用；确认模式（黑 / 白名单）与预期一致；高级输入场景下别忘了设置**异步**按键列表。

**Q7：启用模组后游戏直接退出**
`Info.json` 被修改过（防篡改检测），还原它；或装了 BaseMacro（互斥），卸载之。

**Q8：手法模拟没输出 / 提示 DLL 不可用**
把 `TechniqueSimulator.dll` 放到 `Mods/ADOFAIMacro/` 目录；Release 版缺 DLL 时手法模拟无事件输出。

**Q9：刚进游戏判定飘 / 需要死亡一次？**
是。首次进入需要死亡一次完成时间校准，之后正常。

提 issue 时建议附上：游戏版本、模组版本、关键配置截图、是否使用 SkyHook 及当前输入模式、复现步骤与日志。

---

## 构建（开发者）

- 环境：Visual Studio 2022（或 MSBuild），.NET Framework 4.8.1，C# 12。
- 打开 `ADOFAIMacro-Dev.csproj`（或 `ADOFAIMacro-Dev.slnx`），确认 `HintPath` 指向你本机 ADOFAI 安装目录——需要游戏自带的 `Assembly-CSharp.dll`、`UnityEngine*.dll`、`SkyHook.Unity.dll`、`UnityModManager.dll`，以及 `Newtonsoft.Json.dll`（本地化系统依赖，从游戏目录或 NuGet 获取）。
- 还原 NuGet 包（packages.config 方式）后编译 Release。
- 部署产物到 `Mods/ADOFAIMacro/`：`ADOFAIMacro.dll` + `Localization/` + `InputSystem.dll` + `TechniqueSimulator.dll`。

---

## 项目结构

```text
ADOFAIMacro/
├─ Main.cs                      # 入口：模组生命周期、DLL 加载、防篡改检测
├─ Settings.cs                  # 设置定义 + UMM 面板 UI
├─ UIUtils.cs                   # 面板绘制辅助
├─ ShowText.cs                  # 游戏内按键显示覆盖层
├─ Patches.cs                   # Harmony 补丁（输入链路、判定误差回灌等）
├─ Localization/
│  ├─ LocalizationManager.cs    # JSON 本地化（Newtonsoft.Json）
│  ├─ zh-CN.json / en-US.json
├─ Macro/
│  ├─ Macro.cs                  # 核心：时间锚点、工作线程调度、事件生成
│  ├─ VirtualAsyncInput.cs      # 虚拟异步键盘：合成事件直喂游戏输入队列
│  ├─ AsyncInputManager.cs      # SkyHook 输入管理
│  ├─ InputSystem.cs            # InputSystem.dll P/Invoke 封装
│  ├─ TechniqueSimulator.cs     # 手法模拟器 P/Invoke 封装
│  ├─ LevelTechniqueManager.cs  # 关卡特定手法配置
│  ├─ PreciseNow.cs             # 精确本地时间（与游戏判定时钟同域）
│  ├─ DSPTimeSimulater.cs       # 音频 DSP 时间模拟
│  ├─ SkyHookSystem.cs          # SkyHook 结构体定义
│  └─ KeyMap.cs                 # 按键名 → 虚拟键码映射
└─ Platform/
   ├─ Windows.cs / Linux.cs     # 平台高精度计时
   └─ BaseSelect.cs             # 平台选择
```

---

## 许可证与相关项目

- 项目许可证：[LICENSE.txt](LICENSE.txt)
- 异步输入优化许可：[AsyncInputOptimize-LICENSE.txt](AsyncInputOptimize-LICENSE.txt)（GPL-3.0）
- [InputSystem](https://github.com/2228293026/InputSystem) —— 底层输入注入库（`InputSystem.dll`，运行时加载）
