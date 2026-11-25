using ADOFAIMacro.Technique;

internal static class TraceFileChecks
{
    internal static void PortableTraceAndValidation()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "trace-test-" + Guid.NewGuid().ToString("N"));
        UnityEngine.Application.persistentDataPath = dir;
        Directory.CreateDirectory(dir);
        try
        {
            const int floors = 4, fingerprint = 123456;
            string exported = Path.Combine(dir, "renamed.adofaitrace");
            TraceFile.TraceEvent[] events =
            [
                new(1, 1, 70, 2, 0, 0),
                new(2, 1, 0, 3, 70, 1),
                new(2, 1, 70, 0, 0, 2),
                new(2.2, 1, 0, 1, 70, 2),
            ];
            if (!TraceFile.TrySave("path/a", floors, fingerprint, events, TraceFile.TraceStats.Unknown, out _))
                throw new Exception("Could not save a valid trace");
            string cache = TraceFile.LocateCacheFile("path/a", floors, fingerprint) ?? throw new Exception("Missing cache");
            if (!TraceFile.ExportTo(cache, exported) || !TraceFile.ImportFrom(exported, "other/path/b", floors, fingerprint).ok)
                throw new Exception("Portable trace import failed after renaming and relocating");
            if (TraceFile.ImportFrom(exported, "path/a", floors, fingerprint + 1).ok ||
                TraceFile.ImportFrom(exported, "path/a", floors + 1, fingerprint).ok)
                throw new Exception("Mismatched chart/profile was accepted");
            if (!TraceFile.ImportFrom(cache, "path/a", floors, fingerprint).ok)
                throw new Exception("Importing the cache itself must be safe");
            var restored = TraceFile.TryLoad("other/path/b", floors, fingerprint);
            if (restored == null || !restored.Events.SequenceEqual(events)) throw new Exception("Playback data changed across round trip");

            byte[] original = File.ReadAllBytes(exported);
            foreach (var (offset, value) in new[] { (4, 3), (80 + 12, 0), (80 + 16, 255), (80 + 20 + 13, 0x83) })
            {
                byte[] mutated = (byte[])original.Clone();
                mutated[offset] = (byte)value;
                File.WriteAllBytes(exported, mutated);
                if (TraceFile.ImportFrom(exported, "other/path/b", floors, fingerprint).ok)
                    throw new Exception($"Invalid trace accepted (mutated byte {offset})");
            }
            if (TraceFile.TryLoad("other/path/b", floors, fingerprint) == null)
                throw new Exception("Failed import overwrote the good cache");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

// Game edge only: TraceFile's real serialization and validation are linked unchanged.
namespace UnityEngine
{
    internal static class Application { internal static string persistentDataPath = string.Empty; }
}
namespace ADOFAIMacro.Technique
{
    internal static partial class MacroEngine { internal static void Log(string message) { } }
}
