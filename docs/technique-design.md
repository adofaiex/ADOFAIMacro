# 手法规划模型与参考对照 / Technique design and reference comparison

## 目标与分层

玩家手法的目标是更容易、更稳定地打完谱面。当前实现把规则变成可以替换、检查和比较的编排；事件生成正确不等于手法已经更顺手。

管线为：游戏地板快照 → `TechniqueTimeline` 判定时间线 → 节奏分组与手别选择 → 配置键位槽 → 物理按键及松键冲突处理 → `TechniqueEvent` → 轨迹缓存/现有播放器。

- 时间线保留 source floor、onset、逐地板速度、普通击键与长按边界。相邻可玩地板还提供下一拍间的实际转角；跳过地板时不猜测合成转角。
- `IGroupingStrategy` 声明是否读取速度参数，生成覆盖每个 attack 的连续分组。
- `IRunHandPolicy` 独立选择 KPS/节奏窗下一组的手。所选手决定该组可用键位数；不对已生成的组任意翻转手别。
- `TechniqueAssignment` 保留手别、首选槽、冲突处理后的槽及最终 VK。键位槽不是解剖学上的手指；当前没有键到手指/手腕位置的显式映射。
- 公共事件编译负责物理键别名、长按闭合、普通键时长和同刻事件排序。更换分组算法不重复实现这些规则。
- `TechniquePlayback` 统一规划结果到轨迹、规划结果到时间驱动事件、缓存轨迹到时间驱动事件的转换；游戏和离线冒烟调用同一份代码。转换只保留/映射字段，不读取引擎状态或发键。

## 当前规则

| 策略 | 分组依据 | 用手 | 普通按键时长 |
| --- | --- | --- | --- |
| `beatGrid`（默认） | 从实际击键时间推断基础网格，再用局部拍距和实际转角确认节奏分组；空格子仍推进相位 | 成组后按拍交替；保留已经符合节奏的相位 | 当前片/下一片几何 × 键位比例；按原版片界限制松键 |
| `kpsAlternating` | 首击到当前击严格小于 `1/(2*KPS)` 秒；受当前手不同 VK 数限制 | 独立连段用手规则 | 固定 0.125s 基准 × 比例 × 0.5；0.8 比例为 50ms |
| `tempoWindow` | 折叠半拍长度的两倍作为窗口；组内窗口只能收紧；受当前手不同 VK 数限制 | 独立连段用手规则 | 同 KPS，窗口大小不直接决定松键时长 |

`kpsAlternating` 是持久化标识，选择其他用手规则后仍保留此标识，避免旧配置迁移歧义。

两种连段用手规则：

1. `alternating`：第一组用配置起始手，后续组轮换。
2. `recoveredPreferred`：若距配置起始手最后一次 attack 已达到本组初始窗口，优先用起始手，否则交替。它是“稀疏段偏好起始手”的启发式，不表示人体恢复，也不考虑各手指疲劳。

手别历史局限于单次规划。复用策略对象、重试、换谱不携带上次规划的历史。beatGrid 不使用连段用手规则，继续按原相位工作。

KPS 忽略 BPM 上限和速度元数据，使用已经确定的真实 onset。只改这些未使用字段不会改变其指纹；onset、长按、键位/键序/比例、起手和 KPS/用手规则仍参与身份判定。半拍策略额外记录实际转角，节奏窗继续包含速度输入。缓存指纹不是安全校验，也不是手法质量评分。

半拍策略用转角和拍距区分连续 90° 四键组、重复三音或四音组，以及变速后附在一拍内的装饰音。换手发生在确认的拍组边界；休止按空拍推进相位。候选避开长按和键位配置边界，并用普通松键与懒松键的实际键位和持键时长检查新增风险。

