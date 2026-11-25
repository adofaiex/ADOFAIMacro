using ADOFAIMacro.Technique;

internal static class BoundaryCadenceChecks
{
    private static readonly int[][] Orders = [[0], [1,0], [2,1,0], [2,1,0,4],
        [3,2,1,0,4], [3,2,1,0,5,4], [3,2,1,0,6,5,4], [3,2,1,0,7,6,5,4]];
    private static readonly double[] Ratios = [.8,1,1.2,1.4,.8,1,1.2,1.4];
    private static TechniqueProfile Profile() => new(400,
        new HandLayout([82,51,50,81,32,86,162,160], Orders, Ratios),
        new HandLayout([80,187,8,46,165,190,93,163], Orders, Ratios));

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static TechniqueNote[] Notes(params double[] times) =>
        times.Select((time,i) => new TechniqueNote(time,i,1,NoteKind.Tap)).ToArray();

    private static void Repair(TechniqueNote[] notes, List<TechniquePlanner.Slice> pieces, TechniqueProfile? profile = null)
    {
        profile ??= Profile();
        var attacks = notes.Select((n,i)=>(n,i)).Where(x => x.n.Kind != NoteKind.HoldEnd)
            .Select(x => new TechniquePlanner.Attack(x.i,profile,.075)).ToList();
        TechniquePlanner.RepairBoundaryCadence(notes,attacks,pieces);
    }

    internal static void DenseSameHandRowsSwitchWithoutMovingTheirBoundary()
    {
        // Same shape as 570–578: two dense, consecutive 4/5-key pieces restart one hand's row.
        var notes = Notes(0,.011628,.023256,.034884,.040698,.046512,.058140,.069768,.081396);
        List<TechniquePlanner.Slice> pieces = [new(0,4,.052325,.052325,false,0,.040698),
            new(4,9,.052325,.052325,false,.040698,.093023)];
        var before = pieces.ToArray();
        Repair(notes,pieces);
        Assert(!pieces[0].Right && pieces[1].Right, "first-hand preference is fixed; the second piece should use the other hand");
        for (int i=0;i<pieces.Count;i++)
        {
            var a=before[i]; var b=pieces[i];
            Assert((a.Start,a.End,a.Length,a.PressWindow,a.TimeStart,a.TimeEnd)==
                (b.Start,b.End,b.Length,b.PressWindow,b.TimeStart,b.TimeEnd), "repair must not move or resize a piece");
        }
    }

    internal static void NarrowToWideBoundaryCanSwitchWholeRow()
    {
        // The 8182–8195 boundary shape: the narrow row starts with Backspace,
        // which reappears in the wide right-hand row before it has recovered.
        var notes=Notes(0,.001,.002,.020,.022727,.024,.034,.046,.057,.068,.099);
        List<TechniquePlanner.Slice> pieces = [new(0,3,.044116,.044116,true,0,.020),
            new(3,11,.088232,.088232,true,.020,.108232)];
        Repair(notes,pieces);
        Assert(pieces[0].Right && !pieces[1].Right, "wide row must not immediately reuse the same right-hand keys");
        Assert(pieces[1].Count==8 && pieces[1].PressWindow==.088232, "width repair must keep the original ordered row size");
    }

    internal static void SafeSeparatedRowsAndTightSameHandEdgesStayPut()
    {
        List<TechniquePlanner.Slice> pieces = [new(0,2,.075,.075,true,0,.075),
            new(2,4,.075,.075,true,.075,.150)];
        Repair(Notes(0,.025,.154,.179),pieces);
        Assert(pieces.All(p=>p.Right), "complete separated pairs should not be forced to alternate");
        Repair(Notes(0,.020,.021,.035),pieces);
        Assert(pieces.All(p=>p.Right), "switching would split a tight same-hand edge");
    }

