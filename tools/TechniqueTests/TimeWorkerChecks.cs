using ADOFAIMacro.Core;
using ADOFAIMacro.Timing;

// Game-entry placeholders intentionally retain their default values.
#pragma warning disable CS0649, CS0414

namespace ADOFAIMacro.Core
{
    internal sealed class WorkerTestSettings
    {
        internal volatile bool UseRustTrigger;
        internal volatile bool EnableTechniqueSimulation = true;
        internal bool SimulateKeyPress = true;
        internal bool MirrorVirtualKeys;
        internal bool BlockInputWhenUnfocused;
        internal bool EnableKeyFilter;
    }
}

namespace ADOFAIMacro.Technique
{
    // Link the entire production KeyWorker. Only external game/platform services
    // are replaced; its loop, key ownership, cleanup and legacy release paths are real.
    internal static partial class MacroEngine
    {
        private sealed class TimeAnchor
        {
            internal double songPosRef, rate = 1, timeOffset;
            internal long qpcSnapshot;
            internal bool simulateKeyPress = true;
            internal HitEvent[]? hitEvents;
            internal int hitEventCount;
            internal bool valid = true;
        }

        private static TimeAnchor _currentAnchor = new();
        private static readonly SemaphoreSlim _startSignal = new(0, 1);
        private static volatile bool _workerRunning;
        private static int _workerLastTriggeredFloor = -1, _resetVersion;
        private static bool _syncFloorReady, _cachedHighPrecision = true, _angleDriverActive;
        private const double AnchorStaleSec = .5;
        private const double perfFreqInv = 1e-7;
        private const long perfFrequency = 10_000_000;
        private static long _clockReads;
        private static byte _pendingKey, _holdKey;
        private static bool _isKeyDown, _isHoldDown, _cachedSkyHookMode;
        private static IntPtr _gameWindowHandle = (IntPtr)(-1);
        private static readonly byte[] scanCodeCache = new byte[256];
        [ThreadStatic] private static ADOFAIMacro.Input.SkyHookSystem.INPUT _cachedInput;
        private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYDOWN = 0, KEYEVENTF_KEYUP = 2;
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;
        private static scrFloor[]? cachedFloors;
        private static int floorCount, _staticAnchorVersion;
        private static bool initialized;
        private static string? _initializedLevelPath;
        private static byte[] _techLeftKeys = [], _techRightKeys = [];

        private static long GetRawTicks()
        {
            Interlocked.Increment(ref _clockReads);
            return PreciseNow.LocalTicks();
        }
        // Log is supplied by the existing TraceFileChecks partial.
        private static void LogVerbose(string message) { }
        private static void RecordFireError(double seconds) { }
        private static void FlushFireStats() { }
        private static IntPtr _testForegroundWindow = (IntPtr)1;
        private static IntPtr GetForegroundWindow() => Interlocked.CompareExchange(ref _testForegroundWindow, IntPtr.Zero, IntPtr.Zero);
        private static unsafe uint SendInput(uint count, IntPtr inputs, int size) => throw new InvalidOperationException("Native input is forbidden in offline tests");

        internal static void TimeWorkerCSharpReleasesHoldAfterTechniqueToggle() => HoldAfterToggle(false);
        internal static void TimeWorkerRustBranchReleasesHoldAfterTechniqueToggle() => HoldAfterToggle(true);
        internal static void TimeWorkerCSharpPreservesHoldAcrossStaleAnchor() => HoldAcrossStaleAnchor(false);
        internal static void TimeWorkerRustFallbackPreservesHoldAcrossStaleAnchor() => HoldAcrossStaleAnchor(true);

        private static void HoldAfterToggle(bool rust)
        {
            var worker = StartTimeWorker(rust);
            try
            {
                Until(() => Output.Count == 1, "time-driven hold down");
                Main.Settings.EnableTechniqueSimulation = false;
                PublishTimeAnchor(3, 31_000_000);
                Until(() => Output.Count >= 2, "hold release after technique toggle");
            }
            finally { StopTimeWorker(worker); }
            Require(Output.ToArray().SequenceEqual(new (byte, bool)[] { (70, true), (70, false) }),
                "mode changes lost the original owner or produced duplicate cleanup");
        }

        private static void HoldAcrossStaleAnchor(bool rust)
        {
            var worker = StartTimeWorker(rust);
            try
            {
                Until(() => Output.Count == 1, "time-driven hold down");
                // Freeze the anchor while the external clock advances beyond its 0.5s TTL.
                // Multiple clock reads prove the real loop has processed the stale state.
                Interlocked.Exchange(ref PreciseNow.Ticks, 11_000_000);
                ObserveTimeWorker();
                Require(Output.Count == 1, "stale anchor prematurely released a valid hold");
                PublishTimeAnchor(1, 11_000_000);
                ObserveTimeWorker();
                Require(Output.Count == 1, "fresh anchor replayed the hold start or lost ownership");
                PublishTimeAnchor(3, 31_000_000);
                Until(() => Output.Count >= 2, "scheduled hold end after clock recovery");
            }
            finally { StopTimeWorker(worker); }
            Require(Output.ToArray().SequenceEqual(new (byte, bool)[] { (70, true), (70, false) }),
                "clock recovery changed the ordered hold transitions");
        }

