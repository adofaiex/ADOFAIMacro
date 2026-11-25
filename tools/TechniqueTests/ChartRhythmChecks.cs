using ADOFAIMacro.Technique;
using static ADOFAIMacro.Technique.TechniquePlanner;

internal static class ChartRhythmChecks
{
    private static readonly int[][] Orders = [[0], [1, 0], [2, 1, 0], [2, 1, 0, 4],
        [3, 2, 1, 0, 4], [3, 2, 1, 0, 5, 4]];
    private static readonly double[] Ratios = [.8, 1, 1.2, 1.4, .8, 1];
    private static readonly TechniqueProfile Profile = new(400,
        new([82, 51, 50, 81, 32, 86], Orders, Ratios), new([80, 187, 8, 46, 165, 190], Orders, Ratios));

    internal static void FourQuarterTurnsPerHand()
    {
        const double quarter = .033039647;
        var onsets = Enumerable.Range(0, 9).Select(index => index * quarter).Append(8 * quarter + .011013)
            .Concat([12 * quarter, 16 * quarter, 20 * quarter]).ToArray();
        double[] turns = [.5, .5, .5, .5, .5, .5, .5, .5, 1d / 6, 1, 1, 1, 1];
        var notes = Notes(onsets, turns);
        var plan = Plan(notes);
        Check(plan.Assignments.Take(10).Select(hit => hit.Key).SequenceEqual(new byte[] {8, 187, 80, 165, 50, 51, 82, 32, 187, 80}),
            $"the two 90-degree groups must use the four-key order, followed by one same-hand exit pair: {string.Join(',', plan.Assignments.Select(hit => hit.Key))}");
        var tiles = onsets.Prepend(-quarter).Select((time, index) => (TechniqueTile?)new TechniqueTile(time,
            1, false, false, false, index == 0 ? 0 : turns[index - 1])).ToArray();
        var timeline = TechniqueTimeline.Build(tiles);
        Check(timeline.Select(note => note.FollowingTurn).Take(9).SequenceEqual(turns.Take(9)),
            "each attack must receive the turn of the tile it arrives on");
        var fingerprint = TechniqueTimeline.Fingerprint(tiles, 195, 1, includeTurns: true);
        tiles[4] = new TechniqueTile(onsets[3], 1, false, false, false, 1);
        Check(TechniqueTimeline.Fingerprint(tiles, 195, 1, includeTurns: true) != fingerprint,
            "changing the turn must invalidate a beat-grid trace");
        VerifyPresses(notes, plan);
    }

    internal static void SlowSquaresUseFourKeys()
    {
        const double quarter = .066079295;
        double[] offsets = [0, 2, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 16, 18, 20];
        double[] turns = [1, 1, .5, .5, .5, .5, 1, .5, .5, .5, .5, 1, 1, 1, 1];
        var notes = Notes(offsets.Select(offset => offset * quarter).ToArray(), turns);
        var plan = Plan(notes);
        foreach (int start in new[] { 2, 7 })
        {
            var square = plan.Assignments.Skip(start).Take(4).ToArray();
            Check(square.Select(hit => hit.Key).SequenceEqual(new byte[] {51, 82, 80, 187}),
                $"a slow square must use the inner two keys on each hand in order: {string.Join(',', square.Select(hit => hit.Key))}");
        }
        VerifyPresses(notes, plan);
    }

    internal static void OffbeatPickupDoesNotChooseLeadForThePhrase()
    {
        const double beat = .066079295;
        double[] offsets = [-.25, 0, 1d / 6, 2, 3, 4, 4 + 1d / 6, 5, 6, 8, 8 + 1d / 6, 9, 10, 11, 12, 14, 16, 18];
        var notes = Notes(offsets.Select(offset => 1 + offset * beat).ToArray(), Enumerable.Repeat(1d, offsets.Length).ToArray());
        var attacks = notes.Select((note, index) => new Attack(index, Profile, .089285272)).ToList();
        var rows = new List<Slice> { new(0, 3, .089285272, .089285272, true, notes[0].Time, notes[0].Time + .089285272) };
        int[] starts = [3, 4, 5, 7, 8, 9, 11, 12, 13, 14, 15, 16, 17];
        bool[] hands = [false, true, false, true, false, false, true, false, true, false, false, false, true];
        for (int index = 0; index < starts.Length; index++)
        {
            int start = starts[index], stop = index + 1 < starts.Length ? starts[index + 1] : notes.Length;
            rows.Add(new(start, stop, beat, beat, hands[index], notes[start].Time, notes[start].Time + beat));
        }
        var original = rows.ToArray();
        PreferLeadAfterPickup(notes, attacks, rows, true);
        Check(rows[0].Equals(original[0]) && rows[1].Right && !rows[2].Right && rows[3].Right,
            "the offbeat pickup must stay intact; the measured phrase must use the preferred lead and keep its pairs");
    }

    internal static void ShortHalfBeatPhraseKeepsArticulation()
    {
        const double pulse = .13215859;
        double[] onsets = [0, pulse, 2 * pulse, 3 * pulse, 4 * pulse, 4 * pulse + pulse / 6, 7 * pulse, 8 * pulse, 9 * pulse];
        double[] turns = [.5, .5, .5, .5, 1d / 12, 17d / 12, .5, .5, 1];
        var notes = Notes(onsets, turns);
        var plan = Plan(notes);
        Check(Hands(plan).StartsWith("RLRLRR", StringComparison.Ordinal),
            "the short measured phrase alternates by beat, not by subdivision; its final pair is one hand");
        VerifyPresses(notes, plan);
    }