    internal static void JointWidthBoundaryRemovesUnneededZigzag()
    {
        // Identical keys on consecutive rows are already outside the recovery window.
        // The old per-row pass sees zero reuse in either hand and cannot remove the zigzag.
        var notes = Notes(0, .012, .024, .084, .096, .108, .168, .180, .192, .252, .264, .276);
        List<TechniquePlanner.Slice> pieces = [new(0,3,.084,.084,true,0,.084),
            new(3,6,.084,.084,false,.084,.168), new(6,9,.096,.096,true,.168,.252),
            new(9,12,.096,.096,false,.252,.348)];
        var before = pieces.ToArray();
        Repair(notes,pieces);
        Assert(pieces.All(p=>p.Right), "joint planning should keep the first hand and remove redundant switches");
        for (int i = 0; i < pieces.Count; i++)
        {
            var p=pieces[i];var old=before[i];
            Assert((p.Start,p.End,p.PressWindow,p.TimeStart,p.TimeEnd)==
                (old.Start,old.End,old.PressWindow,old.TimeStart,old.TimeEnd), "joint planning must preserve slice geometry");
        }
    }

    internal static void JointWidthBoundaryKeepsNecessaryAlternation()
    {
        var notes=Notes(0,.01,.02,.05,.06,.07,.10,.11,.12,.15,.16,.17);
        List<TechniquePlanner.Slice> pieces=[new(0,3,.05,.05,true,0,.05),
            new(3,6,.05,.05,false,.05,.10),new(6,9,.09,.09,true,.10,.15),
            new(9,12,.09,.09,false,.15,.24)];
        var before=pieces.ToArray();
        Repair(notes,pieces);
        Assert(pieces.SequenceEqual(before), "fewer switches must not create 50 ms same-key reuse");
    }

    private static List<TechniquePlanner.Slice> Rebuild(TechniqueNote[] notes, List<TechniquePlanner.Slice> baseline, double width = .0882329)
    {
        var profile=Profile();
        var attacks=notes.Select((n,i)=>(n,i)).Where(z=>z.n.Kind!=NoteKind.HoldEnd)
            .Select(z=>new TechniquePlanner.Attack(z.i,profile,width)).ToList();
        return TechniquePlanner.RebuildSteadyPulses(notes,attacks,baseline);
    }

    internal static void SteadyPulsesSpanAttachedShortPair()
    {
        const double pulse=60.0/440;
        var times=Enumerable.Range(0,25).Select(i=>1+i*pulse).ToList();
        times.Insert(13,times[12]+.000568);
        var notes=Notes(times.ToArray());
        var pieces=Enumerable.Range(0,25).Select(i=>new TechniquePlanner.Slice(
            i+(i>12?1:0),i+1+(i>=12?1:0),.0882329,.0882329,(i/2)%2==0,
            1+i*pulse,1+i*pulse+.0882329)).ToList();
        var result=Rebuild(notes,pieces);
        Assert(result.Count==25 && result[12].Start==12 && result[12].Count==2 && result[13].Start==14,
            "short pair must occupy one beat rather than break the sustained passage");
        Assert(result.All(x=>Math.Abs(x.PressWindow-pulse)<1e-9) &&
            result.Zip(result.Skip(1)).All(z=>z.First.Right!=z.Second.Right), "one hand change per occupied beat");
        Assert(result.SelectMany(s=>Enumerable.Range(s.Start,s.Count)).SequenceEqual(Enumerable.Range(0,notes.Length)),
            "all original attacks must remain ordered");
    }

