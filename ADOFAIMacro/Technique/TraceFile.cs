using System;
using System.IO;
using System.Text;
using ADOFAIMacro.Core;

#nullable enable
namespace ADOFAIMacro.Technique
{
    /// <summary>Portable trace with a chart/profile identity and validated key transitions.</summary>
    internal static class TraceFile
    {
        private const uint Magic = 0x314D4146;   // 'FAM1'
        // Bump when planner output changes; older events must never masquerade as current.
        private const int FormatVersion = 4;
        private const int HeaderSize = 80; // magic, version, count, floor count, content hash, stats
        private const int MaxEvents = 1_000_000;
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
            public int SolveMs { get; set; }
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

        private static string CachePath(string key, string? cacheDirectory = null)
        {
            // An explicit directory lets offline hosts use the same serializer without
            // calling Unity's native persistentDataPath getter. The caller owns that directory.
            string dir = cacheDirectory ?? CacheDirectory();
            return string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, $"trace_{key}.adotr");
        }

        // ─────────────────────────────────────────────
        //  读
        // ─────────────────────────────────────────────
        /// <summary>
        /// 读缓存。命中返回 Trace（FromCache=true），任何异常/校验不过都返回 null
        /// —— 缓存永远只是加速手段，绝不能成为正确性的前提。
        /// </summary>
        internal static Trace? TryLoad(string levelPath, int floorCount, int techniqueHash,
                                       string? cacheDirectory = null)
        {
            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            string path = CachePath(key, cacheDirectory);
            return path.Length == 0 ? null : ReadTrace(path, key, floorCount, techniqueHash, out _);
        }

        // ─────────────────────────────────────────────
        //  写
        // ─────────────────────────────────────────────
        /// <summary>落盘（先写 .tmp 再原子改名，避免半截文件被当成有效缓存）</summary>
        internal static bool TrySave(string levelPath, int floorCount, int techniqueHash,
                                    TraceEvent[] events, TraceStats stats, out string cacheKey,
                                    string? cacheDirectory = null)
        {
            cacheKey = string.Empty;
            if (events.Length == 0 || events.Length > MaxEvents) return false;

            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            string path = CachePath(key, cacheDirectory);
            if (string.IsNullOrEmpty(path)) return false;

            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new BinaryWriter(fs, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(FormatVersion);
                    w.Write(events.Length);
                    w.Write(floorCount);
                    w.Write(techniqueHash);
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

                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
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

        internal static (bool ok, string message) ImportFrom(
            string importFile, string levelPath, int floorCount, int techniqueHash)
        {
            string key = FingerprintOf(levelPath, floorCount, techniqueHash);
            var trace = ReadTrace(importFile, key, floorCount, techniqueHash, out string error);
            if (trace == null) return (false, error);
            // Write the validated snapshot, not the source file again. Importing the cache
            // itself or changing the source after validation cannot erase the valid cache.
            return TrySave(levelPath, floorCount, techniqueHash, trace.Events, trace.Stats, out _)
                ? (true, $"{trace.Events.Length} 事件 · {trace.Stats}")
                : (false, "无法写入轨迹缓存");
        }

        private static Trace? ReadTrace(string path, string key, int floorCount, int techniqueHash, out string error)
        {
            error = "轨迹文件无效或已损坏";
            try
            {
                if (!File.Exists(path)) { error = "文件不存在"; return null; }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(fs, Encoding.UTF8);
                if (fs.Length < HeaderSize || r.ReadUInt32() != Magic) return null;
                if (r.ReadInt32() != FormatVersion) { error = "轨迹版本不兼容，请重新生成"; return null; }
                int count = r.ReadInt32();
                if (count <= 0 || count > MaxEvents || fs.Length != HeaderSize + (long)count * EventStride) return null;
                if (r.ReadInt32() != floorCount || r.ReadInt32() != techniqueHash)
                {
                    error = "轨迹与当前谱面或手法配置不匹配";
                    return null;
                }
                var stats = new TraceStats(
                    r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                    r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadInt32());

                var events = new TraceEvent[count];
                var down = new byte[256];
                int held = 0, lastFloor = -1;
                double lastTime = double.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    double time = r.ReadDouble();
                    float fraction = r.ReadSingle();
                    byte keyDown = r.ReadByte(), flags = r.ReadByte(), keyUp = r.ReadByte();
                    if (r.ReadByte() != 0) return null;
                    int floor = r.ReadInt32();
                    if (!TechniqueTimeline.IsFinite(time) || time < lastTime || floor < 0 || floor < lastFloor || floor >= floorCount ||
                        float.IsNaN(fraction) || fraction < 0 || fraction > 1 || (flags & ~3) != 0) return null;
                    byte expected = (byte)((flags & 2) != 0 ? 2 : 1);
                    if ((flags & 1) != 0)
                    {
                        if (keyDown != 0 || keyUp == 0 || down[keyUp] != expected) return null;
                        down[keyUp] = 0;
                        held--;
                    }
                    else
                    {
                        if (keyUp != 0 || keyDown == 0 || down[keyDown] != 0) return null;
                        down[keyDown] = expected;
                        held++;
                    }
                    events[i] = new TraceEvent(time, fraction, keyDown, flags, keyUp, floor);
                    lastTime = time;
                    lastFloor = floor;
                }
                if (held != 0) return null;
                return new Trace { Events = events, Stats = stats, FromCache = true, CacheKey = key };
            }
            catch (Exception ex)
            {
                MacroEngine.Log($"[Macro-Tech] 轨迹文件读取失败: {ex.Message}");
                return null;
            }
        }
    }
}
