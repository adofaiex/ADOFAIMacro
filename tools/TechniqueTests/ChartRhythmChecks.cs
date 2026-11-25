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
        double[] offsets = [0, 2, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 16, 18, 20];
        double[] turns = [1, 1, .5, .5, .5, .5, 1, .5, .5, .5, .5, 1, 1, 1, 1];
        foreach (double quarter in new[] { .045, .066079295, .092, .142 })
        {
            var notes = Notes(offsets.Select(offset => offset * quarter).ToArray(), turns);
            var plan = Plan(notes);
            foreach (int start in new[] { 2, 7 })
            {
                var square = plan.Assignments.Skip(start).Take(4).ToArray();
                Check(square.Select(hit => hit.Key).SequenceEqual(new byte[] {51, 82, 80, 187}) &&
                    Hands(plan).Substring(start, 4) == "LLRR",
                    $"closed square at {quarter * 1000} ms: expected 3,R,P,=, got {string.Join(',', square.Select(hit => hit.Key))}");
            }
            VerifyPresses(notes, plan);
        }
    }

    internal static void PostHoldTripletAndMeasuredPairsKeepTheirHands()
    {
        const double pulse = .125523, pair = pulse / 12;
        var notes = new List<TechniqueNote>
        {
            new(0, 0, 1, NoteKind.HoldStart),
            new(1, 1, 1, NoteKind.HoldEnd),
            new(1.001395, 2, 1, NoteKind.Tap, .092),
            new(1.024408, 3, 1, NoteKind.Tap, .097),
            new(1.048815, 4, 1, NoteKind.Tap, 1.806)
        };
        double entry = 1.048815 + .453278;
        void Tap(double time, double turn) => notes.Add(new(time, notes.Count, 1, NoteKind.Tap, turn));
        Tap(entry, 1);
        Tap(entry + 2 * pulse, 1);
        for (int beat = 4; beat <= 10; beat++)
        {
            bool decorated = beat is not (6 or 9);
            Tap(entry + beat * pulse, decorated ? 1d / 6 : 2);
            if (decorated) Tap(entry + beat * pulse + pair, 11d / 6);
        }
        Tap(entry + 12 * pulse, 1);
        Tap(entry + 14.5 * pulse, 1);
        var source = notes.ToArray();
        var attacks = source.Select((_, index) => index).Where(index => index != 1)
            .Select(index => new Attack(index, Profile, .089285272)).ToList();
        const double folded = .089285272;
        var rows = new List<Slice> { new(0, 1, folded, folded, false, 0, folded),
            new(1, 3, folded, folded, true, 1.001395, 1.048815),
            new(3, 4, folded, folded, false, 1.048815, 1.048815 + folded) };
        for (int start = 4; start < attacks.Count;)
        {
            double time = source[attacks[start].Index].Time;
            int end = start + 1;
            if (end < attacks.Count && source[attacks[end].Index].Time - time < .02) end++;
            rows.Add(new(start, end, folded, folded, false, time, time + folded));
            start = end;
        }
        RefineChartRhythms(source, attacks, rows, true);
        foreach (bool lazy in new[] { false, true })
        {
            var plan = Build(source, 195, 1, true, _ => Profile, new FixedRows(rows), lazy);
            Check(Hands(plan).Substring(1, 3) == "RRR",
                $"the post-hold triplet changed hands: {Hands(plan)}");
            Check(Hands(plan).Substring(6, 12) == "RRLLRLLRRLRR",
                $"the new pulse must keep each pair intact and alternate by beat: {Hands(plan)}");
            Check(plan.Events.Single(edge => edge.Hold && edge.Release).Time == 1, "the active hold must release at its original time");
        }
    }

    internal static void SlowerPhraseStartsOnPreferredHand()
    {
        const double pulse = .125523;
        double[] offsets = [0, .125, .25, 1.25, 2.25, 4.25, 5.25, 6.25, 6.25 + 1d / 6,
            7.25, 8.25, 9.25, 10.25, 12.25, 13.25, 14.25, 14.25 + 1d / 6, 16.25, 18.25, 20.25, 22.75, 24.75];
        double[] turns = [.25, .25, .25, 2, 1, .5, .5, 1d / 12, 5d / 12,
            .5, .5, .5, 1, .5, .5, 1d / 12, 11d / 12, 1, 1, 1, 1, 1];
        var notes = Notes(offsets.Select(offset => offset * pulse).ToArray(), turns);
        var attacks = notes.Select((_, index) => new Attack(index, Profile, .089285272)).ToList();
        var rows = notes.Select((note, index) => new Slice(index, index + 1, pulse, pulse,
            index % 2 == 0, note.Time,
            index + 1 < notes.Length ? notes[index + 1].Time : note.Time + pulse)).ToList();
        RefineChartRhythms(notes, attacks, rows, true);
        var phrase = rows.Where(row => row.Start >= 5 && row.Start <= 12).ToArray();
        Check(phrase.Select(row => row.Start).SequenceEqual(new[] { 5, 6, 7, 9, 10, 11, 12 }) &&
            phrase[2].End == 9 && string.Concat(phrase.Select(row => row.Right ? 'R' : 'L')) == "RLRLRLR",
            "the slowdown must anchor the phrase to the preferred hand and keep its subdivisions on their beats: " +
            string.Join(' ', phrase.Select(row => $"{row.Start}-{row.End}:{row.Right}")));
    }

    private sealed class FixedRows(List<Slice> rows) : IGroupingStrategy
    {
        public bool UsesTempoInputs => false;
        public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight) => rows;
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

    internal static void RepeatedTripletsKeepBeatPhaseAcrossTempoAndLayout()
    {
        foreach (double pulse in new[] { .051, .082, .117, .164, .220, .310 })
        foreach (bool startRight in new[] { false, true })
        foreach (var (leftCapacity, rightCapacity) in new[] { (3, 3), (6, 6), (3, 6), (6, 3) })
        {
            var beatNumbers = new[] { 0, 1, 2, 3, 5, 6, 7, 8 };
            var times = beatNumbers.SelectMany(beat => new[] { (double)beat, beat + 1d / 6, beat + 1d / 3 })
                .Select(beat => beat * pulse).ToArray();
            var turns = times.Select((time, i) => i + 1 < times.Length ? (times[i + 1] - time) / pulse * 2 : 0).ToArray();
            var notes = Notes(times, turns);
            int[][] configuredOrder = [[0], [1, 0], [1, 0, 2], .. Orders.Skip(3)];
            var left = new HandLayout(new byte[] { 82, 51, 50, 81, 32, 86 }[..leftCapacity], configuredOrder, Ratios);
            var right = new HandLayout(new byte[] { 80, 187, 8, 46, 165, 190 }[..rightCapacity], Orders, Ratios);
            var profile = new TechniqueProfile(400, left, right);
            TechniquePlan Compile(bool lazy) => Build(notes, 195, 1, startRight, _ => profile, CreateGroupingStrategy(BeatGridStrategyId, 6.5), lazy);
            var normal = Compile(false);
            var lazy = Compile(true);
            string expected = string.Concat(beatNumbers.Select(beat => new string(startRight ^ (beat % 2 != 0) ? 'R' : 'L', 3)));
            Check(Hands(normal) == expected && normal.Assignments.SequenceEqual(lazy.Assignments),
                $"{pulse*1000} ms, {leftCapacity}/{rightCapacity} keys, startRight={startRight}: expected {expected}, got {Hands(normal)}");
            foreach (var beat in normal.Assignments.Chunk(3))
                Check(beat.Select(hit => hit.Key).SequenceEqual(beat[0].Hand == TechniqueHand.Right
                    ? new byte[] { 8, 187, 80 } : new byte[] { 51, 82, 50 }),
                    $"{pulse * 1000} ms {leftCapacity}/{rightCapacity}, rightLead={startRight}: triplet {beat[0].Floor} used {string.Join(',', beat.Select(hit => hit.Key))}");
        }
    }

    internal static void QuarterTurnRunsDoNotUseFiveOrSixKeyRows()
    {
        foreach (double unit in new[] { .019, .028, .046, .072 })
        foreach (bool startRight in new[] { false, true })
        {
            double[] times = [.. Enumerable.Range(0, 16).Select(i => i * unit), 16 * unit, 20 * unit, 24 * unit];
            double[] turns = [.. Enumerable.Repeat(.5, 16), 2, 2, 0];
            var notes = Notes(times, turns);
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var plan = Build(notes, 195, 1, startRight, _ => Profile, strategy);
            string expected = startRight ? "RRRRLLLLRRRRLLLL" : "LLLLRRRRLLLLRRRR";
            Check(Hands(plan).StartsWith(expected, StringComparison.Ordinal),
                $"{unit*1000} ms quarter-turn train should use cadential groups of four: {Hands(plan)}");
            for (int i = 0; i < 16; i += 4)
            {
                var layout = Profile.Layout(expected[i] == 'R');
                var keys = Enumerable.Range(0, 4).Select(offset => layout.Keys[layout.PreferredIndex(4, offset)]);
                Check(plan.Assignments.Skip(i).Take(4).Select(hit => hit.Key).SequenceEqual(keys),
                    "a quarter-turn group must respect the configured four-key row");
            }
        }
    }

    internal static void RepeatedTripletsCarryPhaseThroughMeasuredTempoStep()
    {
        foreach (var (first, second) in new[] { (.183, .129), (.111, .201) })
        foreach (bool startRight in new[] { false, true })
        {
            double time = 0;
            var times = new List<double>();
            var turns = new List<double>();
            for (int beat = 0; beat < 11; beat++)
            {
                // The downbeat at the seam starts using the new tempo immediately.
                double width = beat < 5 ? first : second;
                times.AddRange([time, time + width / 6, time + width / 3]);
                turns.AddRange([1d / 3, 1d / 3, 4d / 3]);
                time += width;
            }
            var notes = Notes(times.ToArray(), turns.ToArray());
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            foreach (bool lazy in new[] { false, true })
            {
                var plan = Build(notes, 195, 1, startRight, _ => Profile, strategy, lazy);
                string expected = string.Concat(Enumerable.Range(0, 11).Select(beat => new string(startRight ^ (beat % 2 != 0) ? 'R' : 'L', 3)));
                Check(Hands(plan) == expected,
                    $"tempo step {first} -> {second}, startRight={startRight}: expected {expected}, got {Hands(plan)}");
            }
        }
    }

    internal static void QuarterTurnRunsKeepFourKeyOrderAcrossTempoSteps()
    {
        foreach (var (fast, slow) in new[] { (.027, .043), (.048, .026) })
        foreach (bool startRight in new[] { false, true })
        {
            double time = 0;
            var times = new List<double>();
            var turns = new List<double>();
            for (int beat = 0; beat < 6; beat++)
            {
                double unit = beat < 3 ? fast : slow;
                for (int part = 0; part < 4; part++)
                {
                    times.Add(time);
                    turns.Add(.5);
                    time += unit;
                }
            }
            for (int i = 0; i < 3; i++) { times.Add(time); turns.Add(2); time += 4 * slow; }
            var notes = Notes(times.ToArray(), turns.ToArray());
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var normal = Build(notes, 195, 1, startRight, _ => Profile, strategy);
            var lazy = Build(notes, 195, 1, startRight, _ => Profile, strategy, true);
            for (int beat = 0; beat < 6; beat++)
            {
                bool right = startRight ^ (beat % 2 != 0);
                var layout = Profile.Layout(right);
                var expected = Enumerable.Range(0, 4).Select(i => layout.Keys[layout.PreferredIndex(4, i)]);
                Check(normal.Assignments.Skip(beat * 4).Take(4).Select(hit => hit.Key).SequenceEqual(expected),
                    $"quarter turns {fast}->{slow}, beat {beat}, rightLead={startRight}: {Hands(normal)}");
            }
            Check(normal.Assignments.SequenceEqual(lazy.Assignments), "ordinary and lazy release must use the same fingering");
        }
    }

    internal static void QuarterTurnRunsKeepFourKeysAndPhaseAcrossRests()
    {
        int[] beats = [0, 1, 3, 4, 7, 8];
        foreach (var (first, second) in new[] { (.028, .028), (.048, .026), (.027, .043) })
        foreach (bool startRight in new[] { false, true })
        foreach (double bpmLimit in new[] { 400d, 550d })
        {
            double time = 0;
            var times = new List<double>();
            var turns = new List<double>();
            for (int i = 0; i < beats.Length; i++)
            {
                double unit = i < 2 ? first : second;
                int steps = i + 1 < beats.Length ? beats[i + 1] - beats[i] : 2;
                for (int part = 0; part < 4; part++)
                {
                    times.Add(time);
                    turns.Add(part == 3 ? 2 * steps - 1.5 : .5);
                    time += unit * (part == 3 ? 4 * steps - 3 : 1);
                }
            }
            for (int i = 0; i < 3; i++) { times.Add(time); turns.Add(2); time += 4 * second; }
            var notes = Notes(times.ToArray(), turns.ToArray());
            var profile = new TechniqueProfile(bpmLimit, Profile.Layout(false), Profile.Layout(true));
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var normal = Build(notes, 195, 1, startRight, _ => profile, strategy);
            var lazy = Build(notes, 195, 1, startRight, _ => profile, strategy, true);
            for (int i = 0; i < beats.Length; i++)
            {
                bool right = startRight ^ ((beats[i] & 1) != 0);
                var layout = profile.Layout(right);
                var expected = Enumerable.Range(0, 4).Select(j => layout.Keys[layout.PreferredIndex(4, j)]);
                var actual = normal.Assignments.Skip(i * 4).Take(4);
                Check(actual.All(hit => (hit.Hand == TechniqueHand.Right) == right) && actual.Select(hit => hit.Key).SequenceEqual(expected),
                    $"quarter turns with rests {first}->{second}, beat={beats[i]}, rightLead={startRight}, BPM limit={bpmLimit}: {Hands(normal)}");
            }
            Check(normal.Assignments.SequenceEqual(lazy.Assignments), "release policy must not change a four-key beat");
        }
    }

    internal static void EvenTripletsUseTheSurroundingBeatClock()
    {
        foreach (double pulse in new[] { .108, .154, .238 })
        foreach (bool rightLead in new[] { false, true })
        {
            const int intro = 5, tripletBeats = 5;
            var positions = Enumerable.Range(0, intro).Select(i => (double)i)
                .Concat(Enumerable.Range(0, tripletBeats * 3).Select(i => intro + i / 3d))
                .Concat(Enumerable.Range(intro + tripletBeats, 5).Select(i => (double)i)).ToArray();
            var turns = positions.Select((position, i) => i + 1 < positions.Length ? positions[i + 1] - position : 0).ToArray();
            var notes = Notes(positions.Select(position => position * pulse).ToArray(), turns);
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            foreach (bool lazy in new[] { false, true })
            {
                var plan = Build(notes, 195, 1, rightLead, _ => Profile, strategy, lazy);
                string expected = string.Concat(Enumerable.Range(0, intro + tripletBeats + 5)
                    .Select(i => new string(rightLead ^ (i % 2 != 0) ? 'R' : 'L', i >= intro && i < intro + tripletBeats ? 3 : 1)));
                Check(Hands(plan) == expected, $"even triplets at {pulse*1000} ms, rightLead={rightLead}: expected {expected}, got {Hands(plan)}");
            }
        }
    }

    internal static void MixedSubdivisionsUseOneClockAcrossRests()
    {
        int[] counts = [3, 2, 4, 1, 3, 4, 2, 3, 1, 4];
        int[] beats = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10];
        double[] offsets = [0, .11, .23, .34];
        foreach (double pulse in new[] { .119, .173, .237 })
        foreach (bool startRight in new[] { false, true })
        {
            var positions = counts.SelectMany((count, i) => offsets.Take(count).Select(offset => beats[i] + offset)).ToArray();
            var turns = positions.Select((position, i) => i + 1 < positions.Length ? (positions[i + 1] - position) * 2 : 0).ToArray();
            var notes = Notes(positions.Select(position => position * pulse).ToArray(), turns);
            var strategy = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var plan = Build(notes, 195, 1, startRight, _ => Profile, strategy);
            var lazy = Build(notes, 195, 1, startRight, _ => Profile, strategy, true);
            string expected = string.Concat(counts.Select((count, i) => new string(startRight ^ (beats[i] % 2 != 0) ? 'R' : 'L', count)));
            Check(Hands(plan) == expected && plan.Assignments.SequenceEqual(lazy.Assignments),
                $"mixed subdivisions at {pulse*1000} ms, rightLead={startRight}: expected {expected}, got {Hands(plan)}");
        }
    }

    internal static void EvenTripletsUseAvailableBeatAnchorsAtTempoAndChartEdges()
    {
        var cases = new (int Intro, int Tail, double First, double Second)[]
        {
            (5, 0, .154, .154), (0, 5, .154, .154), (5, 5, .143, .192), (5, 5, .201, .128)
        };
        foreach (var (intro, tail, first, second) in cases)
        foreach (bool rightLead in new[] { false, true })
        {
            const int triplets = 6;
            var fixture = new List<(double Time, double Turn, bool Right)>();
            double time = 0;
            for (int beat = 0; beat < intro + triplets + tail; beat++)
            {
                int count = beat >= intro && beat < intro + triplets ? 3 : 1;
                double pulse = beat < intro + 3 ? first : second;
                bool right = rightLead ^ (beat % 2 != 0);
                for (int i = 0; i < count; i++) fixture.Add((time + i * pulse / count, 1d / count, right));
                time += pulse;
            }
            var notes = Notes(fixture.Select(note => note.Time).ToArray(), fixture.Select(note => note.Turn).ToArray());
            var grouping = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var normal = Build(notes, 195, 1, rightLead, _ => Profile, grouping);
            var lazy = Build(notes, 195, 1, rightLead, _ => Profile, grouping, true);
            string expected = string.Concat(fixture.Select(note => note.Right ? 'R' : 'L'));
            Check(Hands(normal) == expected && normal.Assignments.SequenceEqual(lazy.Assignments),
                $"intro={intro}, tail={tail}, {first}->{second}, rightLead={rightLead}: expected {expected}, got {Hands(normal)}");
            var tripletKeys = normal.Assignments.Skip(intro).Take(3 * triplets).Chunk(3);
            foreach (var beat in tripletKeys)
                Check(beat.Select(hit => hit.Key).SequenceEqual(beat[0].Hand == TechniqueHand.Right
                    ? new byte[] { 8, 187, 80 } : new byte[] { 50, 51, 82 }), "even thirds must use the configured three-key row");
        }
    }

    internal static void ConfirmedPhraseKeepsPhaseAtEmptyBeatEntry()
    {
        const double pulse = .128;
        // The phrase resumes after one empty beat. Its subdivision is established
        // for eight beats, even though the old grid gave those groups a reversed lead.
        double[] positions = [0, 1, 2, 3, .. Enumerable.Range(5, 8)
            .SelectMany(beat => new[] { (double)beat, beat + 1d / 6, beat + 1d / 3 })];
        var notes = Notes(positions.Select(p => p * pulse).ToArray(), positions.Select((p, i) =>
            i + 1 < positions.Length ? (positions[i + 1] - p) * 2 : 0).ToArray());
        foreach (bool startRight in new[] { false, true })
        {
            var attacks = notes.Select((_, index) => new Attack(index, Profile, pulse)).ToList();
            var rows = Enumerable.Range(0, 4).Select(i => new Slice(i, i + 1, pulse, pulse,
                startRight ^ (i % 2 != 0), notes[i].Time, notes[i].Time + pulse)).ToList();
            for (int beat = 0; beat < 8; beat++)
            {
                int index = 4 + beat * 3;
                rows.Add(new Slice(index, index + 3, pulse, pulse, startRight ^ (beat % 2 != 0),
                    notes[index].Time, notes[index].Time + pulse));
            }
            RefineChartRhythms(notes, attacks, rows, startRight, measuredPhrases: true);
            Check(rows.Count == 12 && rows[3].Right == rows[4].Right &&
                rows.Skip(4).Select((row, i) => row.Count == 3 && row.Right == (startRight ^ (i % 2 == 0))).All(ok => ok),
                "the observed two-beat interval must preserve phase across the one empty beat");
        }
    }

    internal static void EvenTripletsKeepTheirPhaseAcrossRests()
    {
        foreach (double pulse in new[] { .108, .154, .212 })
        foreach (bool rightLead in new[] { false, true })
        foreach (int emptyBefore in new[] { 0, 1 })
        foreach (int emptyAfter in new[] { 0, 1 })
        {
            var beats = new[] { 0, 1, 2, 3, 4 + emptyBefore, 5 + emptyBefore, 7 + emptyBefore,
                8 + emptyBefore, 9 + emptyBefore, 10 + emptyBefore + emptyAfter, 11 + emptyBefore + emptyAfter,
                12 + emptyBefore + emptyAfter };
            var points = beats.SelectMany((beat, index) => Enumerable.Range(0, index is >= 4 and <= 8 ? 3 : 1)
                .Select(third => (Beat: beat, Position: beat + third / 3d))).ToArray();
            var notes = Notes(points.Select(point => point.Position * pulse).ToArray(), points.Select((point, i) =>
                i + 1 < points.Length ? points[i + 1].Position - point.Position : 0).ToArray());
            var grouping = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var normal = Build(notes, 195, 1, rightLead, _ => Profile, grouping);
            var lazy = Build(notes, 195, 1, rightLead, _ => Profile, grouping, true);
            string expected = string.Concat(points.Select(point => rightLead ^ ((point.Beat & 1) != 0) ? 'R' : 'L'));
            Check(Hands(normal) == expected && normal.Assignments.SequenceEqual(lazy.Assignments),
                $"thirds across rests at {pulse} s, lead={rightLead}, empty before/after={emptyBefore}/{emptyAfter}: expected {expected}, got {Hands(normal)}");
        }
    }

    internal static void EvenTripletsKeepTheBeatThroughChangesAtBothSeams()
    {
        foreach (var (before, inside, after) in new[] { (.193, .154, .231), (.119, .164, .104) })
        foreach (bool preferredRight in new[] { false, true })
        {
            var notes = new List<(double Time, double Turn)>();
            double time = 0;
            for (int beat = 0; beat < 14; beat++)
            {
                int count = beat is >= 4 and < 10 ? 3 : 1;
                double width = beat < 4 ? before : beat < 10 ? inside : after;
                for (int part = 0; part < count; part++) notes.Add((time + width * part / count, 1d / count));
                time += width;
            }
            var source = Notes(notes.Select(note => note.Time).ToArray(), notes.Select(note => note.Turn).ToArray());
            var grouping = CreateGroupingStrategy(BeatGridStrategyId, 6.5);
            var normal = Build(source, 195, 1, preferredRight, _ => Profile, grouping);
            var lazy = Build(source, 195, 1, preferredRight, _ => Profile, grouping, true);
            string expected = string.Concat(Enumerable.Range(0, 14).Select(beat => new string(
                preferredRight ^ ((beat & 1) != 0) ? 'R' : 'L', beat is >= 4 and < 10 ? 3 : 1)));
            Check(Hands(normal) == expected && normal.Assignments.SequenceEqual(lazy.Assignments),
                $"step at both thirds seams ({before}, {inside}, {after}): expected {expected}, got {Hands(normal)}");
        }
    }

    internal static void PickupCanStartOnEitherHandToMeetTheNextPhrase()
    {
        const double pulse = .15;
        foreach (bool preferredRight in new[] { false, true })
        foreach (bool nextRight in new[] { false, true })
        foreach (double nextPulse in new[] { pulse, .21 })
        foreach (int beatsToNext in new[] { 1, 2 })
        foreach (bool establishedNext in new[] { true, false })
        {
            double nextStart = (4 + beatsToNext) * pulse;
            var times = Enumerable.Range(0, 5).SelectMany(beat => new[] { beat * pulse, (beat + .12) * pulse, (beat + .24) * pulse })
                .Concat(Enumerable.Range(0, 4).SelectMany(beat => new[] { nextStart + beat * nextPulse, nextStart + (beat + .36) * nextPulse })).ToArray();
            var notes = Notes(times, Enumerable.Repeat(1d, times.Length).ToArray());
            var attacks = notes.Select((_, i) => new Attack(i, Profile, pulse)).ToList();
            var rows = new List<Slice>();
            for (int beat = 0; beat < 5; beat++)
                rows.Add(new(beat * 3, beat * 3 + 3, pulse, pulse,
                    preferredRight ^ (beat % 2 != 0), times[beat * 3], (beat + 1) * pulse));
            for (int beat = 0; beat < 4; beat++)
                rows.Add(new(15 + beat * 2, 17 + beat * 2, nextPulse, nextPulse,
                    nextRight ^ (establishedNext && beat % 2 != 0), times[15 + beat * 2], nextStart + (beat + 1) * nextPulse));
            var following = rows.Skip(5).ToArray();
            RefineChartRhythms(notes, attacks, rows, preferredRight, measuredPhrases: true);
            bool expectedLead = establishedNext ? nextRight ^ (beatsToNext % 2 != 0) : preferredRight;
            Check(rows.Count == 9 && rows.Skip(5).SequenceEqual(following) &&
                rows.Take(5).Select((row, beat) => row.Count == 3 && row.Right == (expectedLead ^ (beat % 2 != 0))).All(ok => ok),
                $"pickup phase: preferredRight={preferredRight}, nextRight={nextRight}, nextPulse={nextPulse}, beatsToNext={beatsToNext}, establishedNext={establishedNext}");
        }
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
