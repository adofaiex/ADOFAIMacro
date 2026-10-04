using System;
using System.IO;
using System.Text;
using ADOFAIMacro.Core;

#nullable enable
namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 离线求解轨迹的落盘格式。
    ///
    /// 设计要点：
    ///  · 缓存文件**不绑定运行期几何** —— 求解器是纯时间轴的（只认 entryTime），
    ///    存 TriggerTime + AngleFrac∈[0,1]；回放时用当次运行的
    ///    angleLength / isCW 权威值把 AngleFrac 还原成角度阈值。
    ///  · 缓存键 = 关卡指纹（路径 + 地板数）+ 手法配置指纹，
    ///    谱面或手法改动后自动失效，不会有"拿旧轨迹打新谱"的静默错误。
    ///  · 格式自带魔数 / 版本号 / 长度校验，读坏了直接当"没有缓存"处理。
    /// </summary>
    internal static class TraceFile
    {
        private const uint Magic = 0x314D4146;   // 'FAM1'
        // v2：2026-10-04 手法模拟整体换成原版 手法拟真\main.cpp 的 potato() 忠实移植
        //     （含变速点 restart 机制），分区结果与旧实现不同，旧 .adotr 缓存必须失效。
        //     指纹 = levelPath + floorCount + techHash + FormatVersion，
        //     版本号一改，旧的缓存文件在读取时会被判为不匹配而整体重算。
        private const int FormatVersion = 2;
        //  文件头 = 4(魔数)+4(版本)+4(条数)+16(4 个 int)+16(方差+总代价)+24(3 个代价)+4(节点) = 72
        private const int HeaderSize = 72;
        /// <summary>单事件落盘字节数：8(double)+4(float)+1+1+1+1(pad)+4(int)</summary>
        private const int EventStride = 20;

        /// <summary>导出/导入用的可携带轨迹文件扩展名（不含点）</summary>
        internal const string FileExtension = "adofaitrace";

        // ─────────────────────────────────────────────
        //  数据类型
        // ─────────────────────────────────────────────
        internal readonly struct TraceEvent
        {
            /// <summary>曲目时间（秒），与 BuildHitEvents 的 TriggerTime 同一坐标系</summary>
            public readonly double TriggerTime;
            /// <summary>该按键在所属砖块旋转里已扫过的比例 ∈[0,1]</summary>
            public readonly float AngleFrac;
            public readonly byte KeyCode;
            public readonly byte Flags;
            public readonly byte ReleaseKeyCode;
            /// <summary>所属地板在 floors[] 里的下标（角度驱动用）</summary>
            public readonly int FloorIndex;

            public TraceEvent(double triggerTime, float angleFrac, byte keyCode,
                              byte flags, byte releaseKeyCode, int floorIndex)
            {
                TriggerTime = triggerTime;
                AngleFrac = angleFrac;
                KeyCode = keyCode;
                Flags = flags;
                ReleaseKeyCode = releaseKeyCode;
                FloorIndex = floorIndex;
            }

            public bool ReleaseOnly => (Flags & 0x01) != 0;
            public bool IsHoldRelated => (Flags & 0x02) != 0;
        }

        internal readonly struct TraceStats
        {
            public readonly int FragmentCount;     // frag：单键碎片片数
            public readonly int LongestSameHand;   // longest：最长同手连击
            public readonly int DroppedNotes;      // 丢音数（硬约束，应恒为 0）
            public readonly int PieceCount;        // 片数
            public readonly double PieceLenVariance;
            public readonly double TotalCost;
            public readonly double FragCost;
            public readonly double RunCost;
            public readonly double RoughCost;
            public readonly int SolverNodes;       // 展开状态数（性能观测）

            public TraceStats(int fragmentCount, int longestSameHand, int droppedNotes,
                              int pieceCount, double pieceLenVariance, double totalCost,
                              double fragCost, double runCost, double roughCost, int solverNodes)
            {
                FragmentCount = fragmentCount;
                LongestSameHand = longestSameHand;
                DroppedNotes = droppedNotes;
                PieceCount = pieceCount;
                PieceLenVariance = pieceLenVariance;
                TotalCost = totalCost;
                FragCost = fragCost;
                RunCost = runCost;
                RoughCost = roughCost;
                SolverNodes = solverNodes;
            }

            public static readonly TraceStats Unknown =
                new TraceStats(-1, -1, -1, -1, -1, -1, -1, -1, -1, -1);

            public override string ToString() =>
                DroppedNotes < 0
                    ? "未求解"
                    : $"frag {FragmentCount} / longest {LongestSameHand} / 片 {PieceCount}"
                      + $" / 丢音 {DroppedNotes} / 节点 {SolverNodes}";
        }

        /// <summary>一条可播放的轨迹</summary>
        internal sealed class Trace
        {
            public TraceEvent[] Events = Array.Empty<TraceEvent>();
            public TraceStats Stats = TraceStats.Unknown;
            /// <summary>是否来自磁盘缓存（false = 本次刚求解出来的）</summary>
            public bool FromCache;
            /// <summary>求解耗时（毫秒）；仅 !FromCache 时有意义</summary>
            public int SolveMs;
            /// <summary>缓存键（十六进制 8 位）；空串 = 没落盘（目录不可用）</summary>
            public string CacheKey = string.Empty;
        }

        // ─────────────────────────────────────────────
        //  缓存键
        // ─────────────────────────────────────────────
        private static string FingerprintOf(string levelPath, int floorCount, int techniqueHash)
        {
            var sb = new StringBuilder(128);
            sb.Append(levelPath ?? "?").Append('|')
              .Append(floorCount).Append('|')
              .Append(techniqueHash.ToString("x8")).Append('|')
              .Append(FormatVersion);
            return Fnv1a(sb.ToString());
        }

        private static string Fnv1a(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return h.ToString("x8");
            }
        }

        /// <summary>缓存目录：persistentDataPath/ADOFAIMacro/trace/；不可用时返回空串</summary>
        internal static string CacheDirectory()
        {
            try
            {
                string root = UnityEngine.Application.persistentDataPath;
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                string dir = Path.Combine(root, "ADOFAIMacro", "trace");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 缓存目录不可用，本次只走内存轨迹: {ex.Message}");
                return string.Empty;
            }
        }

        private static string CachePath(string key)
        {
            string dir = CacheDirectory();
            return string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, $"trace_{key}.adotr");
        }

        // ─────────────────────────────────────────────
        //  读
        // ─────────────────────────────────────────────
        /// <summary>
        /// 读缓存。命中返回 Trace（FromCache=true），任何异常/校验不过都返回 null
        /// —— 缓存永远只是加速手段，绝不能成为正确性的前提。
        /// </summary>
        internal static Trace? TryLoad(string levelPath, int floorCount, int techniqueHash)
        {
            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            string path = CachePath(key);
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(fs, Encoding.UTF8);

                if (r.ReadUInt32() != Magic) return null;
                if (r.ReadInt32() != FormatVersion) return null;

                int count = r.ReadInt32();
                if (count < 0 || count > 1_000_000) return null;

                var stats = new TraceStats(
                    r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                    r.ReadDouble(), r.ReadDouble(),
                    r.ReadDouble(), r.ReadDouble(), r.ReadDouble(),
                    r.ReadInt32());

                long expected = (long)HeaderSize + (long)count * EventStride;
                if (fs.Length != expected)
                {
                    MacroEngine.Log($"[Macro-Tech] 轨迹缓存长度不符（{fs.Length} != {expected}），忽略");
                    return null;
                }

                var events = new TraceEvent[count];
                for (int i = 0; i < count; i++)
                {
                    double t = r.ReadDouble();
                    float frac = r.ReadSingle();
                    byte vk = r.ReadByte();
                    byte flags = r.ReadByte();
                    byte rel = r.ReadByte();
                    _ = r.ReadByte();                     // 15: pad
                    int floor = r.ReadInt32();
                    events[i] = new TraceEvent(t, frac, vk, flags, rel, floor);
                }

                return new Trace { Events = events, Stats = stats, FromCache = true, CacheKey = key };
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 轨迹缓存读取失败，按未命中处理: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────
        //  写
        // ─────────────────────────────────────────────
        /// <summary>落盘（先写 .tmp 再原子改名，避免半截文件被当成有效缓存）</summary>
        internal static bool TrySave(string levelPath, int floorCount, int techniqueHash,
                                    TraceEvent[] events, TraceStats stats, out string cacheKey)
        {
            cacheKey = string.Empty;
            if (events == null || events.Length == 0) return false;

            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            string path = CachePath(key);
            if (string.IsNullOrEmpty(path)) return false;

            string tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new BinaryWriter(fs, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(FormatVersion);
                    w.Write(events.Length);
                    w.Write(stats.FragmentCount);
                    w.Write(stats.LongestSameHand);
                    w.Write(stats.DroppedNotes);
                    w.Write(stats.PieceCount);
                    w.Write(stats.PieceLenVariance);
                    w.Write(stats.TotalCost);
                    w.Write(stats.FragCost);
                    w.Write(stats.RunCost);
                    w.Write(stats.RoughCost);
                    w.Write(stats.SolverNodes);

                    for (int i = 0; i < events.Length; i++)
                    {
                        var e = events[i];
                        w.Write(e.TriggerTime);
                        w.Write(e.AngleFrac);
                        w.Write(e.KeyCode);
                        w.Write(e.Flags);
                        w.Write(e.ReleaseKeyCode);
                        w.Write((byte)0);            // pad
                        w.Write(e.FloorIndex);
                    }
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                cacheKey = key;
                return true;
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 轨迹缓存写入失败（不影响本次游玩）: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败无所谓 */ }
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  维护
        // ─────────────────────────────────────────────
        /// <summary>清空全部轨迹缓存（UI 的"清除缓存"用）</summary>
        internal static int ClearAll()
        {
            try
            {
                string dir = CacheDirectory();
                if (string.IsNullOrEmpty(dir)) return 0;
                int n = 0;
                foreach (string f in Directory.GetFiles(dir, "trace_*.adotr*"))
                {
                    try { File.Delete(f); n++; } catch { /* 单个失败不阻塞 */ }
                }
                return n;
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 清缓存失败: {ex.Message}");
                return 0;
            }
        }

        // ─────────────────────────────────────────────
        //  导出 / 导入（Spectre 式的可携带轨迹文件）
        // ─────────────────────────────────────────────

        /// <summary>
        /// 找到当前关卡+手法指纹对应的缓存文件在磁盘上的位置（不读内容）。
        /// 返回 null = 没有缓存文件（还没求解过）。
        /// </summary>
        internal static string? LocateCacheFile(string levelPath, int floorCount, int techniqueHash)
        {
            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            string path = CachePath(key);
            return (path.Length > 0 && File.Exists(path)) ? path : null;
        }

        /// <summary>
        /// 导出：把缓存文件复制成用户可携带的 .adofaitrace 文件。
        /// 二进制原样复制（不重新编码），所以导入端就是普通的缓存读取路径，
        /// Magic/版本/长度校验全部自然生效 —— 导入坏文件的行为与"缓存损坏"完全一致。
        /// </summary>
        internal static bool ExportTo(string sourceCachePath, string destFile)
        {
            try
            {
                string? dir = Path.GetDirectoryName(destFile);
                if (dir?.Length > 0) Directory.CreateDirectory(dir);
                File.Copy(sourceCachePath, destFile, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 轨迹导出失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导入：把 .adofaitrace 文件读进内存校验，通过后作为该关卡+手法指纹的
        /// 官方缓存落盘。返回 (成功, 状态统计文本)。
        ///
        /// 校验分两层：
        ///  ① 文件本身有效（Magic/版本/长度/条数合法）→ 用 TryLoadCore；
        ///  ② 文件的指纹与当前关卡匹配 → levelPath/floorCount/techniqueHash
        ///     重新算 key 与调用者传入的一致（否则是"拿别的谱的轨迹打这张谱"）。
        /// 导入成功后写盘，之后的 TryLoad 自然命中 —— 无需任何特殊接线。
        /// </summary>
        internal static (bool ok, string message) ImportFrom(
            string importFile, string levelPath, int floorCount, int techniqueHash)
        {
            string expectedKey = FingerprintOf(levelPath, floorCount, techniqueHash);
            try
            {
                if (!File.Exists(importFile))
                    return (false, "文件不存在");

                var trace = TryLoadFile(importFile, expectedKey);
                if (trace == null)
                    return (false, "文件无效（魔数/版本/长度校验未通过）");

                // 指纹匹配已由调用方保证（expectedKey 就是当前关卡的键）。
                // 落盘：写到缓存目录的 trace_<key>.adotr，TryLoad 以后自然命中。
                string path = CachePath(expectedKey);
                if (string.IsNullOrEmpty(path))
                    return (false, "缓存目录不可用");

                if (File.Exists(path)) File.Delete(path);
                File.Copy(importFile, path, overwrite: true);

                return (true, $"{trace.Events.Length} 事件 · {trace.Stats}");
            }
            catch (Exception ex)
            {
                return (false, $"导入失败: {ex.Message}");
            }
        }

        /// <summary>读指定路径的 .adotr 文件（TryLoad 的路径参数化版本）</summary>
        private static Trace? TryLoadFile(string path, string expectedKey)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(fs, Encoding.UTF8);

                if (r.ReadUInt32() != Magic) return null;
                if (r.ReadInt32() != FormatVersion) return null;

                int count = r.ReadInt32();
                if (count < 0 || count > 1_000_000) return null;

                var stats = new TraceStats(
                    r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                    r.ReadDouble(), r.ReadDouble(),
                    r.ReadDouble(), r.ReadDouble(), r.ReadDouble(),
                    r.ReadInt32());

                long expected = (long)HeaderSize + (long)count * EventStride;
                if (fs.Length != expected)
                {
                    MacroEngine.Log($"[Macro-Tech] 导入文件长度不符（{fs.Length} != {expected}），拒绝");
                    return null;
                }

                var events = new TraceEvent[count];
                for (int i = 0; i < count; i++)
                {
                    double t = r.ReadDouble();
                    float frac = r.ReadSingle();
                    byte vk = r.ReadByte();
                    byte flags = r.ReadByte();
                    byte rel = r.ReadByte();
                    _ = r.ReadByte();
                    int floor = r.ReadInt32();
                    events[i] = new TraceEvent(t, frac, vk, flags, rel, floor);
                }

                return new Trace { Events = events, Stats = stats, FromCache = true, CacheKey = expectedKey };
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 导入文件读取失败: {ex.Message}");
                return null;
            }
        }
    }
}