        internal static void TimeWorkerStopReleasesHeldKeys()
        {
            foreach (bool rust in new[] { false, true })
            {
                var worker = StartTimeWorker(rust);
                try { Until(() => Output.Count == 1, "hold before explicit stop"); }
                finally { StopTimeWorker(worker); }
                Require(Output.ToArray().SequenceEqual(new (byte, bool)[] { (70, true), (70, false) }),
                    "explicit stop failed to release the held key exactly once");
            }
        }

        internal static void FocusBlocksNewDownButAllowsOwnedUp()
        {
            Output.Clear();
            Main.Settings.BlockInputWhenUnfocused=true;
            _gameWindowHandle=(IntPtr)1;
            Interlocked.Exchange(ref _testForegroundWindow,(IntPtr)1);
            try
            {
                SendKey(70,true);
                Interlocked.Exchange(ref _testForegroundWindow,(IntPtr)2);
                SendKey(80,true);
                SendKey(70,false);
                Require(Output.ToArray().SequenceEqual(new (byte,bool)[]{(70,true),(70,false)}),
                    "focus protection must block new downs, never the release of an existing down");
            }
            finally { Main.Settings.BlockInputWhenUnfocused=false; _gameWindowHandle=(IntPtr)(-1); }
        }

        internal static void TimeWorkerUnfocusedScheduledRelease()
        {
            foreach(bool rust in new[]{false,true})
            {
                var worker=StartTimeWorker(rust,focusProtected:true);
                try
                {
                    Until(()=>Output.Count==1,"focused down before losing focus");
                    Interlocked.Exchange(ref _testForegroundWindow,(IntPtr)2);
                    PublishTimeAnchor(3,31_000_000);
                    Until(()=>Output.Count>=2,"scheduled release while unfocused");
                }
                finally { StopTimeWorker(worker); }
                Require(Output.ToArray().SequenceEqual(new (byte,bool)[]{(70,true),(70,false)}),
                    "scheduled release was dropped after focus loss");
            }
        }

        internal static void TimeWorkerUnfocusedStopReleasesLazyKeys()
        {
            foreach(bool rust in new[]{false,true})
            {
                HitEvent[] lazy=[new(0,70,false),new(.1,80,false),new(.2,160,false),
                    new(30,0,true,false,70),new(30,0,true,false,80),new(30,0,true,false,160)];
                var worker=StartTimeWorker(rust,lazy,true);
                try
                {
                    Until(()=>Output.Count==1,"first lazy down");
                    PublishTimeAnchor(.3,4_000_000);
                    Until(()=>Output.Count==3,"multiple lazy keys still owned");
                    Interlocked.Exchange(ref _testForegroundWindow,(IntPtr)2);
                }
                finally { StopTimeWorker(worker); }
                var sent=Output.ToArray();
                Require(sent.Length==6&&sent.Count(e=>e.Item2)==3&&sent.Count(e=>!e.Item2)==3&&
                    sent.Where(e=>!e.Item2).Select(e=>e.Item1).Order().SequenceEqual(new byte[]{70,80,160}),
                    "unfocused stop lost lazy key-ups, including the modifier key");
            }
        }

        private static Thread StartTimeWorker(bool rust, HitEvent[]? events=null, bool focusProtected=false)
        {
            StopAngleAsync();
            Output.Clear();
            Waiting.Reset();
            Unblock.Set();
            Main.Settings.BlockInputWhenUnfocused=focusProtected;
            _gameWindowHandle=focusProtected?(IntPtr)1:(IntPtr)(-1);
            Interlocked.Exchange(ref _testForegroundWindow,(IntPtr)1);
            Main.Settings.UseRustTrigger = rust;
            Main.Settings.EnableTechniqueSimulation = true;
            _isKeyDown = _isHoldDown = false;
            _pendingKey = _holdKey = 0;
            _workerLastTriggeredFloor = -1;
            _syncFloorReady = true;
            _resetVersion++;
            _workerRunning = true;
            PublishTimeAnchor(0, 1_000_000,
                events ?? [new HitEvent(0, 70, false, true), new HitEvent(3, 0, true, true, 70), new HitEvent(20, 80, false)]);
            while (_startSignal.Wait(0)) { }
            var worker = new Thread(WorkerLoop) { IsBackground = true };
            worker.Start();
            _startSignal.Release();
            return worker;
        }

        private static void StopTimeWorker(Thread worker)
        {
            _workerRunning = false;
            Unblock.Set();
            Require(worker.Join(2000), "time worker did not stop");
            Main.Settings.BlockInputWhenUnfocused=false;
            _gameWindowHandle=(IntPtr)(-1);
        }