连续 90° 段每四音查一次配置的四键键序；节拍落在段尾单音上时，一起评估换手和交界重按。紧凑三音按它在一拍内所占的比例和重复位置识别；均分三连音则用前后的整拍确定分组起点。同一拍内用同一手，休止仍按经过的拍数换算手别。这些规则不依赖曲名或地板号。两手装不下目标拍组或实际配键产生新风险时，保留原分组。懒松的最后一击本来就在谱末 15 ms 后释放，候选检查不因这一击换了键而将同样的时长判为新风险。

修订 29 还用同一拍长识别混合单音、双音、三音和四音的乐句：相邻拍点的时间间隔和之间的累计转角都要支持同一个拍数。连续拍段入口可以接续前一拍的手别；例如跨过一个空拍时，入口用手与前一实拍相同。新版候选在旧分组和相位修正完成后比较最终键流，快速重按检查至少覆盖 82.5 ms，避免 BPM 上限高时把验收范围缩得过小。

跨速度与键位的手序、四键键序、三连音和空拍入口回归位于 `tools/TechniqueTests/ChartRhythmChecks.cs`，可用 `dotnet run --project tools/TechniqueTests/TechniqueTests.csproj -c Release` 运行。整谱事件对照只能检验击打、长按和发键风险；验证某段手法还需要独立的拍组和键序预期。三连音没有足够的邻拍证据、快变速无法拟合稳定拍长或键位不够时，仍可能沿用旧网格的分组。

起手设置是没有接段证据时的偏好。前一段已经给出可接续的拍点和手别时，沿用入口相位；否则，如果下一段至少三个拍组的节拍和换手已经稳定，并且与当前段有明确的节拍交界，就允许从下一段反推当前段的起手。这可以选出玩家为接段使用的反手起句（错拍）。下一段保持原样；如果反推的手序放不下当前拍组，或导致新增快速重按、短按，就尝试原来的起手。均分三连音可以用谱头或谱尾补足一侧的边界，并按每组三音的时值接续经过变速点的手序。

均分三连音的空拍需要时间和转角同时支持：若两组三音的起点相隔两拍，后组接着用同一只手。进出三连音时可以分别使用两侧确认的拍长。连续 90° 段若在四音组的边界变速，每组使用自己的拍长和四键顺序，手序跨变速点接续；组内的变速不按这条规则推断。

规划器修订号进入策略指纹：规则或统计含义变化时递增，避免二进制轨迹格式未改、却读到旧算法缓存。当前升级会重新生成一次缓存。片长方差目前仍是策略内诊断：半拍统计名义网格长度，连段策略统计实际首末跨度，不宜将两者直接当作难度评分比较。

## 松键时长修复（2026-11-23）

默认 beatGrid 和 pieceFallback 保留实际片起止时间，以当前片宽、下一片宽/结束时间、是否同手计算松键，恢复 TechniqueSimulator.cpp 的 CalculateReleaseTime 与片界限制；末尾使用一个空哨兵片，长休止不跨到远处的下一组。合并 flam 时扩展松键窗口，移动边界音符时同步两片边界。比例仍对应每个键；没有固定 70/140 ms 限制，也不强制所有键在下一音符前松开。KPS/tempoWindow 的固定 125 ms 按压基准不变，长按与懒松键走原有专用路径。

PlannerRevision 升至 3，使旧公式和 70 ms 版本的轨迹缓存失效。这里恢复的是网格策略的松键计算合同，不声明托管分组与历史原生分组完全相同。新增分支/上限/兜底、170 BPM 比例、flam 片界等回归；本次本地改前/改后对比 808 个输入，四种策略的分组数量、逐音符手别、首选槽位、实际槽位与物理键均无差异。该差分程序在 scratch，不属于仓库回归。

## 分片回退与懒松键（可选，默认关闭）