    internal static void SteadyPulseRebuildsAfterClosedHoldAndKeepsTripletAndSubdivision()
    {
        const double width = .0882329, pulse = .100;
        var notes = new List<TechniqueNote> { new(0, 3980, 1, NoteKind.HoldStart),
            new(.4, 3981, 1, NoteKind.HoldEnd) };
        var slices = new List<TechniquePlanner.Slice> { new(0, 1, width, width, false, 0, width) };
        int attack = 1;
        for (int beat = 0; beat < 12; beat++)
        {
            double time = .400282 + pulse * beat;
            int count = beat == 5 ? 3 : beat == 11 ? 2 : 1;
            for (int k = 0; k < count; k++)
                notes.Add(new TechniqueNote(time + k * (beat == 11 ? .050 : .000282), notes.Count + 3980, 1, NoteKind.Tap));
            slices.Add(new TechniquePlanner.Slice(attack, attack + count, width, width, beat == 11 || (beat / 2) % 2 == 0, time, time + width));
            attack += count;
        }
        var array = notes.ToArray();
        var result = Rebuild(array, slices, width);
        Assert(result[0].Equals(slices[0]), "closed hold row must remain unchanged");
        Assert(result.Count == 14 && result.Skip(1).Take(12).Zip(result.Skip(2).Take(11)).All(p => p.First.Right != p.Second.Right),
            "the entire post-hold pulse must alternate through the last beat");
        Assert(result[6].Count == 3 && result[12].Count == 1 && result[13].Count == 1 &&
            result[13].Right == slices[^1].Right && result[13].TimeStart == array[^1].Time,
            "triplet stays on one beat, while the following subdivision stays separate on its original hand");
        Assert(result.SelectMany(s => Enumerable.Range(s.Start, s.Count)).SequenceEqual(Enumerable.Range(0, attack)),
            "a separated subdivision must neither be dropped nor emitted twice");

        var tail = slices[^1];
        slices[^1] = new TechniquePlanner.Slice(tail.Start, tail.End, tail.Length, tail.PressWindow,
            false, tail.TimeStart, tail.TimeEnd);
        var alternate = Rebuild(array, slices, width);
        Assert(alternate.Count == 14 && !alternate[1].Right && alternate[^2].Right && !alternate[^1].Right,
            "an unsafe preferred phase may be replaced by a safe whole-passage phase");
        Assert(alternate[0].Equals(slices[0]) && alternate[^1].TimeStart == array[^1].Time,
            "a phase change must not touch the hold or the following subdivision");
    }

    internal static void LocalPulsesTrackTempoWithRestsWithoutSwallowingSubdivisions()
    {
        double[] first = [0, .1, .2, .4, .5, .6, .8, .9];
        const double second = .09375;
        var times = first.Concat(Enumerable.Range(0, 8).Select(i => 1.2 + i * second))
            .Concat(new[] {1.899, 1.94, 1.981}).ToArray();
        var notes = Notes(times);
        var original = times.Select((time,i) => new TechniquePlanner.Slice(i,i+1,.0882329,.0882329,true,time,
            i+1 < times.Length ? Math.Min(time+.0882329,times[i+1]) : time+.0882329)).ToList();
        var result = Rebuild(notes, original);
        Assert(result.Count == times.Length && result.All(s => s.Count == 1), "assign every tap exactly once");
        Assert(result.Take(8).All(s => Math.Abs(s.PressWindow-.1)<1e-9) &&
            result.Skip(8).Take(8).All(s => Math.Abs(s.PressWindow-second)<1e-9), "fit the two local pulse widths independently");
        Assert(result[2].Right == result[3].Right && result[3].Right != result[4].Right &&
            result[2].TimeEnd < result[3].TimeStart, "the empty cell advances phase without extending the previous occupied cell");
        Assert(result.Skip(16).SequenceEqual(original.Skip(16)), "off-grid subdivisions must keep their original slices");
    }

    internal static void RebuiltPulseCarriesBeatParityAndKeepsExitPair()
    {
        const double width = .0882329;
        foreach (bool rest in new[] {false, true})
        {
            // The measured clock shifts just enough to restart recognition; the
            // first new onset is still one (or two) beats after the last old one.
            double offset = rest ? .1 : 0;
            double[] times = [0,.1,.2,.3,.4,.5003,.6006+offset,.7006+offset,.8006+offset,
                .9006+offset,1.0006+offset,1.1006+offset,1.2006+offset,1.2009+offset,1.2356+offset];
            var notes = Notes(times);
            var baseline = new List<TechniquePlanner.Slice>();
            for (int i = 0; i < times.Length;)
            {
                int end = i + (i == 12 ? 2 : 1);
                bool right = i >= 6 && !rest;
                baseline.Add(new(i, end, width, width, right, times[i],
                    Math.Min(times[i] + width, end < times.Length ? times[end] : double.MaxValue)));
                i = end;
            }
            var result = Rebuild(notes, baseline);
            Assert((result[5].Right == result[6].Right) == rest, "confirmed seam must count the occupied or empty beat");
            Assert(result.Skip(6).Take(5).Zip(result.Skip(7).Take(5)).All(z => z.First.Right != z.Second.Right),
                "the following complete beats must not inherit the old grid phase");
            Assert(result[^2].Equals(baseline[^2]) && result[^1].Equals(baseline[^1]),
                "the original exit pair and following subdivision must remain unchanged");
        }
    }

