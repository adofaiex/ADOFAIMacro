#nullable enable

namespace ADOFAIMacro.Technique
{
    // Keep the playback payload independent of engine state. Creating this nested
    // value type does not initialize MacroEngine, start its worker or send input.
    internal static partial class MacroEngine
    {
        internal readonly struct HitEvent(double triggerTime, byte keyCode, bool releaseOnly,
                                          bool isHoldRelated = false, byte releaseKeyCode = 0)
        {
            public readonly double TriggerTime = triggerTime;
            public readonly byte KeyCode = keyCode;
            public readonly bool ReleaseOnly = releaseOnly;
            public readonly bool IsHoldRelated = isHoldRelated;
            public readonly byte ReleaseKeyCode = releaseKeyCode;
        }
    }

    /// <summary>Pure adapters shared by live planning, trace caching and playback preparation.</summary>
    internal static class TechniquePlayback
    {
        internal static TraceFile.Trace FromPlan(TechniquePlan plan)
        {
            var events = new TraceFile.TraceEvent[plan.Events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                var e = plan.Events[i];
                byte flags = (byte)((e.Release ? 1 : 0) | (e.Hold ? 2 : 0));
                events[i] = new TraceFile.TraceEvent(e.Time, e.Fraction,
                    e.Release ? (byte)0 : e.Key, flags, e.Release ? e.Key : (byte)0, e.Floor);
            }
            return new TraceFile.Trace
            {
                Events = events,
                Stats = new TraceFile.TraceStats(plan.Fragments, plan.LongestRun, 0, plan.Pieces,
                    plan.Variance, 0, 0, 0, 0, 0),
            };
        }

        internal static MacroEngine.HitEvent[] PlanToHitEvents(TechniquePlan plan)
        {
            var events = new MacroEngine.HitEvent[plan.Events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                var e = plan.Events[i];
                events[i] = new MacroEngine.HitEvent(e.Time, e.Release ? (byte)0 : e.Key,
                    e.Release, e.Hold, e.Release ? e.Key : (byte)0);
            }
            return events;
        }

        // Time playback keeps the ordered key edges and drops only floor/angle coordinates.
        internal static MacroEngine.HitEvent[] TraceToHitEvents(TraceFile.TraceEvent[] traceEvents)
        {
            var events = new MacroEngine.HitEvent[traceEvents.Length];
            for (int i = 0; i < events.Length; i++)
            {
                ref readonly var e = ref traceEvents[i];
                events[i] = new MacroEngine.HitEvent(e.TriggerTime, e.KeyCode,
                    e.ReleaseOnly, e.IsHoldRelated, e.ReleaseKeyCode);
            }
            return events;
        }
    }
}