- `pieceFallback` 分组：实现依据 `docs/technique-behavior-spec.md`（行为规格，第 1 至 8 节）。按半拍网格切片、手别交替；某手装不下时细分当前片，副手片仍溢出时回退重切前一个主手片，层级最高为 7。回退只发生在分组阶段，输出前已确定的分组不会翻转手别。
- 规格第 9 节列出了与外部参考的已知差异，包括步数上限、层级 7 时的截断，以及长按尾不计入切片。差异均未在游戏内验证。
- 懒松键（每个配置单独开关，`lazyRelease`）：非长按按键保持按下，直到同一键再次需要时才在其前松开；时间参数见规格第 10 节。关闭时默认策略的输出与之前一致。
- 验证：51 项规划器测试通过；与此前的实现在 30006 个输入（含随机、突发与真实谱面）上做过一次性的本地逐切片比较，结果一致（该对照程序含旧实现副本，已删除，未纳入仓库，无法在仓库内复现）；模组 Release 编译、配置往返与冒烟测试通过。以上只能确认事件合法与默认输出不变，不能证明更容易打，也未在游戏内验证。
- 许可与来源：规格作者在整理规格前阅读过外部参考实现（memsys-lizi 的 ADOFAI_Macro，未附许可证文件）。因此本实现不是严格的洁净室重写。合并前需确认授权，或由未阅读该参考代码的人依据规格重新实现并比对。
## 两个参考真正提供了什么

`ADOFAI_Macro_for_Publish-main` 的 `InputManager.cpp` 提供单手时间窗、容量不足时缩窗、组间换手、指序方向、松键和按拍对齐等规则。`BeatManager.cpp` 与 `FileManager.cpp` 没有实现，无法完成其完整解析和实机链路。这里的节奏窗是对部分思想的独立简化，不是行为兼容移植；没有照搬参考里的 BPM 修正、按拍对齐、随机偏差或动画。

`Adofai-Macro/app/technique.py` 可以独立调用。它还维护 `plus/lastkey/lk/WHOAMI` 用手状态、四键段特殊指序、多个键位层级和轮指松键传播。当前 KPS 只使用时间阈值思路，并没有复刻这些状态或键序。Python UI 每四键翻转左手键表；对物理键序列比较必须包含该变换。

两个参考都是作者规则，不是真人轨迹或手法难度真值。最长同手段较短、分组较少、方差较小，都不能单独作为“更容易”的判据。

## 可复现的离线对照

```powershell
dotnet run --project tools/TechniqueTests/TechniqueTests.csproj -c Release
python tools/compare_reference_technique.py --reference-root 'D:\Projects\Adofai-Macro' --kps 7 --main-hand right
```

Python 工具只执行参考的纯手法模块，匹配其 UI 的左手四键翻转；不启动 UI、不发键、不写参考项目，输出源码/时间线 SHA256 及手别统计。仅使用标准库。

样例为 `tools/potatoab/main.timedata`，12,908 个 onset，SHA256：`19151c991a91f6a1924407fb18fbaeab2f5da73ebfb84e2f9f9c1fb37f2c6762`。2026-11-21 对照：

| 规划器/条件 | 连续同手段 | 最长同手段 |
| --- | ---: | ---: |
| 当前半拍，4 键/手，BPM 454、上限 550、左起 | 4,989 | 48 |
| 当前 KPS，4 键/手，KPS 6.5、左起、组间交替 | 4,920 | 4 |
| 当前节奏窗，4 键/手，BPM 454、上限 550、左起、组间交替 | 3,933 | 4 |
| 当前 KPS，16 键/手，KPS 7、右起、组间交替 | 4,788 | 10 |
| Python 参考，16 键/手，KPS 7、右主手 | 4,365 | 53 |

最后两行的手别比较采用相同数量且互不重叠的键位；当前 C# 侧用独立 VK 标签，未声称物理键序/时长等价。参考源码 SHA256：`a09486f3def3a23972ab68da70484627b8d605dc94d97e3bf77e0efd79c53e60`。C# 前 24 手为 `RRLRLRLRLRRLRLRLLRLRLRRL`；Python 为 `RRLRLRLRLRRRRRLRRRRRLRRR`，已直接证明两者用手行为不同。

## 验证及仍需实机判断的内容

