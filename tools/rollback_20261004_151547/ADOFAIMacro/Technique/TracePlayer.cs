using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Input;
using ADOFAIMacro.Timing;
using UnityFileDialog;
using Settings = ADOFAIMacro.Settings.Settings;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 轨迹播放器 —— 把「离线求解出来的最优解」当成一份**录制好的操作**来播。
    ///
    /// 进关时把事件表准备好（磁盘缓存 → 没有才求解一次 → 落盘），运行期只做
    /// 「比对 + 发键」，一次计算都不再做。
    ///
    /// 两种驱动方式，可切换（Settings.techniquePlaybackDriver）：
    ///   0 = 时间驱动（默认）—— 事件表直接顶替原来的 HitEvent 表，走工作线程
    ///       那条时间锚点外推链路。与改造前**同一条代码路径**，风险最低。
    ///   1 = 角度驱动 —— 主线程每帧比对行星在当前砖上已扫过的角度比例。
    ///       不看时钟，因此时钟域抖动（dspTime 阶梯、音频缓冲、暂停恢复）
    ///       一律免疫；代价是每帧读一次行星状态，且触发精度被帧率量化
    ///       （用 techniqueAngleLeadMs 补偿）。
    ///
    /// 为什么角度比例可以脱离时间单独成立：
    /// 行星在一块砖的旋转内是**匀角速度**的 —— scrPlanet.cs:383 每帧都从
    ///     angle = snappedLastAngle + (songposition_minusi − lastHit)/crotchet
    ///                                 × π × speed × (isCW ? 1 : −1)
    /// 线性重算（planetEase 只影响贴图位置，不影响 angle）；而 scrLevelMaker 的
    /// entryTime 正是按同样匀速算出来的（scrMisc.GetTimeBetweenAngles 的定义式
    /// 与上面这个角速度逐项对得上，回环砖、holdLength*2π、extraBeats 也都对得上）。
    /// 所以「时间比例」与「角度比例」严格线性等价，且符号相消：
    ///     frac = (angle − snappedLastAngle) / angleLength
    ///
    /// 求解器产出的事件里：按下事件 AngleFrac 恒为 1（压线时刻就在砖的末端），
    /// 松键事件落在这块砖的旋转区间内，FloorIndex 记的是它属于哪块砖的旋转 ——
    /// 这正是角度驱动所需的全部信息。
    /// </summary>
    internal static partial class MacroEngine
    {
        // ─────────────────────────────────────────────
        //  状态
        // ─────────────────────────────────────────────
        /// <summary>当前轨迹；null = 没有轨迹（沿用在线贪心事件表）</summary>
        private static TraceFile.Trace? _trace;

        /// <summary>角度驱动是否正在接管发键。为 false 时事件表已顶替 _hitEvents 走时间驱动</summary>
        private static bool _angleDriverActive;

        /// <summary>角度驱动：正在推进的事件下标</summary>
        private static int _anglePlayIndex = -1;

        /// <summary>角度驱动：本次进关是否已完成过落后快进</summary>
        private static bool _angleResynced;

        // ── 判定时刻直打（v3：取代插值回填）──────────────────────
        // 原理（全部反编译证据）：
        //  · 异步输入模式下游戏判定完全不看角度窗——scrPlanet.cs:961-975 GetHitMargin
        //    局部函数：事件带 hitTick 时 rawMsDiff = num7*1000，
        //    num7 = (hitTick − offsetTick)/1e7 − GetSongPositionAt(下一砖 entryTime)。
        //  · GetSongPositionAt (AsyncInputUtils.cs:16-19) =
        //    entryTime/pitch + dspTimeSongPosZero + calibration_i。
        //  · 逆运算得每个事件的【理想判定时刻】（.NET ticks 域）：
        //      hitTick* = offsetTick + (TriggerTime/pitch + songZero) * 1e7
        //    其中 songZero = dspTimeSongPosZero + calibration_i（关卡内不变）。
        //  · TriggerTime 与游戏 entryTime 同域（求解器输入 t = fl.entryTime，
        //    NativeTechnique.cs BuildTechniqueFloorInputs）。
        // 事件时间戳 = 理想判定时刻 − 提前量，恒 ≤ 当下（到点才发）；
        // 游戏按事件时间戳回拨判定 → num7 = −lead，精确 Perfect，视觉无前跳。
        // GCS.d_oldConductor 为 public static readonly false（GCS.cs:179）→ 新 conductor
        // 公式恒适用，无需分支。
        private static double _songZeroCache;      // dspTimeSongPosZero + calibration_i
        private static double _pitchCache = 1.0;   // song.pitch（全曲恒定）
        private static bool _songZeroValid;        // 进关/换砖回退后重读
        private static long _lastSweepFloor = long.MinValue;  // 砖序回退检测（重试/检查点）
        private static int _timingWarnSpam;        // 异常限频日志计数
        private static int _frameSendBudget = 256; // 单帧发键预算（超密段防主线程洪峰，见 AngleDriverTick）
        private static long _frameBudgetHits;      // 预算截断累计次数（诊断：>0 说明谱面存在超密段）
        // ── 超速直判（2026-10-04 The Sun 卡死修复；判据 2026-10-04 晚修正）────
        // 相邻事件真实间隔 < 阈值（判定需求 >250/s）时，带时间戳的直喂判定链
        // （keyQueue→sortedKeyQueue→ProcessKeyInputs→margin 计算+误差表 DOTween）
        // 单次成本 2-4ms，游戏消化不过来 → 歌曲时间慢放、永远追不上 → 帧时间爆炸=「卡死」。
        // 解法：超密段改走【直接命中计数通道】—— _workerNeedsHit++ → Update 收割后
        // hitPlayer.Hit(null,false)（游戏 Die 同款调用，角度窗判定、零 keyQueue 开销）。
        // 判据必须量【真实间隔】而非 floor.speed：砖时长由角度跨度决定，speed 高 ≠ 间隔短
        // （Qyoh speed=2048 段多数砖宽间隔，按 speed 一刀切会把好谱面切进无按键通道）。
        internal const double DirectHitGapThresholdSec = 0.004;  // 相邻事件间隔 <4ms 走直判（>250 判定/s）
        private static long _directHitCount;               // 直判累计（诊断）
        private static int _directHitFrameBudget = 512;    // 直判每帧上限（Hit(null) 也有成本）

        /// <summary>轨迹状态文本（UI 显示用）。本局接线后优先显示，否则回落到加载期预热信息</summary>
        private static string _playbackInfo = string.Empty;

        /// <summary>加载期预热状态文本（还没进 PlayerControl 时显示）</summary>
        private static string _prewarmInfo = string.Empty;

        /// <summary>
        /// 加载期预热好的轨迹。它由 PrewarmTrace 产出、由 PrepareTrace 消费，
        /// **不随 ResetTraceState 清除** —— 否则 PrepareTrace 一开头就会把
        /// 自己正要用的那份轨迹扔掉。失效由 (关卡路径 + 手法指纹 + 地板数) 三元组判定。
        /// </summary>
        private static TraceFile.Trace? _prewarmedTrace;

        private static string _prewarmedLevelPath = string.Empty;
        private static int _prewarmedFingerprint;
        private static int _prewarmedFloorCount;

        /// <summary>
        /// UI 可读：轨迹状态文本。已接线显示本局信息，否则显示加载期预热信息 ——
        /// 预热失败时两者都空，回落到求解器层面的原因（SolveAndCacheTrace 写的）。
        /// </summary>
        internal static string PlaybackInfo =>
            _trace != null ? _playbackInfo
            : _prewarmInfo.Length > 0 ? _prewarmInfo
            : _playbackInfo;

        /// <summary>UI 可读：轨迹缓存是否命中（本次进关未重算）</summary>
        internal static bool PlaybackFromCache { get; private set; }

        /// <summary>清空全部轨迹缓存，返回删除的文件数</summary>
        internal static int ClearTraceCache()
        {
            // 内存里的预热结果也要一并丢掉，否则「清除缓存」后下一关仍然
            // 直接复用内存副本，用户看不到重新求解。
            _prewarmedTrace = null;
            _prewarmedLevelPath = string.Empty;
            _prewarmInfo = "轨迹缓存已清除，下次进关将重新求解";
            return TraceFile.ClearAll();
        }

        /// <summary>
        /// 运行中切换驱动方式。轨迹事件表与角度字段无关，两种驱动共用同一份缓存，
        /// 所以这里只需要重新接线，不必重算、不必重落盘。
        /// </summary>
        internal static void TechniquePlaybackDriverChanged()
        {
            var trace = _trace;
            if (trace == null) return;

            if (Main.Settings.techniquePlaybackDriver == 1)
            {
                _hitEvents = [];
                _hitEventCount = 0;
                _angleDriverActive = true;
                _anglePlayIndex = -1;
                _angleResynced = false;
                _songZeroValid = false;
                _lastSweepFloor = long.MinValue;
            }
            else
            {
                var converted = ConvertTraceToHitEvents(trace.Events);
                _hitEvents = converted;
                _hitEventCount = converted.Length;
                _angleDriverActive = false;
                _anglePlayIndex = -1;
                _angleResynced = false;
                _songZeroValid = false;
                _lastSweepFloor = long.MinValue;
            }
            _staticAnchorVersion++;
        }

        // ═══════════════════════════════════════════════════════════════
        //  准备（Initialize 里调用一次）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 加载期预热：关卡**加载完但还没开始玩**时就把轨迹求解出来并落盘。
        ///
        /// 为什么这里能算：整首谱的地板数据与每块砖的 entryTime 在
        /// scnGame 加载时就全算好了（scnGame.LoadAndPlayLevel 尾部依次跑
        /// levelMaker.MakeLevel() → ApplyEventsToFloors() →
        /// scrLevelMaker.CalculateFloorEntryTimes()，后者是纯函数，只用
        /// listFloors 自身字段 + conductor 的 pitch/bpm/countdownTicks）。
        /// 求解器要的就是这些 —— 与玩家按不按键、有没有开始计时毫无关系。
        ///
        /// 之前把求解挂在 KeyWorker.Initialize 上，而 Initialize 由
        /// MacroEngine.Update 调用、Update 只挂在 scrController.PlayerControl_Update
        /// 前缀上 → 必须先"进关并开始"才有机会算，这就是用户说的"还要让宏玩一次"。
        ///
        /// 这里刻意**不碰** initialized / _hitEvents / 工作线程锚点：
        /// 本方法只产出 _prewarmedTrace，真正接线仍由 PrepareTrace 负责，
        /// 两者的职责边界不清会让「宏到底启用了没有」变得无法判断。
        /// </summary>
        internal static void PrewarmTrace()
        {
            // 先清上一次的预热结果（换关）
            _prewarmedTrace = null;
            _prewarmedLevelPath = string.Empty;
            _prewarmInfo = string.Empty;

            try
            {
                if (!TryPrepareSolveEnv(out string why)) { _prewarmInfo = $"未预热：{why}"; return; }

                var trace = SolveAndCacheTrace();
                if (trace == null) return;

                _prewarmedTrace = trace;
                _prewarmedLevelPath = SafeLevelPath() ?? string.Empty;
                _prewarmedFingerprint = _techniqueFingerprint;
                _prewarmedFloorCount = floorCount;

                string keyText = trace.CacheKey.Length > 0 ? trace.CacheKey : "未落盘";
                _prewarmInfo = trace.FromCache
                    ? $"加载期就绪（缓存 {trace.CacheKey} · 读 {trace.SolveMs}ms）· {trace.Stats}"
                    : trace.SolveMs > 0
                        ? $"加载期已求解并缓存 {trace.CacheKey}（{trace.SolveMs}ms）· {trace.Stats}"
                        : $"加载期已求解（{keyText}）· {trace.Stats}";
                Log($"[Macro-Player] 预热 {_prewarmInfo} · 地板 {floorCount}");
                GameNotifier.Info($"轨迹已就绪 · {trace.Stats}");
            }
            catch (Exception ex)
            {
                // 预热是纯增强：失败就安静放弃，进关后 PrepareTrace 会照常走
                // 「读缓存 → 求解 → 在线贪心」这条路，绝不因此影响能不能玩。
                _prewarmedTrace = null;
                _prewarmInfo = "";
                Log($"[Macro-Player] 预热失败（不影响游玩）: {ex.Message}");
            }
        }

        /// <summary>
        /// 准备求解所需的环境：关卡地板快照 + conductor + 手法配置。
        ///
        /// 加载期预热与手动「离线计算」按钮**共用**这一段 —— 只有这样，两条路
        /// 喂给求解器的输入才逐字节相同，否则「按按钮算出来的」和「自动算出来的」
        /// 可能是两份不同的解，缓存键也会对不上。
        /// </summary>
        private static bool TryPrepareSolveEnv(out string whyNot)
        {
            if (!Main.Settings.Macro) { whyNot = "宏未启用"; return false; }
            if (!Main.Settings.EnableTechniqueSimulation) { whyNot = "手法模拟未启用"; return false; }
            if (!Main.Settings.SimulateKeyPress) { whyNot = "按键模拟未启用"; return false; }
            if (!TechniqueSimulator.SolverAvailable) { whyNot = "原生求解器不可用（TechniqueSimulator.dll 缺导出）"; return false; }

            var lm = scrLevelMaker.instance;
            if (lm?.listFloors == null || lm.listFloors.Count == 0)
            { whyNot = "尚未加载关卡（没有可计算的地板）"; return false; }

            var cond = scrConductor.instance;
            if (cond == null) { whyNot = "节拍器未就绪"; return false; }

            // 手法指纹依赖关卡配置（分段/按键/手性），必须先确保它已加载，
            // 否则会用全局配置算出指纹、再用关卡配置求解，缓存键与内容不符。
            try { LevelTechniqueManager.CheckAndLoadLevelConfig(); }
            catch { /* 拿不到就用全局配置，不影响 */ }

            levelMaker = lm;
            conductor = cond;
            cachedFloors = [.. lm.listFloors];
            floorCount = cachedFloors.Length;
            _initializedLevelPath = SafeLevelPath();

            whyNot = string.Empty;
            return true;
        }

        /// <summary>
        /// 读缓存 / 求解 / 落盘。加载期预热与 Initialize 走的是同一段逻辑，
        /// 两者喂给求解器的输入逐字节相同（同一份 BuildTechniqueFloorInputs）。
        /// </summary>
        private static TraceFile.Trace? SolveAndCacheTrace()
        {
            if (!TechniqueSimulator.SolverAvailable)
            {
                _playbackInfo = "离线求解器不可用（沿用在线贪心）";
                return null;
            }

            var floors = cachedFloors;
            if (floors == null || floors.Length == 0) return null;

            // 指纹必须在**查缓存之前**算好：缓存键的一半就是它。
            // 加载期预热是本关第一次进来，上一次关卡的指纹还留在字段里 ——
            // 直接查缓存会用错键（永远 miss，白算一遍还可能把新解存成旧键）。
            // ParseTechniqueConfig 是纯解析，重复调用无副作用。
            ParseTechniqueConfig();

            string levelPath = SafeLevelPath() ?? "?";
            int floorCountNow = floors.Length;
            int techFp = _techniqueFingerprint;

            // ① 磁盘缓存
            var sw = Stopwatch.StartNew();
            var cached = TraceFile.TryLoad(levelPath, floorCountNow, techFp);
            sw.Stop();
            if (cached != null && cached.Events.Length > 0)
            {
                cached.SolveMs = (int)sw.ElapsedMilliseconds;
                _playbackInfo =
                    $"轨迹缓存命中 {cached.CacheKey}（{cached.Events.Length} 事件 / 读 {sw.ElapsedMilliseconds}ms）· {cached.Stats}";
                return cached;
            }

            // ② 求解（唯一一次重计算；此处尚未进入 NoGC 区域，安全）
            BuildTechniqueFloorInputs();
            var evTime = _evTimeRecycle;
            if (evTime.Count == 0)
            {
                _playbackInfo = "本谱无判定事件（沿用在线贪心）";
                return null;
            }

            PushTechniqueConfigToNative();

            sw.Restart();
            bool ok = TechniqueSimulator.SolveTrace(
                [.. evTime], [.. _evPressRecycle], [.. _evFloorRecycle], [.. _evSpeedRecycle],
                evTime.Count,
                conductor!.bpm, ADOBase.controller.playerOne.planetarySystem.speed,
                out _, out var traceEvents, out var stats);
            sw.Stop();
            int solveMs = (int)sw.ElapsedMilliseconds;

            if (!ok || traceEvents == null || traceEvents.Length == 0)
            {
                _playbackInfo = "离线求解未产出事件（沿用在线贪心）";
                Log($"[Macro-Player] {_playbackInfo}");
                return null;
            }

            // ③ 落盘（失败无所谓，本次照常播）
            string savedKey = TraceFile.TrySave(levelPath, floorCountNow, techFp, traceEvents, stats ?? TraceFile.TraceStats.Unknown, out string key)
                ? key : string.Empty;

            var trace = new TraceFile.Trace
            {
                Events = traceEvents,
                Stats = stats ?? TraceFile.TraceStats.Unknown,
                FromCache = false,
                SolveMs = solveMs,
                CacheKey = savedKey,
            };
            _playbackInfo = savedKey.Length > 0
                ? $"本次求解 {solveMs}ms → 已缓存 {savedKey}（{traceEvents.Length} 事件）· {trace.Stats}"
                : $"本次求解 {solveMs}ms（缓存目录不可用）· {trace.Stats}";
            return trace;
        }

        /// <summary>
        /// 「离线计算」按钮：用户自己决定何时对当前关卡求解。
        ///
        /// 与加载期预热共用 <see cref="SolveAndCacheTrace"/>，所以两边算出来的
        /// 永远是同一份解（同一份输入 + 同一个缓存键）。
        ///
        /// 结果存进 _prewarmedTrace —— PrepareTrace 会认它，下一次 Initialize
        /// （重开关卡 / 第一次进 PlayerControl）直接零计算接管。
        /// **刻意不在游玩中途重新接线**：正在播的轨迹被中途换掉会让按下/松键
        /// 序列错位（多按或卡键），这类风险不值得为「立刻生效」去冒。
        /// </summary>
        internal static void SolveNow()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!TryPrepareSolveEnv(out string why))
                {
                    _prewarmInfo = $"无法离线计算：{why}";
                    _playbackInfo = _prewarmInfo;
                    Log($"[Macro-Player] {_prewarmInfo}");
                    GameNotifier.Warning($"无法离线计算：{why}");
                    return;
                }

                var trace = SolveAndCacheTrace();
                if (trace == null)
                {
                    _prewarmInfo = _playbackInfo.Length > 0 ? _playbackInfo : "无法离线计算：求解未产出结果";
                    Log($"[Macro-Player] 手动离线计算未成功 · {_prewarmInfo}");
                    GameNotifier.Warning("离线计算未成功，详见设置面板状态行");
                    return;
                }

                // 存成「预热结果」，PrepareTrace 的三元组校验（关卡路径 + 指纹 + 地板数）
                // 通过后就会直接用它。
                _prewarmedTrace = trace;
                _prewarmedLevelPath = SafeLevelPath() ?? string.Empty;
                _prewarmedFingerprint = _techniqueFingerprint;
                _prewarmedFloorCount = floorCount;

                sw.Stop();
                string where = _trace != null
                    ? "（本局已在播放旧轨迹，重进关卡后生效）"
                    : "（下次进关即生效）";
                string how = trace.FromCache ? "已命中缓存" : "离线计算完成";
                _prewarmInfo = $"{how} {sw.ElapsedMilliseconds}ms · {trace.CacheKey} · {trace.Stats}{where}";
                Log($"[Macro-Player] 手动离线计算 · {_prewarmInfo} · 地板 {floorCount}");
                GameNotifier.Info($"{how}（{sw.ElapsedMilliseconds}ms）· {trace.Stats}{where}");
            }
            catch (Exception ex)
            {
                // 纯增强：任何异常都不许影响宏的正常发键。
                _prewarmInfo = $"离线计算失败：{ex.Message}";
                Log($"[Macro-Player] {_prewarmInfo}");
                GameNotifier.Warning($"离线计算失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 导出当前关卡的轨迹到用户选的 .adofaitrace 文件。
        /// 只在「已求解过（缓存文件存在）」时可用；否则提示先离线计算。
        /// </summary>
        internal static void ExportTrace()
        {
            try
            {
                if (!TryPrepareSolveEnv(out string why))
                {
                    _prewarmInfo = $"无法导出：{why}";
                    _playbackInfo = _prewarmInfo;
                    GameNotifier.Warning($"无法导出：{why}");
                    return;
                }

                ParseTechniqueConfig();
                string levelPath = SafeLevelPath() ?? "?";
                string? src = TraceFile.LocateCacheFile(levelPath, floorCount, _techniqueFingerprint);
                if (src == null)
                {
                    _prewarmInfo = "没有可导出的轨迹（先点「离线计算」）";
                    _playbackInfo = _prewarmInfo;
                    Log($"[Macro-Player] {_prewarmInfo}");
                    GameNotifier.Warning("没有可导出的轨迹，请先点「离线计算」");
                    return;
                }

                string suggested = BuildSuggestedExportName(levelPath);
                string dest = FileBrowser.SaveFile(
                    Main.Mod != null ? Main.Mod.Path : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    suggested,
                    "ADOFAI 宏轨迹文件",
                    new[] { TraceFile.FileExtension },
                    "导出轨迹");

                if (string.IsNullOrEmpty(dest))
                {
                    _prewarmInfo = "导出已取消";
                    return;    // 用户取消，不刷状态行
                }

                // Unity 的 SaveFile 不带扩展名时自动补第一个过滤扩展
                if (!dest.EndsWith("." + TraceFile.FileExtension, StringComparison.OrdinalIgnoreCase))
                    dest += "." + TraceFile.FileExtension;

                bool ok = TraceFile.ExportTo(src, dest);
                _prewarmInfo = ok
                    ? $"已导出 → {Path.GetFileName(dest)}"
                    : "导出失败（见 Player.log）";
                _playbackInfo = _prewarmInfo;
                Log($"[Macro-Player] {_prewarmInfo}（来源 {src}）");
                if (ok) GameNotifier.Info($"已导出 → {Path.GetFileName(dest)}");
                else GameNotifier.Warning("导出失败（详见日志）");
            }
            catch (Exception ex)
            {
                _prewarmInfo = $"导出失败：{ex.Message}";
                Log($"[Macro-Player] {_prewarmInfo}");
                GameNotifier.Warning($"导出失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 从用户选的 .adofaitrace 文件导入轨迹，作为当前关卡的官方缓存。
        /// 导入成功后下一次 Initialize（重进关卡）直接零计算命中。
        /// </summary>
        internal static void ImportTrace()
        {
            try
            {
                if (!TryPrepareSolveEnv(out string why))
                {
                    _prewarmInfo = $"无法导入：{why}";
                    _playbackInfo = _prewarmInfo;
                    GameNotifier.Warning($"无法导入：{why}");
                    return;
                }

                ParseTechniqueConfig();
                string levelPath = SafeLevelPath() ?? "?";

                string file = FileBrowser.PickFile(
                    Main.Mod != null ? Main.Mod.Path : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "ADOFAI 宏轨迹文件",
                    new[] { TraceFile.FileExtension },
                    "导入轨迹");

                if (string.IsNullOrEmpty(file))
                {
                    _prewarmInfo = "导入已取消";
                    return;
                }

                var (ok, msg) = TraceFile.ImportFrom(file, levelPath, floorCount, _techniqueFingerprint);
                if (ok)
                {
                    // 丢掉内存预热副本，让下一次 Initialize 从缓存重读导入的轨迹。
                    _prewarmedTrace = null;
                    _prewarmedLevelPath = string.Empty;
                    _prewarmInfo = $"导入成功（{msg}），重进关卡生效";
                    Log($"[Macro-Player] 导入轨迹成功 ← {file}");
                    GameNotifier.Info($"导入成功（{msg}），重进关卡生效");
                }
                else
                {
                    _prewarmInfo = $"导入失败：{msg}";
                    Log($"[Macro-Player] {_prewarmInfo}");
                    GameNotifier.Warning($"导入失败：{msg}");
                }
                _playbackInfo = _prewarmInfo;
            }
            catch (Exception ex)
            {
                _prewarmInfo = $"导入失败：{ex.Message}";
                Log($"[Macro-Player] {_prewarmInfo}");
                GameNotifier.Warning($"导入失败：{ex.Message}");
            }
        }

        /// <summary>导出文件的建议名：关卡文件名 + 指纹前 8 位</summary>
        private static string BuildSuggestedExportName(string levelPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(levelPath);
            if (string.IsNullOrEmpty(baseName)) baseName = "level";
            return $"trace_{baseName}";
        }

        /// <summary>
        /// 准备轨迹并把它接进发键链路。
        ///
        /// · 时间驱动：把 TraceEvent 翻成 HitEvent 顶替 _hitEvents（角度信息留作观测）。
        /// · 角度驱动：_hitEvents 清空（工作线程无事可做），改由 AngleDriverTick 发键。
        /// · 任何一步失败都安静回落到在线贪心事件表 —— 播放器是纯增强，
        ///   绝不能成为「能不能玩」的开关。
        /// </summary>
        private static void PrepareTrace()
        {
            ResetTraceState();

            bool angleDriver = Main.Settings.techniquePlaybackDriver == 1;
            if (!Main.Settings.EnableTechniqueSimulation) return;

            var floors = cachedFloors;
            if (floors == null || floors.Length == 0) return;

            // 加载期已经算好的（关卡路径 + 手法指纹 + 地板数全一致）→ 直接复用，
            // 进关这一刻零计算。指纹是 ParseTechniqueConfig 算出来的，而
            // BuildTechniqueFloorInputs 也会再调一次解析，两者一致。
            TraceFile.Trace? trace = null;
            string levelPath = SafeLevelPath() ?? "?";
            int techFp = _techniqueFingerprint;
            if (_prewarmedTrace != null &&
                string.Equals(_prewarmedLevelPath, levelPath, StringComparison.Ordinal) &&
                _prewarmedFingerprint == techFp &&
                _prewarmedFloorCount == floors.Length)
            {
                trace = _prewarmedTrace;
                _playbackInfo = trace.FromCache
                    ? $"轨迹缓存命中 {trace.CacheKey}（加载期已就绪）· {trace.Stats}"
                    : $"加载期已求解 {trace.SolveMs}ms · {trace.Stats}";
                PlaybackFromCache = trace.FromCache;
            }
            else
            {
                trace = SolveAndCacheTrace();
                if (trace == null) return;      // _playbackInfo 已由 SolveAndCacheTrace 填好
                PlaybackFromCache = trace.FromCache;
            }

            if (trace == null || trace.Events.Length == 0) return;

            _trace = trace;

            // 接线
            if (angleDriver)
            {
                _hitEvents = [];
                _hitEventCount = 0;
                _angleDriverActive = true;
                _anglePlayIndex = -1;
                _angleResynced = false;
            }
            else
            {
                var converted = ConvertTraceToHitEvents(trace.Events);
                if (converted.Length == 0)
                {
                    _playbackInfo += " · 轨迹无可播事件（沿用在线贪心）";
                    return;
                }
                _hitEvents = converted;
                _hitEventCount = converted.Length;
            }

            _staticAnchorVersion++;      // 让工作线程下一帧换到新表
            Log($"[Macro-Player] {_playbackInfo} · 驱动={(angleDriver ? "角度" : "时间")} · 事件 {_hitEventCount}/{trace.Events.Length}");
        }

        /// <summary>
        /// 清理播放器**本局**状态（ResetState / 换关时调用）。
        /// 刻意不动 _prewarmedTrace —— 加载期预热要活到 Initialize 来取用它为止。
        /// </summary>
        internal static void ResetTraceState()
        {
            _trace = null;
            _angleDriverActive = false;
            _anglePlayIndex = -1;
            _angleResynced = false;
            _songZeroValid = false;
            _lastSweepFloor = long.MinValue;
            _playbackInfo = string.Empty;
            PlaybackFromCache = false;
        }

        /// <summary>TraceEvent → HitEvent（时间驱动：丢掉角度信息，语义与原生事件表一致）</summary>
        private static HitEvent[] ConvertTraceToHitEvents(TraceFile.TraceEvent[] events)
        {
            var result = new HitEvent[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                ref readonly var e = ref events[i];
                result[i] = new HitEvent(
                    e.TriggerTime,
                    e.KeyCode,
                    e.ReleaseOnly,
                    e.IsHoldRelated,
                    e.ReleaseKeyCode);
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        //  角度驱动（主线程每帧）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 轨迹下标（listFloors 0 基）↔ 游戏 currfloor.seqID（1 基）的换算表。
        /// seqID 的起点在不同关卡生成路径下并不统一（scrLevelMaker 有多条
        /// Generate*，有的用 j+1、有的用自增 num3，还存在 seqID==0 的特例地板），
        /// 所以不在代码里写死偏移，而是拿实际 cachedFloors 建一次映射。
        /// </summary>
        private static Dictionary<int, int>? _seqToIndex;

        /// <summary>重建 seqID → listFloors 下标 的映射（换关时调用一次）</summary>
        internal static void BuildFloorIndexMap()
        {
            var floors = cachedFloors;
            if (floors == null || floors.Length == 0) { _seqToIndex = null; return; }

            var map = new Dictionary<int, int>(floors.Length);
            for (int i = 0; i < floors.Length; i++)
            {
                var f = floors[i];
                if (f == null) continue;
                if (!map.ContainsKey(f.seqID)) map[f.seqID] = i;
            }
            _seqToIndex = map;

            // 一行诊断：seqID 从几起、有没有 0、末砖 seqID 是多少。
            // 这三个数直接决定角度驱动对不对，日志里看一眼就够验证。
            Main.Log($"[Macro-Player] seqID 映射：共 {floors.Length} 块，首块 seqID={floors[0]?.seqID} " +
                     $"末块 seqID={floors[floors.Length - 1]?.seqID}，含 seqID=0: {map.ContainsKey(0)}");
        }

        /// <summary>把 currfloor 的 seqID 翻译成 listFloors 0 基下标</summary>
        private static int ResolveSweepFloor(scrFloor curr)
        {
            var map = _seqToIndex;
            if (map != null && map.TryGetValue(curr.seqID, out int idx)) return idx;
            // 映射缺失（表没建/该 seqID 异常）：退回 seqID-1（游戏
            // scrPlanet.cs:414 自己就是 listFloors[seqID - 1]），并夹进合法范围。
            int guess = curr.seqID - 1;
            var floors = cachedFloors;
            if (floors == null || floors.Length == 0) return 0;
            if (guess < 0) return 0;
            if (guess >= floors.Length) return floors.Length - 1;
            return guess;
        }

        /// <summary>
        /// 角度驱动推进器（v3：判定时刻直打）。由 Harmony 前缀与游戏状态机同帧调用
        /// （见 Patches 的 PlayerControl_Update 前缀）。不再读 planet.angle ——
        /// 异步输入模式下判定基准就是事件时间戳（scrPlanet.cs:961-975），
        /// 我们直接按游戏判定公式逆运算出每个事件的理想判定时刻 hitTick*，
        /// 到点发键、时间戳打 hitTick* → 游戏 num7 ≈ 0 → 精确判定，零帧量化。
        /// </summary>
        private static void AngleDriverTick(scrController controller)
        {
            var trace = _trace;
            if (trace == null) return;
            var events = trace.Events;
            int count = events.Length;

            // ── 运行守卫 ──────────────────────────────────────
            // 暂停/死亡/非游玩状态一律不发键。暂停时 scrController.enabled=false
            // （scrController.cs:2583），本方法压根不被调；这里再兜一层状态检查
            // 防死亡动画等边界状态发键。
            if (count == 0 || controller == null || controller.paused
                || controller.state != global::States.PlayerControl)
                return;

            scrPlanet? planet = controller.chosenPlanet;
            if (planet == null) return;
            scrPlayer? player = planet.player;
            if (player == null) return;
            scrFloor? curr = planet.currfloor;
            if (curr == null) return;
            var cond = conductor;
            if (cond == null || cond.song == null) return;

            // ── 轨迹下标 ↔ 游戏 seqID 的换算 ──────────────────
            // 轨迹里的 FloorIndex 是 listFloors 的 0 基下标（BuildTechniqueFloorInputs
            // 填的 evFloor[i]），而 curr.seqID 是 1 基的 seqID。seqID 起点在
            // 不同关卡生成路径下不统一，运行时建映射表 O(1) 查。
            int sweepFloor = ResolveSweepFloor(curr);

            // ── 砖序回退检测 ──────────────────────────────────
            // 重试 / 检查点回跳 / 读档会让 sweepFloor 变小。此时游标与判定
            // 时刻换算必须整体重置，否则后续事件永远落后于当前砖。
            if (sweepFloor < _lastSweepFloor)
            {
                Main.Log($"[Macro-Player] 砖序回退 {(_lastSweepFloor == long.MinValue ? "?" : _lastSweepFloor.ToString())}→{sweepFloor}，重同步游标");
                _anglePlayIndex = -1;
                _angleResynced = false;
                _songZeroValid = false;
            }
            _lastSweepFloor = sweepFloor;

            // ── 入场对齐 ─────────────────────────────────────────
            // 轨迹是整首的，进关/读档可能直接落在中段。若还停在 -1，就从第一条
            // 不早于当前砖的事件起步 —— 否则会在第一帧把整首补按一遍。
            if (_anglePlayIndex < 0)
            {
                _anglePlayIndex = 0;
                _angleResynced = false;
            }
            if (!_angleResynced)
            {
                int i = _anglePlayIndex;
                while (i < count && events[i].FloorIndex < sweepFloor) i++;
                if (i != _anglePlayIndex)
                    Main.Log($"[Macro-Player] 落后当前砖(seqID={curr.seqID}/idx={sweepFloor})，事件下标 {_anglePlayIndex} → {i}");
                _anglePlayIndex = i;
                _angleResynced = true;
            }
            if (_anglePlayIndex >= count) return;

            // ── 判定时刻换算缓存 ──────────────────────────────
            // songZero = dspTimeSongPosZero + calibration_i。
            // 【2026-10-04 稳定性修复】它并非关卡内不变：checkpoint 重试/跳砖会走
            // scrConductor.ScrubMusicToTime / ScrubMusicToTile（:938/:956）重设 dspTimeSong
            // —— 每次重试后 songZero 都会变。旧实现只在砖序回退时失效缓存，检测结果：
            // Qyoh 从 checkpoint（seqID=386）进关后判定漂移 +10ms 且整局持续（用户：
            // 「角度驱动完全不稳定」）。修法：每帧比对缓存值与游戏现值，不一致（差 >1ms
            // 等效 1e4 ticks/秒）即重缓存 + 游标重同步（时间基准变了，判定时刻映射全变）。
            {
                double songZeroNow = cond.dspTimeSongPosZero + scrConductor.calibration_i;
                if (!_songZeroValid || Math.Abs(songZeroNow - _songZeroCache) > 0.001)
                {
                    if (_songZeroValid)
                    {
                        Main.Log($"[Macro-Player] 歌曲时间基准漂移 {_songZeroCache:F3}→{songZeroNow:F3}（重试/跳砖），重同步判定时刻换算");
                        _anglePlayIndex = -1;
                        _angleResynced = false;
                    }
                    _pitchCache = cond.song.pitch;
                    if (_pitchCache < 0.05) _pitchCache = 1.0;   // 防零/异常 pitch
                    _songZeroCache = songZeroNow;
                    _songZeroValid = true;
                }
            }

            // offsetTick 每帧都在被游戏平滑更新（scrConductor.Update →
            // UpdateOffsetTime(100)），本帧直接读现值——暂停恢复后它会重新对齐，
            // 我们跟着读就永远不会陈旧。（注意：游戏类与我们的 Input.AsyncInputManager
            // 同名，必须 global:: 限定。）
            long offsetTick = global::AsyncInputManager.offsetTick;
            long nowTicks = PreciseNow.LocalTicks();
            long minPast = nowTicks - 2_500_000;             // 时间戳回退下限 250ms
            // 注意：不允许事件时间戳落在未来。scrController.UpdateInput 每帧把
            // keyQueue 全量出队（sortedKeyQueue 只排序不过滤，:2906-2945），
            // 未来时间戳的事件会被游戏立即消费并把行星角度推进到未来
            // （AdjustAngle→AsyncRefreshAngles），视觉上星球瞬移/抖动。
            // 到点才发 + 时间戳封顶当下，角度回拨永远是 ≤0，视觉恒平滑。

            bool simulateKey = Main.Settings.SimulateKeyPress;
            bool enableTechnique = Main.Settings.EnableTechniqueSimulation;
            double leadMs = Main.Settings.techniqueAngleLeadMs;
            long leadTicks = (long)(leadMs * 10_000.0);      // ms → ticks（1ms = 1e4 ticks）

            // ── 单帧发键预算 ──────────────────────────────────
            // 超密段（本谱实测：1024 倍速下 0.25ms/砖，一帧 ~64 事件；变速点文件显示
            // 极端处 0.01ms 间隔）里，到点即发的 while 单帧可能连发数百上千事件，
            // 每次 SendKey→Invoke+SendMirror(SendInput 系统调用) 挤占主线程，游戏
            // UpdateInput 同帧消费巨量同 tick 事件 → 帧时间爆炸 → AppHang（2026-10-04
            // 13:24 实测：判定推进到 floor≈7000 的超密段时游戏整体挂起）。
            // 预算 256/帧：正常谱每帧 ≤64 事件不受影响；超密段按帧分摊（每帧最多
            // 追账 256 个，几帧内追平），宁可判定延后几帧也不冻结游戏。
            int frameSends = 0;

            while (_anglePlayIndex < count)
            {
                if (frameSends >= _frameSendBudget)
                {
                    _frameBudgetHits++;
                    if (_frameBudgetHits == 1 || (_frameBudgetHits % 600 == 0))
                        Main.Log($"[Macro-Player] 单帧发键预算触顶（{_frameSendBudget}/帧），本帧剩余事件延后分摊；累计 {_frameBudgetHits} 帧 —— 谱面存在超密段");
                    break;
                }
                ref readonly var ev = ref events[_anglePlayIndex];

                if (ev.FloorIndex > sweepFloor)
                    break;                                                             // 这块砖还没轮到，等

                // 这块砖的旋转已经过去了（ev.FloorIndex < sweepFloor）。
                // 不管事件原本落在旋转途中的哪个角度，此刻都必须发出去：
                //   · 按下事件的 AngleFrac 恒为 1，压线就在砖的末端，而游戏在同一瞬间
                //     就把 snappedLastAngle 重置到下一块砖的起点（scrPlanet.cs:1124）。
                //   · 松键事件若拖到这里，说明上一帧没发出去；宁可晚发，也不能不发
                //     （漏发 = 键一直按着不放 = 卡键）。
                // 补发无法还原理想判定时刻（砖的旋转上下文已换），打当下时刻——
                // 游戏按"当下帧"判定，与真实玩家慢了一拍的手感等价，不会灾难偏移。
                if (ev.FloorIndex < sweepFloor)
                {
                    FireTraceEvent(ev, simulateKey, enableTechnique);
                    frameSends++;
                    _anglePlayIndex++;
                    continue;
                }

                // ── 判定时刻直打 ──────────────────────────────
                // hitTick* = offsetTick + (TriggerTime/pitch + songZero) * 1e7
                // （scrPlanet.cs:967 num7 公式的逆运算；TriggerTime 与游戏
                //   entryTime 同为谱面秒域。）
                // 到点即发：hitTick* - leadTicks <= nowTicks + 100ms。
                double songSec = ev.TriggerTime / _pitchCache + _songZeroCache;
                double hitStar = (double)offsetTick + songSec * 1e7;
                if (double.IsNaN(hitStar) || double.IsInfinity(hitStar)
                    || hitStar < long.MinValue || hitStar > long.MaxValue)
                {
                    if (_timingWarnSpam++ < 8)
                        Main.Log($"[Macro-Player] 事件判定时刻异常 (TriggerTime={ev.TriggerTime:F3})，按当下发");
                    FireTraceEvent(ev, simulateKey, enableTechnique);
                    frameSends++;
                    _anglePlayIndex++;
                    continue;
                }
                long hitStarTicks = (long)hitStar;

                // 到点判据（含用户提前量）：还没到点 → 后面的更没到，整批等下一帧。
                // （必须"到点才发"——若提前入队，游戏会立刻把判定与视觉推进到
                //   未来时刻，星球前跳；晚一帧发不影响精度，判定由时间戳决定。）
                if (hitStarTicks - leadTicks > nowTicks)
                    break;

                // 事件时间戳 = 理想判定时刻 − 用户提前量，钳进 [now-250ms, now]：
                //   · 直喂路径判定完全由时间戳决定（num7 = (eventTicks−offsetTick)/1e7
                //     − 谱面理想位置），所以提前量必须写进时间戳——这正是 v1/v2
                //     leadFrac 前移阈值的 v3 对应物（补偿音频延迟等系统性偏晚）。
                //   · 到点判据已保证 hitStar − lead ≤ now → 上钳位只兜浮点舍入；
                //   · 已过点较久（补发/卡顿后追账）→ 钳到下限，至少不会灾难回拨。
                long eventTicks = hitStarTicks - leadTicks;
                if (eventTicks < minPast) eventTicks = minPast;
                if (eventTicks > nowTicks) eventTicks = nowTicks;

                // hold 链：松旧键与按新键共享同一 eventTicks 时，.NET PriorityQueue
                // 同优先级出队顺序未定义 → 顺序颠倒会卡键。给松键 -1 tick 强制
                // "松旧"严格先于"按新"。
                // 【超速直判】判据=本事件到下一事件的真实间隔 <4ms（判定需求 >250/s，
                // 超出游戏直喂判定链消化能力 ~250/s，见 The Sun 案例）→ 改走直接命中
                // 计数通道。量真实间隔而不是 floor.speed：砖时长由角度跨度决定，speed
                // 高不等于间隔短（Qyoh 的 speed=2048 段多数砖仍是宽间隔），按 speed 一刀切
                // 会把手法完好的谱面切进无按键通道（2026-10-04 用户爆发点）。
                // hold 相关事件仍走真实按键（hold 状态机需要成对的按下/松键语义）。
                bool ultraDense = false;
                if (_anglePlayIndex + 1 < count)
                {
                    double gapSec = events[_anglePlayIndex + 1].TriggerTime - ev.TriggerTime;
                    ultraDense = gapSec < 0.004;
                }
                if (ultraDense && !ev.IsHoldRelated)
                {
                    FireTraceEvent(ev, simulateKey: false, enableTechnique, eventTicks);
                    frameSends++; _directHitCount++;
                    _anglePlayIndex++;
                    continue;
                }
                FireTraceEvent(ev, simulateKey, enableTechnique, eventTicks);
                frameSends++;
                _anglePlayIndex++;
            }
        }

        /// <summary>
        /// 把一条轨迹事件变成实际发键。语义与 KeyWorker.WorkerLoop 的四类分支
        /// 逐行对齐（那边多一层锚点重读，这里是主线程同步执行，不需要）。
        /// eventTicks 非零 = 理想判定时刻，直喂路径的事件携带它
        /// （游戏按事件时间戳判定）；注入路径无法携带时间戳，自动退化为当下时刻。
        /// 松键一律比按下早 1 tick 入队：.NET PriorityQueue 同优先级出队顺序
        /// 未定义，显式错开才能保证「先松旧键、后按新键」的确定性时序。
        /// </summary>
        private static void FireTraceEvent(in TraceFile.TraceEvent ev, bool simulateKey, bool enableTechnique, long eventTicks = 0)
        {
            long upTicks = eventTicks > 0 ? eventTicks - 1 : 0;   // 松键时间戳（确定性排序）

            if (!simulateKey)
            {
                // 不模拟按键 = 只需要命中计数。累加后由 Update 的现有通道灌进游戏。
                Interlocked.Increment(ref _workerNeedsHit);
                return;
            }

            if (ev.ReleaseOnly)
            {
                if (enableTechnique)
                {
                    byte keyToRelease = ev.IsHoldRelated
                        ? (ev.ReleaseKeyCode != 0 ? ev.ReleaseKeyCode : _holdKey)
                        : ev.ReleaseKeyCode;
                    SendKey(keyToRelease, false, upTicks);
                    if (ev.IsHoldRelated) { _holdKey = 0; _isHoldDown = false; }
                }
                else
                {
                    if (ev.IsHoldRelated) WorkerReleaseHoldKey();
                    else WorkerReleaseKey(ev.ReleaseKeyCode);
                }
                return;
            }

            if (ev.IsHoldRelated)
            {
                if (enableTechnique)
                {
                    if (_isHoldDown) { SendKey(_holdKey, false, upTicks); _holdKey = 0; _isHoldDown = false; }
                    SendKey(ev.KeyCode, true, eventTicks);
                    _holdKey = ev.KeyCode; _isHoldDown = true;
                }
                else WorkerHoldKey(ev.KeyCode);
                return;
            }

            if (enableTechnique) SendKey(ev.KeyCode, true, eventTicks);
            else WorkerPressKey(ev.KeyCode);
        }
    }
}