    internal static void SteadyMiddlePassageDoesNotChangeSurroundingHands()
    {
        const double pulse=.1081081;
        var times=new[]{0.0,.072}.Concat(Enumerable.Range(0,10).Select(i=>.24+i*pulse)).Concat(new[]{1.5,1.62});
        var notes=Notes(times.ToArray());
        var old=notes.Select((n,i)=>new TechniquePlanner.Slice(i,i+1,.0882329,.0882329,(i/2)%2==0,n.Time,
            Math.Min(n.Time+.0882329, i+1<notes.Length?notes[i+1].Time:double.MaxValue))).ToList();
        var result=Rebuild(notes,old);
        Assert(result.Take(2).SequenceEqual(old.Take(2)) && result.Skip(12).SequenceEqual(old.Skip(12)),
            "an interior pulse correction must not rephase unrelated slices");
        var middle=result.Skip(2).Take(10).ToArray();
        Assert(middle.Length==10 && middle.All(s=>s.Count==1 && Math.Abs(s.PressWindow-pulse)<1e-9) &&
            middle.Zip(middle.Skip(1)).All(z=>z.First.Right!=z.Second.Right), "alternate through the detected interior pulse");
    }

    internal static void SteadyPulsePreservesAlignedMixedBeatsAndRejectsHoldOrDrift()
    {
        const double width=.0882329;
        var times=Enumerable.Range(0,20).Select(i=>1+i*width).ToList();
        times.Insert(5,times[4]+.002);
        var notes=Notes(times.ToArray());
        var pieces=Enumerable.Range(0,20).Select(i=>new TechniquePlanner.Slice(i+(i>4?1:0),i+1+(i>=4?1:0),
            width,width,i%2==0,1+i*width,1+(i+1)*width)).ToList();
        Assert(Rebuild(notes,pieces).SequenceEqual(pieces), "aligned mixed beats keep their original rows and hand phase");
        var drift=Notes(Enumerable.Range(0,20).Select(i=>i*.136+i*i*.0001).ToArray());
        var simple=drift.Select((n,i)=>new TechniquePlanner.Slice(i,i+1,width,width,true,n.Time,n.Time+width)).ToList();
        Assert(Rebuild(drift,simple).SequenceEqual(simple), "drifting onsets are not a fixed pulse");
        var held=Notes(Enumerable.Range(0,20).Select(i=>i*.136).ToArray());
        held[0]=new TechniqueNote(0,0,1,NoteKind.HoldStart);
        Assert(Rebuild(held,simple).SequenceEqual(simple), "do not reanchor taps inside a hold");
    }

    internal static void SteadyPulseBeforeDistantHoldStillAlternates()
    {
        const double pulse = .12578616, folded = .089285272;
        var notes = Notes(Enumerable.Range(0, 12).Select(index => index * pulse).ToArray()).ToList();
        notes.Add(new(4.4, 12, 1, NoteKind.HoldStart));
        notes.Add(new(7.2, 13, 1, NoteKind.HoldEnd));
        var oldHands = new[] { false, false, true, true, false, true, true, false, false, true, false, false };
        var baseline = notes.Take(13).Select((note, index) => new TechniquePlanner.Slice(index, index + 1,
            folded, folded, index == 12 || oldHands[index], note.Time, note.Time + folded)).ToList();
        var rebuilt = Rebuild(notes.ToArray(), baseline, folded);
        Assert(rebuilt.Count == baseline.Count && rebuilt[^1].Equals(baseline[^1]), "the next hold and attack count must stay intact");
        Assert(rebuilt.Take(11).Zip(rebuilt.Skip(1)).All(pair => pair.First.Right != pair.Second.Right),
            "the unrelated hold must not prevent 12 measured beats from alternating");
    }

