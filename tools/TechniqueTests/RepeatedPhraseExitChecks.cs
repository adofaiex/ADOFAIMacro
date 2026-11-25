using ADOFAIMacro.Technique;
using static ADOFAIMacro.Technique.TechniquePlanner;

internal static class RepeatedPhraseExitChecks
{
    internal static void AlignsBothPairsByMergingAdjacentRows()
    {
        foreach (var (epoch, floorOffset) in new[] { (0d, 0), (300d, 1000) })
        {
            var (notes, attacks, baseline, runs) = Fixture(epoch, floorOffset);
            var rows = new List<Slice>(baseline);
            AlignRepeatedPhraseExits(notes, attacks, rows, runs);
            var merge = rows.Single(row => row.Start == runs[1].End);
            Check(merge.Right && merge.Count == 6 && merge.PressWindow == .089285272,
                "the repeated phrase must select a right six-key exit on the original nominal window");
            Check(rows.Count == baseline.Count - 1 && merge.TimeEnd == baseline[^2].TimeEnd,
                "the merge must span exactly the two original exit rows");

            foreach (bool lazy in new[] { false, true })
            {
                var before = Build(notes, 195, 1, true, n => attacks.Single(a => notes[a.Index].Floor == n).Profile,
                    new FixedRows(baseline), lazy);
                var after = Build(notes, 195, 1, true, n => attacks.Single(a => notes[a.Index].Floor == n).Profile,
                    new FixedRows(rows), lazy);
                var firstPair = after.Assignments.Skip(runs[1].Start + 2).Take(2).ToArray();
                var secondPair = after.Assignments.Skip(runs[1].Start + 6).Take(2).ToArray();
                Check(firstPair.Concat(secondPair).All(a => a.Hand == TechniqueHand.Left) &&
                    firstPair.Select(a => a.Key).SequenceEqual(new byte[] { 51, 82 }) &&
                    secondPair.Select(a => a.Key).SequenceEqual(new byte[] { 51, 82 }), "both pseudo-chords must align");
                Check(after.Assignments.Skip(merge.Start).Take(6).Select(a => a.Key)
                    .SequenceEqual(new byte[] { 46, 8, 187, 80, 190, 165 }), "preserve configured six-key order");
                Check(Risks(after).IsSubsetOf(Risks(before)), "merge added a fast repeat or short tap");
            }
        }
    }

    internal static void KeepsExistingRowsWhenJointCandidateIsUnsafe()
    {
        foreach (string reason in new[] { "rhythm", "capacity", "alias", "profile", "active hold", "short tap" })
        {
            var (notes, attacks, baseline, runs) = Fixture(0, 0, reason);
            var rows = new List<Slice>(baseline);
            AlignRepeatedPhraseExits(notes, attacks, rows, runs);
            Check(rows.SequenceEqual(baseline), $"joint edit must be rejected for {reason}");
        }
    }

    private static (TechniqueNote[], List<Attack>, List<Slice>, List<(int Start, int End)>) Fixture(
        double epoch, int floorOffset, string reason = "")
    {
        int[][] orders = [[0], [1, 0], [2, 1, 0], [2, 1, 0, 4], [3, 2, 1, 0, 4],
            reason == "alias" ? [3, 2, 1, 0, 4, 4] : [3, 2, 1, 0, 5, 4]];
        double[] ratios = [.8, 1, 1.2, 1.4, reason == "short tap" ? .1 : .8, 1];
        var profile = new TechniqueProfile(400, new([82, 51, 50, 81, 32], orders, ratios),
            new(reason == "capacity" ? [80, 187, 8, 46, 165] : [80, 187, 8, 46, 165, 190], orders, ratios));
        var rows = new List<Slice>();
        var notes = new List<TechniqueNote>();
        var runs = new List<(int Start, int End)>();
        const double pulse = .066079295;
        const double width = .089285272;
        Add(epoch, width, width, true, 0);
        AddRun(epoch + 1, false, true);
        Add(epoch + 2.5, width, width, false, 0, .011);
        AddRun(epoch + 3, true, false);
        double exit = epoch + 3 + 16 * pulse + .03304;
        Add(exit, width, .09912, false, 0, .011013218, .033039651, .044052869);
        Add(exit + .09912, pulse, pulse, true, 0, .011013218);
        Add(exit + .09912 + pulse, pulse, pulse, false, 0);
        if (reason == "active hold")
            notes[0] = new TechniqueNote(notes[0].Time, notes[0].Floor, 1, NoteKind.HoldStart);
        var alternate = new TechniqueProfile(400, profile.Layout(false), profile.Layout(true));
        var attacks = notes.Select((_, i) => new Attack(i,
            reason == "profile" && i == rows[^2].Start ? alternate : profile, width)).ToList();
        return (notes.ToArray(), attacks, rows, runs);

        void AddRun(double start, bool right, bool reference)
        {
            int first = notes.Count;
            int[] beats = [0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16];
            for (int i = 0; i < beats.Length; i++)
            {
                if (i > 0 && beats[i] - beats[i - 1] == 1) right = !right;
                double time = start + beats[i] * pulse;
                if (i == 7 && reason == "rhythm" && reference) time += .008;
                Add(time, pulse, i == beats.Length - 1 ? .03304 : pulse, right,
                    i is 2 or 5 or 9 or 13 or 14 ? [0, .011013218] : [0]);
            }
            if (reference) Add(start + 18 * pulse, pulse, pulse, false, 0);
            runs.Add((first, notes.Count));
        }

        void Add(double start, double nominal, double span, bool right, params double[] offsets)
        {
            int first = notes.Count;
            foreach (double t in offsets) notes.Add(new TechniqueNote(start + t, floorOffset + notes.Count, 1, NoteKind.Tap));
            rows.Add(new Slice(first, notes.Count, nominal, nominal, right, start, start + span));
        }
    }

    private static HashSet<(int From, int To, byte Key)> Risks(TechniquePlan plan)
    {
        var risks = new HashSet<(int, int, byte)>();
        var last = new Dictionary<byte, TechniqueEvent>();
        foreach (var e in plan.Events)
        {
            if (e.Release)
            {
                if (!e.Hold && e.Time - last[e.Key].Time < .0167) risks.Add((last[e.Key].Floor, -1, e.Key));
                continue;
            }
            if (last.TryGetValue(e.Key, out var previous) && e.Time - previous.Time < .0825)
                risks.Add((previous.Floor, e.Floor, e.Key));
            last[e.Key] = e;
        }
        return risks;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedRows(List<Slice> rows) : IGroupingStrategy
    {
        public bool UsesTempoInputs => false;
        public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight) => rows;
    }
}
