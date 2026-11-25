using ADOFAIMacro.Technique;

internal static class ReleaseTimingChecks
{
    private static void Near(double actual, double expected, string label)
    {
        if (Math.Abs(actual - expected) > 1.1e-6)
            throw new InvalidOperationException($"{label}: expected {expected:R}, got {actual:R}");
    }

    internal static void NativeReleaseBranchesAndCaps()
    {
        Near(TechniquePlanner.CalculatePieceRelease(.05, 0, .1, .15, .3, .45, false, false, .8), .11, "larger next / regular end");
        Near(TechniquePlanner.CalculatePieceRelease(.05, 0, .1, .08, .3, .38, false, false, .8), .182, "larger next / shortened end");
        Near(TechniquePlanner.CalculatePieceRelease(.04, 0, .1, .15, .05, .2, false, false, .8), .084, "smaller next / extended end");
        Near(TechniquePlanner.CalculatePieceRelease(.04, 0, .1, .1, .05, .2, false, false, .8), .104, "smaller next / regular end");
        Near(TechniquePlanner.CalculatePieceRelease(.04, 0, .1, .1, .1, .2, true, false, 8), .099999, "same-hand cap");
        Near(TechniquePlanner.CalculatePieceRelease(.04, 0, .1, .1, .1, .2, false, false, 8), .199999, "other-hand cap");
        Near(TechniquePlanner.CalculatePieceRelease(.04, 0, .1, .1, .1, .2, true, true, 8), .199999, "empty/sentinel cap");
        Near(TechniquePlanner.CalculatePieceRelease(0, 0, .1, .1, .1, .2, false, false, .001), .0167, "one-frame rescue when cap allows");
        Near(TechniquePlanner.CalculatePieceRelease(0, 0, .001, .001, .001, .002, false, false, .001), .000001, "rescue must not cross a tiny cap");
        Near(TechniquePlanner.CalculatePieceRelease(.11, 0, .1, .1, .1, .2, true, false, .8), .146, "past-boundary fallback");
    }

    private static HandLayout Hand(byte[] keys, double[] ratios) => new(keys,
        Enumerable.Range(1, keys.Length).Select(n => Enumerable.Range(0, n).ToArray()).ToArray(), ratios);

    internal static void EqualCellsUseTwoCellHorizonAndConfiguredRatios()
    {
        TechniqueNote[] notes = [new(0, 0, 1, NoteKind.Tap), new(.25, 1, 1, NoteKind.Tap), new(.5, 2, 1, NoteKind.Tap)];
        foreach (double ratio in new[] { .4, .8, 1.2, 1.4 })
        {
            var profile = new TechniqueProfile(120, Hand([82], [ratio]), Hand([80], [ratio]));
            var plan = TechniquePlanner.Build(notes, 120, 1, false, _ => profile);
            var up = plan.Events.Where(e => e.Release).OrderBy(e => e.Time).ToArray();
            Near(up[0].Time, .25 * ratio, "first-cell configured ratio");
            Near(up[1].Time, .25 + .25 * ratio, "second-cell configured ratio");
            Near(up[2].Time, .5 + .25 * ratio, "terminal sentinel");
            if (!plan.Assignments.Select(a => a.Key).SequenceEqual(new byte[] {82,80,82}))
                throw new InvalidOperationException("release repair changed key selection");
        }
    }

    internal static void RatiosRemainPerKeyAndOffsetAware()
    {
        TechniqueNote[] notes = [new(1, 0, 1, NoteKind.Tap), new(1.03, 1, 1, NoteKind.Tap), new(1.25, 2, 1, NoteKind.Tap)];
        var profile = new TechniqueProfile(120, Hand([82,51], [.8,1.2]), Hand([80], [.8]));
        var plan = TechniquePlanner.Build(notes, 120, 1, false, _ => profile);
        Near(plan.Events.Single(e => e.Release && e.Key == 82).Time, 1.2, "first key");
        Near(plan.Events.Single(e => e.Release && e.Key == 51).Time, 1.312, "second key uses remaining horizon");
    }


    internal static void Folded170BpmRestoresRatioMeaning()
    {
        double width = 30.0 / 340.0;
        TechniqueNote[] notes = [new(0, 0, 1, NoteKind.Tap), new(width, 1, 1, NoteKind.Tap), new(width * 2, 2, 1, NoteKind.Tap)];
        var profile = new TechniqueProfile(400, Hand([82], [.8]), Hand([80], [.8]));
        var plan = TechniquePlanner.Build(notes, 170, 1, true, _ => profile);
        Near(plan.Events.First(e => e.Release).Time, width * .8, "170 BPM / ratio 0.8 must mean ~70.6 ms, not ~35.3 ms");
    }

    internal static void MergedFlamKeepsFullReleaseWindow()
    {
        // A near-boundary flam belongs to one hand after refinement, with the full merged window.
        TechniqueNote[] notes = [new(0, 0, 1, NoteKind.Tap), new(.249, 1, 1, NoteKind.Tap), new(.251, 2, 1, NoteKind.Tap), new(.75, 3, 1, NoteKind.Tap)];
        var profile = new TechniqueProfile(120, Hand([82,51,50], [.8,.8,.8]), Hand([80,187,8], [.8,.8,.8]));
        var plan = TechniquePlanner.Build(notes, 120, 1, false, _ => profile);
        if (plan.Assignments[1].Hand != plan.Assignments[2].Hand)
            throw new InvalidOperationException("flam grouping changed");
        byte key = plan.Assignments[2].Key;
        double up = plan.Events.First(e => e.Release && e.Key == key && e.Time >= .251).Time;
        if (up - .251 < .09)
            throw new InvalidOperationException("merged flam release window collapsed to the old cell boundary");
    }

    internal static void EmptyCellsDoNotHoldAcrossLongRests()
    {
        TechniqueNote[] notes = [new(0, 0, 1, NoteKind.Tap), new(10, 1, 1, NoteKind.Tap)];
        var profile = new TechniqueProfile(120, Hand([82], [.8]), Hand([80], [.8]));
        var plan = TechniquePlanner.Build(notes, 120, 1, false, _ => profile);
        Near(plan.Events.First(e => e.Release).Time, .2, "immediate empty cell not distant occupied cell");
    }
}