    internal static void RestAfterHoldUsesPreferredHand()
    {
        const double pulse = .059523808;
        var profile = Profile();
        foreach (bool preferredRight in new[] { true, false })
        {
            var notes = new List<TechniqueNote> { new(0, 0, 1, NoteKind.HoldStart), new(2.7, 1, 1, NoteKind.HoldEnd) };
            double[] offsets = [0, pulse, 2 * pulse, 3 * pulse, 4 * pulse, 6 * pulse, 8 * pulse, 8 * pulse + .004960318,
                8.5 * pulse, 9 * pulse, 9.5 * pulse, 10 * pulse, 10 * pulse + .004960318, 10.5 * pulse, 11 * pulse];
            notes.AddRange(offsets.Select((offset, index) => new TechniqueNote(6 + offset, index + 2, 1, NoteKind.Tap)));
            var attacks = notes.Select((note, index) => (note, index)).Where(item => item.note.Kind != NoteKind.HoldEnd)
                .Select(item => new TechniquePlanner.Attack(item.index, profile, pulse)).ToList();
            var rows = new List<TechniquePlanner.Slice> { new(0, 1, pulse, pulse, preferredRight, 0, pulse) };
            int[] starts = [1, 2, 3, 4, 5, 6, 7, 9, 12, 16];
            for (int index = 0; index + 1 < starts.Length; index++)
            {
                int start = starts[index], end = starts[index + 1];
                double onset = notes[attacks[start].Index].Time;
                double boundary = end < attacks.Count ? notes[attacks[end].Index].Time : onset + 2 * pulse;
                rows.Add(new(start, end, pulse, pulse, preferredRight ^ (index % 2 == 0), onset, Math.Min(boundary, onset + pulse)));
            }
            var original = rows.ToArray();
            TechniquePlanner.PreferPostHoldLead(notes.ToArray(), attacks, rows, preferredRight);
            Assert(rows[1].Right == preferredRight && rows.Skip(1).Take(4).Zip(rows.Skip(2)).All(pair => pair.First.Right != pair.Second.Right),
                "the first five beats should alternate from the configured hand after a closed hold and a long rest");
            Assert(rows[0].Equals(original[0]) && rows.Skip(6).SequenceEqual(original.Skip(6)),
                "the hold, empty beat and downstream hand phase must not be rewritten");
        }
    }

    internal static void CompactSeamBeatAlternatesBetweenMeasuredRuns()
    {
        const double pulse = .13953488, folded = .089285272;
        var notes = Notes(0, pulse, 2 * pulse, 3 * pulse - .00279, 3 * pulse,
            4 * pulse, 5 * pulse, 6 * pulse);
        var profile = Profile();
        var attacks = notes.Select((note, index) => new TechniquePlanner.Attack(index, profile, folded)).ToList();
        var rows = new List<TechniquePlanner.Slice>();
        foreach (var (start, end, right) in new[] { (0, 1, false), (1, 2, true), (2, 3, false),
            (3, 5, false), (5, 6, false), (6, 7, true), (7, 8, false) })
        {
            double width = end - start == 2 ? folded : pulse;
            rows.Add(new(start, end, width, width, right, notes[start].Time, notes[start].Time + width));
        }
        var original = rows.ToArray();
        TechniquePlanner.RepairMeasuredSeamBeat(notes, attacks, rows);
        Assert(rows[3].Right && rows[3].Count == 2 && rows[3].PressWindow == folded &&
            rows.Where((_, index) => index != 3).SequenceEqual(original.Where((_, index) => index != 3)),
            "both measured neighbors should agree on the missing alternating hand without breaking the pair");

        notes[5] = new(notes[5].Time + .03, 5, 1, NoteKind.Tap);
        rows = original.ToList();
        TechniquePlanner.RepairMeasuredSeamBeat(notes, attacks, rows);
        Assert(rows.SequenceEqual(original), "an off-clock exit must not force the neighboring hands to alternate");
    }