        private static void PublishTimeAnchor(double songPosition, long ticks, HitEvent[]? events = null)
        {
            events ??= Volatile.Read(ref _currentAnchor).hitEvents;
            Interlocked.Exchange(ref PreciseNow.Ticks, ticks);
            Volatile.Write(ref _currentAnchor, new TimeAnchor
            {
                songPosRef = songPosition, qpcSnapshot = ticks, hitEvents = events, hitEventCount = events!.Length
            });
        }

        private static void ObserveTimeWorker()
        {
            long reads = Interlocked.Read(ref _clockReads);
            Until(() => Interlocked.Read(ref _clockReads) >= reads + 12, "worker clock observation");
        }

        // Initialize is deliberately not run: these game-entry dependencies fail closed.
        private static string? SafeLevelPath() => throw new NotSupportedException();
        private static void ResetTraceState() => throw new NotSupportedException();
        private static void ParseKeyCodes() => throw new NotSupportedException();
        private static void BuildFloorIndexMap() => throw new NotSupportedException();
        private static bool PrepareTrace(WorkerTestSource source) => throw new NotSupportedException();
        private static WorkerTestSource CaptureTechniqueSource(scrFloor[] floors, scrConductor conductor) => throw new NotSupportedException();
        private static void BuildTechniqueHitEvents(WorkerTestSource source) => throw new NotSupportedException();
        private static void BuildHitEvents() => throw new NotSupportedException();
        private static int SyncFloor(double time) => throw new NotSupportedException();
        private sealed class WorkerTestSource
        {
            internal void PublishKeyDiagnostics() => throw new NotSupportedException();
        }
    }

    internal static class DeviationProbe
    {
        internal static void OnKeySent(byte key, bool down, long ticks) { }
    }
    internal static class LevelTechniqueManager
    {
        internal static void CheckAndLoadLevelConfig() => throw new NotSupportedException();
    }

    // A decision-only Rust replacement exercises WorkerLoop's Rust dispatch branch.
    // Waits fall back to C# so its real anchor refresh remains under test.
    // No DLL is loaded; native Rust timing itself is not under test here.
    internal static class TriggerRsHost
    {
        internal const int ACTION_NONE = 0, ACTION_PRESS = 1, ACTION_RELEASE = 2, ACTION_HOLD_PRESS = 3,
            ACTION_FINISHED = 5, ACTION_WAIT_AGAIN = 6, ACTION_SLEEP = 7;
        internal struct Anchor
        {
            internal IntPtr Events;
            internal int EventCount, ResetVersion;
            internal long QpcSnapshot, QpcFreq;
            internal double SongPosRef, Rate, TimeOffset, AnchorStaleSec;
        }
        internal struct WaitAdvice { internal double SleepSec, DeltaSec; }
        internal static bool TryInitialize() => true;
        internal static bool Available => true;
        internal static void Shutdown() { }
        internal static unsafe int Step(ref Anchor anchor, int index, long now, out WaitAdvice wait)
        {
            wait = default;
            double elapsed = (double)(now - anchor.QpcSnapshot) / anchor.QpcFreq;
            if (elapsed > anchor.AnchorStaleSec) return ACTION_NONE;
            if (index >= anchor.EventCount) return ACTION_FINISHED;
            var ev = ((MacroEngine.HitEvent*)anchor.Events)[index];
            wait.DeltaSec = ev.TriggerTime + anchor.TimeOffset - anchor.SongPosRef - elapsed * anchor.Rate;
            if (wait.DeltaSec > 0) return ACTION_NONE;
            return ev.ReleaseOnly ? ACTION_RELEASE : ev.IsHoldRelated ? ACTION_HOLD_PRESS : ACTION_PRESS;
        }
    }
}

namespace ADOFAIMacro.Input
{
    internal static class VirtualAsyncInput
    {
        internal static bool Active => true;
        internal static bool Send(byte key, bool down)
        {
            ADOFAIMacro.Technique.MacroEngine.RecordTestKey(key, down);
            return true;
        }
        internal static bool SendAtTicks(byte key, bool down, long ticks) => Send(key, down);
        internal static void RegisterInjectedKey(byte key, bool down) => throw new NotSupportedException();
    }
    internal static class AsyncInputManager
    {
        internal static int DirectPushKey(byte key, bool down) => throw new NotSupportedException();
    }
    internal static class SkyHookSystem
    {
        internal struct INPUT { internal uint type; internal InputUnion u; }
        internal struct InputUnion { internal Keyboard ki; }
        internal struct Keyboard { internal ushort wVk, wScan; internal uint dwFlags; }
    }
}

// Game type placeholders needed only to compile the unused Initialize entry point.
namespace UnityEngine { }
internal sealed class scrFloor { }
internal sealed class scrLevelMaker
{
    internal static scrLevelMaker? instance;
    internal List<scrFloor>? listFloors;
}
internal sealed class scrConductor
{
    internal static scrConductor? instance;
    internal double songposition_minusi;
}