    internal static void ReturnFromTripletsKeepsSubdivisionOnOneHand()
    {
        const double fast = .06410256, slow = .08547008;
        var onsets = new List<double>();
        for (int beat = 0; beat < 4; beat++)
        {
            onsets.Add(beat * fast);
            onsets.Add(beat * fast + fast / 12);
            onsets.Add(beat * fast + fast / 6);
        }
        double exit = 4 * fast;
        onsets.AddRange([exit, exit + slow / 8, exit + slow, exit + 2 * slow, exit + 3 * slow,
            exit + 4 * slow, exit + 4.5 * slow, exit + 4.625 * slow]);
        for (int beat = 5; beat <= 12; beat++) onsets.Add(exit + beat * slow);
        var notes = Notes(onsets.ToArray(), Enumerable.Repeat(1d, onsets.Count).ToArray());
        var plan = Plan(notes);
        string hands = Hands(plan);
        Check(hands[17] == hands[18] && hands[18] == hands[19] && hands[16] != hands[17] && hands[19] != hands[20],
            "the half-beat articulation after the triplets must stay in one row between the slow beats");
        VerifyPresses(notes, plan);
    }

    internal static void DensePhraseCarriesPhaseThroughMixedSizesAndRests()
    {
        const double pulse = .06410256;
        var counts = new[] { 4, 4, 4, 4, 2, 2, 2, 2, 2, 1, 1, 2, 2, 2, 2, 1, 4 };
        var onsets = counts.SelectMany((count, index) => Enumerable.Range(0, count).Select(offset =>
            (index + (index >= 16 ? 2 : 0)) * pulse + offset * pulse / (count == 4 ? 16 : 6)))
            .Concat([22.5 * pulse, 27.5 * pulse, 32.5 * pulse]).ToArray();
        var notes = Notes(onsets, Enumerable.Repeat(1d, onsets.Length).ToArray());
        var plan = Plan(notes);
        int index = 0;
        for (int beat = 0; beat < counts.Length; beat++)
        {
            Check(plan.Assignments.Skip(index).Take(counts[beat]).All(hit => hit.Hand ==
                (beat % 2 == 0 ? TechniqueHand.Right : TechniqueHand.Left)), $"beat {beat} changed hand inside its articulation: {Hands(plan)}");
            index += counts[beat];
        }
        VerifyPresses(notes, plan);
    }

    internal static void HalfPulseEntryAlternatesAcrossTheSeam()
    {
        const double fast = .08810573, slow = 2 * fast, folded = .089285272;
        foreach (int count in new[] { 1, 8 })
        {
            double[] gaps = [slow, slow, slow, .. Enumerable.Repeat(fast, count + (count == 1 ? 1 : 0)),
                count == 1 ? slow : 6 * fast, slow, slow];
            var notes = Notes(gaps.Select((_, index) => gaps.Take(index).Sum()).Append(gaps.Sum()).ToArray(),
                gaps.Select(gap => gap == slow ? 5d / 6 : 5d / 12).Append(5d / 6).ToArray());
            var attacks = notes.Select((note, index) => new Attack(index, Profile, folded)).ToList();
            var rows = notes.Select((note, index) => new Slice(index, index + 1,
                index is > 3 && index <= 3 + count ? folded : slow,
                index is > 3 && index <= 3 + count ? folded : slow,
                index <= 3 ? index % 2 != 0 : count == 1 && index == 5 ? false : index <= 3 + count ? index % 2 == 0 : index % 2 != 0,
                note.Time, index + 1 < notes.Length ? notes[index + 1].Time : note.Time + slow)).ToList();
            var original = rows.ToArray();
            RepairHalfPulseEntry(notes, attacks, rows);
            Check(rows[3].Right != rows[4].Right && rows[4].Right != rows[5].Right,
                "an isolated half beat or a faster run must not inherit a duplicate hand at the seam");
            int stop = 4 + (count == 1 ? 2 : count);
            Check(rows.Take(4).SequenceEqual(original.Take(4)) && rows.Skip(stop).SequenceEqual(original.Skip(stop)) &&
                rows.Zip(original).All(pair => (pair.First.Start, pair.First.End, pair.First.TimeStart, pair.First.TimeEnd) ==
                    (pair.Second.Start, pair.Second.End, pair.Second.TimeStart, pair.Second.TimeEnd)),
                "only the seam phase may change; keep other rows, boundaries and onsets");
        }
    }

    private static TechniqueNote[] Notes(double[] onsets, double[] turns) => onsets.Select((time, index) =>
        new TechniqueNote(time, index, 1, NoteKind.Tap, turns[index])).ToArray();

    private static TechniquePlan Plan(TechniqueNote[] notes, bool lazy = false) =>
        Build(notes, 195, 1, true, _ => Profile, CreateGroupingStrategy(BeatGridStrategyId, 6.5), lazy);

    private static string Hands(TechniquePlan plan) => string.Concat(plan.Assignments.Select(hit => hit.Hand == TechniqueHand.Right ? 'R' : 'L'));

    private static void VerifyPresses(TechniqueNote[] notes, TechniquePlan plan)
    {
        var lazy = Plan(notes, true);
        Check(plan.Assignments.Length == notes.Length && lazy.Assignments.SequenceEqual(plan.Assignments),
            "every attack must survive and lazy release must keep its physical key");
        foreach (var variant in new[] { plan, lazy })
        {
            var held = new HashSet<byte>();
            foreach (var edge in variant.Events)
                Check(edge.Release ? held.Remove(edge.Key) : held.Add(edge.Key), "unpaired or duplicate physical key edge");
            Check(held.Count == 0, "keys left held at chart end");
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