    internal static void LocalPhrasesKeepDonorFragmentsAtConfirmedEdges()
    {
        const double pulse = .0625, folded = .112;
        double[] times = [.02, .125, .128, .1875, .1905, .25, .253, .3125, .3155,
            .375, .378, .4375, .4375 + pulse / 4 + 2e-10, .54];
        var notes = Notes(times);
        var profile = Profile();
        var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
        List<TechniquePlanner.Slice> old = [new(0,3,.15,folded,false,.02,.17),
            new(3,7,.13,folded,true,.17,.30), new(7,11,.13,folded,false,.30,.43),
            new(11,14,.13,folded,true,.43,.56)];
        var result = TechniquePlanner.RefineLocalPhrases(notes,attacks,old);
        Assert(result.Count==8 && result[0].Start==0 && result[0].End==1 &&
            result[^1].Start==13 && result[^1].End==14, "preserve both donor fragments around a confirmed pulse");
        Assert(result.Skip(1).Take(6).Select(s=>s.Start).SequenceEqual(new[]{1,3,5,7,9,11}) &&
            result.Skip(1).Take(6).All(s=>s.Count==2 && Math.Abs(s.PressWindow-pulse)<1e-9),
            "each beat stays intact, including the final quarter-pulse double");
        Assert(result.Skip(1).Take(5).Zip(result.Skip(2).Take(5)).All(p=>p.First.Right!=p.Second.Right),
            "keep one alternating phase through the recut phrase");
        Assert(result.SelectMany(s=>Enumerable.Range(s.Start,s.Count)).SequenceEqual(Enumerable.Range(0,times.Length)),
            "each original attack must belong to exactly one row");
    }

    internal static void LocalEvidenceAcceptsOneConfirmedAccent()
    {
        const double pulse = .088105728849591, folded = .112;
        var times = Enumerable.Range(0, 8).Select(i => i * pulse)
            .Concat(new[] { 7 * pulse + .0055 }).ToArray();
        var notes = Notes(times);
        var profile = Profile();
        var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
        var baseline = new List<TechniquePlanner.Slice> {
            new(0,2,folded,folded,false,times[0],times[2]), new(2,4,folded,folded,true,times[2],times[4]),
            new(4,6,folded,folded,false,times[4],times[6]), new(6,9,folded,folded,true,times[6],times[^1]+.06) };
        var result = TechniquePlanner.RefineLocalPhrases(notes,attacks,baseline);
        Assert(result.Select(s=>s.Count).SequenceEqual(new[]{1,1,1,1,1,1,1,2}),
            "one confirmed short accent at the exit must not hide the preceding measured beats");
    }

    internal static void LocalEvidenceAcceptsMeasuredTempoBoundary()
    {
        const double first = .0627615, second = .0836820, folded = .112;
        var times = Enumerable.Range(0, 6).Select(i => i * first)
            .Concat(Enumerable.Range(0, 8).Select(i => 5 * first + (i + 1) * second)).ToArray();
        var notes = Notes(times);
        var profile = Profile();
        var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
        var baseline = Enumerable.Range(0, 7).Select(i => new TechniquePlanner.Slice(
            i*2, Math.Min(i*2+2, notes.Length), folded, folded, i%2==0,
            times[Math.Min(i*2, times.Length-1)], times[Math.Min(i*2, times.Length-1)] + folded)).ToList();
        var result = TechniquePlanner.RefineLocalPhrases(notes,attacks,baseline);
        Assert(result.Count == notes.Length && result.All(s=>s.Count==1),
            "a sustained measured pulse after a real tempo boundary must not remain folded");
        Assert(result.Take(6).All(s=>Math.Abs(s.PressWindow-first)<1e-9) &&
               result.Skip(6).All(s=>Math.Abs(s.PressWindow-second)<1e-9),
            "the two measured pulse widths must remain local");
    }

    internal static void LocalEvidenceAcceptsSingleAccentInShortRun()
    {
        const double pulse = .0660792951541822, folded = .112;
        var times = new[]{0d,pulse,2*pulse,3*pulse,3*pulse+.0110132,4*pulse,5*pulse,6*pulse};
        var notes = Notes(times);
        var profile = Profile();
        var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
        var baseline = new List<TechniquePlanner.Slice> {
            new(0,2,folded,folded,false,times[0],times[2]), new(2,4,folded,folded,true,times[2],times[4]),
            new(4,6,folded,folded,false,times[4],times[6]), new(6,8,folded,folded,true,times[6],times[^1]+.06) };
        var result = TechniquePlanner.RefineLocalPhrases(notes,attacks,baseline);
        Assert(result.Select(s=>s.Count).SequenceEqual(new[]{1,1,1,2,1,1,1}),
            "one supported short accent in a six-beat run must preserve the main-beat starts");
    }