回归检查击键完整、松键配对、长按末尾、重复键/同刻触发、长谱、配置变化、指纹相关性、策略对象的历史隔离，以及各策略声明的合成节奏分组行为。`verify-technique-profiles.ps1` 读取实际构建 DLL，校验配置 XML 往返、Clone 和旧配置缺省值；CI 在构建后运行。

可独立生成验证包（`PackageOutputPath` 覆盖默认打包位置）：

```powershell
dotnet build ADOFAIMacro/ADOFAIMacro-Dev.csproj -c Release -p:OutputPath=bin/TechniqueVerification/ -p:IntermediateOutputPath=obj/TechniqueVerification/ -p:PackageOutputPath=D:/Projects/ADOFAIMacro/out/TechniqueVerification/ --nologo -v minimal
./tools/verify-technique-profiles.ps1 -AssemblyPath ./ADOFAIMacro/bin/TechniqueVerification/ADOFAIMacro.dll
dotnet run --project tools/TechniqueSmoke/TechniqueSmoke.csproj -c Release -- ./out/TechniqueVerification/ADOFAIMacro.dll
```

`TechniqueSmoke` 没有引用或链接规划器源码，而是通过反射调用指定的实际 Mod DLL。它在独立的 .NET 10 进程中执行三种分组及两种有效连段用手规则，共 5 种组合 × 2 个场景：12,908 次击键的仓库谱面，以及跳过装饰/自动砖块、双手同键、重复键槽、长按尾部与同刻终点击键场景；另外检查空谱的转换。检查击键与松键配对、onset 和 source floor、缓存首次保存和原子替换、读取后事件及统计逐项一致、在线规划与读缓存后生成的工作线程事件逐项一致、配置/地板数不匹配及旧版本/截断文件拒绝。2026-11-21 本地结果：11/11 通过；每种组合的长谱均为 25,816 个事件。

轨迹读写可显式传入工具自己创建的临时目录，避免在离线宿主调用 Unity 的 `persistentDataPath` 原生接口。游戏调用继续使用默认目录。冒烟直接调用游戏所用的 `TechniquePlayback` 转换代码，没有启动播放器、初始化 `MacroEngine`、加载 Mod 入口或发键；因此它不覆盖 Unity 缓存目录获取、UMM/Harmony 生命周期、输入桥或实际回放时钟。CI 在打包后、上传前运行同一冒烟命令。

离线检查不能验证 UMM 控件的实际布局、游戏版本输入桥、角度驱动实机判定或“这段是否更顺手”。实际体验应比较同一谱面片段的三种规则，关注切段是否打断节奏、单手轮指是否合适、长按是否占用预期键位；不要仅比较通关结果或组数。

尚未建模解剖学指位、跨键手位移动、疲劳、音乐重音、参考的完整多层指序，也未证明任何候选规则普遍优于默认。新增策略没有替换默认值。

## English summary

The planner now separates chart facts, grouping windows, run hand choice, configured key slots, physical-key resolution and playback. Default half-beat behavior remains; KPS and Tempo Window are explicit experimental heuristics. Hand choice can alternate or prefer the starting hand after a grouping-window-sized gap. These are not anatomical recovery or learned player models.

The supplied Python reference has demonstrably different hand history, tiered key ordering and rolling releases. The comparison script executes only that pure module and reports source/fixture hashes. Synthetic and real-chart regressions establish event correctness and declared rule behavior; they do not establish easier gameplay. Runtime UI, input integration and player judgment remain outside offline acceptance.

The compiled-mod smoke tool uses reflection against the supplied Release DLL, with no linked planner sources. Eleven local cases cover five effective grouping/hand-policy combinations over a real-chart fixture and a shared-key hold-tail fixture, plus an empty chart. It calls the same TechniquePlayback adapters as the game and compares live-plan worker events with worker events reconstructed after disk caching, as well as atomic save/replacement, exact event/stat roundtrip and incompatible/corrupt trace rejection. An explicit temporary cache directory bypasses Unity's native path getter. This is a .NET 10 offline host check, not Unity playback or a check of the game's input bridge. CI runs it against the packaged DLL before upload.
