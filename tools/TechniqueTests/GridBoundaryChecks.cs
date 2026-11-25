using ADOFAIMacro.Technique;

internal static class GridBoundaryChecks
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

    private static TechniqueNote[] ReplayBoundaryNotes() =>
    [
        new(139.32353040296826,2137,8,NoteKind.Tap),
        new(139.3308833454526,2138,8,NoteKind.Tap),
        new(139.4117656970859,2139,8,NoteKind.Tap),
        new(139.4137264821669,2140,15,NoteKind.Tap),
        new(139.45588334424934,2141,15,NoteKind.Tap),
        new(139.45784412933034,2142,15,NoteKind.Tap),
        new(139.50000099141278,2143,15,NoteKind.Tap),
        new(139.50196177649377,2144,15,NoteKind.Tap),
        new(139.5441186385762,2145,15,NoteKind.Tap),
        new(139.5460794236572,2146,15,NoteKind.Tap),
        new(139.58823628573964,2147,15,NoteKind.Tap),
        new(139.59019707082064,2148,15,NoteKind.Tap),
    ];

    private sealed class ExplicitMetricGrid : TechniquePlanner.IGroupingStrategy
    {
        public bool UsesTempoInputs => true;

        public List<TechniquePlanner.Slice> Group(TechniqueNote[] notes,
            List<TechniquePlanner.Attack> attacks, bool startRight) =>
            TechniquePlanner.GroupMetricCells(notes, attacks, startRight);
    }

    // Test the boundary algorithm with explicit cell widths independently of inference.
    internal static TechniquePlan BuildOnExplicitGrid(TechniqueNote[] notes, double bpm, double speed,
        bool startRight, Func<int, TechniqueProfile> resolve) =>
        TechniquePlanner.Build(notes, bpm, speed, startRight, resolve, new ExplicitMetricGrid());

    private static List<TechniquePlanner.Slice> Group(TechniqueNote[] notes, TechniqueProfile profile,
                                                     double bpm = 170, bool startRight = false)
    {
        var attacks = notes.Select((n,i) =>
        {
            double folded = bpm * n.Speed;
            while (folded > profile.BpmLimit) folded *= .5;
            while (folded <= profile.BpmLimit * .5) folded *= 2;
            return new TechniquePlanner.Attack(i,profile,30 / folded);
        }).ToList();
        return TechniquePlanner.GroupMetricCells(notes, attacks, startRight);
    }

    internal static void Replay2140MovesFlamOnlyAndKeepsConfiguredOrder()
    {
        var notes = ReplayBoundaryNotes();
        var profile = Profile();
        var plan = BuildOnExplicitGrid(notes,170,1,false,_ => profile);
        var slices = Group(notes,profile);
        Check(slices.Select(s => s.Count).SequenceEqual(new[] {2,2,4,4}),
            "2140 regression: expected 2+2+4+4, not a split flam or six-key absorption");
        Check(plan.Assignments.Select(a => a.Key).SequenceEqual(new byte[] {51,82,187,80,50,51,82,32,8,187,80,165}),
            "2140 regression: use configured rows, never rotate keys to hide bad grouping");
        Check(plan.Assignments[2].Hand == plan.Assignments[3].Hand &&
              plan.Assignments[3].Hand != plan.Assignments[4].Hand,
            "2139/2140 must share a hand, with the following four notes on the opposite hand");
        Check(plan.Events.Count(e => !e.Release) == notes.Length && plan.Events.Count(e => e.Release) == notes.Length,
            "boundary refinement lost a down or its release");
    }

    internal static void TempoBoundaryWindowsShareAnOrderedBoundary()
    {
        var notes = ReplayBoundaryNotes();
        var slices = Group(notes,Profile());
        for (int i=1;i<slices.Count;i++)
            Check(slices[i-1].TimeEnd <= slices[i].TimeStart + 1e-9,
                "tempo boundary left overlapping nominal slice windows");
        Check(Math.Abs(slices[1].TimeEnd-notes[4].Time) < 1e-9 &&
              Math.Abs(slices[2].TimeStart-notes[4].Time) < 1e-9,
            "moving the flam must move both sides of the shared boundary");
    }

    internal static void PartialTempoCellsAdvancePhaseWithoutRestartingHand()
    {
        TechniqueNote[] notes = [new(0,0,1,NoteKind.Tap),new(.1,1,1.5,NoteKind.Tap),new(.2,2,1.25,NoteKind.Tap)];
        foreach (bool right in new[] {false,true})
        {
            var profile = Profile(120);
            var slices = Group(notes,profile,120,right);
            Check(slices.Select(s => s.Right).SequenceEqual(new[] {right,!right,right}),
                "each closed partial cell advances the inherited phase, not the global preferred hand");
            Check(slices[0].TimeEnd == .1 && slices[1].TimeEnd == .2,
                "a tempo change must close the old partial cell at the change time");
        }
    }

    internal static void TempoChangeCountsEmptyCellParity()
    {
        TechniqueNote[] notes = [new(0,0,1,NoteKind.Tap),new(1,1,1.5,NoteKind.Tap),new(1+1.0/3,2,1.5,NoteKind.Tap)];
        var profile=Profile(120);
        var plan=BuildOnExplicitGrid(notes,120,1,false,_=>profile);
        Check(plan.Assignments.Select(a=>a.Hand).SequenceEqual(new[] {TechniqueHand.Left,TechniqueHand.Left,TechniqueHand.Right}),
            "empty cells before a tempo change must retain their phase parity");
    }

    internal static void SmallIsolatedCrossTempoFlamStaysTogether()
    {
        TechniqueNote[] notes = [new(0,0,8,NoteKind.Tap),new(.002,1,15,NoteKind.Tap)];
        var slices=Group(notes,Profile());
        Check(slices.Count==1 && slices[0].Count==2,"an isolated two-note cross-tempo flam must share a hand");
    }

    internal static void CrossTempoPrefixTripletMovesAsACluster()
    {
        double w=30.0/340;
        TechniqueNote[] notes = [new(0,0,8,NoteKind.Tap),new(w,1,8,NoteKind.Tap),
            new(w+.002,2,15,NoteKind.Tap),new(w+.003,3,15,NoteKind.Tap),
            new(w+.044,4,15,NoteKind.Tap),new(w+.046,5,15,NoteKind.Tap),
            new(w+.088,6,15,NoteKind.Tap),new(w+.090,7,15,NoteKind.Tap)];
        var slices=Group(notes,Profile());
        Check(slices.Select(s=>s.Count).SequenceEqual(new[]{1,3,4}),
            "a boundary triplet must move together; never split its second near-simultaneous edge");
    }

    internal static void UniformCrossTempoStreamIsNotAbsorbed()
    {
        var notes=Enumerable.Range(0,7).Select(i=>new TechniqueNote(i*.002,i,i<3?8:15,NoteKind.Tap)).ToArray();
        var slices=Group(notes,Profile());
        Check(slices.Count==2 && slices[0].Count==3 && slices[1].Count==4,
            "a uniform fast stream is not an isolated boundary cluster and must not be swallowed");
    }

    internal static void CrossTempoRefinementRespectsProfileBoundaries()
    {
        var first=Profile();
        var second=new TechniqueProfile(400,Hand([65,66,67,68,69,70,71,72]),Hand([73,74,75,76,77,78,79,80]));
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(.002,1,15,NoteKind.Tap)];
        var plan=BuildOnExplicitGrid(notes,170,1,false,f=>f==0?first:second);
        Check(plan.Assignments[0].Key==82 && plan.Assignments[1].Key==73,
            "cross-tempo refinement may not cross a different configured profile");
    }

    internal static void Isolated14msPairUsesConfiguredTwoKeyRow()
    {
        double first=2*30.0/340-.005;
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(first,2200,8,NoteKind.Tap),new(first+.014705881,2201,4,NoteKind.Tap),new(.4,2202,4,NoteKind.Tap)];
        var profile=Profile();
        var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        Check(plan.Assignments[1].Hand==plan.Assignments[2].Hand,"isolated 14.7ms pair still splits into R/P");
        Check(plan.Assignments[1].Key==187&&plan.Assignments[2].Key==80,"use the configured right-hand two-key row, not a special R/P remap");
    }

    internal static void IsolatedPairCanLeaveOneNoteInDonor()
    {
        double first=2*30.0/340-.005;
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(first,1,8,NoteKind.Tap),new(first+.014705881,2,8,NoteKind.Tap),new(.25,3,8,NoteKind.Tap)];
        var profile=Profile();
        var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        Check(plan.Assignments[1].Hand==plan.Assignments[2].Hand&&plan.Assignments[2].Hand!=plan.Assignments[3].Hand,
            "1+2 must become 2+1 for the isolated pair; the distant third note must not be swallowed");
        Check(plan.Assignments.Select(a=>a.Key).SequenceEqual(new byte[]{82,187,80,82}),"2+1 regrouping lost configured rows");
    }

    internal static void IsolatedPairBetweenTwoGroupsDoesNotMergeBothGroups()
    {
        double first=2*30.0/340-.005;
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(.1,1,8,NoteKind.Tap),new(first,2,8,NoteKind.Tap),new(first+.014705881,3,8,NoteKind.Tap),new(.25,4,8,NoteKind.Tap)];
        var profile=Profile();
        var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        var slices=Group(notes,Profile());
        Check(plan.Assignments[2].Hand==plan.Assignments[3].Hand,"2+2 boundary pair still split");
        Check(slices.Select(s=>s.Count).SequenceEqual(new[]{1,3,1}),"join only the bounded same-hand prefix plus pair, not both surrounding groups");
        Check(plan.Assignments.Select(a=>a.Key).SequenceEqual(new byte[]{82,8,187,80,82}),
            "a coherent three-note phrase must use the configured three-key row, not P followed by =P");
    }

    internal static void Uniform14msStreamIsNotAnIsolatedPair()
    {
        var notes=Enumerable.Range(0,10).Select(i=>new TechniqueNote(i*30.0/340/6,i,8,NoteKind.Tap)).ToArray();
        var slices=Group(notes,Profile());
        Check(slices.Count==2&&slices[0].Count==6&&slices[1].Count==4,"uniform fast stream must retain grid groups, not become isolated pairs");
    }

    internal static void IsolatedPairRespectsOneKeyHandCapacity()
    {
        double first=2*30.0/340-.005;
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(first,1,8,NoteKind.Tap),new(first+.014705881,2,8,NoteKind.Tap)];
        var profile=new TechniqueProfile(400,new([82],[[0]],[.8]),new([80],[[0]],[.8]));
        var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        Check(plan.Assignments[1].Hand!=plan.Assignments[2].Hand,"one physical key per hand cannot provide a two-key same-hand flam");
        Check(plan.Events.Count(e=>e.Release)==3,"capacity-limited pair lost a release");
    }

    internal static void Isolated14msPairRespectsLayoutBoundary()
    {
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(.0147,1,8,NoteKind.Tap)];
        var first=Profile();var second=new TechniqueProfile(400,Hand([65,66,67,68,69,70,71,72]),Hand([73,74,75,76,77,78,79,80]));
        var plan=BuildOnExplicitGrid(notes,170,1,false,f=>f==0?first:second);
        Check(plan.Assignments[0].Key==82&&plan.Assignments[1].Key==73,"isolated pair crossed a configured layout boundary");
    }

    internal static void TempoHandCorrectionSurvivesPairIsolation()
    {
        TechniqueNote[] notes = [.. ReplayBoundaryNotes(),
            new(139.63235393290307,2149,15,NoteKind.Tap),new(139.63431471798407,2150,15,NoteKind.Tap),
            new(139.6764715800665,2151,15,NoteKind.Tap),new(139.6784323651475,2152,15,NoteKind.Tap),
            new(139.72058922722994,2153,15,NoteKind.Tap),new(139.72255001231093,2154,15,NoteKind.Tap),
            new(139.76470687439337,2155,15,NoteKind.Tap),new(139.77941275585587,2156,4.00000054,NoteKind.Tap),
            new(139.9411774205548,2157,4.00000054,NoteKind.Tap),new(139.9558833020173,2158,4.00000054,NoteKind.Tap),
            new(140.0294126967744,2159,4.00000054,NoteKind.Tap),new(140.0441185782369,2160,4.00000054,NoteKind.Tap)];
        var profile=Profile();var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        var tail=plan.Assignments.Where(a=>a.Floor>=2153).ToArray();
        Check(tail.Select(a=>a.Hand).SequenceEqual(new[]{TechniqueHand.Right,TechniqueHand.Right,
            TechniqueHand.Left,TechniqueHand.Left,TechniqueHand.Right,TechniqueHand.Right,TechniqueHand.Left,TechniqueHand.Left}),
            "pair isolation overwrote the tempo-boundary correction and produced =P =P on one hand");
        Check(tail.Select(a=>a.Key).SequenceEqual(new byte[]{187,80,51,82,187,80,51,82}),
            "corrected pair phrase must keep configured two-key rows");
    }

    internal static void SameGridHandCorrectionSurvivesPairIsolation()
    {
        const double rate=4.00000054;
        TechniqueNote[] notes=[new(139.77941275585587,2156,rate,NoteKind.Tap),
            new(142.10294163457414,2207,rate,NoteKind.Tap),new(142.11764751603664,2208,rate,NoteKind.Tap),
            new(142.14705927582278,2209,rate,NoteKind.Tap),new(142.1617651572853,2210,rate,NoteKind.Tap)];
        var profile=Profile();var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        Check(plan.Assignments.Skip(1).Select(a=>a.Hand).SequenceEqual(new[]{TechniqueHand.Left,TechniqueHand.Left,TechniqueHand.Right,TechniqueHand.Right}),
            "same-grid refinement must keep the adjacent 44ms pair groups on alternate hands");
        Check(plan.Assignments.Skip(1).Select(a=>a.Key).SequenceEqual(new byte[]{51,82,187,80}),
            "do not fix same-hand repetition by rotating configured keys");
    }

    internal static void DistantPrefixIsNotAbsorbedIntoPair()
    {
        double first=2*30.0/340-.005;
        TechniqueNote[] notes=[new(0,0,8,NoteKind.Tap),new(.089,1,8,NoteKind.Tap),
            new(first,2,8,NoteKind.Tap),new(first+.014705881,3,8,NoteKind.Tap)];
        var profile=Profile();var slices=Group(notes,profile);
        Check(slices.Select(s=>s.Count).SequenceEqual(new[]{1,1,2}),
            "a prefix outside one metric window must not be absorbed to disguise repetition");
    }

    internal static void AdjustedSlicesStillUseEachConfiguredRatio()
    {
        var notes=ReplayBoundaryNotes();var profile=Profile();var slices=Group(notes,profile);
        var plan=BuildOnExplicitGrid(notes,170,1,false,_=>profile);
        // The moved two-note right-hand piece uses ratio 1 for '=', and .8 for P.
        for(int i=2;i<=3;i++)
        {
            var a=plan.Assignments[i];var s=slices[1];var next=slices[2];
            double expected=TechniquePlanner.CalculatePieceRelease(a.Time,s.TimeStart,s.PressWindow,s.TimeEnd,
                next.PressWindow,next.TimeEnd,false,false,profile.Layout(true).Ratio(a.KeySlot));
            double up=plan.Events.First(e=>e.Release&&e.Key==a.Key&&e.Time>=a.Time).Time;
            Check(Math.Abs(up-expected)<1e-9,"adjusted slice ignored the configured per-key release ratio");
        }
    }
}
