using System.Collections.Concurrent;
using ADOFAIMacro.Timing;

namespace ADOFAIMacro.Core
{
    internal static class Main
    {
        internal static readonly WorkerTestSettings Settings = new();
        internal static void Log(string message) { }
    }
}

namespace ADOFAIMacro.Timing
{
    internal static class PreciseNow
    {
        internal static long Ticks;
        internal static long LocalTicks() => Interlocked.Read(ref Ticks);
    }
}

namespace ADOFAIMacro.Technique
{
    // Link the production driver, replace only the clock, native timer and key sink.
    internal static partial class MacroEngine
    {
        private static readonly ConcurrentQueue<(byte Key, bool Down)> Output = new();
        private static readonly ManualResetEventSlim Waiting = new(false);
        private static readonly ManualResetEventSlim Unblock = new(false);
        private static int _workerNeedsHit;
        private static void timeBeginPeriod(int milliseconds) { }
        private static void timeEndPeriod(int milliseconds) { }
        private static void HighResSleep(double seconds)
        {
            Waiting.Set();
            if (!Unblock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not unblock sender");
        }
        internal static void RecordTestKey(byte keyCode, bool isDown) => Output.Enqueue((keyCode, isDown));

        internal static void PauseInvalidatesDequeuedEdge()
        {
            StartTest();
            try
            {
                Publish(0, 70, hold: true);
                Until(() => Output.Count == 1, "hold down");
                Publish(1, 80, future: true);
                Require(Waiting.Wait(2000), "no edge was dequeued into the timing wait");
                Publish(2, 80, release: true, future: true);
                Require(FlushAngleAsync() == 1, "pause must return the dequeued, unsent index");
                Require(FlushAngleAsync() == -1, "repeated pause must not move the cursor again");
                ResumeAngleAsync();
                Publish(1, 90);
                Publish(2, 90, release: true);
                AdvanceClock();
                Until(() => Output.Count >= 4, "resume output");
                StopAngleAsync();
                Require(Output.ToArray().SequenceEqual(new (byte, bool)[] { (70, true), (70, false), (90, true), (90, false) }),
                    "stale events escaped after resume, or retained hold was not released first");
            }
            finally { FinishTest(); }
        }

        internal static void SaturationPreservesFifoAndTail()
        {
            StartTest();
            try
            {
                Publish(0, 160, future: true);
                Require(Waiting.Wait(2000), "no in-flight edge to saturate the queue around");
                for (int i = 1; i <= AadCapacity; i++)
                    Publish(i, (byte)(20 + (i - 1) / 2 % 100), release: i % 2 == 0);
                Require(!Enqueue(AadCapacity + 1, 160, release: true), "full FIFO accepted an extra edge");
                AdvanceClock();
                Until(() => Output.Count >= 1, "first edge after saturation");
                Until(() => Enqueue(AadCapacity + 1, 160, release: true), "retry after space is available");
                Until(() => Output.Count >= AadCapacity + 2, "final release after FIFO drain");
                StopAngleAsync();
                var got = Output.ToArray();
                Require(got.Length == AadCapacity + 2 && got[0] == ((byte)160, true) && got[^1] == ((byte)160, false),
                    "backpressure lost first/final edge or leaked duplicate cleanup");
                for (int i = 1; i <= AadCapacity; i++)
                    Require(got[i] == ((byte)(20 + (i - 1) / 2 % 100), i % 2 != 0), "FIFO reordered or evicted a key transition");
            }
            finally { FinishTest(); }
        }

        internal static void TimedOutStopDoesNotStartAnotherWorker()
        {
            StartTest();
            try
            {
                Publish(0, 73, future: true);
                Require(Waiting.Wait(2000), "sender did not enter timing wait");
                var original = _aadThread;
                StopAngleAsync();
                Require(original?.IsAlive == true && ReferenceEquals(original, _aadThread), "stop erased live worker identity");
                Require(!EnsureAngleAsyncRunning(), "second worker started before old worker completed cleanup");
                AdvanceClock();
                Require(original!.Join(2000), "stop did not cancel the outstanding wait");
                Require(Output.IsEmpty && EnsureAngleAsyncRunning(), "canceled edge fired or driver could not restart");
            }
            finally { FinishTest(); }
        }

        internal static void AngleWorkerFocusLossOnlyReleasesOwnedKeys()
        {
            StartTest();
            Core.Main.Settings.BlockInputWhenUnfocused = true;
            _gameWindowHandle = (IntPtr)1;
            Interlocked.Exchange(ref _testForegroundWindow, (IntPtr)1);
            try
            {
                Publish(0, 160);
                Until(() => Output.Count == 1, "focused angle down");
                Interlocked.Exchange(ref _testForegroundWindow, (IntPtr)2);
                Publish(1, 80);
                Publish(2, 80, release: true);
                Publish(3, 160, release: true);
                Until(() => Output.Any(e => e == ((byte)160, false)), "angle up after focus loss");
                StopAngleAsync();
                Require(Output.ToArray().SequenceEqual(new (byte, bool)[] { (160, true), (160, false) }),
                    "unfocused angle playback emitted an orphan up or lost its owned up");
            }
            finally
            {
                FinishTest();
                Core.Main.Settings.BlockInputWhenUnfocused = false;
                _gameWindowHandle = (IntPtr)(-1);
            }
        }

        private static bool Enqueue(int index, byte key, bool release = false, bool hold = false, bool future = false) =>
            PublishAngleEvent(index, future ? 2_000_000 : 1_000_000, 0,
                release ? (byte)0 : key, release ? key : (byte)0, release, hold, true, true);
        private static void Publish(int index, byte key, bool release = false, bool hold = false, bool future = false) =>
            Require(Enqueue(index, key, release, hold, future), "failed to enqueue non-full event");
        private static void AdvanceClock() { Interlocked.Exchange(ref PreciseNow.Ticks, 2_000_000); Unblock.Set(); }
        private static void StartTest()
        {
            StopAngleAsync();
            Output.Clear();
            Waiting.Reset();
            Unblock.Reset();
            Interlocked.Exchange(ref PreciseNow.Ticks, 1_000_000);
            Require(EnsureAngleAsyncRunning(), "angle worker did not start");
        }
        private static void FinishTest() { AdvanceClock(); StopAngleAsync(); }
        private static void Until(Func<bool> predicate, string phase) =>
            Require(SpinWait.SpinUntil(predicate, 3000), "timed out: " + phase);
        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