    internal static void LocalEvidenceRejectsUncorroboratedFastSubdivisions()
    {
        const double pulse = .0625, folded = .125;
        var profile = Profile();
        foreach (bool edgeAccent in new[] { false, true })
        {
            // Uneven entrance and exit are not tempo evidence; neither is a
            // lone accent on the edge of a short, faster-than-nominal stream.
            var times = new List<double> { 0 };
            times.AddRange(Enumerable.Range(0,6).Select(i => .215 + i*pulse));
            if (edgeAccent) times.Add(times[^1] + .010);
            times.Add(.699);
            var notes = Notes(times.ToArray());
            var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
            int[] ends = [1,3,5,times.Count-1,times.Count];
            var baseline = new List<TechniquePlanner.Slice>();
            int start = 0;
            foreach (int end in ends)
            {
                baseline.Add(new(start,end,folded,folded,baseline.Count%2==0,times[start],
                    end < times.Count ? times[end] : times[^1]+folded));
                start = end;
            }
            Assert(TechniquePlanner.RefineLocalPhrases(notes,attacks,baseline).SequenceEqual(baseline),
                "irregular bounds and a lone fast edge accent do not distinguish subdivisions from beats");
        }
    }

    internal static void FoldedLocalPhrasesCountRestsAndCarryThroughTempoSteps()
    {
        double[] starts = [0,.0625,.125,.25,.3125,.375,.445,.515,.585,.655,.725,.795];
        var notes = Notes(starts.SelectMany(t => new[] {t,t+.003}).ToArray());
        var profile = Profile();
        const double folded = .112;
        var attacks = notes.Select((n,i) => new TechniquePlanner.Attack(i,profile,folded)).ToList();
        var baseline = Enumerable.Range(0,6).Select(i => new TechniquePlanner.Slice(i*4,i*4+4,
            folded,folded,i%2==0,starts[i*2],starts[i*2]+folded)).ToList();
        var result = TechniquePlanner.RefineLocalPhrases(notes,attacks,baseline);
        Assert(result.Count == starts.Length && result.All(s => s.Count == 2),
            "two compact bursts must not be combined into one folded row");
        for (int i=0; i<result.Count; i++)
        {
            var row = result[i];
            Assert(row.Start==2*i && Math.Abs(row.PressWindow-(i<6?.0625:.070))<1e-9,
                "use actual burst clocks on both sides of the tempo step");
            if(i>0) Assert((row.Right==result[i-1].Right)==(i==3),
                "an empty cell advances parity; a tempo step does not reset it");
        }

        // The same fast clock without any cluster or rest evidence is ambiguous.
        var singles = Notes(Enumerable.Range(0,12).Select(i=>i*.0625).ToArray());
        var singleAttacks = singles.Select((n,i)=>new TechniquePlanner.Attack(i,profile,folded)).ToList();
        var unaccented = Enumerable.Range(0,6).Select(i=>new TechniquePlanner.Slice(i*2,i*2+2,
            folded,folded,i%2==0,i*.125,i*.125+folded)).ToList();
        Assert(TechniquePlanner.RefineLocalPhrases(singles,singleAttacks,unaccented).SequenceEqual(unaccented),
            "do not reinterpret a uniform fast subdivision stream as an accented phrase");
    }

