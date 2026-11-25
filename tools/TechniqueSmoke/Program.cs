using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;

// Deliberately no project reference or linked production sources: every planner,
// timeline, playback adapter and serializer call targets the supplied, built mod DLL.
internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly mod = null!;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/TechniqueSmoke -c Release -- <built ADOFAIMacro.dll>");
            return 1;
        }

        string outputRoot = Path.GetFullPath(AppContext.BaseDirectory);
        string work = Path.Combine(outputRoot, "smoke-" + Guid.NewGuid().ToString("N"));
        bool created = false;
        try
        {
            string assemblyPath = Path.GetFullPath(args[0]);
            mod = Assembly.LoadFrom(assemblyPath);
            Check(mod.GetName().Name == "ADOFAIMacro", "Expected the built mod assembly");
            Console.WriteLine($"Assembly: {mod.Location}");
            Console.WriteLine($"SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant()}");
            Check(!Directory.Exists(work), "Smoke workspace already exists");
            Directory.CreateDirectory(work);
            created = true;

            var tail = TailFixture();
            var chart = ChartFixture();
            (string Strategy, string HandPolicy)[] policies =
            [
                ("beatGrid", "alternating"),
                ("kpsAlternating", "alternating"),
                ("kpsAlternating", "recoveredPreferred"),
                ("tempoWindow", "alternating"),
                ("tempoWindow", "recoveredPreferred"),
            ];
            int passed = 0;
            foreach (var fixture in new[] { tail, chart })
            foreach (var policy in policies)
            {
                var watch = Stopwatch.StartNew();
                int events = RoundTrip(fixture, policy.Strategy, policy.HandPolicy, work);
                Console.WriteLine($"PASS {fixture.Name} / {policy.Strategy} / {policy.HandPolicy}: {events} events ({watch.ElapsedMilliseconds} ms)");
                passed++;
            }
            EmptyPlayback(tail.Profile);
            passed++;
            Console.WriteLine("PASS empty chart / trace and playback adapters");
            Console.WriteLine($"{passed}/{policies.Length * 2 + 1} compiled-mod smoke cases passed (offline; no game or key injection)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL compiled-mod smoke: {ex}");
            return 1;
        }
        finally
        {
            // Only this run's GUID directory, below this tool's own build output.
            string resolved = Path.GetFullPath(work);
            if (created && Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(outputRoot) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed record Fixture(string Name, Array Notes, int Floors, double Bpm, object Profile);

    private static Fixture TailFixture()
    {
        Type tile = TypeOf("TechniqueTile");
        Array tiles = Array.CreateInstance(typeof(Nullable<>).MakeGenericType(tile), 5);
        tiles.SetValue(New(tile, 0d, 1.25, false, false, false, 0d), 0);
        // Leave a null decoration, skip an auto tile, then hold into a terminal tap.
        tiles.SetValue(New(tile, 1d, 1.5, false, true, true, 0d), 2);
        tiles.SetValue(New(tile, 2d, 1d, true, false, false, 0d), 3);
        tiles.SetValue(New(tile, 4d, 1d, false, false, false, 0d), 4);
        var notes = (Array)Call(TypeOf("TechniqueTimeline"), "Build", tiles);
        Check(notes.Length == 3, "Tail timeline must contain hold start, hold end and terminal tap");
        Check(Field<object>(notes.GetValue(0)!, "Kind").ToString() == "HoldStart" &&
              Field<object>(notes.GetValue(1)!, "Kind").ToString() == "HoldEnd" &&
              Field<object>(notes.GetValue(2)!, "Kind").ToString() == "Tap", "Tail judgment kinds changed");
        Check(Field<int>(notes.GetValue(0)!, "Floor") == 0 &&
              Field<int>(notes.GetValue(1)!, "Floor") == 2 &&
              Field<int>(notes.GetValue(2)!, "Floor") == 4, "Tail source floors changed");
        // Alias both hands to the same physical key, including repeated configured slots.
        return new("hold-alias-tail", notes, tiles.Length, 120, Profile([65, 65], [65]));
    }

    private static Fixture ChartFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "main.timedata");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Check(hash == "19151c991a91f6a1924407fb18fbaeab2f5da73ebfb84e2f9f9c1fb37f2c6762", "Chart fixture hash changed");
        var rows = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Split(',')).ToArray();
        Check(rows.Length == 12_908, "Unexpected real-chart onset count");
        Array notes = Array.CreateInstance(TypeOf("TechniqueNote"), rows.Length);
        object tap = Enum.Parse(TypeOf("NoteKind"), "Tap");
        int maxFloor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            double time = long.Parse(rows[i][0], CultureInfo.InvariantCulture) / 1e6;
            int floor = int.Parse(rows[i][2], CultureInfo.InvariantCulture);
            notes.SetValue(New(TypeOf("TechniqueNote"), time, floor, 1d, tap, 0d), i);
            maxFloor = Math.Max(maxFloor, floor);
        }
        return new("chart-12908", notes, maxFloor + 1, 454, Profile([65, 83, 68, 70], [74, 75, 76, 186]));
    }

    private static object Profile(byte[] left, byte[] right)
    {
        object Hand(byte[] keys) => New(TypeOf("HandLayout"), keys,
            Enumerable.Range(1, keys.Length).Select(n => Enumerable.Range(0, n).ToArray()).ToArray(),
            Enumerable.Repeat(.8, keys.Length).ToArray());
        return New(TypeOf("TechniqueProfile"), 550d, Hand(left), Hand(right));
    }

    private static int RoundTrip(Fixture fixture, string strategyId, string handPolicy, string directory)
    {
        object strategy = Call(TypeOf("TechniquePlanner"), "CreateGroupingStrategy", strategyId, 6.5, handPolicy);
        Type resolverType = typeof(Func<,>).MakeGenericType(typeof(int), TypeOf("TechniqueProfile"));
        var resolver = Expression.Lambda(resolverType, Expression.Constant(fixture.Profile), Expression.Parameter(typeof(int))).Compile();
        object plan = Call(TypeOf("TechniquePlanner"), "Build", fixture.Notes, fixture.Bpm, 1d, false, resolver, strategy);
        var events = Field<Array>(plan, "Events");
        var assignments = Field<Array>(plan, "Assignments");
        var attacks = fixture.Notes.Cast<object>().Where(note => Field<object>(note, "Kind").ToString() != "HoldEnd").ToArray();
        Check(assignments.Length == attacks.Length && events.Length == attacks.Length * 2, "Attack/release count changed");

        object trace = Call(TypeOf("TechniquePlayback"), "FromPlan", plan);
        var traceEvents = Field<Array>(trace, "Events");
        Check(traceEvents.Length == events.Length && !Field<bool>(trace, "FromCache") && Field<string>(trace, "CacheKey") == "",
            "A fresh trace must preserve event count and start without a cache identity");
        var down = new HashSet<byte>();
        int attack = 0, lastFloor = -1;
        double lastTime = double.NegativeInfinity;
        foreach (var (item, i) in events.Cast<object>().Select((item, i) => (item, i)))
        {
            double time = Field<double>(item, "Time");
            int floor = Field<int>(item, "Floor");
            float fraction = Field<float>(item, "Fraction");
            byte key = Field<byte>(item, "Key");
            bool release = Field<bool>(item, "Release"), hold = Field<bool>(item, "Hold");
            Check(double.IsFinite(time) && time >= lastTime && floor >= 0 && floor >= lastFloor && floor < fixture.Floors,
                "Events must be finite, ordered and inside the chart");
            Check(float.IsFinite(fraction) && fraction >= 0 && fraction <= 1 && key != 0, "Invalid event fraction/key");
            Check(release ? down.Remove(key) : down.Add(key), "Unpaired or overlapping physical key");
            if (!release)
            {
                object expected = attacks[attack];
                Check(time == Field<double>(expected, "Time") && floor == Field<int>(expected, "Floor") &&
                      hold == (Field<object>(expected, "Kind").ToString() == "HoldStart"), "An onset or hold boundary changed");
                Check(Field<byte>(assignments.GetValue(attack)!, "Key") == key, "Assignment key differs from event key");
                attack++;
            }
            object traceEvent = traceEvents.GetValue(i)!;
            Check(Field<double>(traceEvent, "TriggerTime") == time && Field<float>(traceEvent, "AngleFrac") == fraction &&
                  Field<int>(traceEvent, "FloorIndex") == floor, "Production trace adapter changed time or floor/angle coordinates");
            Check(Field<byte>(traceEvent, "KeyCode") == (release ? 0 : key) &&
                  Field<byte>(traceEvent, "ReleaseKeyCode") == (release ? key : 0) &&
                  Field<byte>(traceEvent, "Flags") == ((release ? 1 : 0) | (hold ? 2 : 0)),
                "Production trace adapter changed a key edge or hold marker");
            lastTime = time;
            lastFloor = floor;
        }
        Check(down.Count == 0 && attack == attacks.Length, "A key remained down or an attack was lost");
        if (fixture.Name == "hold-alias-tail")
            Check(Field<double>(events.GetValue(1)!, "Time") == 4 && Field<bool>(events.GetValue(1)!, "Release"),
                "Hold must release at the tail before its same-key terminal tap");

        object stats = Field<object>(trace, "Stats");
        Check(Field<int>(stats, "FragmentCount") == Field<int>(plan, "Fragments") &&
              Field<int>(stats, "LongestSameHand") == Field<int>(plan, "LongestRun") &&
              Field<int>(stats, "PieceCount") == Field<int>(plan, "Pieces") &&
              Field<double>(stats, "PieceLenVariance") == Field<double>(plan, "Variance") &&
              Field<int>(stats, "DroppedNotes") == 0, "Production trace adapter lost planner statistics");
        int fingerprint = (int)Call(TypeOf("TechniquePlanner"), "GroupingStrategyFingerprint", strategyId, 6.5, handPolicy);
        string level = fixture.Name + "/" + strategyId + "/" + handPolicy;
        object?[] saveArgs = [level, fixture.Floors, fingerprint, traceEvents, stats, null, directory];
        Check((bool)Call(TypeOf("TraceFile"), "TrySave", saveArgs), "Compiled serializer failed to save");
        string cacheKey = (string)saveArgs[5]!;
        string path = Path.Combine(directory, $"trace_{cacheKey}.adotr");
        byte[] original = File.ReadAllBytes(path);
        // Exercise replacement of an existing cache, not only first creation.
        Check((bool)Call(TypeOf("TraceFile"), "TrySave", saveArgs), "Atomic cache replacement failed");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "Same trace serialized differently");
        object restored = Call(TypeOf("TraceFile"), "TryLoad", level, fixture.Floors, fingerprint, directory);
        Check(restored != null && Field<bool>(restored, "FromCache") && Field<string>(restored, "CacheKey") == cacheKey,
            "Compiled cache load failed");
        Check(Field<Array>(restored!, "Events").Cast<object>().SequenceEqual(traceEvents.Cast<object>()), "Playback data changed after roundtrip");
        Check(Field<object>(restored!, "Stats").Equals(stats), "Trace stats changed after roundtrip");
        var direct = (Array)Call(TypeOf("TechniquePlayback"), "PlanToHitEvents", plan);
        var cachedPlayback = (Array)Call(TypeOf("TechniquePlayback"), "TraceToHitEvents", Field<Array>(restored!, "Events"));
        Check(direct.Cast<object>().SequenceEqual(cachedPlayback.Cast<object>()),
            "Live plan and disk-cached playback produce different worker events");
        Check(direct.Length == events.Length, "Playback adapter lost events");
        foreach (var (item, i) in direct.Cast<object>().Select((item, i) => (item, i)))
        {
            object expected = events.GetValue(i)!;
            bool release = Field<bool>(expected, "Release");
            byte key = Field<byte>(expected, "Key");
            Check(Field<double>(item, "TriggerTime") == Field<double>(expected, "Time") &&
                  Field<bool>(item, "ReleaseOnly") == release && Field<bool>(item, "IsHoldRelated") == Field<bool>(expected, "Hold") &&
                  Field<byte>(item, "KeyCode") == (release ? 0 : key) &&
                  Field<byte>(item, "ReleaseKeyCode") == (release ? key : 0), "Worker payload differs from the planned key edge");
        }
        Check(Read(path, fixture.Floors + 1, fingerprint) == null && Read(path, fixture.Floors, fingerprint ^ 1) == null,
            "Mismatched floor count/profile was accepted");
        byte[] oldVersion = (byte[])original.Clone();
        oldVersion[4] = 3;
        string corrupt = Path.Combine(directory, "invalid.adotr");
        File.WriteAllBytes(corrupt, oldVersion);
        Check(Read(corrupt, fixture.Floors, fingerprint) == null, "Incompatible trace version was accepted");
        File.WriteAllBytes(corrupt, original[..^1]);
        Check(Read(corrupt, fixture.Floors, fingerprint) == null, "Truncated trace was accepted");
        Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic save left temporary files behind");
        return events.Length;
    }

    private static void EmptyPlayback(object profile)
    {
        Array notes = Array.CreateInstance(TypeOf("TechniqueNote"), 0);
        Type resolverType = typeof(Func<,>).MakeGenericType(typeof(int), TypeOf("TechniqueProfile"));
        var resolver = Expression.Lambda(resolverType, Expression.Constant(profile), Expression.Parameter(typeof(int))).Compile();
        object plan = Call(TypeOf("TechniquePlanner"), "Build", notes, 120d, 1d, false, resolver);
        object trace = Call(TypeOf("TechniquePlayback"), "FromPlan", plan);
        Check(Field<Array>(trace, "Events").Length == 0 &&
              ((Array)Call(TypeOf("TechniquePlayback"), "PlanToHitEvents", plan)).Length == 0 &&
              ((Array)Call(TypeOf("TechniquePlayback"), "TraceToHitEvents", Field<Array>(trace, "Events"))).Length == 0,
            "An empty chart produced playback events");
    }

    private static object? Read(string path, int floors, int fingerprint) =>
        Call(TypeOf("TraceFile"), "ReadTrace", path, "smoke", floors, fingerprint, null);

    private static Type TypeOf(string name) => mod.GetType("ADOFAIMacro.Technique." + name, throwOnError: true)!;
    private static object New(Type type, params object?[] args) => Activator.CreateInstance(type, Instance, null, args, CultureInfo.InvariantCulture)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;
    private static object Call(Type type, string name, params object?[] args) =>
        type.GetMethods(Static).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(null, args)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
