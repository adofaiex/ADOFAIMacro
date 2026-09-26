using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAIMacro;
using ADOFAIMacro.Core;
using ADOFAIMacro.Technique;

#nullable enable

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 事件表构建：把整张谱的地板转成一条按键事件流。
    ///
    /// 三条路径产出同一份事件表 ——
    ///   原生 TechniqueSimulator.dll（Release 首选）
    ///   C# 分片实现（DEBUG / DLL 不可用时回退，见 PieceBuilder.cs）
    ///   纯轮键（不启用手法模拟时）
    ///
    /// 终点砖、auto 砖、中旋的取舍都照抄游戏做法，源码引用见各处注释。
    /// </summary>
    internal static partial class MacroEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildHitEvents()
        {
            if (Main.Settings.SimulateKeyPress && Main.Settings.EnableTechniqueSimulation)
            {
                BuildTechniqueHitEvents();
                return;
            }

            var floors = cachedFloors!;
            int n = floors.Length;
            bool simulate = Main.Settings.SimulateKeyPress;

            byte[] keys = _keyCodesSnapshot;
            int keyLen = keys.Length;
            int keyIdx = 0;

            // 使用对象池，减少 GC
            _hitEventPoolUsed = 0;
            var pool = _hitEventPool;

            // 事件数上界 = 地板数 - 1。池容量不足时必须退化为动态分配：
            // 旧实现在每次写入前判 `_hitEventPoolUsed < pool.Length`，超出部分被
            // 静默丢弃 —— 事件数超过 65536 的极端长谱后半段会完全不触发。
            var overflow = (n - 1) > pool.Length ? new List<HitEvent>(n) : null;

            // 终点砖（最后一块）也要按键 —— 通关条件是
            // GCS.checkpointNum >= listFloors.Count（scrConductor.cs:411）。
            for (int i = 0; i < n; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;
                if (floor.auto) continue;

                // 找"下一块真正要判定的砖"：照抄游戏 scrPlayer.cs:411-415
                // （跳过中旋再判 holdLength）。中旋自己可以带长按
                // （scnGame.cs:1174 把 Hold 的 duration 写在这块砖上），
                // 只有"不带长按的中旋"才跳过。
                int ni = i + 1;
                while (ni < n - 1)
                {
                    var cf = floors[ni];
                    if (cf == null) { ni++; continue; }
                    if (cf.midSpin) { if (cf.holdLength > -1) break; ni++; continue; }
                    if (cf.auto) { ni++; continue; }
                    break;
                }

                bool isLastFloor = (i == n - 1);

                // 后面全是 auto/中旋、且当前不是终点砖：长按仍要发松键尾事件
                if (ni >= n - 1 && !isLastFloor)
                {
                    if (simulate && floor.holdLength > -1 && !floor.midSpin)
                    {
                        double te = floors[n - 1]?.entryTime ?? double.MaxValue;
                        if (overflow != null) overflow.Add(new HitEvent(te, 0, releaseOnly: true));
                        else pool[_hitEventPoolUsed++] = new HitEvent(te, 0, releaseOnly: true);
                    }
                    // continue 而非 break：处理完还要继续后面的砖（含终点砖）
                    continue;
                }

                if (floor.midSpin && floor.holdLength <= -1) continue;

                // 终点砖：普通按键，无长按头/尾
                if (isLastFloor)
                {
                    if (overflow != null) overflow.Add(new HitEvent(floor.entryTime, keys[keyIdx], releaseOnly: false));
                    else pool[_hitEventPoolUsed++] = new HitEvent(floor.entryTime, keys[keyIdx], releaseOnly: false);
                    if (++keyIdx >= keyLen) keyIdx = 0;
                    continue;
                }

                double t = floors[ni]?.entryTime ?? double.MaxValue;

                if (simulate && floor.holdLength > -1 && ni < n)
                {
                    var nf = floors[ni];
                    if (nf != null && nf.holdLength == -1)
                    {
                        if (overflow != null) overflow.Add(new HitEvent(t, 0, releaseOnly: true));
                        else pool[_hitEventPoolUsed++] = new HitEvent(t, 0, releaseOnly: true);
                        continue;
                    }
                }

                byte key = keys[keyIdx];
                if (++keyIdx >= keyLen) keyIdx = 0;
                if (overflow != null) overflow.Add(new HitEvent(t, key, releaseOnly: false));
                else pool[_hitEventPoolUsed++] = new HitEvent(t, key, releaseOnly: false);
            }

            if (overflow != null)
            {
                _hitEvents = overflow.ToArray();
                _hitEventCount = _hitEvents.Length;
                Main.Log($"[Macro-Main] 事件数 {_hitEventCount} 超出事件池容量 {pool.Length}，已改用动态分配");
            }
            else if (_hitEventPoolUsed > 0)
            {
                _hitEvents = pool.AsSpan(0, _hitEventPoolUsed).ToArray();
                _hitEventCount = _hitEvents.Length;
            }
            else
            {
                _hitEvents = [];
                _hitEventCount = 0;
            }

            Log($"[Macro-Main] BuildHitEvents 完成，共 {_hitEventCount} 个事件");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SyncFloor(double currentTime)
        {
            if (_hitEvents == null || _hitEvents.Length == 0) return -1;
            int left = 0, right = _hitEvents.Length - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                double t = _hitEvents[mid].TriggerTime;
                if (t < currentTime) left = mid + 1;
                else if (t > currentTime) right = mid - 1;
                else return mid;
            }
            return left - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize() =>
            levelMaker?.listFloors.Count != floorCount ||
            !string.Equals(SafeLevelPath(), _initializedLevelPath, StringComparison.Ordinal);

        /// <summary>
        /// 安全读取当前关卡路径。
        /// ADOBase.levelPath 的实际实现是 `scnGame.instance.levelPath`（ldsfld + ldfld），
        /// 当 scnGame 实例不存在时（编辑器试玩、非 scnGame 场景）会对 null 取字段并抛
        /// NullReferenceException —— 作者在 LevelTechniqueManager 里正是用
        /// catch (NullReferenceException) 兜住它的。
        /// 而 Initialize/NeedReinitialize 都在 Harmony prefix 里（NeedReinitialize 每帧调用），
        /// 异常会直接打断游戏的 scrController.PlayerControl_Update，因此这里必须自己兜住。
        /// </summary>
        private static string? SafeLevelPath()
        {
            try { return ADOBase.levelPath; }
            catch { return null; }
        }
    }
}