    internal static void RecurringWideBurstsUseCompleteRowsAndStablePhase()
    {
        const double pulse = .068, nominal = .088;
        int[] sizes = [6,4,4,6,4,4,6,4,4,6,4,4,3,3,1];
        var times = sizes.SelectMany((size,i) => Enumerable.Range(0,size).Select(k => i*pulse+k*.0018)).ToArray();
        var notes = Notes(times);
        var profile = Profile();
        var attacks = notes.Select((n,i)=>new TechniquePlanner.Attack(i,profile,nominal)).ToList();
        var baseline = TechniquePlanner.GroupMetricCells(notes,attacks,true);
        var plan = TechniquePlanner.PlanArticulatedPhrases(notes,attacks,baseline,true);
        Assert(plan.Select(r=>r.Count).SequenceEqual(sizes), "repeated bursts must not merge across their real starts");
        Assert(plan[0].Right && plan.Zip(plan.Skip(1)).All(p=>p.First.Right!=p.Second.Right),
            "alternation must run through the entire 6/4/4 motif and its cadence");
        Assert(plan.All(r=>Math.Abs(r.PressWindow-pulse)<1e-9 && r.TimeStart==times[r.Start]),
            "width and onset come from the actual burst cadence");

        var noMotif = Notes(times.Take(28).ToArray());
        var truncated = attacks.Take(28).ToList();
        var shortBaseline = TechniquePlanner.GroupMetricCells(noMotif,truncated,true);
        Assert(TechniquePlanner.PlanArticulatedPhrases(noMotif,truncated,shortBaseline,true).SequenceEqual(shortBaseline),
            "two motifs alone are insufficient evidence to override the folded grid");
    }

    internal static void MixedPulseUsesConfiguredLeadAcrossABoundary()
    {
        const double firstPulse = .079, secondPulse = .0782;
        const int count = 110, change = 101;
        var times = new List<double>{0};
        var baseline = new List<TechniquePlanner.Slice>{new(0,1,.088,.088,false,0,.088)};
        for (int i=0;i<count;i++)
        {
            double t = .16 + i*firstPulse + Math.Max(0,i-change)*(secondPulse-firstPulse);
            int size = i==0 ? 3 : i%4==0 ? 2 : 1;
            int at = times.Count;
            for(int k=0;k<size;k++) times.Add(t+k*.001);
            double width = i<change ? firstPulse : secondPulse;
            baseline.Add(new(at,times.Count,width,width,i%2!=0,t,t+width));
        }
        var notes=Notes(times.ToArray());
        var profile=Profile();
        var attacks=notes.Select((n,i)=>new TechniquePlanner.Attack(i,profile,.088)).ToList();
        var result=TechniquePlanner.PlanArticulatedPhrases(notes,attacks,baseline,true);
        Assert(result.Count==baseline.Count && result[0].Equals(baseline[0]) && result[1].Right && result[1].Count==3,
            "the first accented beat must use the configured right hand, not the prior grid phase");
        Assert(result.Skip(1).Zip(result.Skip(2)).All(p=>p.First.Right!=p.Second.Right),
            "small local tempo changes cannot flip the lead in a sustained phrase");
        Assert(TechniquePlanner.PlanArticulatedPhrases(notes,attacks,baseline,false)[1].Right==false,
            "preferred hand is a configuration, not a right-hand special case");
    }

    internal static void ActiveHoldAndInsufficientCapacityPreventSwitch()
    {
        TechniqueNote[] withHold = [new(0,0,1,NoteKind.HoldStart),new(.02,1,1,NoteKind.Tap),
            new(.04,2,1,NoteKind.Tap),new(.05,3,1,NoteKind.Tap),new(.07,4,1,NoteKind.Tap),new(.20,5,1,NoteKind.HoldEnd)];
        List<TechniquePlanner.Slice> pieces = [new(0,1,.02,.02,false,0,.02),
            new(1,3,.03,.03,true,.02,.05),new(3,5,.075,.075,true,.05,.125)];
        Repair(withHold,pieces);
        Assert(pieces[1].Right && pieces[2].Right, "do not estimate a tap row while a real hold is active");

        var profile = new TechniqueProfile(400, new HandLayout([82], [[0]], [.8]), Profile().Layout(true));
        List<TechniquePlanner.Slice> limited=[new(0,2,.04,.04,true,0,.04),new(2,4,.075,.075,true,.04,.115)];
        Repair(Notes(0,.02,.04,.06),limited,profile);
        Assert(limited[1].Right, "a repair cannot exceed the opposite hand's capacity");
    }
}
