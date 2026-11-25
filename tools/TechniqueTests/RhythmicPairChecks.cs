using ADOFAIMacro.Technique;

internal static class RhythmicPairChecks
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static HandLayout Hand(byte[] keys) => new(keys,
        [[0], [1,0], [2,1,0], [2,1,0,4], [3,2,1,0,4], [3,2,1,0,5,4], [3,2,1,0,6,5,4], [3,2,1,0,7,6,5,4]],
        [.8,1,1.2,1.4,.8,1,1.2,1.4]);

    private static TechniqueProfile Profile(double limit = 400) => new(limit,
        Hand([82,51,50,81,32,86,162,160]), Hand([80,187,8,46,165,190,93,163]));

    // Source-floor/time inputs copied from the actual 6534c944 trace; no angleData index assumption.
    private static TechniqueNote[] AtohsExcerpt() =>
    [
        new(49.6923079803654, 690, 4, NoteKind.Tap),
        new(49.705128489993385, 691, 4, NoteKind.Tap),
        new(49.717949005094276, 692, 4, NoteKind.Tap),
        new(49.76923105728848, 693, 4, NoteKind.Tap),
        new(49.84615413421156, 694, 4, NoteKind.Tap),
        new(49.851852142139265, 695, 4.5, NoteKind.Tap),
        new(49.92307721038464, 697, 4.5, NoteKind.Tap),
        new(49.928775218312346, 698, 4.5, NoteKind.Tap),
        new(50.00000028690231, 700, 4.5, NoteKind.Tap),
        new(50.00569829483002, 701, 4.5, NoteKind.Tap),
        new(50.07692336376458, 703, 4.5, NoteKind.Tap),
        new(50.082621371692284, 704, 4.5, NoteKind.Tap),
        new(50.15384644032279, 706, 4.5, NoteKind.Tap),
        new(50.15954444825049, 707, 4.5, NoteKind.Tap),
        new(50.2307695204688, 709, 4.5, NoteKind.Tap),
        new(50.2364675283965, 710, 4.5, NoteKind.Tap),
        new(50.30769259706755, 712, 4.5, NoteKind.Tap),
        new(50.313390604995256, 713, 4.5, NoteKind.Tap),
        new(50.38461567011905, 715, 4.5, NoteKind.Tap),
        new(50.39031367804675, 716, 4.5, NoteKind.Tap),
        new(50.461538750001544, 718, 4.5, NoteKind.Tap),
        new(50.48717978020333, 719, 2, NoteKind.Tap),
        new(50.6153849093206, 720, 2, NoteKind.Tap),
        new(50.76923106316676, 721, 2, NoteKind.Tap),
        new(50.92307721701291, 722, 2, NoteKind.Tap),
        new(50.94871824721469, 723, 2, NoteKind.Tap),
        new(51.07692337633197, 724, 2, NoteKind.Tap),
        new(51.10256440653375, 725, 2, NoteKind.Tap),
        new(51.23076953565103, 726, 2, NoteKind.Tap),
        new(51.30769261531056, 727, 2, NoteKind.Tap),
        new(51.38461569497009, 728, 2, NoteKind.Tap),
        new(51.41025672517187, 729, 2, NoteKind.Tap),
        new(51.538461854289146, 730, 2, NoteKind.Tap),
        new(51.6923080081353, 731, 2, NoteKind.Tap),
        new(51.71794903833708, 732, 2, NoteKind.Tap),
        new(51.84615416745436, 733, 2, NoteKind.Tap),
        new(52.00000032130051, 734, 2, NoteKind.Tap),
        new(52.153846475146665, 735, 2, NoteKind.Tap),
        new(52.17948750534845, 736, 2, NoteKind.Tap),
        new(52.307692634465724, 737, 2, NoteKind.Tap),
        new(52.33333366466751, 738, 2, NoteKind.Tap),
        new(52.46153879378478, 739, 2, NoteKind.Tap),
        new(52.615384947630936, 740, 2, NoteKind.Tap),
        new(52.64102597783272, 741, 2, NoteKind.Tap),
        new(52.769231106949995, 742, 2, NoteKind.Tap),
        new(52.79487213715178, 743, 2, NoteKind.Tap),
        new(52.84615419208244, 744, 2, NoteKind.Tap),
        new(52.87179522228422, 745, 2, NoteKind.Tap),
        new(52.92307727721488, 746, 2, NoteKind.Tap),
        new(52.94871830741666, 747, 2, NoteKind.Tap),
        new(53.07692343653394, 748, 2, NoteKind.Tap),
        new(53.23076959038009, 749, 2, NoteKind.Tap),
        new(53.384615744226245, 750, 2, NoteKind.Tap),
    ];

    private static void PairUsesConfiguredRow(TechniqueAssignment a, TechniqueAssignment b)
    {
        Check(a.Hand == b.Hand, $"rhythmic pair {a.Floor}/{b.Floor} still splits across hands");
        byte[] expected = a.Hand == TechniqueHand.Right ? [187,80] : [51,82];
        Check(a.Key == expected[0] && b.Key == expected[1],
            $"pair {a.Floor}/{b.Floor} must use its configured two-key row, not deeper keys or R/P");
    }

    internal static void Atohs695UsesEightAlternatingTwoKeyPieces()
    {
        var notes=AtohsExcerpt(); var profile=Profile();
        foreach (bool startRight in new[]{false,true})
        {
            var plan=TechniquePlanner.Build(notes,195,1,startRight,_=>profile);
            var a=plan.Assignments.ToDictionary(a=>a.Floor);
            TechniqueHand? previous=null;
            for (int f=694; f<=715; f+=3)
            {
                PairUsesConfiguredRow(a[f],a[f+1]);
                Check(previous!=a[f].Hand, "695 phrase must alternate complete pairs, not concatenate two pairs into four/eight-key pieces");
                previous=a[f].Hand;
            }
            Check(plan.Events.Count(e=>!e.Release)==notes.Length&&plan.Events.Count(e=>e.Release)==notes.Length,
                "ATOHS phrase lost a judgment attack or a matching release");
        }
    }

    internal static void Atohs719AndFollowing25msPairsKeepWholeRows()
    {
        var notes=AtohsExcerpt();var profile=Profile();
        var plan=TechniquePlanner.Build(notes,195,1,true,_=>profile);
        var a=plan.Assignments.ToDictionary(a=>a.Floor);
        foreach (int f in new[]{718,722,724,728,731,735,737,740,742,744,746}) PairUsesConfiguredRow(a[f],a[f+1]);
        int[] pairs = [718,722,724,728,731,735,737,740,742,744,746];
        // This excerpt includes singles between some pairs. Only consecutive pairs share phase.
        for (int i = 1; i < pairs.Length; i++)
            if (pairs[i] == pairs[i - 1] + 2)
                Check(a[pairs[i - 1]].Hand != a[pairs[i]].Hand,
                    $"consecutive pairs did not alternate at {pairs[i - 1]} and {pairs[i]}");
    }

    internal static void AtohsLaterPairsUseConfiguredRowsWithoutFastRepresses()
    {
        // Consecutive source floors 751–850 from the actual revision 7 checkpoint trace.
        double[] times =
        [
            53.41025677442803, 53.46153882935869, 53.48717985956047, 53.53846191449113,
            53.564102933747094, 53.69230805191856, 53.71794907117452, 53.76923112610518,
            53.84615420576471, 53.871795235966495, 54.00000036508377, 54.153846518929925,
            54.17948754913171, 54.30769267824898, 54.46153883209514, 54.61538498594129,
            54.64102601614307, 54.76923114526035, 54.848558068251755, 54.92788499124316,
            55.00721191423457, 55.08653883722597, 55.16586576021738, 55.245192683208785,
            55.32451960620019, 55.4038465291916, 55.42948755939338, 55.557692688510656,
            55.71153884235681, 55.86538499620296, 55.891026026404745, 55.942308081335405,
            55.96794911153719, 56.01923116646785, 56.04487219666963, 56.09615425160029,
            56.25000040544644, 56.27564142470241, 56.32692347963307, 56.48076963347922,
            56.506410663681, 56.557692707665844, 56.63461578732537, 56.660256817527156,
            56.78846194664443, 56.942308100490585, 57.09615425433674, 57.12179528453852,
            57.2500004136558, 57.27564144385758, 57.40384657297486, 57.480769652634386,
            57.557692732293916, 57.5833337624957, 57.63461580648054, 57.71153888614007,
            57.7884619657996, 57.86538504545913, 57.89102607566091, 58.01923120477819,
            58.17307735862434, 58.32692351247049, 58.352564542672276, 58.40384658665712,
            58.48076966631665, 58.50641069651843, 58.55769274050327, 58.711538894349424,
            58.73717992455121, 58.78846196853605, 58.9423081223822, 58.967949141638165,
            59.019231196568825, 59.096154276228354, 59.12179529548432, 59.17307735041498,
            59.32692350426113, 59.352564523517096, 59.403846578447755, 59.55769273229391,
            59.58333376249569, 59.63461580648053, 59.71153888614006, 59.737179916341844,
            59.86538504545912, 60.019231199305274, 60.04487221856124, 60.0961542734919,
            60.326923501524675, 60.339744016625566, 60.403846581184204, 60.480769658107285,
            60.493590173208176, 60.557692737766814, 60.570513252867705, 60.634615817426344,
            60.647436332527235, 60.71153889708587, 60.78846197400895, 60.801282489109845,
        ];
        var notes = AtohsExcerpt().Concat(times.Select((time, i) =>
        {
            int floor = i + 751;
            double speed = floor is >= 769 and < 777 or >= 840 ? 4 : 2;
            return new TechniqueNote(time, floor, speed, NoteKind.Tap);
        })).ToArray();
        var profile = Profile();
        foreach (bool startRight in new[] { false, true })
        {
            var plan = TechniquePlanner.Build(notes, 195, 1, startRight, _ => profile);
            var assignments = plan.Assignments.ToDictionary(a => a.Floor);
            int[] splitInRevision7 = [756,766,787,790,803,812,815,818,821,824,827,830,836];
            foreach (int floor in splitInRevision7)
                PairUsesConfiguredRow(assignments[floor], assignments[floor + 1]);

            var last = new Dictionary<byte, TechniqueAssignment>();
            foreach (var a in plan.Assignments)
            {
                Check(a.Floor < 751 || !last.TryGetValue(a.Key, out var prev) || a.Time - prev.Time >= 0.075,
                    $"pair repair caused a fast same-key repeat at {a.Floor}");
                last[a.Key] = a;
            }
        }
    }

    internal static void AtohsEarlyPairsSeparatedBySinglesPreserveHands()
    {
        // Actual source-floor onsets (57–157) and the revision 8 key stream (58–151).
        double[] times =
        [
            11.076922962721742, 11.102563992923525, 11.230769122040805, 11.384615275886958,
            11.538461429733111, 11.564102459934894, 11.692307589052174, 11.846153742898327,
            11.99999989674448, 12.025640926946263, 12.153846056063543, 12.230769135723074,
            12.307692215382605, 12.333333245584388, 12.461538374701668, 12.615384528547821,
            12.769230682393975, 12.794871712595757, 12.923076841713037, 13.07692299555919,
            13.230769149405344, 13.256410179607126, 13.384615308724406, 13.461538388383937,
            13.538461468043469, 13.564102498245251, 13.692307627362531, 13.846153781208685,
            13.999999935054838, 14.02564096525662, 14.1538460943739, 14.307692248220054,
            14.461538402066207, 14.48717943226799, 14.61538456138527, 14.6923076410448,
            14.769230720704332, 14.794871750906115, 14.923076880023395, 15.076923033869548,
            15.230769187715701, 15.256410217917484, 15.384615347034764, 15.410256377236546,
            15.538461506353826, 15.615384586013358, 15.692307665672889, 15.717948695874671,
            15.769230739859513, 15.846153819519044, 15.923076899178575, 15.999999978838106,
            16.025641009039887, 16.153846138157167, 16.30769229200332, 16.461538445849474,
            16.487179476051256, 16.615384605168536, 16.76923075901469, 16.923076912860843,
            16.948717943062626, 17.076923072179905, 17.153846151839435, 17.230769231498964,
            17.256410261700747, 17.384615390818027, 17.53846154466418, 17.692307698510334,
            17.717948728712116, 17.846153857829396, 18.00000001167555, 18.153846165521703,
            18.179487195723485, 18.307692324840765, 18.384615404500295, 18.461538484159824,
            18.487179514361607, 18.615384643478887, 18.76923079732504, 18.923076951171193,
            19.076923105017347, 19.10256413521913, 19.23076926433641, 19.384615418182563,
            19.410256448384345, 19.538461577501625, 19.615384657161155, 19.692307736820684,
            19.717948767022467, 19.846153896139747, 20.0000000499859, 20.025641080187683,
            20.153846209304962, 20.307692363151116, 20.320512878252007, 20.46153851699727,
            20.538461593920346, 20.615384670843422, 20.628205185944314, 20.692307750502952,
            20.76923082742603,
        ];
        byte[] expected =
        [
            80, 80, 80, 187, 80, 80, 80, 187, 80, 80, 82, 187, 80, 80, 80, 187, 80,
            80, 80, 187, 80, 80, 82, 187, 80, 80, 80, 187, 80, 80, 80, 187, 80, 80,
            82, 187, 80, 80, 80, 187, 80, 51, 82, 80, 82, 187, 80, 82, 80, 82, 187,
            80, 80, 80, 187, 80, 80, 80, 187, 80, 80, 82, 187, 80, 80, 80, 187, 80,
            80, 80, 187, 80, 80, 82, 187, 80, 80, 80, 80, 187, 80, 80, 187, 80, 80,
            82, 187, 80, 80, 187, 80, 80, 187, 80,
        ];
        var notes = times.Select((time, i) => new TechniqueNote(time, i + 57,
            i == 0 ? 1 : i + 57 < 151 ? 2 : 4, NoteKind.Tap)).ToArray();
        var profile = Profile();
        var plan = TechniquePlanner.Build(notes, 195, 1, true, _ => profile);
        foreach (var a in plan.Assignments.Where(a => a.Floor is >= 58 and <= 151))
            Check(a.Key == expected[a.Floor - 58], $"early ATOHS hand or key changed at {a.Floor}");
        Check(plan.Assignments.Count(a => a.Floor is >= 58 and <= 151) == expected.Length,
            "early ATOHS omitted a judgment point");
    }

    internal static void EquivalentAtohsAngleAndSpeedEncodingsGiveIdenticalPieces()
    {
        const double bpm = 195;
        var original = AtohsExcerpt();
        var alternate = new TechniqueNote[original.Length];
        alternate[0] = original[0];
        for (int i = 1; i < original.Length; i++)
        {
            var source = original[i - 1];
            var target = original[i];
            double alternateSpeed = source.Floor is >= 695 and < 719 ? 4 : source.Speed;
            double gap = target.Time - source.Time;
            // Re-encode the effective angular sweep at a compensating SetSpeed.
            double originalSweep = gap * (3 * bpm * source.Speed);
            double alternateSweep = originalSweep * (alternateSpeed / source.Speed);
            double decoded = alternate[i - 1].Time + alternateSweep / (3 * bpm * alternateSpeed);
            Check(Math.Abs(decoded - target.Time) < 1e-10, "alternate encoding must preserve the judgment time");
            // Match the observed engine time after checking the mathematical equivalence.
            alternate[i] = new TechniqueNote(target.Time, target.Floor,
                target.Floor is >= 695 and < 719 ? 4 : target.Speed, target.Kind);
        }
        Check(original.Zip(alternate).Any(p => p.First.Speed != p.Second.Speed), "fixture must change the tempo metadata");
        var strategy = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.BeatGridStrategyId, 6.5);
        var profile = Profile();
        var attacks = original.Select((n, i) => new TechniquePlanner.Attack(i, profile, .25)).ToList();
        foreach (bool startRight in new[] { false, true })
        {
            var originalPieces = strategy.Group(original, attacks, startRight);
            var alternatePieces = strategy.Group(alternate, attacks, startRight);
            Check(originalPieces.SequenceEqual(alternatePieces), "equivalent encodings must retain every piece boundary and hand");
            var a = TechniquePlanner.Build(original, bpm, 1, startRight, _ => profile, strategy);
            var b = TechniquePlanner.Build(alternate, bpm, 1, startRight, _ => profile, strategy);
            Check(a.Pieces == b.Pieces && a.Assignments.SequenceEqual(b.Assignments) && a.Events.SequenceEqual(b.Events),
                "equal ATOHS onsets must keep the same piece count, hand order, key rows and releases");
            var byFloor = a.Assignments.ToDictionary(x => x.Floor);
            TechniqueHand? previous = null;
            foreach (int floor in new[] { 694, 697, 700, 703, 706, 709, 712, 715, 718 })
            {
                var first = byFloor[floor];
                PairUsesConfiguredRow(first, byFloor[floor + 1]);
                Check(first.Hand != previous, $"continuous ATOHS pairs must alternate at {floor}");
                previous = first.Hand;
            }
        }
        Check(!strategy.UsesTempoInputs, "beat-grid must not consume a second clock from raw BPM metadata");
    }

    internal static void RhythmicPairShapeSurvivesTempoAndEpochScaling()
    {
        foreach (double scale in new[]{.5,1.0,2.0})
        foreach (double epoch in new[]{0.0,49.0,10000.0})
        {
            double pulse=30.0/390*scale, gap=5.698007928e-3*scale;
            var notes=Enumerable.Range(0,16).Select(i=>new TechniqueNote(epoch+i/2*pulse+i%2*gap,i,4.5,NoteKind.Tap)).ToArray();
            var profile=Profile(400/scale);
            var plan=TechniquePlanner.Build(notes,195/scale,1,true,_=>profile);
            for(int i=0;i<notes.Length;i+=2)
            {
                PairUsesConfiguredRow(plan.Assignments[i],plan.Assignments[i+1]);
                if(i>0) Check(plan.Assignments[i].Hand!=plan.Assignments[i-2].Hand,"scaled pair pulse lost hand phase");
            }
        }
    }

    internal static void SlowRhythmicPairsAdvanceOccupiedPulsePhase()
    {
        var profile=Profile();
        var notes=Enumerable.Range(0,12).Select(i=>new TechniqueNote(17+i/2*60.0/390+i%2*60.0/390/6,i,2,NoteKind.Tap)).ToArray();
        var plan=TechniquePlanner.Build(notes,195,1,false,_=>profile);
        for(int i=0;i<notes.Length;i+=2)
        {
            PairUsesConfiguredRow(plan.Assignments[i],plan.Assignments[i+1]);
            if(i>0) Check(plan.Assignments[i].Hand!=plan.Assignments[i-2].Hand,
                "an empty half-cell inside a repeated two-note pulse must not reset every pulse to one hand");
        }
    }

    internal static void RhythmicRefinementDoesNotTreatUniformStreamAsPairs()
    {
        var profile=Profile();
        var notes=Enumerable.Range(0,16).Select(i=>new TechniqueNote(i*30.0/390/3,i,2,NoteKind.Tap)).ToArray();
        var plan=TechniquePlanner.Build(notes,195,1,true,_=>profile);
        Check(plan.Pieces==6&&plan.Assignments.Take(3).All(a=>a.Hand==TechniqueHand.Right),
            "uniform 25.6ms stream must retain grid grouping; adjacency alone is not a pseudo-double");
    }

    internal static void RhythmicPairRefinementRespectsOneKeyCapacity()
    {
        var notes=Enumerable.Range(0,8).Select(i=>new TechniqueNote(i/2*60.0/390+i%2*60.0/390/6,i,2,NoteKind.Tap)).ToArray();
        var profile=new TechniqueProfile(400,new([82],[[0]],[.8]),new([80],[[0]],[.8]));
        var plan=TechniquePlanner.Build(notes,195,1,true,_=>profile);
        for(int i=0;i<notes.Length;i+=2) Check(plan.Assignments[i].Hand!=plan.Assignments[i+1].Hand,
            "a rhythmic pair cannot fit a one-physical-key hand");
    }
}
