using System;
using System.Collections.Generic;

#nullable enable

namespace ADOFAIMacro.Technique
{
    internal sealed class HandLayout(byte[] keys, int[][] orders, double[] ratios)
    {
        internal readonly byte[] Keys = keys.Length != 0 ? keys : throw new ArgumentException("A hand needs at least one key.", nameof(keys));

        internal int Capacity { get; } = CountDistinct(keys);

        private static int CountDistinct(byte[] keys)
        {
            var unique = new HashSet<byte>(keys);
            return unique.Count;
        }

        internal int PreferredIndex(int size, int position)
        {
            var row = orders.Length == 0 ? null : orders[Math.Min(size, orders.Length) - 1];
            if (row == null || position >= row.Length) return position % Keys.Length;
            return Math.Min(Keys.Length - 1, Math.Max(0, row[position]));
        }

        internal bool IsEquivalentTo(HandLayout other)
        {
            if (Keys.Length != other.Keys.Length) return false;
            for (int i = 0; i < Keys.Length; i++)
                if (Keys[i] != other.Keys[i] || Ratio(i) != other.Ratio(i)) return false;
            for (int size = 1; size <= Keys.Length; size++)
                for (int position = 0; position < size; position++)
                    if (PreferredIndex(size, position) != other.PreferredIndex(size, position)) return false;
            return true;
        }

        internal double Ratio(int index)
        {
            double ratio = index < ratios.Length ? ratios[index] : 0.8;
            return TechniqueTimeline.IsFinite(ratio) && ratio > 0.0 ? ratio : 0.8;
        }
    }

    internal sealed class TechniqueProfile(double bpmLimit, HandLayout left, HandLayout right)
    {
        internal readonly double BpmLimit = bpmLimit;
        internal HandLayout Layout(bool rightHand) => rightHand ? right : left;
    }

    internal enum TechniqueHand : byte { Left, Right }

    // KeySlot identifies a configured position in that hand's layout. It is not
    // yet an anatomical finger claim; a later strategy may supply that mapping.
    internal readonly struct TechniqueAssignment(double time, int floor, TechniqueHand hand,
                                                  int preferredKeySlot, int keySlot, byte key, bool hold)
    {
        internal readonly double Time = time;
        internal readonly int Floor = floor;
        internal readonly TechniqueHand Hand = hand;
        internal readonly int PreferredKeySlot = preferredKeySlot;
        internal readonly int KeySlot = keySlot;
        internal readonly byte Key = key;
        internal readonly bool Hold = hold;
    }

    internal readonly struct TechniqueEvent(double time, int floor, float fraction, byte key, bool release, bool hold)
    {
        internal readonly double Time = time;
        internal readonly int Floor = floor;
        internal readonly float Fraction = fraction;
        internal readonly byte Key = key;
        internal readonly bool Release = release;
        internal readonly bool Hold = hold;
    }

    internal sealed class TechniquePlan(TechniqueEvent[] events, TechniqueAssignment[] assignments,
                                        int pieces, int fragments, int longestRun, double variance)
    {
        internal readonly TechniqueEvent[] Events = events;
        internal readonly TechniqueAssignment[] Assignments = assignments;
        internal readonly int Pieces = pieces;
        internal readonly int Fragments = fragments;
        internal readonly int LongestRun = longestRun;
        internal readonly double Variance = variance;
    }

    /// <summary>Deterministic chart compiler. No Unity state, clocks, native globals, or I/O.</summary>
    internal static class TechniquePlanner
    {
        internal const string BeatGridStrategyId = "beatGrid";
        internal const string KpsAlternatingStrategyId = "kpsAlternating";
        internal const string TempoWindowStrategyId = "tempoWindow";
        internal const string AlternatingHandPolicyId = "alternating";
        internal const string RecoveredPreferredHandPolicyId = "recoveredPreferred";
        internal const string PieceFallbackStrategyId = "pieceFallback";
        // Bump when grouping, hand choice, key mapping or derived statistics change;
        // this invalidates caches without changing the trace's binary file format.
        private const int PlannerRevision = 31;
        private const double Frame = 0.0167;
        private const double MinimumRepeatSafety = .0825;
        private const double NearSimultaneous = 0.003;
        private const double MinGrid = 0.000001;
        private const double Epsilon = 1e-9;
        private const int MaxSubdivisions = 24;
        // Lazy release: a plain press stays down until its key is needed again (behaviour spec, section 10).
        // Lead and minimum hold are observed timing parameters; the final cutoff is a separate constant.
        private const double LazyReleaseLead = 0.0002;
        private const double LazyMinHold = 0.0003;
        private const double LazyFinalReleaseDelay = 0.015;
        // Finest piece subdivision level: the cell width divided by 2^7.
        private const int MaxPieceMultiplier = 7;
        // Avoid materializing empty cells: the cell number determines hand parity.
        private const double BoundaryTolerance = 0.005;

        internal readonly struct Attack(int index, TechniqueProfile profile, double cellLength)
        {
            internal readonly int Index = index;
            internal readonly TechniqueProfile Profile = profile;
            internal readonly double CellLength = cellLength;
        }

        internal readonly struct Slice(int start, int end, double length, double pressWindow, bool right,
                                       double timeStart = double.NaN, double timeEnd = double.NaN, bool reverseKeyOrder = false)
        {
            internal readonly int Start = start;
            internal readonly int End = end;
            internal readonly double Length = length;
            internal readonly double PressWindow = pressWindow;
            internal readonly bool Right = right;
            // Actual metric boundaries, including empty cells. NaN selects fixed-duration run strategies.
            internal readonly double TimeStart = timeStart;
            internal readonly double TimeEnd = timeEnd;
            internal int Count => End - Start;
            internal readonly bool ReverseKeyOrder = reverseKeyOrder;
            internal int PreferredIndex(HandLayout layout, int attack) =>
                layout.PreferredIndex(Count, ReverseKeyOrder ? End - 1 - attack : attack - Start);
        }

        internal interface IGroupingStrategy
        {
            bool UsesTempoInputs { get; }
            List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight);
        }

        private sealed class BeatGridGroupingStrategy : IGroupingStrategy
        {
            public bool UsesTempoInputs => false;
            public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight)
            {
                attacks = InferBeatGrid(notes, attacks);
                var slices = GroupMetricCells(notes, attacks, startRight);
                RepairBoundaryCadence(notes, attacks, slices);
                slices = RebuildSteadyPulses(notes, attacks, slices);
                slices = PlanArticulatedPhrases(notes, attacks, slices, startRight);
                slices = RefineLocalPhrases(notes, attacks, slices);
                RepairMeasuredSeamBeat(notes, attacks, slices);
                PreferIndependentPhraseLead(notes, attacks, slices, startRight);
                PreferPostHoldLead(notes, attacks, slices, startRight);
                RefineChartRhythms(notes, attacks, slices, startRight);
                PreferLeadAfterPickup(notes, attacks, slices, startRight);
                RepairHalfPulseEntry(notes, attacks, slices);
                // Validate the measured groups against the final neighbors; an
                // upstream edit can change which seams later repairs choose.
                RefineChartRhythms(notes, attacks, slices, startRight, measuredPhrases: true);
                return slices;
            }
        }

        // Change only the phase of a separated tap phrase, after all metric and
        // seam repairs. One missing beat, a tempo change, or a slow steady flow is
        // not a new phrase; a solitary occupied cell is not enough evidence either.
        internal static void PreferIndependentPhraseLead(TechniqueNote[] notes, List<Attack> attacks,
            List<Slice> slices, bool preferredRight)
        {
            int timeline = 0;
            bool activeHold = false;
            for (int first = 0; first < slices.Count;)
            {
                int end = first + 1;
                while (end < slices.Count && !Separated(end)) end++;
                int startAttack = slices[first].Start;
                int stopAttack = slices[end - 1].End;
                bool tapsOnly = true;
                double horizon = .08;
                for (int a = startAttack; a < stopAttack; a++)
                {
                    int index = attacks[a].Index;
                    while (timeline < index)
                    {
                        if (notes[timeline].Kind == NoteKind.HoldEnd) activeHold = false;
                        timeline++;
                    }
                    var kind = notes[timeline++].Kind;
                    if (activeHold || kind != NoteKind.Tap) tapsOnly = false;
                    if (kind == NoteKind.HoldStart) activeHold = true;
                    horizon = Math.Max(horizon, MinimumCellLength(attacks[a].Profile.BpmLimit) * 1.1);
                }

                // Require a formed entry (two local intervals); do not turn a
                // string of isolated hits into permanent single-hand play.
                if (first > 0 && end - first >= 3 && tapsOnly && slices[first].Right != preferredRight &&
                    EstablishedEntry(first, end) && Time(startAttack) - Time(slices[first - 1].End - 1) >= horizon &&
                    (end == slices.Count || Time(slices[end].Start) - Time(stopAttack - 1) >= horizon))
                {
                    var baseline = slices.GetRange(first, end - first);
                    var alternate = new List<Slice>(baseline.Count);
                    foreach (var row in baseline)
                    {
                        if (row.Count > attacks[row.Start].Profile.Layout(!row.Right).Capacity) break;
                        alternate.Add(new Slice(row.Start, row.End, row.Length, row.PressWindow,
                            !row.Right, row.TimeStart, row.TimeEnd));
                    }
                    if (alternate.Count == baseline.Count &&
                        Risks(alternate, horizon) is { } proposed && Risks(baseline, horizon) is { } original &&
                        proposed.IsSubsetOf(original))
                        for (int k = 0; k < alternate.Count; k++) slices[first + k] = alternate[k];
                }
                first = end;
            }

            // Rest gaps isolate the release windows and the rapid-reuse horizon.
            // In a tap-only row with strictly ordered onsets the allocator uses
            // the configured slot unchanged, even when the two layouts share keys.
            HashSet<(int From, int To, int Key)>? Risks(List<Slice> rows, double horizon)
            {
                var risks = new HashSet<(int, int, int)>();
                var last = new int[256];
                var up = new double[256];
                for (int k = 0; k < last.Length; k++) last[k] = -1;
                double previousTime = double.NegativeInfinity;
                for (int k = 0; k < rows.Count; k++)
                {
                    var row = rows[k];
                    var layout = attacks[row.Start].Profile.Layout(row.Right);
                    if (row.Count > layout.Capacity) return null;
                    for (int a = row.Start; a < row.End; a++)
                    {
                        double down = Time(a);
                        if (down <= previousTime) return null;
                        previousTime = down;
                        int slot = layout.PreferredIndex(row.Count, a - row.Start);
                        byte key = layout.Keys[slot];
                        int prior = last[key];
                        if (prior >= 0)
                        {
                            double priorTime = Time(prior);
                            if (down - priorTime < horizon) risks.Add((prior, a, key));
                            double normalUp = up[key] < down ? up[key] : Math.Max(priorTime, down - MinGrid);
                            if (normalUp - priorTime < Frame) risks.Add((prior, -1, -1));
                            if (LazyReleaseBefore(priorTime, down) - priorTime < Frame) risks.Add((prior, -2, -1));
                        }
                        last[key] = a;
                        up[key] = ReleaseForSlice(rows, k, down, layout.Ratio(slot));
                    }
                }
                for (int k = 0; k < last.Length; k++)
                    if (last[k] >= 0 && up[k] - Time(last[k]) < Frame) risks.Add((last[k], -1, -1));
                // At chart tail, the last occurrence of a key uses the existing
                // final-release delay rather than the next-key formula.
                if (rows[rows.Count - 1].End == attacks.Count)
                    for (int k = 0; k < last.Length; k++)
                        if (last[k] >= 0 && notes[notes.Length - 1].Time + LazyFinalReleaseDelay - Time(last[k]) < Frame)
                            risks.Add((last[k], -2, -1));
                return risks;
            }

            double Time(int a) => AttackTime(notes, attacks, a);
            bool EstablishedEntry(int i, int end)
            {
                var first = slices[i];
                var second = slices[i + 1];
                double pulse = Time(second.Start) - Time(first.Start);
                double tolerance = pulse * .025 + Epsilon;
                var profile = attacks[first.Start].Profile;
                if (pulse < first.PressWindow * .75 || pulse > first.PressWindow * 1.5 ||
                    Time(first.End - 1) - Time(first.Start) > pulse * .25 + Epsilon ||
                    !ReferenceEquals(profile, attacks[second.Start].Profile)) return false;

                // A subdivided beat may occupy extra slices. Look for a third
                // row onset on the same clock, allowing one unoccupied beat.
                for (int k = i + 2; k < Math.Min(end, i + 5); k++)
                {
                    var row = slices[k];
                    if (!ReferenceEquals(profile, attacks[row.Start].Profile)) return false;
                    double offset = Time(row.Start) - Time(second.Start);
                    if (offset > 2 * pulse + tolerance) break;
                    if (Math.Abs(offset - pulse) <= tolerance || Math.Abs(offset - 2 * pulse) <= tolerance)
                        return true;
                }
                return false;
            }

            bool Separated(int i)
            {
                var left = slices[i - 1];
                var right = slices[i];
                double width = Math.Max(left.PressWindow, right.PressWindow);
                double gap = Time(right.Start) - Time(left.End - 1);
                if (right.TimeStart - left.TimeEnd + Epsilon < 3 * width || gap + Epsilon < 4 * width)
                    return false;
                // Measured neighboring pulse, not only the (possibly folded)
                // grid width: a regular slow passage must not be reset every beat.
                double before = i >= 2 ? Time(left.Start) - Time(slices[i - 2].Start) : 0;
                double after = i + 1 < slices.Count ? Time(slices[i + 1].Start) - Time(right.Start) : 0;
                if (gap < 3 * before || gap < 3 * after) return false;
                // With the same measured pulse on both sides and an integral
                // number of beats between them, prefer the continuous reading.
                double pulse = Math.Min(before, after);
                double tolerance = pulse * .025 + Epsilon;
                double longer = Math.Max(before, after);
                if (pulse > Epsilon && (Math.Abs(longer - pulse) <= tolerance ||
                    Math.Abs(longer - 2 * pulse) <= tolerance))
                {
                    double span = Time(right.Start) - Time(left.Start);
                    if (Math.Abs(span - Math.Round(span / pulse) * pulse) <= tolerance) return false;
                }
                return true;
            }
        }

        // Keep slice geometry, first-hand preference and configured finger rows intact.
        // Compare both hands only in the local cadence window, never rotate keys.
        internal static void RepairBoundaryCadence(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices)
        {
            const int searchLimit = 8;
            // A tap inside a chart hold may be remapped by the key allocator. Its
            // configured row alone is not a sufficient basis for switching hands.
            var insideHold = new bool[attacks.Count];
            bool activeHold = false;
            int attack = 0;
            foreach (var note in notes)
            {
                if (note.Kind == NoteKind.HoldEnd) { activeHold = false; continue; }
                insideHold[attack++] = activeHold;
                if (note.Kind == NoteKind.HoldStart) activeHold = true;
            }
            for (int i = 1; i < slices.Count; i++) TrySwitch(i);
            for (int i = slices.Count - 1; i > 0; i--) TrySwitch(i);
            // At width changes a single flip can strand the neighboring hand.
            // Compare a bounded set of joint assignments against the repaired baseline.
            for (int pivot = 1; pivot < slices.Count; pivot++)
                if (Math.Abs(slices[pivot - 1].PressWindow - slices[pivot].PressWindow) > Epsilon)
                    TryJoint(pivot);

            void TryJoint(int pivot)
            {
                var profile = attacks[slices[pivot].Start].Profile;
                if (!ReferenceEquals(profile, attacks[slices[pivot - 1].Start].Profile)) return;
                double cadence = MinimumCellLength(profile.BpmLimit);
                double lookaround = cadence * 1.1;
                if (Time(slices[pivot].Start) - Time(slices[pivot - 1].End - 1) >= cadence) return;
                int lo = pivot - 1, hi = pivot;
                if (lo > 0 && IsClose(lo - 1, lo) && SameProfile(lo - 1)) lo--;
                if (hi + 1 < slices.Count && IsClose(hi, hi + 1) && SameProfile(hi + 1)) hi++;
                int outerLo = lo, outerHi = hi;
                while (outerLo > 0 && Time(slices[lo].Start) - Time(slices[outerLo - 1].End - 1) < lookaround)
                {
                    if (lo - outerLo == searchLimit) return;
                    outerLo--;
                }
                while (outerHi + 1 < slices.Count && Time(slices[outerHi + 1].Start) - Time(slices[hi].End - 1) < lookaround)
                {
                    if (outerHi - hi == searchLimit) return;
                    outerHi++;
                }
                // Include both joining edges, even when an outside row is beyond recovery range.
                outerLo = Math.Min(outerLo, Math.Max(0, lo - 1));
                outerHi = Math.Max(outerHi, Math.Min(slices.Count - 1, hi + 1));
                int allowed = 0;
                for (int i = outerLo; i <= outerHi; i++)
                {
                    var row = slices[i];
                    if (!SameProfile(i) || row.Count > profile.Layout(row.Right).Capacity) return;
                    for (int a = row.Start; a < row.End; a++)
                        if (insideHold[a] || notes[attacks[a].Index].Kind != NoteKind.Tap) return;
                    if (i >= lo && i <= hi && i > 0 && row.Count > 2 && row.Count <= profile.Layout(!row.Right).Capacity)
                        allowed |= 1 << (i - lo);
                }
                if (allowed == 0) return;

                // Cache row-pair scores for each hand combination; the window has at most four mutable rows.
                var terms = new List<(int A, int B, double Total, double Worst, int Reuses)[]>();
                for (int i = outerLo; i <= hi; i++)
                for (int j = Math.Max(i, lo); j <= outerHi; j++)
                {
                    if (Time(slices[j].Start) - Time(slices[i].End - 1) >= lookaround) break;
                    if (i == j && !CanFlip(i) || !CanFlip(i) && !CanFlip(j)) continue;
                    var values = new (int A, int B, double Total, double Worst, int Reuses)[4];
                    for (int mask = 0; mask < 4; mask++)
                    {
                        var l = slices[i]; var r = slices[j];
                        var left = profile.Layout(l.Right ^ ((mask & 1) != 0));
                        var right = profile.Layout(r.Right ^ ((mask & 2) != 0));
                        double total = 0, worst = 0;
                        int reuses = 0;
                        for (int a = l.Start; a < l.End; a++)
                        for (int b = i == j ? a + 1 : r.Start; b < r.End; b++)
                        {
                            if (PreferredKey(left, l, a) != PreferredKey(right, r, b)) continue;
                            double interval = Time(b) - Time(a);
                            if (interval < lookaround) reuses++;
                            double deficit = 1 - interval / cadence;
                            if (deficit <= 0) continue;
                            total += deficit * deficit;
                            worst = Math.Max(worst, deficit);
                        }
                        values[mask] = (i, j, total, worst, reuses);
                    }
                    terms.Add(values);
                }
                var original = Score(0);
                var best = original;
                int choice = 0;
                for (int mask = allowed; mask != 0; mask = (mask - 1) & allowed)
                {
                    if (!KeepsTightEdges(mask)) continue;
                    var candidate = Score(mask);
                    if (candidate.Worst > original.Worst + Epsilon || candidate.Total > original.Total + Epsilon ||
                        candidate.Switches > original.Switches || candidate.Reuses > original.Reuses) continue;
                    if (candidate.Switches < best.Switches || candidate.Switches == best.Switches &&
                        (candidate.Total < best.Total - Epsilon || Math.Abs(candidate.Total - best.Total) <= Epsilon &&
                         (candidate.Worst < best.Worst - Epsilon || Math.Abs(candidate.Worst - best.Worst) <= Epsilon &&
                          candidate.Changed < best.Changed)))
                    {
                        best = candidate;
                        choice = mask;
                    }
                }
                for (int i = lo; i <= hi; i++)
                {
                    if (!Flipped(choice, i)) continue;
                    var row = slices[i];
                    slices[i] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                        !row.Right, row.TimeStart, row.TimeEnd);
                }

                bool SameProfile(int i) => ReferenceEquals(attacks[slices[i].Start].Profile, profile);
                bool IsClose(int left, int right) => Time(slices[right].Start) - Time(slices[left].End - 1) < cadence;
                bool CanFlip(int i) => Flipped(allowed, i);
                bool Flipped(int mask, int i) => i >= lo && i <= hi && (mask & (1 << (i - lo))) != 0;

                bool KeepsTightEdges(int mask)
                {
                    for (int i = Math.Max(lo, 1); i <= Math.Min(hi + 1, slices.Count - 1); i++)
                        if (Flipped(mask, i - 1) != Flipped(mask, i) && slices[i - 1].Right == slices[i].Right &&
                            IsTightPair(slices[i].Start)) return false;
                    return true;
                }

                (int Switches, double Total, double Worst, int Reuses, int Changed) Score(int mask)
                {
                    double total = 0, worst = 0;
                    int reuses = 0;
                    foreach (var values in terms)
                    {
                        var entry = values[(Flipped(mask, values[0].A) ? 1 : 0) | (Flipped(mask, values[0].B) ? 2 : 0)];
                        total += entry.Total;
                        reuses += entry.Reuses;
                        worst = Math.Max(worst, entry.Worst);
                    }
                    int switches = 0, changed = 0;
                    for (int i = Math.Max(lo, 1); i <= Math.Min(hi + 1, slices.Count - 1); i++)
                        if ((slices[i - 1].Right ^ Flipped(mask, i - 1)) != (slices[i].Right ^ Flipped(mask, i))) switches++;
                    for (int i = lo; i <= hi; i++) if (Flipped(mask, i)) changed++;
                    return (switches, total, worst, reuses, changed);
                }
            }

            bool IsTightPair(int i)
            {
                double gap = Time(i) - Time(i - 1);
                if (gap < NearSimultaneous) return true;
                if (gap > Math.Max(Frame, 0.5 * Math.Min(attacks[i - 1].CellLength, attacks[i].CellLength))) return false;
                double contrast = gap * 2 * (1 - BoundaryTolerance);
                return (i < 2 || Time(i - 1) - Time(i - 2) >= contrast) &&
                       (i + 1 == attacks.Count || Time(i + 1) - Time(i) >= contrast);
            }

            void TrySwitch(int index)
            {
                var slice = slices[index];
                if (slice.Count < 2) return;
                var profile = attacks[slice.Start].Profile;
                if (!CanScore(slice) || slice.Count > profile.Layout(!slice.Right).Capacity) return;
                if (SplitsPair(index - 1, index) || SplitsPair(index, index + 1)) return;

                double cadence = MinimumCellLength(profile.BpmLimit);
                int lo = index, hi = index;
                double first = Time(slice.Start), last = Time(slice.End - 1);
                while (lo > 0 && first - Time(slices[lo - 1].End - 1) < cadence)
                {
                    // Do not make a partial comparison across a layout or hold boundary,
                    // or when an exceptionally dense passage exceeds the search budget.
                    if (index - lo == searchLimit || !CanScore(slices[--lo])) return;
                }
                while (hi + 1 < slices.Count && Time(slices[hi + 1].Start) - last < cadence)
                    if (hi - index == searchLimit || !CanScore(slices[++hi])) return;
                if (lo == hi) return;

                var current = Cost(slice.Right);
                if (current.Total == 0) return;
                var alternate = Cost(!slice.Right);
                if (alternate.Total + Epsilon < current.Total && alternate.Worst <= current.Worst + Epsilon)
                    slices[index] = new Slice(slice.Start, slice.End, slice.Length, slice.PressWindow,
                        !slice.Right, slice.TimeStart, slice.TimeEnd);

                bool CanScore(Slice s)
                {
                    if (!ReferenceEquals(attacks[s.Start].Profile, profile) ||
                        s.Count > profile.Layout(s.Right).Capacity) return false;
                    for (int i = s.Start; i < s.End; i++)
                        if (insideHold[i] || notes[attacks[i].Index].Kind != NoteKind.Tap) return false;
                    return true;
                }

                bool SplitsPair(int left, int right)
                {
                    if (right == slices.Count || slices[left].Right != slices[right].Right) return false;
                    return IsTightPair(slices[right].Start);
                }

                (double Total, double Worst) Cost(bool hand)
                {
                    var layout = profile.Layout(hand);
                    double total = 0, worst = 0;
                    for (int i = slice.Start; i < slice.End; i++)
                    {
                        byte key = PreferredKey(layout, slice, i);
                        double time = Time(i);
                        // Different order rows are allowed to alias a physical key. Score
                        // internal reuse too, even if the layout has enough distinct keys.
                        for (int j = slice.Start; j < i; j++)
                            if (key == PreferredKey(layout, slice, j)) Add(time, Time(j));
                        for (int n = lo; n <= hi; n++)
                        {
                            if (n == index) continue;
                            var other = slices[n];
                            var otherLayout = profile.Layout(other.Right);
                            for (int j = other.Start; j < other.End; j++)
                                if (key == PreferredKey(otherLayout, other, j)) Add(time, Time(j));
                        }
                    }
                    return (total, worst);

                    void Add(double a, double b)
                    {
                        double deficit = 1 - Math.Abs(a - b) / cadence;
                        if (deficit <= 0) return;
                        total += deficit * deficit;
                        worst = Math.Max(worst, deficit);
                    }
                }
            }

            double Time(int i) => AttackTime(notes, attacks, i);
            static byte PreferredKey(HandLayout layout, Slice s, int i) =>
                layout.Keys[layout.PreferredIndex(s.Count, i - s.Start)];
        }

        internal static List<Slice> GroupMetricCells(TechniqueNote[] notes, List<Attack> attacks, bool startRight)
        {
            var slices = Partition(notes, attacks, startRight);
            RefineBoundaries(notes, attacks, slices);
            slices = RefineIsolatedPairs(notes, attacks, slices);
            return RefineRhythmicPairs(notes, attacks, slices);
        }

        internal static bool IsKpsAlternatingStrategy(string? strategyId) =>
            string.Equals(strategyId, KpsAlternatingStrategyId, StringComparison.OrdinalIgnoreCase);

        internal static bool IsTempoWindowStrategy(string? strategyId) =>
            string.Equals(strategyId, TempoWindowStrategyId, StringComparison.OrdinalIgnoreCase);

        internal static bool IsPieceFallbackStrategy(string? strategyId) =>
            string.Equals(strategyId, PieceFallbackStrategyId, StringComparison.OrdinalIgnoreCase);

        internal static string NormalizeStrategyId(string? strategyId) =>
            IsKpsAlternatingStrategy(strategyId) ? KpsAlternatingStrategyId
                : IsTempoWindowStrategy(strategyId) ? TempoWindowStrategyId
                : IsPieceFallbackStrategy(strategyId) ? PieceFallbackStrategyId
                : BeatGridStrategyId;

        internal static double NormalizeSingleFingerKps(double kps)
        {
            if (!TechniqueTimeline.IsFinite(kps) || kps <= 0) kps = 6.5;
            return Math.Min(30.0, Math.Max(0.5, kps));
        }

        internal static string NormalizeHandPolicyId(string? handPolicyId) =>
            string.Equals(handPolicyId, RecoveredPreferredHandPolicyId, StringComparison.OrdinalIgnoreCase)
                ? RecoveredPreferredHandPolicyId : AlternatingHandPolicyId;

        internal static IGroupingStrategy CreateGroupingStrategy(string? strategyId, double singleFingerKps,
                                                                 string? handPolicyId = null)
        {
            IRunHandPolicy handPolicy = NormalizeHandPolicyId(handPolicyId) == RecoveredPreferredHandPolicyId
                ? new RecoveredPreferredHandPolicy() : new AlternatingRunHandPolicy();
            if (IsKpsAlternatingStrategy(strategyId)) return new KpsAlternatingGroupingStrategy(singleFingerKps, handPolicy);
            if (IsTempoWindowStrategy(strategyId)) return new TempoWindowGroupingStrategy(handPolicy);
            if (IsPieceFallbackStrategy(strategyId)) return new PieceFallbackGroupingStrategy();
            return DefaultGroupingStrategy;
        }

        internal static int GroupingStrategyFingerprint(string? strategyId, double singleFingerKps,
                                                        string? handPolicyId = null)
        {
            var hash = new TechniqueHash();
            hash.Add(PlannerRevision);
            bool kps = IsKpsAlternatingStrategy(strategyId);
            int strategy = kps ? 1 : IsTempoWindowStrategy(strategyId) ? 2 : IsPieceFallbackStrategy(strategyId) ? 3 : 0;
            hash.Add(strategy);
            if (kps) hash.Add(NormalizeSingleFingerKps(singleFingerKps));
            // Omitted and explicit alternation have identical fingerprints. A run hand
            // policy has no effect on beat-grid, which owns its own metric phase.
            if ((strategy == 1 || strategy == 2) && NormalizeHandPolicyId(handPolicyId) == RecoveredPreferredHandPolicyId)
                hash.Add(1);
            return hash.Value;
        }

        internal static void AddProfileFingerprint(ref TechniqueHash hash, TechniqueProfile profile,
                                                   string? strategyId)
        {
            if (!IsKpsAlternatingStrategy(strategyId)) hash.Add(profile.BpmLimit);
            AddHandFingerprint(ref hash, profile.Layout(false));
            AddHandFingerprint(ref hash, profile.Layout(true));
        }

        internal static int ProfileFingerprint(TechniqueProfile profile, string? strategyId)
        {
            var hash = new TechniqueHash();
            AddProfileFingerprint(ref hash, profile, strategyId);
            return hash.Value;
        }

        private static void AddHandFingerprint(ref TechniqueHash hash, HandLayout hand)
        {
            hash.Add(hand.Keys.Length);
            for (int i = 0; i < hand.Keys.Length; i++)
            {
                hash.Add((int)hand.Keys[i]);
                hash.Add(hand.Ratio(i));
            }
            for (int count = 1; count <= hand.Keys.Length; count++)
                for (int pos = 0; pos < count; pos++) hash.Add(hand.PreferredIndex(count, pos));
        }

        // Alternative policy inspired by the reference macro's single-finger KPS
        // threshold: tight runs stay on one hand and use that hand's configured slots.
        // It remains opt-in until its assignments have been reviewed in gameplay.
        internal sealed class KpsAlternatingGroupingStrategy(double singleFingerKps,
            IRunHandPolicy? handPolicy = null) : IGroupingStrategy
        {
            private readonly double _groupWindow = 1.0 / (2.0 * NormalizeKps(singleFingerKps));
            private readonly IRunHandPolicy _handPolicy = handPolicy ?? new AlternatingRunHandPolicy();
            public bool UsesTempoInputs => false;

            public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight) =>
                GroupRuns(notes, attacks, startRight, _handPolicy, _ => _groupWindow);

            private static double NormalizeKps(double kps)
                => NormalizeSingleFingerKps(kps);
        }

        // C++ reference-inspired policy: estimate a one-hand run window from the
        // current chart tempo, shrink it when tempo rises mid-run, and cap each run
        // at the hand's distinct configured keys.
        internal sealed class TempoWindowGroupingStrategy(IRunHandPolicy? handPolicy = null) : IGroupingStrategy
        {
            private readonly IRunHandPolicy _handPolicy = handPolicy ?? new AlternatingRunHandPolicy();
            public bool UsesTempoInputs => true;

            public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight) =>
                GroupRuns(notes, attacks, startRight, _handPolicy, Window);

            private static double Window(Attack attack)
            {
                double halfBeat = TechniqueTimeline.IsFinite(attack.CellLength) && attack.CellLength > 0
                    ? attack.CellLength : 0.25;
                return Math.Min(60.0, halfBeat * 2.0);
            }
        }

        internal interface IRunHandPolicy
        {
            bool ChooseHand(bool preferredRight, bool? previousRight, double sincePreferredAttack, double window);
        }

        private sealed class AlternatingRunHandPolicy : IRunHandPolicy
        {
            public bool ChooseHand(bool preferredRight, bool? previousRight, double sincePreferredAttack, double window) =>
                previousRight.HasValue ? !previousRight.Value : preferredRight;
        }

        // A declared heuristic, not an anatomical recovery model or a Python port:
        // prefer the starting hand after at least one current grouping window since
        // its last attack; otherwise alternate. All history is local to each build.
        private sealed class RecoveredPreferredHandPolicy : IRunHandPolicy
        {
            public bool ChooseHand(bool preferredRight, bool? previousRight, double sincePreferredAttack, double window) =>
                sincePreferredAttack >= window || !previousRight.HasValue
                    ? preferredRight : !previousRight.Value;
        }

        private static List<Slice> GroupRuns(TechniqueNote[] notes, List<Attack> attacks, bool preferredRight,
                                             IRunHandPolicy handPolicy, Func<Attack, double> windowFor)
        {
            var groups = new List<Slice>(attacks.Count);
            bool? previousRight = null;
            double lastPreferredAttack = double.NegativeInfinity;
            for (int start = 0; start < attacks.Count;)
            {
                var first = attacks[start];
                double firstTime = notes[first.Index].Time;
                double allowedWindow = windowFor(first);
                bool right = handPolicy.ChooseHand(preferredRight, previousRight,
                    firstTime - lastPreferredAttack, allowedWindow);
                var layout = first.Profile.Layout(right);
                int capacity = Math.Max(1, layout.Capacity);
                int end = start + 1;
                while (end < attacks.Count && end - start < capacity)
                {
                    var next = attacks[end];
                    if (!ReferenceEquals(first.Profile, next.Profile) &&
                        !layout.IsEquivalentTo(next.Profile.Layout(right))) break;
                    double nextWindow = Math.Min(allowedWindow, windowFor(next));
                    double span = notes[next.Index].Time - firstTime;
                    if (span >= nextWindow) break;
                    allowedWindow = nextWindow;
                    end++;
                }

                double lastTime = notes[attacks[end - 1].Index].Time;
                // Fixed press basis is separate from the grouping window. Default
                // ratio 0.8 yields 50ms; key aliases and holds are resolved later.
                groups.Add(new Slice(start, end, lastTime - firstTime, 0.125, right));
                if (right == preferredRight) lastPreferredAttack = lastTime;
                previousRight = right;
                start = end;
            }
            return groups;
        }

        // Lazy release closes a plain press just before the same key's next press. The release
        // keeps the minimum hold after its own press; a re-press too close to clamp it.
        private static double LazyReleaseBefore(double previousDown, double nextPress)
        {
            double close = Math.Max(previousDown + LazyMinHold, nextPress - LazyReleaseLead);
            close = Math.Min(close, nextPress - MinGrid);
            return Math.Max(close, previousDown);
        }

        // Piece fallback grouping, implemented from docs/technique-behavior-spec.md sections 1-8.
        // Pieces are cut on a half-beat grid. A dense off-hand piece steps the subdivision level up;
        // when that fails, the previous main-hand piece is popped and re-cut one level finer, so emitted
        // groups never change hand. Steps beyond the budget fall back to the default beat grid.
        private sealed class PieceFallbackGroupingStrategy : IGroupingStrategy
        {
            private const int MaxLevel = 7;
            private const int CounterBits = 18;
            private const int CounterOffset = 16;

            public bool UsesTempoInputs => true;

            public List<Slice> Group(TechniqueNote[] notes, List<Attack> attacks, bool startRight)
            {
                int n = attacks.Count;
                if (n == 0) return new List<Slice>();

                // Run end: first index after i whose profile is a different object.
                var runEnd = new int[n];
                runEnd[n - 1] = n;
                for (int i = n - 2; i >= 0; i--)
                    runEnd[i] = ReferenceEquals(attacks[i + 1].Profile, attacks[i].Profile) ? runEnd[i + 1] : i + 1;

                var emitted = new List<Emitted>();
                var counters = new int[MaxLevel + 1];
                int next = 0, level = 0;
                double time = 0.0;
                bool hand = startRight, mainHand = startRight;
                bool multiAllowed = false, pendingBack = false;
                long steps = 0, limit = Math.Max(1000000L, 256L * n);

                while (next < n)
                {
                    if (++steps > limit) return GroupMetricCells(notes, attacks, startRight);

                    int i = next;
                    double width = CellWidth(attacks[i].CellLength, level);
                    double cutoff = time + width * 0.995;
                    int cnt = CountBefore(notes, attacks, i, runEnd[i], cutoff);
                    int capacity = Math.Max(1, attacks[i].Profile.Layout(hand).Capacity);
                    bool offHand = hand != mainHand;

                    if (cnt > capacity)
                    {
                        if (multiAllowed && offHand) pendingBack = true;
                        if (level < MaxLevel)
                        {
                            level++;
                            counters[level] = 0;
                            continue;
                        }
                        cnt = capacity;
                    }

                    if (pendingBack)
                    {
                        pendingBack = false;
                        if (emitted.Count > 0 && emitted[emitted.Count - 1].Level < MaxLevel)
                        {
                            var top = emitted[emitted.Count - 1];
                            emitted.RemoveAt(emitted.Count - 1);
                            hand = mainHand;
                            time = top.Time;
                            next = top.Start;
                            Array.Copy(top.CountersBefore, counters, counters.Length);
                            level = Math.Min(top.Level + 1, MaxLevel);
                            multiAllowed = false;
                            continue;
                        }
                    }

                    // Emit the segment, then decay the cooldown counters (spec step 7).
                    var record = new Emitted(next, cnt, hand, width, time, level, (int[])counters.Clone());
                    emitted.Add(record);
                    for (int c = level; c >= 1; c--)
                        counters[c] = (counters[c] + (1 << (CounterOffset - (level - c)))) & ((1 << CounterBits) - 1);
                    while (level > 0 && counters[level] == 0) level--;

                    next += cnt;
                    time += width;
                    hand = !hand;
                    multiAllowed = true;
                    if (next < n && Math.Abs(AttackTime(notes, attacks, next) - time) < width * 0.01)
                        time = AttackTime(notes, attacks, next);
                }

                var slices = new List<Slice>(emitted.Count);
                foreach (var record in emitted)
                    if (record.Count > 0)
                        slices.Add(new Slice(record.Start, record.Start + record.Count, record.Width, record.Width, record.Hand,
                            record.Time, record.Time + record.Width));
                return slices;
            }

            private sealed class Emitted
            {
                public readonly int Start, Count, Level;
                public readonly bool Hand;
                public readonly double Width, Time;
                public readonly int[] CountersBefore;

                public Emitted(int start, int count, bool hand, double width, double time, int level, int[] countersBefore)
                {
                    Start = start; Count = count; Hand = hand; Width = width; Time = time; Level = level;
                    CountersBefore = countersBefore;
                }
            }

            private static double CellWidth(double cellLength, int level)
                => Math.Max(cellLength / (1 << level), 1e-9);

            private static int CountBefore(TechniqueNote[] notes, List<Attack> attacks, int from, int end, double cutoff)
            {
                int lo = from, hi = end;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (AttackTime(notes, attacks, mid) < cutoff) lo = mid + 1;
                    else hi = mid;
                }
                return lo - from;
            }
        }

        private static readonly IGroupingStrategy DefaultGroupingStrategy = new BeatGridGroupingStrategy();

        // Each down owns exactly one up. Other notes can shorten that interval, but cannot
        // accidentally steal its matching up, even when both hands share a physical key.
        private struct Stroke(int down, bool rightHand, int preferredKeySlot, int keySlot,
                              double pressWindow, bool hold, int sliceIndex)
        {
            internal byte Key;
            internal readonly int Down = down;
            internal readonly bool RightHand = rightHand;
            internal readonly int PreferredKeySlot = preferredKeySlot;
            internal int KeySlot = keySlot;
            internal readonly double PressWindow = pressWindow;
            internal readonly int SliceIndex = sliceIndex;
            internal readonly bool Hold = hold;
            internal double ReleaseAt;
            internal int ReleaseAnchor = -1;
        }

        private readonly struct Edge(double time, long order, int anchor, byte key, bool release, bool hold)
        {
            internal readonly double Time = time;
            internal readonly long Order = order;
            internal readonly int Anchor = anchor;
            internal readonly byte Key = key;
            internal readonly bool Release = release;
            internal readonly bool Hold = hold;
        }

        internal static TechniquePlan Build(TechniqueNote[] notes, double bpm, double fallbackSpeed,
                                            bool startRight, Func<int, TechniqueProfile> resolve)
            => Build(notes, bpm, fallbackSpeed, startRight, resolve, DefaultGroupingStrategy);

        internal static TechniquePlan Build(TechniqueNote[] notes, double bpm, double fallbackSpeed,
                                            bool startRight, Func<int, TechniqueProfile> resolve,
                                            IGroupingStrategy groupingStrategy)
            => Build(notes, bpm, fallbackSpeed, startRight, resolve, groupingStrategy, lazyRelease: false);

        internal static TechniquePlan Build(TechniqueNote[] notes, double bpm, double fallbackSpeed,
                                            bool startRight, Func<int, TechniqueProfile> resolve,
                                            IGroupingStrategy groupingStrategy, bool lazyRelease)
        {
            if (groupingStrategy == null) throw new ArgumentNullException(nameof(groupingStrategy));
            var attacks = new List<Attack>(notes.Length);
            for (int i = 0; i < notes.Length; i++)
            {
                var note = notes[i];
                if (!TechniqueTimeline.IsFinite(note.Time) || note.Floor < 0 ||
                    (i > 0 && (note.Time < notes[i - 1].Time || note.Floor < notes[i - 1].Floor)))
                    throw new ArgumentException("Technique notes must have finite, nondecreasing times and floors.", nameof(notes));
                if (note.Kind == NoteKind.HoldEnd) continue;
                var profile = resolve(note.Floor);
                double cellLength = groupingStrategy.UsesTempoInputs
                    ? GridLength(bpm, note.Speed, fallbackSpeed, profile.BpmLimit)
                    : 0.25;
                attacks.Add(new Attack(i, profile, cellLength));
            }
            if (attacks.Count == 0) return new TechniquePlan([], [], 0, 0, 0, 0);

            var slices = groupingStrategy.Group(notes, attacks, startRight);
            ValidateGroups(slices, attacks);
            var strokes = AssignKeySlots(notes, attacks, slices);
            ResolvePhysicalKeys(notes, attacks, strokes, slices, lazyRelease);
            var events = Project(notes, strokes);
            var assignments = new TechniqueAssignment[strokes.Count];
            for (int i = 0; i < strokes.Count; i++)
            {
                var stroke = strokes[i];
                var note = notes[stroke.Down];
                assignments[i] = new TechniqueAssignment(note.Time, note.Floor,
                    stroke.RightHand ? TechniqueHand.Right : TechniqueHand.Left,
                    stroke.PreferredKeySlot, stroke.KeySlot, stroke.Key, stroke.Hold);
            }

            int fragments = 0, longest = 0, run = 0;
            bool lastRight = !slices[0].Right;
            double mean = 0.0, m2 = 0.0;
            for (int i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                if (s.Count == 1) fragments++;
                run = s.Right == lastRight ? run + s.Count : s.Count;
                longest = Math.Max(longest, run);
                lastRight = s.Right;
                double delta = s.Length - mean;
                mean += delta / (i + 1);
                m2 += delta * (s.Length - mean);
            }
            return new TechniquePlan(events, assignments, slices.Count, fragments, longest, m2 / slices.Count);
        }

        private static void ValidateGroups(List<Slice> slices, List<Attack> attacks)
        {
            int next = 0;
            foreach (var slice in slices)
            {
                if (slice.Start != next || slice.End <= slice.Start || slice.End > attacks.Count ||
                    !TechniqueTimeline.IsFinite(slice.Length) || slice.Length < 0 ||
                    !TechniqueTimeline.IsFinite(slice.PressWindow) || slice.PressWindow <= 0)
                    throw new ArgumentException("Grouping strategy must return a contiguous, nonempty partition with finite group spans and positive press windows.", nameof(slices));

                var layout = attacks[slice.Start].Profile.Layout(slice.Right);
                for (int i = slice.Start + 1; i < slice.End; i++)
                    if (!ReferenceEquals(attacks[slice.Start].Profile, attacks[i].Profile) &&
                        !layout.IsEquivalentTo(attacks[i].Profile.Layout(slice.Right)))
                        throw new ArgumentException("A technique group cannot cross a hand-layout boundary.", nameof(slices));
                next = slice.End;
            }
            if (next != attacks.Count)
                throw new ArgumentException("Grouping strategy must assign every attack exactly once.", nameof(slices));
        }

        // Infer a stable cell width from the played timeline. A short articulation is
        // part of its preceding pulse, so it must not vote for the grid itself.
        private static List<Attack> InferBeatGrid(TechniqueNote[] notes, List<Attack> attacks)
        {
            var inferred = new List<Attack>(attacks.Count);
            for (int start = 0; start < attacks.Count;)
            {
                var profile = attacks[start].Profile;
                int end = start + 1;
                while (end < attacks.Count && ReferenceEquals(attacks[end].Profile, profile)) end++;
                double minimum = MinimumCellLength(profile.BpmLimit);
                if (InferStraightPulse(start, end, profile, minimum))
                {
                    start = end;
                    continue;
                }

                var buckets = new Dictionary<int, (int Count, double Sum)>();
                int previousStart = start;
                for (int i = start + 1; i < end; i++)
                {
                    double gap = Gap(i);
                    double contrast = gap * 2 * (1 - BoundaryTolerance);
                    bool ornament = gap < NearSimultaneous || (gap <= minimum * 0.5 &&
                        (i == start + 1 || Gap(i - 1) >= contrast) &&
                        (i + 1 == end || Gap(i + 1) >= contrast));
                    if (ornament) continue;

                    double sample = AttackTime(notes, attacks, i) - AttackTime(notes, attacks, previousStart);
                    previousStart = i;
                    if (sample <= 0 || sample > minimum * 8) continue;
                    while (sample >= minimum * 2) sample *= 0.5;
                    double lowerBound = minimum * (1 - BoundaryTolerance);
                    if (sample < lowerBound) sample *= Math.Ceiling(lowerBound / sample);
                    int bin = (int)Math.Round(sample / (minimum * BoundaryTolerance));
                    buckets.TryGetValue(bin, out var value);
                    buckets[bin] = (value.Count + 1, value.Sum + sample);
                }
                double width = minimum;
                int best = 1;
                foreach (var value in buckets.Values)
                {
                    if (value.Count > best || value.Count == best && value.Sum / value.Count < width)
                    {
                        best = value.Count;
                        width = value.Sum / value.Count;
                    }
                }
                int denseEnd = start;
                double denseWidth = width;
                for (int at = start; at < end; at++)
                {
                    // A denser run of repeated tight pairs can begin midway through a cell.
                    // Let the entry pair finish on the old grid, then anchor the faster pulse.
                    if (at >= denseEnd && at >= start + 4 && at + 3 < end && Gap(at - 1) < NearSimultaneous &&
                        Gap(at + 1) < NearSimultaneous && Gap(at + 3) < NearSimultaneous)
                    {
                        double pulse = Gap(at - 1) + Gap(at);
                        double next = Gap(at + 1) + Gap(at + 2);
                        double previous = Gap(at - 2);
                        if (pulse >= minimum * .5 && pulse < minimum &&
                            Math.Abs(pulse - next) <= pulse * BoundaryTolerance &&
                            previous >= pulse * 1.5)
                        {
                            denseWidth = pulse * 2;
                            denseEnd = at + 4;
                            while (denseEnd + 1 < end && Gap(denseEnd + 1) < NearSimultaneous &&
                                Math.Abs(Gap(denseEnd - 1) + Gap(denseEnd) - pulse) <= pulse * BoundaryTolerance)
                                denseEnd += 2;
                        }
                    }
                    inferred.Add(new Attack(attacks[at].Index, profile, at < denseEnd ? denseWidth : width));
                }
                start = end;
            }
            return inferred;

            double Gap(int i) => AttackTime(notes, attacks, i) - AttackTime(notes, attacks, i - 1);

            bool InferStraightPulse(int start, int end, TechniqueProfile profile, double minimum)
            {
                // Distinct, sustained intervals in an unbroken stream are real pulse
                // changes. Do not re-anchor mixed subdivisions or isolated rests here.
                if (end - start < 5) return false;
                for (int i = start + 1; i < end; i++)
                    if (Gap(i) < minimum * (1 - BoundaryTolerance) || Gap(i) > minimum * 2)
                        return false;

                int run = start;
                var changes = new List<(int Start, double Width)>();
                while (run < end - 1)
                {
                    double width = Gap(run + 1);
                    int next = run + 1;
                    while (next < end - 1 && Math.Abs(Gap(next + 1) - width) <= width * BoundaryTolerance)
                        next++;
                    if (next - run < 2) return false;
                    changes.Add((run, width));
                    run = next;
                }
                if (changes.Count < 2) return false;

                for (int i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    int next = i + 1 < changes.Count ? changes[i + 1].Start : end;
                    for (int at = change.Start; at < next; at++)
                        inferred.Add(new Attack(attacks[at].Index, profile, change.Width));
                }
                return true;
            }
        }

        // Recognize sustained beat starts from actual onsets, including a tightly
        // attached second tap. Replace only complete old slices so phase changes
        // do not propagate outside the detected passage.
        internal static List<Slice> RebuildSteadyPulses(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices)
        {
            const int confirmationGaps = 4;
            const int minimumDirectGaps = 3;
            const double phaseTolerance = BoundaryTolerance;
            if (attacks.Count <= confirmationGaps) return slices;

            var owner = new int[attacks.Count];
            for (int i = 0; i < slices.Count; i++)
                for (int a = slices[i].Start; a < slices[i].End; a++) owner[a] = i;
            List<Slice>? rebuilt = null;
            int copied = 0;
            Slice? previousPulse = null;

            // Separate holds and layout changes before discovering any tap pulses.

            var beats = new List<(int Start, int End)>();
            int cursor = 0;
            while (cursor < attacks.Count)
            {
                var first = attacks[cursor];
                if (notes[first.Index].Kind != NoteKind.Tap)
                {
                    cursor++;
                    while (cursor < attacks.Count && !HasHoldEnd(attacks[cursor - 1].Index, attacks[cursor].Index)) cursor++;
                    continue;
                }
                int end = cursor + 1;
                while (end < attacks.Count && ReferenceEquals(attacks[end].Profile, first.Profile) &&
                    notes[attacks[end].Index].Kind == NoteKind.Tap && attacks[end].Index == attacks[end - 1].Index + 1)
                    end++;
                double minimum = MinimumCellLength(first.Profile.BpmLimit);
                int clusterCapacity = Math.Min(4, Math.Min(first.Profile.Layout(false).Capacity, first.Profile.Layout(true).Capacity));
                beats.Clear();
                for (int at = cursor; at < end;)
                {
                    int next = at + 1;
                    if (at == cursor || Time(at) - Time(at - 1) >= minimum * .75)
                    {
                        // An attached flourish must fit near the beginning of its beat
                        // and leave a clear gap before the following attack.
                        while (next < end && next - at < clusterCapacity &&
                            Time(next) - Time(at) <= minimum * .25) next++;
                        if (next < end && Time(next) - Time(next - 1) < minimum * .75) next = at + 1;
                    }
                    beats.Add((at, next));
                    at = next;
                }

                for (int start = 0; start + confirmationGaps < beats.Count;)
                {
                    double origin = Time(beats[start].Start);
                    double firstGap = Time(beats[start + 1].Start) - origin;
                    int bestStop = start;
                    double bestPulse = 0;
                    for (int divisions = 1; divisions <= 2; divisions++)
                    {
                        double pulse = firstGap / divisions;
                        if (!TechniqueTimeline.IsFinite(pulse) || pulse < minimum * (1.0 - BoundaryTolerance) ||
                            pulse > minimum * 4) continue;
                        double previousBeat = 0;
                        int direct = 0, stop = start + 1;
                        for (; stop < beats.Count; stop++)
                        {
                            double phase = (Time(beats[stop].Start) - origin) / pulse;
                            double beat = Math.Round(phase);
                            double interval = beat - previousBeat;
                            if (interval < 1 || interval > 2 || Math.Abs(beat - phase) > phaseTolerance) break;
                            if (interval == 2 && direct >= minimumDirectGaps && SustainedSlowerPulse(stop, pulse))
                            {
                                // Leave the shared anchor for the slower local grid.
                                stop--;
                                previousBeat = Math.Round((Time(beats[stop - 1].Start) - origin) / pulse);
                                break;
                            }
                            if (interval == 1) direct++;
                            // Reject unsupported half-period hypotheses early, even on long sparse runs.
                            if (stop - start >= confirmationGaps * 3 && direct < minimumDirectGaps) break;
                            previousBeat = beat;
                        }
                        if (stop - start > confirmationGaps && direct >= minimumDirectGaps && stop > bestStop)
                        {
                            bestStop = stop;
                            bestPulse = (Time(beats[stop - 1].Start) - origin) / previousBeat;
                        }
                    }
                    if (bestStop == start) { start++; continue; }
                    Replace(start, bestStop, bestPulse);
                    start = bestStop;
                    bool SustainedSlowerPulse(int at, double pulse)
                    {
                        if (at + confirmationGaps - 1 >= beats.Count) return false;
                        double anchor = Time(beats[at - 1].Start);
                        for (int k = 0; k < confirmationGaps; k++)
                            if (Math.Abs((Time(beats[at + k].Start) - anchor) / pulse - (k + 1) * 2) > phaseTolerance)
                                return false;
                        return true;
                    }
                }
                cursor = end;
            }

            if (rebuilt == null) return slices;
            CopyThrough(slices.Count);
            return rebuilt;

            double Time(int i) => AttackTime(notes, attacks, i);
            bool HasHoldEnd(int previous, int current)
            {
                for (int i = previous + 1; i < current; i++)
                    if (notes[i].Kind == NoteKind.HoldEnd) return true;
                return false;
            }

            void Replace(int start, int stop, double pulse)
            {
                // Keep the entry slice intact, including a preceding pickup that
                // happens to share a slice with the first detected beat.
                while (start < stop && slices[owner[beats[start].Start]].Start != beats[start].Start) start++;
                if (stop - start <= confirmationGaps) return;
                bool? incomingRight = null;
                if (start > 0 && previousPulse is Slice prior && prior.End == beats[start].Start &&
                    prior.Start == beats[start - 1].Start &&
                    ReferenceEquals(attacks[prior.Start].Profile, attacks[prior.End].Profile))
                {
                    // Count a bridge only if both sides agree on the number of beats.
                    // An empty cell carries the same parity as an occupied one.
                    double gap = Time(prior.End) - Time(prior.Start);
                    double cells = Math.Round(gap / pulse);
                    if (cells >= 1 && cells <= 2 && Math.Abs(gap / pulse - cells) <= phaseTolerance &&
                        Math.Abs(gap / prior.PressWindow - cells) <= phaseTolerance)
                        incomingRight = prior.Right ^ (cells == 1);
                }
                int beatEnd = beats[stop - 1].End;
                var boundaryRow = slices[owner[beatEnd - 1]];
                Slice? continuation = null;
                if (beatEnd < boundaryRow.End)
                {
                    double boundary = Time(beatEnd);
                    bool canSeparate = boundary > Time(beatEnd - 1) && boundary < boundaryRow.TimeEnd;
                    int lastBeatStart = beats[stop - 1].Start;
                    bool keepPair = boundaryRow.Count == 2 && boundary - Time(beatEnd - 1) <= attacks[beatEnd - 1].CellLength * .5 &&
                        beatEnd + 1 < attacks.Count && Time(beatEnd + 1) - boundary >= 1.99 * (boundary - Time(beatEnd - 1));
                    // Keep the established exit pair on its original row. The complete
                    // beats before it can still be repaired without splitting the pair.
                    if (keepPair && !incomingRight.HasValue) return;
                    if (keepPair) canSeparate = false;
                    // Split only a row whose prefix is the final confirmed beat and
                    // whose suffix starts a distinct subdivision, not another flam.
                    if (boundaryRow.Start != lastBeatStart || boundary - Time(beatEnd - 1) < pulse * .25)
                        canSeparate = false;
                    var layout = attacks[lastBeatStart].Profile.Layout(boundaryRow.Right);
                    for (int a = beatEnd; a < boundaryRow.End; a++)
                        canSeparate &= layout.Keys[layout.PreferredIndex(boundaryRow.Count, a - boundaryRow.Start)] ==
                            layout.Keys[layout.PreferredIndex(boundaryRow.End - beatEnd, a - beatEnd)] &&
                            notes[attacks[a].Index].Kind == NoteKind.Tap &&
                            attacks[a].Index == attacks[a - 1].Index + 1 &&
                            ReferenceEquals(attacks[a].Profile, attacks[lastBeatStart].Profile);
                    if (canSeparate)
                        continuation = new Slice(beatEnd, boundaryRow.End, boundaryRow.Length, boundaryRow.PressWindow,
                            boundaryRow.Right, boundary, boundaryRow.TimeEnd);
                }
                if (!continuation.HasValue)
                    while (start < stop && slices[owner[beats[stop - 1].End - 1]].End != beats[stop - 1].End) stop--;
                if (stop - start <= confirmationGaps) return;

                int first = owner[beats[start].Start], last = owner[beats[stop - 1].End - 1];
                if (first < copied) return;
                bool aligned = true, alternating = true, hasCrossBeat = false;
                bool? previous = null;
                for (int i = start; i < stop; i++)
                {
                    var beat = beats[i];
                    var old = slices[owner[beat.Start]];
                    double cells = pulse / attacks[beat.Start].CellLength;
                    aligned &= Math.Abs(cells - 1) <= BoundaryTolerance;
                    hasCrossBeat |= i > start && owner[beat.Start] == owner[beats[i - 1].Start];
                    bool rest = i > start && Math.Round((Time(beat.Start) - Time(beats[i - 1].Start)) / pulse) == 2;
                    alternating &= old.Start == beat.Start && old.End == beat.End &&
                        (!previous.HasValue || (previous.Value == old.Right) == rest);
                    previous = old.Right;
                }
                // Keep correctly phased runs and don't disturb an unrelated nominal grid.
                bool badJoin = incomingRight.HasValue && incomingRight.Value != slices[first].Right;
                if (!badJoin && (alternating || aligned && !hasCrossBeat)) return;

                double lastOnset = Time(beats[stop - 1].End - 1);
                double endTime = Time(beats[stop - 1].Start) + pulse;
                if (last + 1 < slices.Count) endTime = Math.Min(endTime, slices[last + 1].TimeStart);
                if (continuation.HasValue) endTime = Math.Min(endTime, continuation.Value.TimeStart);
                if (endTime <= lastOnset) return;
                double begin = Time(beats[start].Start);
                var candidate = new List<Slice>(stop - start);
                bool right = incomingRight ?? slices[first].Right;
                for (int i = start; i < stop; i++)
                {
                    var beat = beats[i];
                    double next = i + 1 < stop ? Math.Min(begin + pulse, Time(beats[i + 1].Start)) : endTime;
                    if (next <= Time(beat.End - 1)) return;
                    candidate.Add(new Slice(beat.Start, beat.End, pulse, pulse, right, begin, next));
                    if (i + 1 < stop)
                    {
                        double nextOnset = Time(beats[i + 1].Start);
                        if (Math.Round((nextOnset - begin) / pulse) == 1) right = !right;
                        begin = nextOnset;
                    }
                }
                // Score the replacement with the adjoining rows, including the key-up
                // horizon of the preceding row and any matching key after the join.
                double lookaround = MinimumCellLength(attacks[candidate[0].Start].Profile.BpmLimit) * 1.1;
                int left = Math.Max(0, first - 1), rightEnd = last + 1;
                double from = Time(candidate[0].Start), to = lastOnset;
                while (left > 0 && from - Time(slices[left - 1].End - 1) < lookaround) left--;
                while (rightEnd < slices.Count && (Time(slices[rightEnd].Start) - to < lookaround ||
                    rightEnd == last + 1 && slices[rightEnd].TimeStart <= Math.Max(slices[last].TimeEnd, endTime) + 5e-6)) rightEnd++;
                var before = new List<Slice>();
                var after = new List<Slice>();
                // Earlier replacements cannot overlap this run, but can immediately precede it.
                if (rebuilt != null)
                    for (int k = rebuilt.Count - 1; k >= 0; k--)
                    {
                        var priorSlice = rebuilt[k];
                        if (priorSlice.End <= slices[left].Start) break;
                        before.Add(priorSlice);
                    }
                before.Reverse();
                for (int k = Math.Max(left, copied); k < first; k++) before.Add(slices[k]);
                after.AddRange(before);
                int prefix = after.Count;
                if (prefix > 0 && after[prefix - 1].TimeEnd > from)
                {
                    var preceding = after[prefix - 1];
                    for (int a = preceding.Start; a < preceding.End; a++)
                        if (notes[attacks[a].Index].Kind != NoteKind.Tap) return;
                    after[prefix - 1] = new Slice(preceding.Start, preceding.End, preceding.Length,
                        preceding.PressWindow, preceding.Right, Math.Min(preceding.TimeStart, from), from);
                }
                for (int k = first; k <= last; k++) before.Add(slices[k]);
                after.AddRange(candidate);
                if (continuation.HasValue) after.Add(continuation.Value);
                for (int k = last + 1; k < rightEnd; k++) { before.Add(slices[k]); after.Add(slices[k]); }
                if (rightEnd < slices.Count && slices[rightEnd].TimeStart <= slices[rightEnd - 1].TimeEnd + 5e-6)
                { before.Add(slices[rightEnd]); after.Add(slices[rightEnd]); }

                var baseline = MeasureCadence(notes, attacks, before, lookaround, from);
                if (baseline == null) return;
                if (!Safe(MeasureCadence(notes, attacks, after, lookaround, from)))
                {
                    if (first == 0 || incomingRight.HasValue) return;
                    for (int i = 0; i < candidate.Count; i++)
                    {
                        var row = candidate[i];
                        candidate[i] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                            !row.Right, row.TimeStart, row.TimeEnd);
                        after[prefix + i] = candidate[i];
                    }
                    if (!Safe(MeasureCadence(notes, attacks, after, lookaround, from))) return;
                }
                bool Safe((int Reuses, int ShortTaps, double Closest)? score) => score.HasValue &&
                    score.Value.Reuses <= baseline.Value.Reuses && score.Value.ShortTaps <= baseline.Value.ShortTaps &&
                    score.Value.Closest + Epsilon >= baseline.Value.Closest;
                rebuilt ??= new List<Slice>(slices.Count);
                CopyThrough(first);
                if (prefix > 0) rebuilt[rebuilt.Count - 1] = after[prefix - 1];
                rebuilt.AddRange(candidate);
                previousPulse = candidate[candidate.Count - 1];
                if (continuation.HasValue) rebuilt.Add(continuation.Value);
                copied = last + 1;
            }

            void CopyThrough(int end)
            {
                for (; copied < end; copied++) rebuilt!.Add(slices[copied]);
            }
        }

        // Articulated phrases have a stronger time-domain signature than the folded
        // cells: recurring wide bursts or a long, directly measured mixed-size pulse.
        // Decide hand phase once for the phrase and check both joins before committing.
        internal static List<Slice> PlanArticulatedPhrases(TechniqueNote[] notes, List<Attack> attacks,
            List<Slice> baseline, bool preferredRight)
        {
            const int motifLength = 3, motifRepeats = 3, sustainedBeats = 96;
            var edits = new List<(int First, int Last, List<Slice> Rows)>();
            var owner = new int[attacks.Count];
            for (int i = 0; i < baseline.Count; i++)
                for (int j = baseline[i].Start; j < baseline[i].End; j++) owner[j] = i;
            double Time(int at) => AttackTime(notes, attacks, at);
            bool Matches(double a, double b) => Math.Abs(a - b) <= b * BoundaryTolerance + Epsilon;

            for (int start = 0; start < attacks.Count;)
            {
                var profile = attacks[start].Profile;
                if (notes[attacks[start].Index].Kind != NoteKind.Tap)
                {
                    // Never infer or score a phrase through an active chart hold.
                    while (++start < attacks.Count && attacks[start].Index == attacks[start - 1].Index + 1) { }
                    continue;
                }
                int end = start + 1;
                while (end < attacks.Count && ReferenceEquals(profile, attacks[end].Profile) &&
                    notes[attacks[end].Index].Kind == NoteKind.Tap && attacks[end].Index == attacks[end - 1].Index + 1) end++;
                double minimum = MinimumCellLength(profile.BpmLimit);
                int capacity = Math.Min(profile.Layout(false).Capacity, profile.Layout(true).Capacity);
                var bursts = new List<(int Start, int End)>();
                for (int a = start, b = start + 1; b <= end; b++)
                    if (b == end || Time(b) - Time(b - 1) >= minimum * .5)
                    {
                        bursts.Add((a, b));
                        a = b;
                    }

                int Size(int at) => bursts[at].End - bursts[at].Start;
                double Onset(int at) => Time(bursts[at].Start);
                bool Compact(int at, double pulse, int max) => Size(at) <= max &&
                    Time(bursts[at].End - 1) - Onset(at) <= pulse * .25;
                for (int i = 0; i + 1 < bursts.Count;)
                {
                    double pulse = Onset(i + 1) - Onset(i);
                    int next = i + 1;
                    // A faster-than-nominal burst is not a new beat unless a full
                    // asymmetric three-beat motif recurs, with compact bursts and clear gaps.
                    if (i + motifLength * motifRepeats <= bursts.Count && pulse >= minimum * .75 &&
                        pulse < minimum * (1 - BoundaryTolerance) && Size(i) > Size(i + 1) && Size(i) > 4 &&
                        Size(i + 1) >= 3 && Size(i + 1) == Size(i + 2))
                    {
                        int j = i;
                        for (; j < bursts.Count; j++)
                            if (Size(j) != Size(i + (j - i) % motifLength) || !Compact(j, pulse, capacity) ||
                                !Matches(Onset(j) - Onset(i), (j - i) * pulse) ||
                                j > i && !Matches(Onset(j) - Onset(j - 1), pulse)) break;
                        if (j - i >= motifLength * motifRepeats)
                        {
                            var rows = new List<(int Start, int End)>();
                            // The first burst may share a gap component with an incoming roll.
                            // Recover up to one preceding motif using its predicted onsets and sizes.
                            int a = bursts[i].Start;
                            for (int m = 1; i > 0 && m <= motifLength; m++)
                            {
                                int from = a - Size(i + (motifLength - m) % motifLength);
                                if (from < start || !Matches(Onset(i) - Time(from), m * pulse) ||
                                    Time(a - 1) - Time(from) > pulse * .25 || Time(a) - Time(a - 1) < pulse * .65) break;
                                rows.Insert(0, (from, a));
                                a = from;
                            }
                            // Include the articulated cadence after the repeating motif,
                            // but not the separated slow phrase that follows it.
                            next = j;
                            while (next < bursts.Count && next - j < motifLength && Compact(next, pulse, capacity) &&
                                Matches(Onset(next) - Onset(next - 1), pulse)) next++;
                            for (int b = i; b < next; b++) rows.Add(bursts[b]);
                            var windows = new double[rows.Count];
                            for (int b = 0; b < windows.Length; b++) windows[b] = pulse;
                            bool inherited = a == 0 ? preferredRight : !baseline[owner[a - 1]].Right;
                            TryPhrase(rows, windows, inherited, fixedLead: false);
                        }
                    }
                    // A compact accented entry after a real gap starts the mixed-size
                    // phrase, even when it also fits the previous slower pulse's parity.
                    else if (i + sustainedBeats <= bursts.Count && pulse >= minimum && pulse <= minimum * 1.2 &&
                        Size(i) >= 3 && Compact(i, pulse, Math.Min(4, capacity)) &&
                        (bursts[i].Start == start || Onset(i) - Time(bursts[i].Start - 1) >= pulse * 1.75))
                    {
                        int confirm = i + 1, singles = 0, decorated = 0;
                        while (confirm < i + sustainedBeats && Matches(Onset(confirm) - Onset(i), (confirm - i) * pulse) &&
                            Matches(Onset(confirm) - Onset(confirm - 1), pulse) && Compact(confirm, pulse, Math.Min(4, capacity)))
                        {
                            if (Size(confirm) == 1) singles++; else decorated++;
                            confirm++;
                        }
                        if (confirm == i + sustainedBeats && singles >= 4 && decorated >= 2)
                        {
                            // Track small tempo steps without resetting hand parity. Stop
                            // at a real gap, subdivision stream, hold, or change of layout.
                            var rows = new List<(int Start, int End)> {bursts[i]};
                            var windows = new List<double> {baseline[owner[bursts[i].Start]].PressWindow};
                            double local = pulse;
                            for (next = i + 1; next < bursts.Count; next++)
                            {
                                double width = Onset(next) - Onset(next - 1);
                                if (Math.Abs(width - local) > local * .015 || !Compact(next, width, Math.Min(4, capacity))) break;
                                rows.Add(bursts[next]);
                                windows.Add(width);
                                local = width;
                            }
                            // A boundary onset measures the preceding cell, not its own.
                            for (int b = 1; b + 1 < windows.Count; b++)
                            {
                                var row = baseline[owner[rows[b].Start]];
                                double outgoing = windows[b + 1];
                                windows[b] = outgoing;
                                if (row.Start == rows[b].Start && row.End == rows[b].End &&
                                    Math.Abs(row.PressWindow - outgoing) <= outgoing * .015)
                                    windows[b] = row.PressWindow;
                            }
                            TryPhrase(rows, windows.ToArray(), preferredRight, fixedLead: true);
                        }
                    }
                    i = next;
                }
                start = end;
            }
            if (edits.Count == 0) return baseline;
            var result = new List<Slice>(baseline.Count);
            int copied = 0;
            foreach (var edit in edits)
            {
                for (; copied < edit.First; copied++) result.Add(baseline[copied]);
                result.AddRange(edit.Rows);
                copied = edit.Last + 1;
            }
            for (; copied < baseline.Count; copied++) result.Add(baseline[copied]);
            return result;

            void TryPhrase(List<(int Start, int End)> beats, double[] widths, bool lead, bool fixedLead)
            {
                int a = beats[0].Start, b = beats[beats.Count - 1].End;
                int first = owner[a], last = owner[b - 1];
                if (edits.Count != 0 && first <= edits[edits.Count - 1].Last) return;
                var profile = attacks[a].Profile;
                double horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                List<Slice>? best = null;
                int earliest = Math.Max(0, first - (fixedLead ? 1 : 2));
                int lo = Math.Max(0, earliest - 1), hi = Math.Min(baseline.Count - 1, last + 1);
                double from = Time(baseline[earliest].Start);
                while (lo > 0 && from - Time(baseline[lo - 1].End - 1) < horizon) lo--;
                while (hi + 1 < baseline.Count && Time(baseline[hi + 1].Start) - Time(baseline[last].End - 1) < horizon) hi++;
                hi = Math.Min(baseline.Count - 1, hi + 1); // release horizon for the last scored row
                // A nearby earlier edit would invalidate the untouched seam context.
                if (edits.Count != 0 && lo <= edits[edits.Count - 1].Last) return;
                var before = baseline.GetRange(lo, hi - lo + 1);
                var reference = MeasureCadence(notes, attacks, before, horizon, Time(a));
                if (!reference.HasValue) return;
                var entryReference = MeasureCadence(notes, attacks, before, horizon, Time(a), Time(a));
                int entryVariants = !fixedLead && first >= 2 && baseline[first].Start < a
                    ? 1 + Math.Max(profile.Layout(false).Capacity, profile.Layout(true).Capacity) : 1;
                for (int variant = 0; variant < entryVariants && best == null; variant++)
                for (int phase = 0; phase < (fixedLead || a == 0 ? 1 : 2); phase++)
                {
                    var rows = new List<Slice>();
                    int begin = first;
                    var head = baseline[first];
                    if (variant > 0)
                    {
                        begin = first - 2;
                        if (edits.Count != 0 && begin <= edits[edits.Count - 1].Last) continue;
                        var incoming = baseline[begin];
                        int cut = incoming.Start + variant;
                        if (cut >= a || !Taps(incoming.Start, a) ||
                            !ReferenceEquals(attacks[incoming.Start].Profile, profile) ||
                            !ReferenceEquals(attacks[cut].Profile, profile) || Time(cut) - Time(cut - 1) < horizon * .08) continue;
                        bool leadRight = lead ^ (phase != 0);
                        if (begin == 0 && leadRight != baseline[0].Right) continue;
                        if (variant > profile.Layout(leadRight).Capacity || a - cut > profile.Layout(!leadRight).Capacity) continue;
                        var donor = baseline[first - 1];
                        rows.Add(new Slice(incoming.Start, cut, incoming.Length, incoming.PressWindow, leadRight,
                            incoming.TimeStart, Time(cut)));
                        rows.Add(new Slice(cut, a, donor.Length, donor.PressWindow, !leadRight, Time(cut), Time(a)));
                    }
                    else if (head.Start < a)
                    {
                        if (!Taps(head.Start, a)) continue;
                        rows.Add(new Slice(head.Start, a, head.Length, head.PressWindow, head.Right, head.TimeStart, Time(a)));
                    }
                    else if (first > lo && baseline[first - 1].TimeEnd > Time(a))
                    {
                        head = baseline[--begin];
                        if (!Taps(head.Start, head.End) || edits.Count != 0 && begin <= edits[edits.Count - 1].Last) continue;
                        rows.Add(new Slice(head.Start, head.End, head.Length, head.PressWindow, head.Right, head.TimeStart, Time(a)));
                    }
                    bool right = lead ^ (phase != 0);
                    for (int k = 0; k < beats.Count; k++, right = !right)
                    {
                        var beat = beats[k];
                        double limit = beat.End < attacks.Count ? Time(beat.End) : double.PositiveInfinity;
                        rows.Add(new Slice(beat.Start, beat.End, widths[k], widths[k], right, Time(beat.Start),
                            Math.Min(limit, Time(beat.Start) + widths[k])));
                    }
                    var exit = baseline[last];
                    if (b < exit.End)
                    {
                        if (fixedLead || !Taps(b, exit.End)) continue;
                        rows.Add(new Slice(b, exit.End, exit.Length, exit.PressWindow, exit.Right, Time(b), exit.TimeEnd));
                    }
                    var after = baseline.GetRange(lo, begin - lo);
                    after.AddRange(rows);
                    after.AddRange(baseline.GetRange(last + 1, hi - last));
                    var score = MeasureCadence(notes, attacks, after, horizon, Time(a));
                    if (!score.HasValue || score.Value.Reuses > reference.Value.Reuses ||
                        score.Value.ShortTaps > reference.Value.ShortTaps || score.Value.Closest + Epsilon < reference.Value.Closest) continue;
                    var entry = MeasureCadence(notes, attacks, after, horizon, Time(a), Time(a));
                    if (!entry.HasValue || !entryReference.HasValue || entry.Value.Reuses > entryReference.Value.Reuses ||
                        entry.Value.Closest + Epsilon < entryReference.Value.Closest) continue;
                    best = rows;
                    first = begin;
                    break;
                }
                if (best != null) edits.Add((first, last, best));
            }

            bool Taps(int a, int b)
            {
                for (int j = a; j < b; j++)
                    if (notes[attacks[j].Index].Kind != NoteKind.Tap ||
                        j > a && attacks[j].Index != attacks[j - 1].Index + 1) return false;
                return true;
            }
        }

        private static (int Reuses, int ShortTaps, double Closest)? MeasureCadence(TechniqueNote[] notes, List<Attack> attacks,
            List<Slice> window, double lookaround, double from, double reuseUntil = double.PositiveInfinity)
        {
            // The configured row predicts the allocator only for distinct tap onsets
            // outside holds. Leave ambiguous joins on their original grid.
            double Time(int i) => AttackTime(notes, attacks, i);
            var recent = new (double Down, double Up)[256];
            for (int i = 0; i < recent.Length; i++) recent[i].Down = double.NegativeInfinity;
            int reuses = 0, shortTaps = 0;
            double closest = lookaround;
            double lastTime = double.NegativeInfinity;
            for (int k = 0; k < window.Count; k++)
            {
                var row = window[k];
                var layout = attacks[row.Start].Profile.Layout(row.Right);
                for (int a = row.Start; a < row.End; a++)
                {
                    int note = attacks[a].Index;
                    if (notes[note].Kind != NoteKind.Tap)
                    {
                        // The row is outside the edited passage. When its long hold has
                        // already ended before the edit and its down is outside the scoring horizon,
                        // no tap can truncate it or reuse its key in that horizon.
                        if (row.Count != 1 || notes[note].Kind != NoteKind.HoldStart || note + 1 >= notes.Length ||
                            notes[note + 1].Kind != NoteKind.HoldEnd || notes[note + 1].Time > from ||
                            from - Time(a) < lookaround ||
                            a + 1 < attacks.Count && notes[note + 1].Time > Time(a + 1))
                            return null;
                        continue;
                    }
                    double down = Time(a);
                    if (down <= lastTime) return null;
                    lastTime = down;
                    int slot = layout.PreferredIndex(row.Count, a - row.Start);
                    byte key = layout.Keys[slot];
                    var previous = recent[key];
                    double interval = down - previous.Down;
                    if (down < reuseUntil && interval < lookaround) { reuses++; closest = Math.Min(closest, interval); }
                    if (!double.IsNegativeInfinity(previous.Down))
                    {
                        double up = previous.Up >= down ? Math.Max(previous.Down, down - MinGrid) : previous.Up;
                        if (up - previous.Down < Frame) shortTaps++;
                    }
                    recent[key] = (down, ReleaseForSlice(window, k, down, layout.Ratio(slot)));
                }
            }
            foreach (var key in recent)
                if (!double.IsNegativeInfinity(key.Down) && key.Up - key.Down < Frame) shortTaps++;
            return (reuses, shortTaps, closest);

        }

        // Strong local evidence can override a folded cell without changing the
        // BPM limit for unaccented or ambiguous streams. All timings are onsets.
        internal static List<Slice> RefineLocalPhrases(TechniqueNote[] notes, List<Attack> attacks, List<Slice> baseline)
        {
            var result = new List<Slice>(baseline);
            var owner = new Slice[attacks.Count];
            var pulseRuns = new List<(int Start, int End)>();
            foreach (var row in baseline)
                for (int a = row.Start; a < row.End; a++) owner[a] = row;
            double Time(int a) => AttackTime(notes, attacks, a);
            bool Tap(int a) => notes[attacks[a].Index].Kind == NoteKind.Tap;
            bool Connected(int a, int b) => attacks[b].Index - attacks[a].Index == b - a &&
                ReferenceEquals(attacks[a].Profile, attacks[b].Profile);
            bool Close(double a, double b) => Math.Abs(a - b) <= b * BoundaryTolerance + Epsilon;

            for (int begin = 0; begin < attacks.Count;)
            {
                if (!Tap(begin))
                {
                    while (++begin < attacks.Count && !HoldEnded(begin - 1, begin)) { }
                    continue;
                }
                int end = begin + 1;
                while (end < attacks.Count && Tap(end) && Connected(end - 1, end)) end++;
                var profile = attacks[begin].Profile;
                double minimum = MinimumCellLength(profile.BpmLimit);
                int capacity = Math.Min(4, Math.Min(profile.Layout(false).Capacity, profile.Layout(true).Capacity));
                var beats = new List<(int A, int B)>();
                for (int a = begin, b = begin + 1; b <= end; b++)
                    if (b == end || Time(b) - Time(b - 1) >= minimum * .25)
                    {
                        beats.Add((a, b));
                        a = b;
                    }
                double Gap(int b) => Time(beats[b].A) - Time(beats[b - 1].A);
                bool Compact(int b, double pulse) => beats[b].B - beats[b].A <= capacity &&
                    Time(beats[b].B - 1) - Time(beats[b].A) <= pulse * .25 + Epsilon;
                int Step(double gap, double pulse) => Close(gap, pulse) ? 1 : Close(gap, pulse * 2) ? 2 : 0;
                for (int i = 0; i + 5 < beats.Count;)
                {
                    double pulse = Gap(i + 1);
                    if (pulse < minimum * .75 || pulse > minimum * 2 || !Compact(i, pulse)) { i++; continue; }
                    var widths = new List<double> {pulse};
                    var steps = new List<int> {0};
                    int accent = -1, stable = 0;
                    bool measuredStep = false;
                    int j = i + 1, direct = 0, ornaments = beats[i].B - beats[i].A > 1 ? 1 : 0, rests = 0;
                    for (; j < beats.Count; j++)
                    {
                        double gap = Gap(j);
                        int step = Step(gap, pulse);
                        // Three equal doubled intervals are a slower run, not three empty beats.
                        if (step == 2 && j + 2 < beats.Count && Close(Gap(j + 1), gap) && Close(Gap(j + 2), gap)) step = 0;
                        if (step == 0)
                        {
                            // A tempo step needs independent support on the new side.
                            // Count the transition once, rather than restarting hand parity.
                            if (direct < 4 || j + 3 >= beats.Count || gap < minimum * .75 || gap > minimum * 2 ||
                                !Close(Gap(j + 1), gap) || !Close(Gap(j + 2), gap) || !Close(Gap(j + 3), gap)) break;
                            if (!Compact(j, gap)) break;
                            measuredStep |= stable >= 4 && gap > pulse * .5 && gap < pulse * 2 &&
                                !Close(gap, pulse * .5) && !Close(gap, pulse * 2);
                            stable = 0;
                            pulse = gap;
                            step = 1;
                        }
                        if (!Compact(j, pulse)) break;
                        widths.Add(pulse);
                        steps.Add(step);
                        if (step == 1) { direct++; stable++; }
                        else { rests++; stable = 0; }
                        if (beats[j].B - beats[j].A > 1) { ornaments++; accent = j; }
                    }
                    // A single interior accent can corroborate a clock on both sides.
                    // At an edge, require a longer run at or above the nominal pulse floor.
                    // An unaccented stream needs a measured non-octave tempo transition.
                    bool strong = ornaments >= 2 || rests >= 2;
                    bool supported = ornaments == 1 && (rests == 1 ||
                        accent >= i + 2 && accent < j - 2 ||
                        direct >= 7 && widths[0] >= minimum && pulse >= minimum) || measuredStep;
                    if (j - i < 6 || direct < 4 || !strong && !supported) { i++; continue; }
                    int lo = i, hi = j;
                    while (lo < hi && owner[beats[lo].A].Start != beats[lo].A) lo++;
                    while (lo < hi && owner[beats[hi - 1].B - 1].End != beats[hi - 1].B) hi--;
                    if (lo != i || hi != j)
                    {
                        if (TryRun(i, j, true) || hi != j && lo > i && hi - i >= 6 && TryRun(i, hi, true) ||
                            lo != i && hi < j && j - lo >= 6 && TryRun(lo, j, true)) { i = j; continue; }
                    }
                    // One uncertain edge can be left intact without discarding
                    // the rhythmic evidence already collected for the interior.
                    bool repaired = (hi - lo >= 6 || hi - lo == 5 && lo == i && hi == j - 1) && TryRun(lo, hi, false);
                    if (!repaired && !strong && rests > 0 && i >= 2)
                    {
                        // A confirmed clock can recover one wider incoming articulation
                        // at its predicted onset, without admitting wide clusters into the scan.
                        var prior = beats[i - 1];
                        double width = widths[0];
                        if (prior.B - prior.A >= 3 && prior.B - prior.A <= capacity &&
                            Close(Gap(i), width) && Close(Gap(i - 1), width) &&
                            Time(prior.B - 1) - Time(prior.A) <= width / 3 + width * BoundaryTolerance)
                            TryRun(i, j, true, prior);
                    }
                    i = j;

                    bool TryRun(int lo, int hi, bool recutEdges, (int A, int B)? incoming = null)
                    {
                        var targets = new List<(int A, int B, double Width, int Step)>();
                        if (incoming is { } prior)
                            targets.Add((prior.A, prior.B, widths[0], 0));
                        bool needsRepair = false, crossesBeat = false, hasFoldedWidth = false;
                        bool fast = false, changesTempo = false;
                        for (int k = lo; k < hi; k++)
                        {
                            var beat = beats[k];
                            double width = widths[k - i];
                            targets.Add((beat.A, beat.B, width, incoming.HasValue && k == lo ? 1 : steps[k - i]));
                            var row = owner[beat.A];
                            crossesBeat |= row.Start < beat.A || row.End > beat.B;
                            hasFoldedWidth |= !Close(row.PressWindow, width) && Close(row.PressWindow, attacks[beat.A].CellLength);
                            fast |= width < minimum * (1 - BoundaryTolerance);
                            changesTempo |= k > lo && !Close(width, widths[k - i - 1]);
                            needsRepair |= row.Start != beat.A || row.End != beat.B || k > lo &&
                                ((row.Right == owner[beats[k - 1].A].Right) != (steps[k - i] == 2));
                        }
                        // Do not rephase a passage already planned on its measured grid.
                        // This pass targets a fast folded pulse, a confirmed tempo step,
                        // or a mixture of measured and still-folded rows.
                        bool mixedGrid = hasFoldedWidth && targets.Exists(t => Close(owner[t.A].PressWindow, t.Width));
                        return needsRepair && hasFoldedWidth && (fast || changesTempo || mixedGrid || crossesBeat) &&
                            Replace(targets, minimum, true, recutEdges, incoming.HasValue);
                    }
                }
                for (int a = begin + 1; a + 2 < end; a++)
                {
                    var left = owner[a]; var right = owner[a + 1];
                    double gap = Time(a + 1) - Time(a);
                    double before = Time(a) - Time(a - 1), after = Time(a + 2) - Time(a + 1);
                    // Strong relative isolation, with no donor fragments. Do not
                    // steal one attack from a configured roll or tie shorter notes.
                    if (left.Count != 1 || right.Count != 1 || left.Right == right.Right || gap < Frame ||
                        Math.Min(before, after) < gap * 3 || gap > minimum * .5 || capacity < 2) continue;
                    double endOfPair = Math.Min(Time(a + 2), Math.Max(right.TimeEnd, Time(a) + left.PressWindow));
                    if (endOfPair <= Time(a + 1)) continue;
                    Replace(new List<(int A, int B, double Width, int Step)> { (a, a + 2, endOfPair - Time(a), 0) }, minimum, false);
                    a++;
                }
                begin = end;
            }
            AlignRepeatedPhraseExits(notes, attacks, result, pulseRuns);
            return result;

            bool HoldEnded(int a, int b)
            {
                for (int k = attacks[a].Index + 1; k < attacks[b].Index; k++)
                    if (notes[k].Kind == NoteKind.HoldEnd) return true;
                return false;
            }

            bool Replace(List<(int A, int B, double Width, int Step)> targets, double minimum, bool pulseRun, bool recutEdges = false,
                bool confirmIncoming = false)
            {
                int a = targets[0].A, b = targets[targets.Count - 1].B;
                int first = Find(a), last = Find(b - 1);
                var head = result[first];
                var tail = result[last];
                bool prefix = head.Start < a, suffix = b < tail.End;
                if (prefix && (!recutEdges || !CanCut(head, a, targets[0].Width)) ||
                    suffix && (!recutEdges || !CanCut(tail, b, targets[targets.Count - 1].Width))) return false;

                bool CanCut(Slice row, int cut, double pulse)
                {
                    if (!Tap(row.Start) || !Connected(row.Start, row.End - 1)) return false;
                    for (int k = row.Start; k < row.End; k++) if (!Tap(k)) return false;
                    if (!Close(row.PressWindow, attacks[row.Start].CellLength)) return false;
                    double gap = Time(cut) - Time(cut - 1);
                    // Two consecutive half pulses are a subdivision at the seam,
                    // not evidence for moving the edge of the measured phrase.
                    if (HalfPulse(gap) && (cut >= 2 && Connected(cut - 2, cut) && Tap(cut - 2) &&
                        HalfPulse(Time(cut - 1) - Time(cut - 2)) || cut + 1 < attacks.Count &&
                        Connected(cut - 1, cut + 1) && Tap(cut + 1) && HalfPulse(Time(cut + 1) - Time(cut)))) return false;
                    if (cut >= 2 && cut + 1 < attacks.Count && gap <= .04 &&
                        Time(cut - 1) - Time(cut - 2) >= 1.99 * gap && Time(cut + 1) - Time(cut) >= 1.99 * gap) return false;
                    return gap + Epsilon >= pulse * (row.Count == 2 ? .5 : .25) &&
                        Time(cut) > row.TimeStart && Time(cut) < row.TimeEnd;

                    bool HalfPulse(double interval) => Math.Abs(interval - pulse * .5) <= pulse * .015;
                }
                double horizon = minimum * 1.1, from = Time(a), to = Time(b - 1);
                int lo = Math.Max(0, first - 1), hi = Math.Min(result.Count - 1, last + 1);
                while (lo > 0 && from - Time(result[lo - 1].End - 1) < horizon) lo--;
                while (hi + 1 < result.Count && Time(result[hi + 1].Start) - to < horizon) hi++;
                // A far-away row cannot affect a release across intervening empty
                // cells. Do not pull an unrelated hold into the cadence window.
                if (hi + 1 < result.Count && result[hi + 1].TimeStart <= result[hi].TimeEnd + 5e-6) hi++;
                var old = result.GetRange(lo, hi - lo + 1);
                var reference = MeasureCadence(notes, attacks, old, horizon, from);
                if (!reference.HasValue) return false;
                bool lead = head.Right;
                bool fixedLead = first == 0;
                if (pulseRun && first > 0 && !prefix)
                {
                    var neighbor = result[first - 1];
                    double gap = Time(a) - Time(neighbor.Start), width = targets[0].Width;
                    if (Connected(neighbor.Start, a) && Tap(neighbor.Start) &&
                        Time(neighbor.End - 1) - Time(neighbor.Start) <= width * .25 &&
                        Close(gap, width))
                    {
                        lead = !neighbor.Right;
                        fixedLead = true;
                    }
                    else if (Connected(neighbor.Start, a) && Tap(neighbor.Start) &&
                        Time(neighbor.End - 1) - Time(neighbor.Start) <= width * .25 &&
                        Close(gap, width * 2) && Close(neighbor.PressWindow, width))
                    {
                        lead = neighbor.Right;
                        fixedLead = true;
                    }
                }
                // Try the established solution first. If only the joins block a
                // confirmed run, compare both phrase phases with the neighboring
                // finger rows instead of lowering the reuse/short-tap thresholds.
                if (!pulseRun) return TryPhases(0, 0);
                if (TryEdges(0, 0)) return true;
                const int seamRows = 3;
                for (int cost = 1; cost <= seamRows * 2; cost++)
                    for (int left = Math.Max(0, cost - seamRows); left <= Math.Min(seamRows, cost); left++)
                        if (TryEdges(left, cost - left)) return true;
                // If mirroring complete rows only moves a collision, try one
                // supported internal cut on an adjoining folded row.
                for (int side = 0; side < 2; side++)
                {
                    int k = side == 0 ? first - 1 : last + 1;
                    if (k <= 0 || k >= result.Count || !CanMirror(k, side == 0)) continue;
                    var row = result[k];
                    if (row.Count < 3 || !Close(row.PressWindow, attacks[row.Start].CellLength)) continue;
                    for (int cut = row.Start + 1; cut < row.End; cut++)
                    {
                        double boundary = Time(cut);
                        if (boundary <= row.TimeStart || boundary >= row.TimeEnd) continue;
                        double gap = boundary - Time(cut - 1);
                        if (gap < minimum * .5 || gap < 1.99 * (cut - 1 > row.Start ? Time(cut - 1) - Time(cut - 2) : 0) ||
                            gap < 1.99 * (cut + 1 < row.End ? Time(cut + 1) - Time(cut) : 0)) continue;
                        for (int hands = 0; hands < 4; hands++)
                            if (TryEdges(side == 0 ? 1 : 0, side == 0 ? 0 : 1, k, cut, hands)) return true;
                    }
                }
                return false;

                bool TryEdges(int left, int right, int recut = -1, int cut = -1, int hands = 0)
                {
                    for (int bits = 0; bits < 4; bits++)
                    {
                        if ((bits & 1) != 0 && (!prefix || head.Start == 0) || (bits & 2) != 0 && !suffix) continue;
                        if (TryPhases(left, right, recut, cut, hands, bits)) return true;
                    }
                    return false;
                }

                bool TryPhases(int left, int rightCount, int recut = -1, int cut = -1, int hands = 0, int edgeHands = 0)
                {
                    int start = first - left, stop = last + rightCount;
                    if (start < 0 || stop >= result.Count || left > 0 && start == 0) return false;
                    for (int k = start; k < first; k++) if (!CanMirror(k, true)) return false;
                    for (int k = last + 1; k <= stop; k++) if (!CanMirror(k, false)) return false;
                    bool joint = prefix || suffix || left != 0 || rightCount != 0;
                    int before = lo, after = hi;
                    var baselineScore = reference.Value;
                    if (joint)
                    {
                        before = Math.Min(lo, Math.Max(0, start - 1));
                        after = Math.Max(hi, Math.Min(result.Count - 1, stop + 1));
                        double firstTime = Time(result[start].Start), lastTime = Time(result[stop].End - 1);
                        while (before > 0 && firstTime - Time(result[before - 1].End - 1) < horizon) before--;
                        while (after + 1 < result.Count && Time(result[after + 1].Start) - lastTime < horizon) after++;
                        if (after + 1 < result.Count && result[after + 1].TimeStart <= result[after].TimeEnd + 5e-6) after++;
                        var extended = MeasureCadence(notes, attacks, result.GetRange(before, after - before + 1), horizon, firstTime);
                        if (!extended.HasValue) return false;
                        baselineScore = extended.Value;
                    }
                    bool phaseLead = lead ^ (fixedLead && left > 0 && (recut != first - 1 || (hands & 2) != 0));
                    for (int phase = 0; phase < (fixedLead ? 1 : 2); phase++)
                    {
                        bool right = phaseLead ^ (phase == 1);
                        var replacement = new List<Slice>(targets.Count);
                        foreach (var beat in targets)
                        {
                            if (beat.Step == 1 && replacement.Count != 0) right = !right;
                            double onset = Time(beat.A);
                            double end = Math.Min(onset + beat.Width, beat.B < attacks.Count ? Time(beat.B) : double.PositiveInfinity);
                            if (end <= Time(beat.B - 1)) break;
                            replacement.Add(new Slice(beat.A, beat.B, beat.Width, beat.Width, right, onset, end));
                        }
                        if (replacement.Count != targets.Count) continue;
                        if (recutEdges && replacement.Exists(row => {
                            var prior = owner[row.Start];
                            return prior.Start == row.Start && prior.End == row.End &&
                                !Close(prior.PressWindow, attacks[row.Start].CellLength) && prior.Right != row.Right;
                        })) continue;
                        var entry = new List<Slice>();
                        for (int k = start; k < first; k++) AddNeighbor(entry, k);
                        if (prefix)
                            entry.Add(new Slice(head.Start, a, head.Length, head.PressWindow,
                                head.Right ^ ((edgeHands & 1) != 0), head.TimeStart, from));
                        // The recovered cluster starts on a confirmed beat. Its
                        // retained donor prefix is also on that clock, not a pickup.
                        if (confirmIncoming && prefix && Close(from - Time(head.Start), targets[0].Width) &&
                            Time(a - 1) - Time(head.Start) <= targets[0].Width * .25 + Epsilon &&
                            entry[entry.Count - 1].Right == replacement[0].Right) continue;
                        replacement.InsertRange(0, entry);
                        if (suffix)
                            replacement.Add(new Slice(b, tail.End, tail.Length, tail.PressWindow,
                                tail.Right ^ ((edgeHands & 2) != 0), Time(b), tail.TimeEnd));
                        for (int k = last + 1; k <= stop; k++) AddNeighbor(replacement, k);
                        if (replacement.Exists(r => r.Count > attacks[r.Start].Profile.Layout(r.Right).Capacity)) continue;
                        var candidate = result.GetRange(before, start - before);
                        candidate.AddRange(replacement);
                        candidate.AddRange(result.GetRange(stop + 1, after - stop));
                        if (SplitsEstablishedPair(candidate)) continue;
                        var score = MeasureCadence(notes, attacks, candidate, horizon, joint ? Time(result[start].Start) : from);
                        if (!score.HasValue || score.Value.Reuses > baselineScore.Reuses ||
                            score.Value.ShortTaps > baselineScore.ShortTaps || score.Value.Closest + Epsilon < baselineScore.Closest) continue;
                        result.RemoveRange(start, stop - start + 1);
                        result.InsertRange(start, replacement);
                        foreach (var row in replacement)
                            for (int k = row.Start; k < row.End; k++) owner[k] = row;
                        if (pulseRun) pulseRuns.Add((a, b));
                        return true;
                    }
                    return false;

                    void AddNeighbor(List<Slice> rows, int k)
                    {
                        var row = result[k];
                        if (k != recut) { rows.Add(Mirror(row)); return; }
                        double boundary = Time(cut);
                        rows.Add(new Slice(row.Start, cut, row.Length, row.PressWindow,
                            row.Right ^ ((hands & 1) != 0), row.TimeStart, boundary));
                        rows.Add(new Slice(cut, row.End, row.Length, row.PressWindow,
                            row.Right ^ ((hands & 2) != 0), boundary, row.TimeEnd));
                    }
                }

                bool SplitsEstablishedPair(List<Slice> candidate)
                {
                    for (int k = 1; k < candidate.Count; k++)
                    {
                        var left = candidate[k - 1];
                        var right = candidate[k];
                        int cut = right.Start;
                        if (left.Right == right.Right || cut < 2 || cut + 1 >= attacks.Count ||
                            owner[cut - 1].Right != owner[cut].Right || !Tap(cut - 1) || !Tap(cut) ||
                            !Connected(cut - 1, cut)) continue;
                        double gap = Time(cut) - Time(cut - 1);
                        if (gap > 0 && gap <= .04 && Time(cut - 1) - Time(cut - 2) >= 1.99 * gap &&
                            Time(cut + 1) - Time(cut) >= 1.99 * gap) return true;
                    }
                    return false;
                }

                bool CanMirror(int k, bool left)
                {
                    var row = result[k];
                    int pivot = left ? a : b - 1;
                    if (!Connected(Math.Min(pivot, row.Start), Math.Max(pivot, row.End - 1))) return false;
                    for (int n = row.Start; n < row.End; n++) if (!Tap(n)) return false;
                    var layout = attacks[row.Start].Profile.Layout(!row.Right);
                    double gap = left ? from - Time(row.End - 1) : Time(row.Start) - to;
                    return row.Count <= layout.Capacity && gap <= 2 * Math.Max(row.PressWindow, horizon);
                }
                static Slice Mirror(Slice row) => new Slice(row.Start, row.End, row.Length, row.PressWindow,
                    !row.Right, row.TimeStart, row.TimeEnd);
            }

            int Find(int attack)
            {
                int lo = 0, hi = result.Count - 1;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (result[mid].End <= attack) lo = mid + 1; else hi = mid;
                }
                return lo;
            }
        }

        // Work on accepted pulse runs so the reference uses their measured beat rows and phase.
        // A matching opening may have a different continuation, but two complete pairs and
        // most of the shorter run must agree before its hand phase is used as a reference.
        internal static void AlignRepeatedPhraseExits(TechniqueNote[] notes, List<Attack> attacks,
            List<Slice> rows, List<(int Start, int End)> runs)
        {
            if (runs.Count < 2) return;
            const int referenceLookback = 8;
            var insideHold = new bool[notes.Length];
            bool holding = false;
            for (int i = 0; i < notes.Length; i++)
            {
                insideHold[i] = holding;
                if (notes[i].Kind == NoteKind.HoldStart) holding = true;
                else if (notes[i].Kind == NoteKind.HoldEnd) holding = false;
            }
            for (int run = 1; run < runs.Count; run++)
            {
                var span = runs[run];
                int first = Find(span.Start), stop = Find(span.End);
                if (first == 0 || stop + 2 >= rows.Count || rows[first].Start != span.Start ||
                    rows[stop].Start != span.End) continue;

                var suffix = rows[stop];
                var neighbor = rows[stop + 1];
                var profile = attacks[span.Start].Profile;
                double width = suffix.PressWindow;
                double horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                if (suffix.Count < 2 || neighbor.Count < 2 ||
                    !Near(width, attacks[suffix.Start].CellLength) || Near(width, neighbor.PressWindow) ||
                    !Near(neighbor.PressWindow, rows[stop - 1].PressWindow) ||
                    Math.Abs(suffix.TimeEnd - neighbor.TimeStart) > 5e-6 ||
                    Time(suffix.Start) - Time(span.End - 1) >= horizon ||
                    Time(neighbor.Start) - Time(suffix.End - 1) >= horizon ||
                    neighbor.TimeEnd - suffix.TimeStart > 2 * width + Epsilon ||
                    Time(neighbor.End - 1) >= neighbor.TimeEnd || !PlainConnected(span.Start, neighbor.End)) continue;

                // The nearest matching occurrence establishes the phase for this one.
                int reference = run - 1;
                while (reference >= Math.Max(0, run - referenceLookback) && !Matches(runs[reference], first, stop)) reference--;
                if (reference < Math.Max(0, run - referenceLookback) ||
                    rows[Find(runs[reference].Start)].Right == rows[first].Right) continue;

                var trial = new List<Slice>(rows);
                bool fits = true;
                for (int i = first; i < stop; i++)
                {
                    var row = rows[i];
                    if (row.Count > profile.Layout(!row.Right).Capacity) { fits = false; break; }
                    trial[i] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                        !row.Right, row.TimeStart, row.TimeEnd);
                }
                if (!fits) continue;

                // Try this joint edit only when the matching phase by itself introduces a risk.
                var original = MeasurePlanRisks(notes, attacks, rows, horizon);
                if (Safe(trial, original, horizon)) continue;
                foreach (bool right in new[] { !trial[stop - 1].Right, trial[stop - 1].Right })
                {
                    var layout = profile.Layout(right);
                    int count = neighbor.End - suffix.Start;
                    if (count > layout.Capacity) continue;
                    var keys = new HashSet<byte>();
                    for (int i = 0; i < count; i++) keys.Add(layout.Keys[layout.PreferredIndex(count, i)]);
                    if (keys.Count != count) continue;

                    var candidate = new List<Slice>(trial);
                    candidate[stop] = new Slice(suffix.Start, neighbor.End, suffix.Length, width, right,
                        suffix.TimeStart, neighbor.TimeEnd);
                    candidate.RemoveAt(stop + 1);
                    if (SplitsPair(first, rows[first - 1].Right, trial[first].Right) ||
                        SplitsPair(stop, trial[stop - 1].Right, right) ||
                        SplitsPair(stop + 2, right, rows[stop + 2].Right) || !Safe(candidate, original, horizon)) continue;
                    rows.RemoveRange(first, stop + 2 - first);
                    rows.InsertRange(first, candidate.GetRange(first, stop + 1 - first));
                    break;
                }
            }

            bool Matches((int Start, int End) reference, int first, int stop)
            {
                int other = Find(reference.Start), otherEnd = Find(reference.End);
                if (reference.End > rows[first].Start || otherEnd == rows.Count ||
                    rows[other].Start != reference.Start || rows[otherEnd].Start != reference.End ||
                    !ReferenceEquals(attacks[reference.Start].Profile, attacks[rows[first].Start].Profile)) return false;
                int count = Math.Min(stop - first, otherEnd - other), shared = 0, pairs = 0;
                if (!PlainConnected(reference.Start, reference.End)) return false;
                bool swap = rows[first].Right != rows[other].Right;
                for (; shared < count; shared++)
                {
                    var a = rows[first + shared];
                    var b = rows[other + shared];
                    if (a.Count != b.Count || a.Right != (b.Right ^ swap) || !Near(a.PressWindow, b.PressWindow) ||
                        shared > 0 && !Near(Time(a.Start) - Time(rows[first + shared - 1].Start),
                                           Time(b.Start) - Time(rows[other + shared - 1].Start))) break;
                    bool same = true;
                    for (int i = 1; i < a.Count; i++)
                        same &= Near(Time(a.Start + i) - Time(a.Start + i - 1), Time(b.Start + i) - Time(b.Start + i - 1));
                    if (!same) break;
                    if (a.Count == 2) pairs++;
                }
                return shared >= 6 && shared * 3 >= count * 2 && pairs >= 2;
            }

            bool SplitsPair(int seam, bool left, bool right)
            {
                int cut = rows[seam].Start;
                if (left == right || rows[seam - 1].Right != rows[seam].Right || cut < 2 || cut + 1 >= attacks.Count) return false;
                double gap = Time(cut) - Time(cut - 1);
                return gap < NearSimultaneous || gap <= .04 && Time(cut - 1) - Time(cut - 2) >= 1.99 * gap &&
                    Time(cut + 1) - Time(cut) >= 1.99 * gap;
            }

            bool PlainConnected(int start, int end)
            {
                if (insideHold[attacks[start].Index]) return false;
                for (int i = start; i < end; i++)
                    if (notes[attacks[i].Index].Kind != NoteKind.Tap ||
                        attacks[i].Index - attacks[start].Index != i - start || !ReferenceEquals(attacks[i].Profile, attacks[start].Profile) ||
                        i > start && Time(i) <= Time(i - 1)) return false;
                return true;
            }

            bool Safe(List<Slice> candidate, HashSet<(int From, int To, byte Key, int Mode)> original, double horizon)
                => MeasurePlanRisks(notes, attacks, candidate, horizon).IsSubsetOf(original);

            double Time(int index) => AttackTime(notes, attacks, index);
            static bool Near(double a, double b) => Math.Abs(a - b) <= Math.Abs(b) * BoundaryTolerance + Epsilon;

            int Find(int attack)
            {
                int lo = 0, hi = rows.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (rows[mid].End <= attack) lo = mid + 1;
                    else hi = mid;
                }
                return lo;
            }
        }

        internal static void RefineChartRhythms(TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows, bool preferredRight,
            bool measuredPhrases = false)
        {
            if (attacks.Count < 5) return;
            var plain = new bool[attacks.Count];
            int timeline = 0;
            bool hold = false;
            for (int index = 0; index < attacks.Count; index++)
            {
                while (timeline <= attacks[index].Index)
                {
                    var kind = notes[timeline++].Kind;
                    if (kind == NoteKind.HoldEnd) hold = false;
                    else if (kind == NoteKind.HoldStart) hold = true;
                }
                plain[index] = !hold;
            }

            for (int begin = 0; begin < attacks.Count;)
            {
                if (!plain[begin] || !(Note(begin).FollowingTurn > 0)) { begin++; continue; }
                var profile = attacks[begin].Profile;
                int end = begin + 1;
                while (end < attacks.Count && plain[end] &&
                    attacks[end].Index == attacks[end - 1].Index + 1 &&
                    ReferenceEquals(attacks[end].Profile, profile) && Time(end) > Time(end - 1)) end++;
                for (int start = begin; start + 4 < end;)
                {
                    double gap = Gap(start);
                    int stop = start;
                    int note = attacks[start].Index;
                    if (!measuredPhrases && start == begin && start > 0 && note > 0 && notes[note - 1].Kind == NoteKind.HoldEnd &&
                        Time(start) - notes[note - 1].Time < Frame && gap >= Frame &&
                        Near(Gap(start + 1), gap, .1) && Time(start + 2) - Time(start) < attacks[start].CellLength &&
                        Gap(start + 2) > 4 * attacks[start].CellLength)
                    {
                        double width = attacks[start].CellLength;
                        if (TryApply([(start, start + 3, width, 1)], profile, !rows[Find(start - 1)].Right))
                        {
                            start += 3;
                            continue;
                        }
                    }
                    if (!measuredPhrases && start >= begin + 3 && start + 7 < end && gap is >= .039 and <= .05 &&
                        Note(start).FollowingTurn >= .6 && Gap(start + 1) < .014 &&
                        Near(Time(start + 3) - Time(start), 2 * gap, .012) &&
                        Near(Gap(start - 1), 2 * gap, .012) && Near(Gap(start - 2), 2 * gap, .012) &&
                        Near(Gap(start + 3), 2 * gap, .012) && Near(Gap(start + 4), 2 * gap, .012))
                    {
                        var groups = new List<(int Start, int End, double Width, int Beats)> { (start, start + 3, 2 * gap, 1) };
                        int cursor = start + 3;
                        while (cursor + 1 < end && Near(Gap(cursor), 2 * gap, .012) && groups.Count < 32)
                        {
                            groups.Add((cursor, cursor + 1, 2 * gap, 1));
                            cursor++;
                        }
                        if (TryApply(groups, profile)) { start = cursor; continue; }
                    }
                    if ((measuredPhrases || gap is >= .025 and <= .075) && gap > 0 && Near(Note(start).FollowingTurn, .5, .02))
                    {
                        if (measuredPhrases && TryChangingQuarterTurns(start, end, profile, out int quarterEnd))
                        { start = quarterEnd; continue; }
                        while (stop + 1 < end && Near(Gap(stop), gap, .012) && Near(Note(stop).FollowingTurn, .5, .02)) stop++;
                        bool closedSquare = (measuredPhrases || gap is >= .025 and <= .075) && start > begin && stop + 1 < end && stop - start == 4 &&
                            Near(Gap(start - 1), 2 * gap, .025) && Near(Gap(stop), 2 * gap, .025) &&
                            Near(Note(start - 1).FollowingTurn, 1, .035) && Near(Note(stop).FollowingTurn, 1, .035);
                        if (closedSquare && (measuredPhrases || gap >= .05))
                        {
                            if (TryApply([(start, start + 2, 2 * gap, 1), (start + 2, start + 4, 2 * gap, 1)],
                                profile, false, reverseRightPair: true)) { start += 4; continue; }
                        }
                        if (stop - start >= 6 || !measuredPhrases && closedSquare)
                        {
                            var groups = new List<(int Start, int End, double Width, int Beats)>();
                            for (int attack = start; attack + 4 <= stop + 1; attack += 4)
                                groups.Add((attack, attack + 4, 4 * gap, 1));
                            if (groups.Count >= 2 || closedSquare)
                            {
                                int cursor = groups[groups.Count - 1].End;
                                if (!closedSquare && cursor + 2 < end && Near(Gap(cursor - 1), gap, .012) && Gap(cursor) <= gap * .5 && Gap(cursor + 1) > 2 * gap)
                                    groups.Add((cursor, cursor + 2, 4 * gap, 1));
                                else if (measuredPhrases && !closedSquare && cursor == stop && cursor + 1 < end &&
                                    Near(Gap(cursor - 1), gap, .012) && Near(Gap(cursor), 4 * gap, .025) &&
                                    Near(Note(cursor).FollowingTurn, 2, .035))
                                    groups.Add((cursor, cursor + 1, 4 * gap, 1));
                                if (TryApply(groups, profile)) { start = groups[groups.Count - 1].End; continue; }
                            }
                        }
                    }

                    if (measuredPhrases && TryEvenThirds(begin, start, end, profile, out int thirdsEnd))
                    { start = thirdsEnd; continue; }

                    bool matched = false;
                    for (int size = 3; size <= 4 && start + 3 * size <= end; size++)
                    {
                        double period = Time(start + size) - Time(start);
                        if (!(Note(start).FollowingTurn > 0) ||
                            (measuredPhrases
                                ? Gap(start) > period * .20 || Time(start + size - 1) - Time(start) > period * .40 || Gap(start + size - 1) < period * .60
                                : Gap(start) > .014 || period < .045 || period > .10 ||
                                    Time(start + size - 1) - Time(start) > period * .55 || Gap(start + size - 1) < period * .45)) continue;
                        bool Cycle(int attack)
                        {
                            for (int offset = 0; offset < size - 1; offset++)
                                if (!Near(Gap(attack + offset), Gap(start + offset), .035)) return false;
                            return true;
                        }
                        var groups = new List<(int Start, int End, double Width, int Beats)> { (start, start + size, period, 1) };
                        stop = start + size;
                        while (stop + size <= end && Cycle(stop))
                        {
                            double elapsed = Time(stop) - Time(groups[groups.Count - 1].Start);
                            int step = measuredPhrases ? BeatStep(elapsed, period) : Near(elapsed, period, .012) ? 1 : 0;
                            if (step == 0) break;
                            groups.Add((stop, stop + size, period, step));
                            stop += size;
                        }
                        if (groups.Count < 3) continue;
                        int cursor = stop;
                        int limit = Math.Min(end, stop + 192);
                        while (cursor < limit)
                        {
                            int steps = BeatStep(Time(cursor) - Time(groups[groups.Count - 1].Start), period);
                            if (steps == 0) break;
                            if (cursor + 2 < end && Near(Gap(cursor), 2 * period, .018) &&
                                Near(Gap(cursor + 1), 2 * period, .018)) break;
                            int next = cursor + 1;
                            while (next < end && Time(next) - Time(cursor) <= period * .24) next++;
                            if (next - cursor > 4 || next < end && Time(next) - Time(cursor) < period * .55) break;
                            groups.Add((cursor, next, period, steps));
                            cursor = next;
                        }
                        int attempts = 0;
                        while (groups.Count >= 3 && !TryApply(groups, profile))
                        {
                            if (++attempts == 5) { groups.Clear(); break; }
                            groups.RemoveAt(groups.Count - 1);
                        }
                        if (groups.Count < 3) continue;
                        start = groups[groups.Count - 1].End;
                        matched = true;
                        break;
                    }
                    if (matched) continue;
                    if (measuredPhrases)
                    {
                        if (TryMixedPulse(start, end, profile, out int next)) { start = next; continue; }
                        start++;
                        continue;
                    }

                    // A measured pair can open a new pulse after the two slower beats.
                    // Group by the pair's onset, not by the earlier folded grid cells.
                    if (start >= begin + 2 && gap <= .02 && Note(start).FollowingTurn is > 0 and <= .25 &&
                        Gap(start + 2) <= .02)
                    {
                        double pulse = Time(start + 2) - Time(start);
                        if (pulse is >= .097 and <= .19 && Near(Gap(start - 1), 2 * pulse, .025) &&
                            Near(Gap(start - 2), 2 * pulse, .025))
                        {
                            var groups = new List<(int Start, int End, double Width, int Beats)>();
                            int cursor = start, pairs = 0;
                            while (cursor < end && Near(Time(cursor) - Time(start), groups.Count * pulse, .012))
                            {
                                int next = cursor + 1;
                                if (next < end && Near(Gap(cursor), gap, .12)) next++;
                                if (next < end && Time(next) - Time(cursor) < pulse * .7) break;
                                groups.Add((cursor, next, pulse, 1));
                                if (next - cursor == 2) pairs++;
                                cursor = next;
                            }
                            if (pairs >= 4 && TryApply(groups, profile, preferredRight)) { start = cursor; continue; }
                        }
                    }

                    if (gap >= .097 && gap <= .19 && Near(Note(start).FollowingTurn, .5, .035))
                    {
                        var groups = new List<(int Start, int End, double Width, int Beats)>();
                        int cursor = start, direct = 0;
                        while (cursor < end)
                        {
                            int steps = groups.Count == 0 ? 1 : (int)Math.Round(Math.Min(5, (Time(cursor) - Time(groups[groups.Count - 1].Start)) / gap));
                            if (steps is < 1 or > 4 || groups.Count != 0 && !Near(Time(cursor) - Time(groups[groups.Count - 1].Start), gap * steps, .012)) break;
                            int next = cursor + 1;
                            while (next < end && Time(next) - Time(cursor) <= gap * .24) next++;
                            if (next - cursor > 3 || next < end && Time(next) - Time(cursor) < gap * .65) break;
                            groups.Add((cursor, next, gap, steps));
                            if (steps == 1) direct++;
                            cursor = next;
                        }
                        bool slowEntry = start >= begin + 2 && Near(Gap(start - 1), 2 * gap, .025) &&
                            Near(Note(start - 1).FollowingTurn, 1, .035) &&
                            Note(start - 2).FollowingTurn > 0 &&
                            Gap(start - 2) / Note(start - 2).FollowingTurn <= gap * .75 &&
                            Gap(start - 2) >= MinimumCellLength(profile.BpmLimit) * 1.1;
                        if (direct >= 4 && TryApply(groups, profile, slowEntry ? preferredRight : null))
                        { start = cursor; continue; }
                    }
                    start++;
                }
                begin = end;
            }

            bool TryChangingQuarterTurns(int start, int end, TechniqueProfile profile, out int stop)
            {
                stop = start;
                var groups = new List<(int Start, int End, double Width, int Beats)>();
                int cursor = start;
                bool changed = false;
                while (cursor + 4 < end)
                {
                    double unit = Gap(cursor);
                    bool fits = unit > 0;
                    for (int i = cursor; i < cursor + 4 && fits; i++)
                        fits = Near(Gap(i), unit, .025) && Near(Note(i).FollowingTurn, .5, .02);
                    if (!fits) break;
                    double width = 4 * unit;
                    changed |= groups.Count > 0 && !Near(width, groups[groups.Count - 1].Width, .025);
                    groups.Add((cursor, cursor + 4, width, 1));
                    cursor += 4;
                }
                if (!changed || groups.Count < 3) return false;
                double tailWidth = groups[groups.Count - 1].Width;
                if (cursor + 1 < end && Near(Note(cursor).FollowingTurn, 2, .035) && Near(Gap(cursor), tailWidth, .025))
                {
                    groups.Add((cursor, cursor + 1, tailWidth, 1));
                    cursor++;
                }
                stop = cursor;
                return TryApply(groups, profile);
            }

            bool TryEvenThirds(int begin, int start, int end, TechniqueProfile profile, out int stop)
            {
                stop = start;
                if (!Near(Note(start).FollowingTurn, 1d / 3, .035)) return false;
                double firstPulse = 3 * Gap(start);
                double entryPulse = start >= begin + 2 ? Gap(start - 2) : firstPulse;
                int entrySteps = start >= begin + 2 && IsFullBeat(start - 2, entryPulse)
                    ? FullSteps(start - 1, entryPulse) : 0;
                bool entry = entrySteps != 0;
                if (!entry && start != 0) return false;

                // Check each metric beat against its own onsets. A tempo change at a
                // downbeat changes the width, but does not restart the hand sequence.
                var groups = new List<(int Start, int End, double Width, int Beats)>();
                int cursor = start, steps = entry ? entrySteps : 1;
                while (cursor + 2 < end)
                {
                    double third = Gap(cursor);
                    if (!IsThird(cursor, third) || !IsThird(cursor + 1, third)) break;
                    double pulse = 3 * third;
                    int nextSteps = 0;
                    if (cursor + 3 < end)
                    {
                        nextSteps = BeatStep(Time(cursor + 3) - Time(cursor), pulse);
                        if (nextSteps == 0 || !Near(Note(cursor + 2).FollowingTurn, nextSteps - 2d / 3, .035)) break;
                    }
                    else if (end != attacks.Count) break;
                    groups.Add((cursor, cursor + 3, pulse, steps));
                    steps = nextSteps;
                    cursor += 3;
                }
                if (groups.Count == 0) return false;
                double lastPulse = groups[groups.Count - 1].Width;
                double exitPulse = lastPulse;
                bool exit = IsFullBeat(cursor, exitPulse);
                if (!exit && cursor + 2 < end)
                {
                    // The downbeat ending the thirds may start a slower or faster
                    // clock. Require consecutive whole beats on the new side.
                    double nextPulse = Gap(cursor);
                    if (IsFullBeat(cursor, nextPulse) && IsFullBeat(cursor + 1, nextPulse))
                    {
                        exit = true;
                        exitPulse = nextPulse;
                    }
                }
                if (entry ? !exit && (cursor != end || end != attacks.Count) : !exit || !IsFullBeat(cursor + 1, lastPulse))
                    return false;
                if (exit)
                {
                    do
                    {
                        groups.Add((cursor, cursor + 1, exitPulse, steps));
                        steps = 1;
                        cursor++;
                    } while (cursor < end && IsFullBeat(cursor - 1, exitPulse));
                }
                stop = cursor;
                if (entry)
                {
                    // A folded grid can have the wrong phase even on the plain beats
                    // before a rest. Include their confirmed clock when it is split.
                    int anchor = start - 1;
                    while (anchor > begin && IsFullBeat(anchor - 1, entryPulse)) anchor--;
                    bool folded = false;
                    for (int a = anchor; a < start && !folded; a++)
                    {
                        int row = Find(a);
                        folded = rows[row].Start != a || rows[row].End != a + 1 ||
                            a > anchor && rows[row].Right == rows[Find(a - 1)].Right;
                    }
                    if (folded)
                    {
                        var prefix = new List<(int Start, int End, double Width, int Beats)>();
                        for (int a = anchor; a < start; a++) prefix.Add((a, a + 1, entryPulse, 1));
                        groups.InsertRange(0, prefix);
                        return TryApply(groups, profile);
                    }
                    return TryApply(groups, profile, rows[Find(start - 1)].Right ^ ((entrySteps & 1) != 0));
                }
                return TryApply(groups, profile);

                bool IsThird(int attack, double third) => Near(Note(attack).FollowingTurn, 1d / 3, .035) && Near(Gap(attack), third, .025);
                bool IsFullBeat(int attack, double pulse) => FullSteps(attack, pulse) == 1;
                int FullSteps(int attack, double pulse)
                {
                    if (attack + 1 >= end) return 0;
                    int elapsed = BeatStep(Gap(attack), pulse);
                    return elapsed != 0 && Near(Note(attack).FollowingTurn, elapsed, .035) ? elapsed : 0;
                }
            }

            bool TryMixedPulse(int start, int end, TechniqueProfile profile, out int stop)
            {
                stop = start;
                double turn = Note(start).FollowingTurn;
                if (turn is <= 0 or > .5) return false;
                double secondsPerTurn = Gap(start) / turn;
                foreach (double beatTurn in new[] { 1d, 2d })
                {
                    double pulse = secondsPerTurn * beatTurn;
                    if (pulse < MinimumRepeatSafety || pulse > 4 * MinimumCellLength(profile.BpmLimit)) continue;
                    var groups = new List<(int Start, int End, double Width, int Beats)>();
                    int cursor = start, steps = 1, sizes = 0, decorated = 0, direct = 0;
                    while (cursor < end)
                    {
                        int next = cursor + 1;
                        while (next < end && Time(next) - Time(cursor) <= pulse * .40 + Epsilon) next++;
                        int count = next - cursor;
                        if (count > 4) break;
                        double angle = 0;
                        bool fits = true;
                        for (int attack = cursor; attack < Math.Min(next, end - 1); attack++)
                        {
                            double part = Note(attack).FollowingTurn;
                            if (!(part > 0) || !Near(Gap(attack), part * secondsPerTurn, .025)) { fits = false; break; }
                            angle += part;
                        }
                        if (!fits) break;
                        groups.Add((cursor, next, pulse, steps));
                        sizes |= 1 << count;
                        if (count > 1) decorated++;
                        if (steps == 1) direct++;
                        if (next < end)
                        {
                            steps = BeatStep(Time(next) - Time(cursor), pulse);
                            if (steps == 0 || !Near(angle, steps * beatTurn, .025)) break;
                        }
                        cursor = next;
                    }
                    // Mixed sizes and repeated decorated beats distinguish this clock
                    // from an arbitrary grouping of evenly spaced single notes.
                    if (groups.Count < 6 || direct < 5 || decorated < 3 || (sizes & (sizes - 1)) == 0) continue;
                    stop = groups[groups.Count - 1].End;
                    if (TryApply(groups, profile)) return true;
                }
                return false;
            }

            bool TryApply(List<(int Start, int End, double Width, int Beats)> groups, TechniqueProfile profile,
                bool? requiredLead = null, bool reverseRightPair = false)
            {
                int start = groups[0].Start, stop = groups[groups.Count - 1].End;
                int first = Find(start), last = Find(stop - 1);
                var head = rows[first];
                var tail = rows[last];
                double horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                if (measuredPhrases) horizon = Math.Max(MinimumRepeatSafety, horizon);
                if (measuredPhrases && !requiredLead.HasValue && start > 0)
                {
                    var previous = rows[Find(start - 1)];
                    double width = groups[0].Width;
                    if (previous.End == start && plain[previous.Start] &&
                        ReferenceEquals(attacks[previous.Start].Profile, profile) && Near(previous.PressWindow, width, .025))
                    {
                        int steps = BeatStep(Time(start) - Time(previous.Start), width);
                        if (steps != 0) requiredLead = previous.Right ^ ((steps & 1) != 0);
                    }
                }
                bool? exitLead = measuredPhrases && !requiredLead.HasValue && stop == tail.End
                    ? LeadFromFollowingPhrase(groups, last + 1, profile) : null;
                bool? anchoredLead = requiredLead ?? exitLead;
                if (start == head.Start && stop == tail.End && groups.Count == last - first + 1 &&
                    (!anchoredLead.HasValue || head.Right == anchoredLead.Value))
                {
                    bool matches = true;
                    for (int index = 0; index < groups.Count && matches; index++)
                    {
                        var group = groups[index];
                        var row = rows[first + index];
                        matches = row.Start == group.Start && row.End == group.End &&
                            row.ReverseKeyOrder == (reverseRightPair && row.Right) &&
                            (index == 0 || (row.Right != rows[first + index - 1].Right) == ((group.Beats & 1) != 0));
                    }
                    if (matches) return true;
                }
                if (start > head.Start && !CanCut(start) || stop < tail.End && !CanCut(stop)) return false;
                if (!plain[head.Start] || !plain[tail.End - 1]) return false;
                bool lead = head.Right;
                if (requiredLead.HasValue) lead = requiredLead.Value;
                else if (first > 0 && start == head.Start && Near(Time(start) - Time(rows[first - 1].Start), groups[0].Width, .025))
                    lead = !rows[first - 1].Right;
                else if (start == 0 || Time(start) - Time(start - 1) >= 4 * Math.Max(groups[0].Width, rows[Math.Max(0, first - 1)].PressWindow))
                    lead = preferredRight;
                HashSet<(int From, int To, byte Key, int Mode)>? original = null;
                for (int phase = 0; phase < (requiredLead.HasValue || start == 0 && !exitLead.HasValue ? 1 : 2); phase++)
                {
                    var replacement = new List<Slice>();
                    if (start > head.Start)
                        replacement.Add(new Slice(head.Start, start, head.Length, head.PressWindow, head.Right, head.TimeStart, Time(start)));
                    bool right = (exitLead ?? lead) ^ (phase != 0);
                    bool fits = true;
                    foreach (var group in groups)
                    {
                        if (group.Start != start && (group.Beats & 1) != 0) right = !right;
                        double onset = Time(group.Start);
                        double boundary = group.End < attacks.Count ? Time(group.End) : onset + group.Width;
                        double finish = Math.Min(boundary, Math.Max(onset + group.Width, Time(group.End - 1) + Frame));
                        if (group.End - group.Start > profile.Layout(right).Capacity || finish <= Time(group.End - 1)) { fits = false; break; }
                        replacement.Add(new Slice(group.Start, group.End, group.Width, group.Width, right, onset, finish,
                            reverseRightPair && right));
                    }
                    if (!fits) continue;
                    if (stop < tail.End)
                        replacement.Add(new Slice(stop, tail.End, tail.Length, tail.PressWindow, tail.Right, Time(stop), tail.TimeEnd));
                    if (replacement.Count == last - first + 1 && replacement.TrueForAll(row =>
                        rows[Find(row.Start)].Start == row.Start && rows[Find(row.Start)].End == row.End &&
                        rows[Find(row.Start)].Right == row.Right && rows[Find(row.Start)].ReverseKeyOrder == row.ReverseKeyOrder))
                        return true;
                    original ??= MeasurePlanRisks(notes, attacks, rows, horizon, measuredPhrases);
                    var candidate = new List<Slice>(rows);
                    candidate.RemoveRange(first, last - first + 1);
                    candidate.InsertRange(first, replacement);
                    int nextIndex = first + replacement.Count;
                    if (reverseRightPair && stop == tail.End && nextIndex < candidate.Count)
                    {
                        var next = candidate[nextIndex];
                        double onset = Time(next.Start);
                        // The next occupied grid cell can start before the square has finished.
                        // Use its actual attack boundary so it cannot cap the square's last release early.
                        if (next.TimeStart < replacement[replacement.Count - 1].TimeEnd &&
                            next.TimeStart < onset && onset < next.TimeEnd && plain[next.Start] &&
                            ReferenceEquals(attacks[next.Start].Profile, profile))
                            candidate[nextIndex] = new Slice(next.Start, next.End, next.Length, next.PressWindow,
                                next.Right, onset, next.TimeEnd, next.ReverseKeyOrder);
                    }
                    if (!MeasurePlanRisks(notes, attacks, candidate, horizon, measuredPhrases).IsSubsetOf(original)) continue;
                    rows.RemoveRange(first, last - first + 1);
                    rows.InsertRange(first, replacement);
                    if (nextIndex < rows.Count) rows[nextIndex] = candidate[nextIndex];
                    return true;
                }
                return false;

                bool CanCut(int attack) => Time(attack) - Time(attack - 1) >= Math.Min(.02, groups[0].Width * .25) &&
                    plain[attack - 1] && plain[attack] && ReferenceEquals(attacks[attack - 1].Profile, attacks[attack].Profile);
            }

            bool? LeadFromFollowingPhrase(List<(int Start, int End, double Width, int Beats)> groups,
                int next, TechniqueProfile profile)
            {
                if (groups.Count < 3 || next + 2 >= rows.Count) return null;
                var last = groups[groups.Count - 1];
                var entry = rows[next];
                double width = entry.PressWindow;
                // Use an established change of rhythm as an exit anchor. A lone
                // grid row, an unmeasured gap or a different layout is not an anchor.
                int steps = BeatStep(Time(entry.Start) - Time(last.Start), last.Width);
                if (steps == 0 || entry.Count < 2 ||
                    entry.Count == last.End - last.Start && Near(width, last.Width, .08) ||
                    attacks[entry.Start].Index != attacks[last.End - 1].Index + 1) return null;
                for (int i = next; i < next + 3; i++)
                {
                    var row = rows[i];
                    if (!Near(row.PressWindow, width, .025) || row.Count != entry.Count ||
                        Time(row.End - 1) - Time(row.Start) > width * .40 ||
                        i > next && (row.Right == rows[i - 1].Right ||
                            !Near(Time(row.Start) - Time(rows[i - 1].Start), width, .025))) return null;
                    for (int a = row.Start; a < row.End; a++)
                        if (!plain[a] || !ReferenceEquals(attacks[a].Profile, profile) ||
                            attacks[a].Index != attacks[a - 1].Index + 1) return null;
                }
                for (int i = 1; i < groups.Count; i++) steps ^= groups[i].Beats;
                return entry.Right ^ ((steps & 1) != 0);
            }

            int Find(int attack)
            {
                int lo = 0, hi = rows.Count - 1;
                while (lo < hi)
                {
                    int middle = lo + (hi - lo) / 2;
                    if (rows[middle].End <= attack) lo = middle + 1; else hi = middle;
                }
                return lo;
            }
            TechniqueNote Note(int attack) => notes[attacks[attack].Index];
            double Time(int attack) => Note(attack).Time;
            double Gap(int attack) => Time(attack + 1) - Time(attack);
            static int BeatStep(double gap, double period)
            {
                double ratio = gap / period;
                int steps = (int)Math.Round(Math.Min(ratio, 5));
                return steps is >= 1 and <= 4 && Math.Abs(ratio - steps) <= .018 ? steps : 0;
            }
            static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= expected * tolerance + Epsilon;
        }

        internal static void PreferLeadAfterPickup(TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows, bool preferredRight)
        {
            int timeline = 0;
            bool holding = false;
            for (int first = 1; first + 7 < rows.Count; first++)
            {
                var pickup = rows[first - 1];
                var head = rows[first];
                while (timeline < attacks[pickup.Start].Index)
                {
                    var kind = notes[timeline++].Kind;
                    if (kind == NoteKind.HoldStart) holding = true;
                    else if (kind == NoteKind.HoldEnd) holding = false;
                }
                double pulse = head.PressWindow;
                var profile = attacks[head.Start].Profile;
                if (holding || head.Right == preferredRight || pickup.Count != 3 ||
                    !(notes[attacks[pickup.End - 2].Index].FollowingTurn > 0) ||
                    Near(pickup.PressWindow, pulse) || !Near(Time(head.Start) - Time(pickup.End - 2), 2 * pulse) ||
                    Time(pickup.End - 2) - Time(pickup.Start) >= pulse * .3 ||
                    Time(pickup.End - 1) - Time(pickup.End - 2) >= pulse * .25) continue;

                int decorations = 0;
                bool matched = true;
                for (int index = first; index < first + 8 && matched; index++)
                {
                    var row = rows[index];
                    double width = Time(row.End - 1) - Time(row.Start);
                    matched = row.Count <= 2 && width <= pulse * .25 && Near(row.PressWindow, pulse) &&
                        Plain(row, profile) && (index == first || BeatGap(index, pulse));
                    if (row.Count == 2) decorations++;
                }
                if (!matched || decorations < 2 || !Plain(pickup, profile)) continue;

                int stop = first + 8;
                double horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                for (; stop + 2 < rows.Count && stop - first < 160; stop++)
                {
                    if (!Plain(rows[stop], profile) || attacks[rows[stop].Start].Index != attacks[rows[stop - 1].End - 1].Index + 1) break;
                    double gap = Time(rows[stop].Start) - Time(rows[stop - 1].End - 1);
                    double next = Time(rows[stop + 1].Start) - Time(rows[stop].Start);
                    if (rows[stop].Count == 1 && rows[stop + 1].Count == 1 &&
                        gap >= Math.Max(horizon, 1.8 * pulse) && next >= 1.8 * pulse &&
                        Near(next, Time(rows[stop + 2].Start) - Time(rows[stop + 1].Start))) break;
                }
                if (stop + 2 >= rows.Count || stop - first >= 160 ||
                    Time(rows[stop].Start) - Time(rows[stop - 1].End - 1) < horizon) continue;
                var original = MeasurePlanRisks(notes, attacks, rows, horizon);
                var candidate = new List<Slice>(rows);
                bool fits = true;
                for (int index = first; index < stop; index++)
                {
                    var row = rows[index];
                    if (row.Count >= 5 && !Near(row.PressWindow, pulse) && index > first + 1 &&
                        rows[index - 1].Count == 2 && Near(rows[index - 1].PressWindow, pulse) &&
                        Time(row.Start) - Time(rows[index - 1].End - 1) <= pulse * .5 && row.Right != rows[index - 1].Right)
                    {
                        candidate[index - 1] = rows[index - 1];
                        continue;
                    }
                    if (row.Count > profile.Layout(!row.Right).Capacity) { fits = false; break; }
                    candidate[index] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                        !row.Right, row.TimeStart, row.TimeEnd);
                }
                if (!fits || !MeasurePlanRisks(notes, attacks, candidate, horizon).IsSubsetOf(original)) continue;
                for (int index = first; index < stop; index++) rows[index] = candidate[index];
                first = stop - 1;
            }

            bool Plain(Slice row, TechniqueProfile profile)
            {
                for (int index = row.Start; index < row.End; index++)
                    if (notes[attacks[index].Index].Kind != NoteKind.Tap || !ReferenceEquals(attacks[index].Profile, profile) ||
                        index > row.Start && (attacks[index].Index != attacks[index - 1].Index + 1 || Time(index) <= Time(index - 1))) return false;
                return true;
            }
            bool BeatGap(int index, double pulse)
            {
                double gap = Time(rows[index].Start) - Time(rows[index - 1].Start);
                return Near(gap, pulse) || Near(gap, 2 * pulse);
            }
            double Time(int attack) => AttackTime(notes, attacks, attack);
            static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= expected * .012 + Epsilon;
        }

        internal static void PreferPostHoldLead(TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows, bool preferredRight)
        {
            for (int first = 1; first + 4 < rows.Count; first++)
            {
                var head = rows[first];
                int note = attacks[head.Start].Index;
                var profile = attacks[head.Start].Profile;
                double pulse = head.PressWindow, horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                if (head.Right == preferredRight || notes[note - 1].Kind != NoteKind.HoldEnd ||
                    Time(head.Start) - notes[note - 1].Time < Math.Max(horizon, 4 * pulse)) continue;

                var candidate = new List<Slice>();
                int direct = 0;
                HashSet<(int From, int To, byte Key, int Mode)>? originalRisks = null;
                for (int index = first; index < rows.Count; index++)
                {
                    var row = rows[index];
                    if (index > first)
                    {
                        var prior = rows[index - 1];
                        double gap = Time(row.Start) - Time(prior.Start);
                        if (Near(gap, 2 * pulse) && direct >= 4 && Time(row.Start) - Time(prior.End - 1) >= horizon)
                        {
                            originalRisks ??= MeasurePlanRisks(notes, attacks, rows, horizon);
                            var trial = new List<Slice>(rows);
                            trial.RemoveRange(first, candidate.Count);
                            trial.InsertRange(first, candidate);
                            if (MeasurePlanRisks(notes, attacks, trial, horizon).IsSubsetOf(originalRisks))
                            {
                                for (int offset = 0; offset < candidate.Count; offset++) rows[first + offset] = candidate[offset];
                                break;
                            }
                        }
                        if (!Near(gap, pulse) || row.Right == prior.Right) break;
                        direct++;
                    }
                    if (!Near(row.PressWindow, pulse) || row.Count > profile.Layout(!row.Right).Capacity ||
                        Time(row.End - 1) - Time(row.Start) > pulse * .25) break;
                    bool taps = true;
                    for (int attack = row.Start; attack < row.End && taps; attack++)
                        taps = notes[attacks[attack].Index].Kind == NoteKind.Tap &&
                            ReferenceEquals(attacks[attack].Profile, profile) && attacks[attack].Index - note == attack - head.Start;
                    if (!taps) break;
                    candidate.Add(new Slice(row.Start, row.End, row.Length, row.PressWindow,
                        !row.Right, row.TimeStart, row.TimeEnd));
                }
            }

            double Time(int attack) => AttackTime(notes, attacks, attack);
            static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= expected * BoundaryTolerance + Epsilon;
        }

        internal static void RepairMeasuredSeamBeat(TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows)
        {
            int timeline = 0;
            bool holding = false;
            for (int index = 2; index + 2 < rows.Count; index++)
            {
                var before = rows[index - 1];
                var after = rows[index + 1];
                var row = rows[index];
                var profile = attacks[row.Start].Profile;
                double pulse = (Time(after.Start) - Time(before.Start)) * .5;
                double tolerance = pulse * .025 + Epsilon;
                if (row.Count is < 2 or > 4 || row.Right != before.Right || row.Right != after.Right ||
                    row.Right == rows[index - 2].Right || row.Right == rows[index + 2].Right ||
                    row.Count > profile.Layout(!row.Right).Capacity ||
                    Math.Abs(row.PressWindow - attacks[row.Start].CellLength) > Epsilon ||
                    pulse <= row.PressWindow * 1.1 || Math.Abs(before.PressWindow - pulse) > tolerance ||
                    Math.Abs(after.PressWindow - pulse) > tolerance ||
                    Math.Abs(Time(row.Start) - Time(before.Start) - pulse) > tolerance ||
                    Math.Abs(Time(before.Start) - Time(rows[index - 2].Start) - pulse) > tolerance ||
                    Math.Abs(Time(rows[index + 2].Start) - Time(after.Start) - pulse) > tolerance ||
                    Time(row.End - 1) - Time(row.Start) > pulse * .25) continue;

                int first = rows[index - 2].Start, stop = rows[index + 2].End;
                while (timeline < attacks[first].Index)
                {
                    var kind = notes[timeline++].Kind;
                    if (kind == NoteKind.HoldStart) holding = true;
                    else if (kind == NoteKind.HoldEnd) holding = false;
                }
                if (holding) continue;
                bool taps = true;
                for (int attack = first; attack < stop && taps; attack++)
                    taps = notes[attacks[attack].Index].Kind == NoteKind.Tap &&
                        ReferenceEquals(attacks[attack].Profile, profile) &&
                        attacks[attack].Index - attacks[first].Index == attack - first;
                if (!taps) continue;

                double horizon = MinimumCellLength(profile.BpmLimit) * 1.1;
                var original = MeasurePlanRisks(notes, attacks, rows, horizon);
                rows[index] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                    !row.Right, row.TimeStart, row.TimeEnd);
                if (!MeasurePlanRisks(notes, attacks, rows, horizon).IsSubsetOf(original)) rows[index] = row;
            }

            double Time(int attack) => AttackTime(notes, attacks, attack);
        }

        internal static void RepairHalfPulseEntry(TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows)
        {
            const double tolerance = .012;
            int timeline = 0;
            bool holding = false;
            for (int seam = 3; seam + 2 < rows.Count; seam++)
            {
                var anchor = rows[seam];
                var profile = attacks[anchor.Start].Profile;
                while (timeline < attacks[rows[seam - 2].Start].Index)
                {
                    var kind = notes[timeline++].Kind;
                    if (kind == NoteKind.HoldStart) holding = true;
                    else if (kind == NoteKind.HoldEnd) holding = false;
                }
                double pulse = Gap(seam + 1), slow = pulse * 2;
                if (holding || pulse < MinimumCellLength(profile.BpmLimit) ||
                    !Near(anchor.PressWindow, slow) || !Near(Gap(seam), slow) || !Near(Gap(seam - 1), slow) ||
                    !Near(Gap(seam + 2), pulse) || anchor.Right != rows[seam + 1].Right ||
                    anchor.Right == rows[seam - 1].Right || rows[seam - 1].Right == rows[seam - 2].Right ||
                    !Plain(seam - 2) || !Plain(seam - 1) || !Plain(seam) ||
                    !(Turn(seam + 1) > 0) || !Near(Turn(seam - 1), 2 * Turn(seam)) ||
                    !Near(Turn(seam + 1), Turn(seam))) continue;

                int stop = seam + 1;
                while (stop < rows.Count && Near(Gap(stop), pulse) && Plain(stop) && Near(Turn(stop - 1), Turn(seam))) stop++;
                int count = stop - seam - 1;
                if (stop == rows.Count || !Plain(stop) || Gap(stop) < slow * (1 - tolerance) || count < 2 || count == 3) continue;
                var candidate = new List<Slice>(rows);
                for (int index = seam + 1; index < stop; index++)
                {
                    var row = rows[index];
                    bool right = anchor.Right ^ ((index - seam) % 2 != 0);
                    candidate[index] = new Slice(row.Start, row.End, row.Length, row.PressWindow,
                        right, row.TimeStart, row.TimeEnd);
                }
                double horizon = pulse * 1.05;
                if (!MeasurePlanRisks(notes, attacks, candidate, horizon).IsSubsetOf(MeasurePlanRisks(notes, attacks, rows, horizon))) continue;
                for (int index = seam + 1; index < stop; index++) rows[index] = candidate[index];
                seam = stop - 1;

                bool Plain(int index) => rows[index].Count == 1 &&
                    notes[attacks[rows[index].Start].Index].Kind == NoteKind.Tap &&
                    ReferenceEquals(attacks[rows[index].Start].Profile, profile) &&
                    (index == seam - 2 || attacks[rows[index].Start].Index == attacks[rows[index - 1].Start].Index + 1);
            }

            double Gap(int index) => AttackTime(notes, attacks, rows[index].Start) - AttackTime(notes, attacks, rows[index - 1].Start);
            double Turn(int index) => notes[attacks[rows[index].Start].Index].FollowingTurn;
            static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= tolerance * expected + Epsilon;
        }

        private static HashSet<(int From, int To, byte Key, int Mode)> MeasurePlanRisks(
            TechniqueNote[] notes, List<Attack> attacks, List<Slice> rows, double horizon, bool ignoreFixedLazyTail = false)
        {
            var risks = new HashSet<(int, int, byte, int)>();
            var strokes = AssignKeySlots(notes, attacks, rows);
            var last = new int[256];
            for (int mode = 0; mode < 2; mode++)
            {
                if (mode != 0) strokes = AssignKeySlots(notes, attacks, rows);
                ResolvePhysicalKeys(notes, attacks, strokes, rows, mode != 0);
                for (int i = 0; i < last.Length; i++) last[i] = -1;
                foreach (var stroke in strokes)
                {
                    double down = notes[stroke.Down].Time;
                    int prior = last[stroke.Key];
                    if (prior >= 0 && down - notes[prior].Time < horizon) risks.Add((prior, stroke.Down, stroke.Key, mode));
                    // The lazy tail deliberately releases 15 ms after the chart ends,
                    // independent of which key is used for that final attack.
                    bool lazyFinalAttack = ignoreFixedLazyTail && mode != 0 && stroke.Down == notes.Length - 1 &&
                        stroke.ReleaseAt == down + LazyFinalReleaseDelay;
                    if (!stroke.Hold && !lazyFinalAttack && stroke.ReleaseAt - down < Frame)
                        risks.Add((stroke.Down, -1, stroke.Key, mode));
                    last[stroke.Key] = stroke.Down;
                }
            }
            return risks;
        }

        private static double MinimumCellLength(double limit)
        {
            if (!TechniqueTimeline.IsFinite(limit) || limit <= 0) limit = 500;
            return 30.0 / Math.Min(1e6, Math.Max(1, limit));
        }

        private static double GridLength(double bpm, double rate, double fallback, double limit)
        {
            // Bound untrusted config/timing before multiplying and folding.
            if (!TechniqueTimeline.IsFinite(limit) || limit <= 0) limit = 500;
            limit = Math.Min(1e6, Math.Max(1, limit));
            if (!TechniqueTimeline.IsFinite(rate) || rate <= 0) rate = fallback;
            double actual = bpm * rate;
            if (!TechniqueTimeline.IsFinite(actual) || actual <= 0) actual = limit;
            while (actual > limit) actual *= 0.5;
            while (actual <= limit * 0.5) actual *= 2.0;
            return 30.0 / actual;
        }

        // The inferred grid is only a baseline. Repair recurring articulations
        // without propagating their phase into unrelated music.
        private static List<Slice> RefineRhythmicPairs(TechniqueNote[] notes, List<Attack> attacks, List<Slice> baseline)
        {
            if (attacks.Count < 2) return baseline;
            var owner = new int[attacks.Count];
            for (int s = 0; s < baseline.Count; s++)
                for (int at = baseline[s].Start; at < baseline[s].End; at++) owner[at] = s;
            var phrases = new List<(int Start, int End, double Width, bool CheckNeighborCadence)>();
            double Gap(int at) => AttackTime(notes, attacks, at) - AttackTime(notes, attacks, at - 1);
            bool Close(double a, double b) => Math.Abs(a - b) <= Math.Max(a, b) * BoundaryTolerance + Epsilon;
            bool Pair(int at)
            {
                if (at + 1 >= attacks.Count) return false;
                var a = attacks[at];
                var b = attacks[at + 1];
                if (!ReferenceEquals(a.Profile, b.Profile) || a.Profile.Layout(false).Capacity < 2 ||
                    a.Profile.Layout(true).Capacity < 2 || notes[a.Index].Kind != NoteKind.Tap ||
                    notes[b.Index].Kind != NoteKind.Tap) return false;
                double gap = Gap(at + 1);
                // Relative articulation, not an absolute frame-time pseudo-double cutoff.
                // Equal-gap streams and triplets fail the surrounding-gap contrast.
                double contrast = gap * 2.0 * (1.0 - BoundaryTolerance);
                return gap > Epsilon && gap <= Math.Min(a.CellLength, b.CellLength) * 0.5 + Epsilon &&
                    (at == 0 || Gap(at) + Epsilon >= contrast) &&
                    (at + 2 == attacks.Count || Gap(at + 2) + Epsilon >= contrast);
            }
            double MinimumPulse(TechniqueProfile profile)
            {
                double limit = profile.BpmLimit;
                if (!TechniqueTimeline.IsFinite(limit) || limit <= 0) limit = 500;
                return 30.0 / Math.Min(1e6, Math.Max(1.0, limit));
            }
            bool HasWiderFoldedCell(int start, int end, double pulse)
            {
                for (int at = start; at < end; at++)
                    if (attacks[at].CellLength >= pulse * 1.25) return true;
                return false;
            }
            int examinedRunEnd = 0;
            for (int start = 0; start + 1 < attacks.Count;)
            {
                if (!Pair(start)) { start++; continue; }
                var first = attacks[start];
                double firstTime = AttackTime(notes, attacks, start);
                double shortGap = Gap(start + 1);
                double minimum = MinimumPulse(first.Profile);
                int end = start + 2;
                double pulse = 0.0;
                if (start >= examinedRunEnd && Pair(end) && ReferenceEquals(first.Profile, attacks[end].Profile) &&
                    Close(shortGap, Gap(end + 1)))
                {
                    pulse = AttackTime(notes, attacks, end) - firstTime;
                    end += 2;
                    while (Pair(end) && ReferenceEquals(first.Profile, attacks[end].Profile) &&
                        Close(shortGap, Gap(end + 1)) &&
                        Close(pulse, AttackTime(notes, attacks, end) - AttackTime(notes, attacks, end - 2))) end += 2;
                }
                if (end > start + 2)
                {
                    double nominal = Math.Max(first.CellLength, attacks[start + 1].CellLength);
                    // Faster pair streams still use the existing multi-pair grid. Never force
                    // every dense two-note motif into its own hand, or bridge a long rest.
                    bool widerFold = end - start >= 6 &&
                        HasWiderFoldedCell(start, end, pulse);
                    bool halfCellParity = pulse >= nominal * 1.5 && shortGap >= nominal * 0.25;
                    bool boundaryDrift = Close(pulse, nominal) && shortGap >= nominal * 0.25;
                    if ((widerFold || halfCellParity || boundaryDrift) && pulse >= minimum * (1.0 - BoundaryTolerance) &&
                        pulse <= nominal * 2.0 * (1.0 + BoundaryTolerance))
                    {
                        bool complete = true;
                        bool? previousRight = null;
                        for (int at = start; at < end; at += 2)
                        {
                            var slice = baseline[owner[at]];
                            if (slice.Start != at || slice.End != at + 2 || previousRight == slice.Right)
                                complete = false;
                            previousRight = slice.Right;
                        }
                        if (!complete || shortGap >= nominal * 0.25)
                            phrases.Add((start, end, pulse, false));
                        start = end;
                        continue;
                    }
                    // A rejected run still gets local pair checks. Do not rescan every suffix.
                    examinedRunEnd = end;
                }

                var left = baseline[owner[start]];
                var right = baseline[owner[start + 1]];
                // A slower isolated split is only actionable in a recurring local rhythm.
                // Do not turn an incidental long-short-long transition next to a faster
                // roll into a new two-key piece and restart that roll's finger order.
                int confirmations = 0;
                for (int at = Math.Max(0, start - 12); at < Math.Min(attacks.Count - 1, start + 13); at++)
                {
                    if (at == start || !ReferenceEquals(first.Profile, attacks[at].Profile) ||
                        Math.Abs(AttackTime(notes, attacks, at) - firstTime) > first.CellLength * 12.0) continue;
                    if (Pair(at) && Close(shortGap, Gap(at + 1))) confirmations++;
                }
                // A repeated wider articulation may take one edge from a two-note piece.
                // Keep denser rolls and pieces with a different metric width intact.
                bool twoSingles = left.Count == 1 && right.Count == 1;
                bool widerArticulation = shortGap >= first.CellLength * 0.30 &&
                    Math.Abs(first.CellLength - attacks[start + 1].CellLength) <= Epsilon;
                bool smallDonors = twoSingles || (widerArticulation && left.Count <= 2 && right.Count <= 2);
                // A complete pair keeps its grid hand unless it touches another pair
                // of the same articulation within the occupied pulse cadence.
                bool completePair = left.Start == start && left.End == start + 2;
                bool HasAdjacentPair(int neighbor) => neighbor >= 0 && Pair(neighbor) &&
                    ReferenceEquals(first.Profile, attacks[neighbor].Profile) && Close(shortGap, Gap(neighbor + 1)) &&
                    Math.Abs(AttackTime(notes, attacks, neighbor) - firstTime) <=
                        Math.Max(first.CellLength, attacks[neighbor].CellLength) * 2.0 * (1.0 + BoundaryTolerance);
                bool needsRepair = left.Right != right.Right ||
                    completePair && (HasAdjacentPair(start - 2) || HasAdjacentPair(start + 2));
                if (shortGap >= Frame && confirmations >= 2 && smallDonors && needsRepair)
                {
                    double nominal = Math.Min(first.CellLength, attacks[start + 1].CellLength);
                    double next = start + 2 < attacks.Count ? AttackTime(notes, attacks, start + 2) - firstTime : nominal;
                    double width = next >= minimum * (1.0 - BoundaryTolerance) &&
                        next <= nominal * 2.0 * (1.0 + BoundaryTolerance) ? next : nominal;
                    phrases.Add((start, start + 2, width, !twoSingles));
                }
                start += 2;
            }
            if (phrases.Count == 0) return baseline;
            var result = new List<Slice>(baseline.Count);
            int cursor = 0;
            int previousPair = -1;
            bool previousHand = false;
            foreach (var phrase in phrases)
            {
                CopyThrough(phrase.Start);
                var first = baseline[owner[phrase.Start]];
                var last = baseline[owner[phrase.End - 1]];
                double gap = previousPair >= 0
                    ? AttackTime(notes, attacks, phrase.Start) - AttackTime(notes, attacks, previousPair)
                    : double.PositiveInfinity;
                // Keep adjacent corrected pulses on alternate hands, even when the pulse
                // changes across a tempo marker. A rest or unrelated passage keeps its own phase.
                bool samePhrase = previousPair >= 0 &&
                    ReferenceEquals(attacks[previousPair].Profile, attacks[phrase.Start].Profile) &&
                    (gap <= Math.Max(phrase.Width, attacks[previousPair].CellLength) * 2.0 * (1.0 + BoundaryTolerance) ||
                     Close(Gap(previousPair + 1), Gap(phrase.Start + 1)) &&
                     gap <= attacks[phrase.Start].CellLength * 6.0 * (1.0 + BoundaryTolerance));
                bool right = samePhrase ? !previousHand : first.Right;
                if (previousPair < 0 && result.Count > 0 && result[result.Count - 1].End == phrase.Start &&
                    result[result.Count - 1].TimeEnd > AttackTime(notes, attacks, phrase.Start) - Epsilon &&
                    owner[phrase.Start - 1] == owner[phrase.Start]) right = !result[result.Count - 1].Right;

                // Re-cutting a neighbor changes its order row. Compare both hand choices
                // using those rows and keep the inherited phase when both have equal cost.
                if (phrase.CheckNeighborCadence || phrase.End - phrase.Start > 2)
                {
                    int repeats = BoundaryRepeats(phrase.Start, phrase.End, right);
                    int oppositeRepeats = BoundaryRepeats(phrase.Start, phrase.End, !right);
                    if (oppositeRepeats < repeats) right = !right;
                }

                for (int at = phrase.Start; at < phrase.End; at += 2)
                {
                    double begin = AttackTime(notes, attacks, at);
                    double end = at + 2 < phrase.End ? AttackTime(notes, attacks, at + 2)
                        : phrase.End < last.End ? AttackTime(notes, attacks, phrase.End)
                        : Math.Min(last.TimeEnd, begin + phrase.Width);
                    double width = Close(phrase.Width, attacks[at].CellLength) ? attacks[at].CellLength : phrase.Width;
                    result.Add(new Slice(at, at + 2, width, width, right, begin,
                        Math.Max(end, AttackTime(notes, attacks, at + 1))));
                    previousPair = at;
                    previousHand = right;
                    right = !right;
                }
                cursor = phrase.End;
            }
            CopyThrough(attacks.Count);
            return result;

            int BoundaryRepeats(int start, int end, bool right)
            {
                var profile = attacks[start].Profile;
                double cadence = MinimumPulse(profile);
                int count = 0;
                if (result.Count > 0) CountAgainst(result[result.Count - 1], start, right);
                if (end < attacks.Count)
                {
                    var next = baseline[owner[end]];
                    bool lastRight = right ^ (((end - start) / 2) % 2 == 0);
                    CountAgainst(new Slice(end, next.End, next.Length, next.PressWindow, next.Right), end - 2, lastRight);
                }
                return count;

                void CountAgainst(Slice neighbor, int pairStart, bool pairRight)
                {
                    var pairLayout = profile.Layout(pairRight);
                    var layout = attacks[neighbor.Start].Profile.Layout(neighbor.Right);
                    for (int other = neighbor.Start; other < neighbor.End; other++)
                    {
                        byte key = layout.Keys[layout.PreferredIndex(neighbor.Count, other - neighbor.Start)];
                        for (int i = 0; i < 2; i++)
                        {
                            if (key == pairLayout.Keys[pairLayout.PreferredIndex(2, i)] &&
                                Math.Abs(AttackTime(notes, attacks, other) - AttackTime(notes, attacks, pairStart + i)) < cadence)
                                count++;
                        }
                    }
                }
            }

            void CopyThrough(int end)
            {
                while (cursor < end)
                {
                    var slice = baseline[owner[cursor]];
                    int stop = Math.Min(end, slice.End);
                    double startTime = cursor == slice.Start ? slice.TimeStart : AttackTime(notes, attacks, cursor);
                    if (result.Count > 0) startTime = Math.Max(startTime, result[result.Count - 1].TimeEnd);
                    double endTime = stop == slice.End ? slice.TimeEnd : AttackTime(notes, attacks, stop);
                    result.Add(new Slice(cursor, stop, slice.Length, slice.PressWindow, slice.Right,
                        startTime, Math.Max(startTime, endTime)));
                    cursor = stop;
                }
                if (end < attacks.Count && result.Count > 0)
                {
                    var tail = result[result.Count - 1];
                    double boundary = AttackTime(notes, attacks, end);
                    if (tail.TimeEnd > boundary)
                        result[result.Count - 1] = new Slice(tail.Start, tail.End, tail.Length, tail.PressWindow, tail.Right,
                            Math.Min(tail.TimeStart, boundary), boundary);
                }
            }
        }

        private static List<Slice> Partition(TechniqueNote[] notes, List<Attack> attacks, bool startRight)
        {
            var slices = new List<Slice>(attacks.Count);
            bool regionRight = startRight;
            for (int region = 0; region < attacks.Count;)
            {
                var first = attacks[region];
                double length = first.CellLength;
                int limit = region + 1;
                while (limit < attacks.Count && ReferenceEquals(attacks[limit].Profile, first.Profile) &&
                    Math.Abs(attacks[limit].CellLength - length) <= Epsilon) limit++;

                double origin = AttackTime(notes, attacks, region);
                double lastCell = 0;
                for (int cursor = region; cursor < limit;)
                {
                    double time = AttackTime(notes, attacks, cursor);
                    double cell = Math.Floor((time - origin) / length + BoundaryTolerance);
                    double cellStart = origin + cell * length;
                    double end = origin + (cell + 1 - BoundaryTolerance) * length;
                    int next = cursor + 1;
                    while (next < limit && AttackTime(notes, attacks, next) < end) next++;
                    // Floating point can round the boundary onto an onset. Include all exact
                    // ties even when splitting cannot make progress in the time domain.
                    while (next < limit && AttackTime(notes, attacks, next) == AttackTime(notes, attacks, next - 1)) next++;
                    bool right = regionRight ^ (cell % 2.0 >= 1.0);
                    Subdivide(notes, attacks, slices, cursor, next, cellStart, length, right, 0);
                    lastCell = cell;
                    cursor = next;
                }

                if (limit < attacks.Count)
                {
                    // A tempo/profile boundary closes the old partial cell, rather than
                    // leaving two re-anchored regions with overlapping release boundaries.
                    // Retain the nominal width: per-key ratios still use the two-cell formula.
                    double change = AttackTime(notes, attacks, limit);
                    for (int i = slices.Count - 1; i >= 0 && slices[i].TimeEnd > change; i--)
                    {
                        var tail = slices[i];
                        slices[i] = new Slice(tail.Start, tail.End, tail.Length, tail.PressWindow,
                            tail.Right, Math.Min(tail.TimeStart, change), change);
                    }
                    // Consume the closed partial cell exactly once. Empty old-grid cells
                    // still advance parity; never reset to the global preferred hand.
                    double nextCell = Math.Max(lastCell + 1, Math.Floor((change - origin) / length + BoundaryTolerance));
                    regionRight ^= nextCell % 2.0 >= 1.0;
                }
                region = limit;
            }
            return slices;
        }

        private static double AttackTime(TechniqueNote[] notes, List<Attack> attacks, int index) => notes[attacks[index].Index].Time;

        private static void Subdivide(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices,
                                      int start, int end, double origin, double length, bool right, int depth)
        {
            var profile = attacks[start].Profile;
            if (end - start <= profile.Layout(right).Capacity || depth >= MaxSubdivisions || length < MinGrid ||
                AttackTime(notes, attacks, start) == AttackTime(notes, attacks, end - 1))
            {
                // Coincident onsets still consume every attack; the key allocator handles
                // any unavoidable retriggers without trying to split a zero-width interval.
                slices.Add(new Slice(start, end, length, length, right, origin, origin + length));
                return;
            }

            double half = length * 0.5;
            double midpoint = origin + half;
            int lo = start, hi = end;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (AttackTime(notes, attacks, mid) < midpoint) lo = mid + 1;
                else hi = mid;
            }
            if (lo > start) Subdivide(notes, attacks, slices, start, lo, origin, half, right, depth + 1);
            if (lo < end) Subdivide(notes, attacks, slices, lo, end, midpoint, half, lo == start ? right : !right, depth + 1);
        }

        // Complete isolated short pairs after metric refinement, without overriding its hand phase.
        // A same-hand prefix may join the pair only within one nominal cell and a bounded
        // three/four-note phrase, so configured order is not restarted as P followed by =P.
        private static List<Slice> RefineIsolatedPairs(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices)
        {
            if (slices.Count < 2) return slices;
            var result = new List<Slice>(slices.Count);
            Slice? pending = slices[0];
            double Gap(int at) => AttackTime(notes, attacks, at) - AttackTime(notes, attacks, at - 1);
            for (int i = 1; i < slices.Count; i++)
            {
                var right = slices[i];
                if (!pending.HasValue) { pending = right; continue; }
                var left = pending.Value;
                int lo = left.End - 1, hi = right.Start + 1;
                double gap = Gap(right.Start);
                var profile = attacks[lo].Profile;
                bool isolated = gap >= NearSimultaneous && gap < Frame &&
                    (lo == 0 || Gap(lo) >= Frame) && (hi == attacks.Count || Gap(hi) >= Frame);
                bool sameProfile = ReferenceEquals(profile, attacks[right.Start].Profile);
                bool rightHand = left.Right;
                if (profile.Layout(rightHand).Capacity < 2) rightHand = right.Right;
                if (!isolated || !sameProfile || left.Right == right.Right || profile.Layout(rightHand).Capacity < 2)
                {
                    result.Add(left);
                    pending = right;
                    continue;
                }

                // Regroup a compact prefix and pair as one configured order row. This is
                // metric grouping, not a physical-key rotation or a release-time clamp.
                bool includePrefix = left.Count > 1 && rightHand == left.Right &&
                    left.Count + 1 <= Math.Min(4, profile.Layout(rightHand).Capacity) &&
                    AttackTime(notes, attacks, right.Start) - AttackTime(notes, attacks, left.Start) <=
                    Math.Min(attacks[left.Start].CellLength, attacks[right.Start].CellLength) + Epsilon;
                double start = !includePrefix && left.Count > 1 ? AttackTime(notes, attacks, lo) : left.TimeStart;
                double end = right.Count > 1 ? AttackTime(notes, attacks, hi) : right.TimeEnd;
                if (left.Count > 1 && !includePrefix)
                    result.Add(new Slice(left.Start, lo, left.Length, left.PressWindow, left.Right, left.TimeStart, start));
                result.Add(new Slice(includePrefix ? left.Start : lo, hi, Math.Max(left.Length, right.Length),
                    Math.Max(left.PressWindow, right.PressWindow), rightHand, start, end));
                pending = right.Count > 1
                    ? new Slice(hi, right.End, right.Length, right.PressWindow, right.Right, end, right.TimeEnd)
                    : null;
            }
            if (pending.HasValue) result.Add(pending.Value);
            return result;
        }

        private static void RefineBoundaries(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices)
        {
            if (slices.Count < 2) return;
            int written = 0;
            for (int read = 1; read < slices.Count; read++)
            {
                var left = slices[written];
                var right = slices[read];
                var a = attacks[left.Start];
                var b = attacks[right.Start];
                double Gap(int at) => AttackTime(notes, attacks, at) - AttackTime(notes, attacks, at - 1);
                double gap = Gap(right.Start);
                bool sameGrid = Math.Abs(a.CellLength - b.CellLength) <= Epsilon;
                if (left.Right != right.Right && ReferenceEquals(a.Profile, b.Profile) && gap < Frame)
                {
                    int capacity = a.Profile.Layout(left.Right).Capacity;
                    // A very short seam licenses moving its cluster, not absorbing
                    // entire rows whose onsets extend across several pulses.
                    bool spansMultiplePulses = AttackTime(notes, attacks, right.End - 1) -
                        AttackTime(notes, attacks, left.Start) >= Math.Min(a.CellLength, b.CellLength) * 1.5;
                    bool isolatedCluster = false;
                    if (left.Count + right.Count <= capacity)
                    {
                        // Only inspect a bounded cluster, not the entire run at every
                        // boundary. Do not absorb uniform fast streams into a single hand.
                        int lo = left.End - 1, hi = right.Start;
                        while (hi - lo < 4 && lo > 0 && Gap(lo) < Frame) lo--;
                        while (hi - lo < 4 && hi + 1 < attacks.Count && Gap(hi + 1) < Frame) hi++;
                        isolatedCluster = (hi - lo is 2 or 3 || !sameGrid && hi - lo == 1) &&
                            left.Start >= lo && right.End <= hi + 1 &&
                            (lo == 0 || Gap(lo) >= Frame) && (hi + 1 == attacks.Count || Gap(hi + 1) >= Frame);
                        if (!spansMultiplePulses && (sameGrid && gap < NearSimultaneous || isolatedCluster))
                        {
                            slices[written] = new Slice(left.Start, right.End,
                                Math.Max(left.Length, right.Length), right.TimeEnd - left.TimeStart, left.Right,
                                left.TimeStart, right.TimeEnd);
                            continue;
                        }
                    }

                    // Move just one onset when the new gap is wider and the donor keeps
                    // its single/multi-note articulation. Stored hand phase never shifts.
                    if (!sameGrid || gap < NearSimultaneous && spansMultiplePulses)
                    {
                        RefineTempoBoundary(notes, attacks, ref left, ref right);
                    }
                    else if (right.Count > 2 && left.Count < capacity && Gap(right.Start + 1) > gap + Epsilon)
                    {
                        double boundary = AttackTime(notes, attacks, right.Start + 1);
                        left = new Slice(left.Start, left.End + 1, left.Length, left.PressWindow, left.Right, left.TimeStart, boundary);
                        right = new Slice(right.Start + 1, right.End, right.Length, right.PressWindow, right.Right, boundary, right.TimeEnd);
                    }
                    else if (left.Count > 2 && right.Count < b.Profile.Layout(right.Right).Capacity && Gap(left.End - 1) > gap + Epsilon)
                    {
                        double boundary = AttackTime(notes, attacks, left.End - 1);
                        left = new Slice(left.Start, left.End - 1, left.Length, left.PressWindow, left.Right, left.TimeStart, boundary);
                        right = new Slice(right.Start - 1, right.End, right.Length, right.PressWindow, right.Right, boundary, right.TimeEnd);
                    }
                }
                slices[written++] = left;
                slices[written] = right;
            }
            if (++written < slices.Count) slices.RemoveRange(written, slices.Count - written);
        }

        // Cross-tempo refinement moves only the bounded near-simultaneous boundary cluster.
        // Absorbing both occupied cells would turn a 1+5 boundary into a six-key hand run.
        private static void RefineTempoBoundary(TechniqueNote[] notes, List<Attack> attacks,
                                               ref Slice left, ref Slice right)
        {
            double Gap(int at) => AttackTime(notes, attacks, at) - AttackTime(notes, attacks, at - 1);
            double gap = Gap(right.Start);
            int lo = left.End - 1, hi = right.Start + 1;
            while (hi - lo < 4 && lo > left.Start && Gap(lo) < NearSimultaneous) lo--;
            while (hi - lo < 4 && hi < right.End && Gap(hi) < NearSimultaneous) hi++;
            // A longer uniform stream is not a small boundary flam. Do not split a cluster
            // at the scan limit or transfer just one of a triplet's near-simultaneous edges.
            if (lo > left.Start && Gap(lo) < NearSimultaneous ||
                hi < right.End && Gap(hi) < NearSimultaneous) return;

            var profile = attacks[left.Start].Profile;
            int prefix = hi - right.Start, suffix = left.End - lo;
            bool movePrefix = right.End - hi >= 2 &&
                left.Count + prefix <= profile.Layout(left.Right).Capacity &&
                Gap(hi) >= NearSimultaneous && Gap(hi) > gap + Epsilon;
            bool moveSuffix = lo - left.Start >= 2 &&
                right.Count + suffix <= profile.Layout(right.Right).Capacity &&
                Gap(lo) >= NearSimultaneous && Gap(lo) > gap + Epsilon;
            if (movePrefix && moveSuffix)
                movePrefix = Math.Max(left.Count + prefix, right.Count - prefix) <=
                             Math.Max(left.Count - suffix, right.Count + suffix);

            if (movePrefix)
            {
                double boundary = AttackTime(notes, attacks, hi);
                left = new Slice(left.Start, hi, left.Length, left.PressWindow, left.Right, left.TimeStart, boundary);
                right = new Slice(hi, right.End, right.Length, right.PressWindow, right.Right, boundary, right.TimeEnd);
            }
            else if (moveSuffix)
            {
                double boundary = AttackTime(notes, attacks, lo);
                left = new Slice(left.Start, lo, left.Length, left.PressWindow, left.Right, left.TimeStart, boundary);
                right = new Slice(lo, right.End, right.Length, right.PressWindow, right.Right, boundary, right.TimeEnd);
            }
        }

        private static List<Stroke> AssignKeySlots(TechniqueNote[] notes, List<Attack> attacks, List<Slice> slices)
        {
            var strokes = new List<Stroke>(attacks.Count);
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                var slice = slices[sliceIndex];
                var layout = attacks[slice.Start].Profile.Layout(slice.Right);
                for (int pos = slice.Start; pos < slice.End; pos++)
                {
                    int index = attacks[pos].Index;
                    var note = notes[index];
                    bool hold = note.Kind == NoteKind.HoldStart;
                    int keyIndex = slice.PreferredIndex(layout, pos);
                    strokes.Add(new Stroke(index, slice.Right, keyIndex, keyIndex, slice.PressWindow, hold, sliceIndex));
                }
            }

            return strokes;
        }

        // Restores the timing contract of TechniqueSimulator.cpp CalculateReleaseTime and its caps.
        // Grouping/key selection remains independent. Fixed-window KPS/tempo runs keep their own policy.
        internal static double CalculatePieceRelease(double time, double start, double width, double end,
                                                     double nextWidth, double nextEnd, bool sameHand,
                                                     bool nextEmpty, double ratio)
        {
            const double tolerance = 5e-6;
            double target;
            if (nextWidth > width + tolerance)
                target = start + width > end + tolerance ? nextEnd : start + width * 2.0;
            else
                target = start + width + tolerance < end ? start + width + nextWidth : nextEnd;

            double release = time + (target - time) * ratio * 0.5;
            double cap = !sameHand || nextEmpty ? nextEnd : end;
            if (release >= cap) release = cap - MinGrid;
            if (release <= time) release = time + (nextEnd - time) * 0.4;
            // Preserve the original one-frame rescue only when the metric boundary permits it.
            if (release - time < Frame && cap - MinGrid > time + Frame) release = time + Frame;
            // Original output is truncated to integer microseconds. Do not release before its down.
            double micros = release * 1e6;
            if (TechniqueTimeline.IsFinite(micros)) release = Math.Truncate(micros) / 1e6;
            return Math.Max(time, release);
        }

        private static double ReleaseForSlice(List<Slice> slices, int index, double time, double ratio)
        {
            var current = slices[index];
            double end = current.TimeEnd;
            double width = current.PressWindow;
            bool hasNext = index + 1 < slices.Count;
            var next = hasNext ? slices[index + 1] : default;
            // Grid strategies omit empty cells for efficiency. Restore the immediate empty cell
            // for release timing instead of spanning a long rest to the next occupied group.
            bool empty = !hasNext || next.TimeStart > end + 5e-6;
            double nextWidth = empty ? width : next.PressWindow;
            double nextEnd = empty ? end + width : next.TimeEnd;
            // Tempo/profile reanchors may shorten the previous cell without changing its nominal width.
            if (hasNext && next.TimeStart < end) end = next.TimeStart;
            return CalculatePieceRelease(time, current.TimeStart, width, end,
                nextWidth, nextEnd, !empty && next.Right == current.Right, empty, ratio);
        }

        private static void ResolvePhysicalKeys(TechniqueNote[] notes, List<Attack> attacks, List<Stroke> strokes,
                                                List<Slice> slices, bool lazyRelease)
        {
            var lastByKey = new int[256];
            for (int i = 0; i < lastByKey.Length; i++) lastByKey[i] = -1;
            int activeHold = -1, timeline = 0;

            for (int strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
            {
                var stroke = strokes[strokeIndex];
                int index = stroke.Down;
                var note = notes[index];
                ConsumeHoldEnds(index);
                if (stroke.Hold && activeHold >= 0) CloseHold(note.Time, -1);

                var layout = attacks[strokeIndex].Profile.Layout(stroke.RightHand);
                int keySlot = stroke.KeySlot;
                // Preserve preferred logical slots unless their physical key aliases
                // an active hold or another down at this exact instant.
                if (NeedsAlternative(layout.Keys[keySlot], note.Time))
                    for (int k = 0; k < layout.Keys.Length; k++)
                        if (!NeedsAlternative(layout.Keys[k], note.Time)) { keySlot = k; break; }
                stroke.KeySlot = keySlot;
                stroke.Key = layout.Keys[keySlot];
                double duration = Math.Max(Math.Min(Frame, stroke.PressWindow),
                    layout.Ratio(keySlot) * stroke.PressWindow * 0.5);
                // Very large user ratios may overflow or leave a key down indefinitely.
                // Preserve the relative strengths up to the next nominal two-cell window.
                stroke.ReleaseAt = stroke.Hold || lazyRelease ? double.PositiveInfinity
                    : TechniqueTimeline.IsFinite(slices[stroke.SliceIndex].TimeStart)
                        ? ReleaseForSlice(slices, stroke.SliceIndex, note.Time, layout.Ratio(keySlot))
                        : note.Time + Math.Min(stroke.PressWindow * 2.0, duration);

                int prior = lastByKey[stroke.Key];
                if (prior >= 0 && (strokes[prior].ReleaseAt > note.Time ||
                    (strokes[prior].ReleaseAt == note.Time && strokes[prior].ReleaseAnchor < 0)))
                {
                    var previous = strokes[prior];
                    previous.ReleaseAt = lazyRelease && !previous.Hold
                        ? LazyReleaseBefore(notes[previous.Down].Time, note.Time)
                        : Math.Max(notes[previous.Down].Time, note.Time - MinGrid);
                    previous.ReleaseAnchor = -1;
                    strokes[prior] = previous;
                    if (prior == activeHold) activeHold = -1;
                }
                lastByKey[stroke.Key] = strokeIndex;
                if (stroke.Hold) activeHold = strokeIndex;
                strokes[strokeIndex] = stroke;
                timeline = index + 1;
            }

            ConsumeHoldEnds(notes.Length);
            if (activeHold >= 0)
                CloseHold(Math.Max(notes[notes.Length - 1].Time, notes[strokes[activeHold].Down].Time + Frame), -1);
            if (lazyRelease)
            {
                // Plain presses still held at the end release after the last note, not at infinity.
                double finalRelease = notes[notes.Length - 1].Time + LazyFinalReleaseDelay;
                for (int i = 0; i < strokes.Count; i++)
                {
                    var pending = strokes[i];
                    if (pending.Hold || !double.IsPositiveInfinity(pending.ReleaseAt)) continue;
                    pending.ReleaseAt = finalRelease;
                    strokes[i] = pending;
                }
            }

            bool NeedsAlternative(byte key, double time)
            {
                int prior = lastByKey[key];
                return prior >= 0 && (prior == activeHold || notes[strokes[prior].Down].Time == time);
            }

            void ConsumeHoldEnds(int until)
            {
                for (; timeline < until; timeline++)
                    if (activeHold >= 0 && notes[timeline].Kind == NoteKind.HoldEnd)
                        CloseHold(notes[timeline].Time, timeline);
            }

            void CloseHold(double time, int anchor)
            {
                var stroke = strokes[activeHold];
                stroke.ReleaseAt = time;
                stroke.ReleaseAnchor = anchor;
                strokes[activeHold] = stroke;
                activeHold = -1;
            }
        }

        private static TechniqueEvent[] Project(TechniqueNote[] notes, List<Stroke> strokes)
        {
            var edges = new Edge[checked(strokes.Count * 2)];
            for (int i = 0; i < strokes.Count; i++)
            {
                var s = strokes[i];
                edges[2 * i] = new Edge(notes[s.Down].Time, 2L * s.Down, s.Down, s.Key, false, s.Hold);
                long releaseOrder = 2L * Math.Max(s.Down, s.ReleaseAnchor) + 1;
                edges[2 * i + 1] = new Edge(s.ReleaseAt, releaseOrder, s.ReleaseAnchor, s.Key, true, s.Hold);
            }
            Array.Sort(edges, (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Order.CompareTo(b.Order));

            // Map the sorted edges back onto source-floor coordinates. Explicit onsets
            // keep their own floor at equal times; inferred key-ups never jump backwards.
            var events = new TechniqueEvent[edges.Length];
            int search = 0, last = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                while (search + 1 < notes.Length && notes[search].Time < e.Time) search++;
                int at = e.Anchor >= 0 ? e.Anchor : Math.Max(last, search);
                last = at;
                double end = notes[at].Time;
                double start = at > 0 ? notes[at - 1].Time : Math.Min(0.0, end);
                float fraction = e.Anchor >= 0 || end <= start ? 1f : (float)Math.Min(1.0, Math.Max(0.0, (e.Time - start) / (end - start)));
                events[i] = new TechniqueEvent(e.Time, notes[at].Floor, fraction, e.Key, e.Release, e.Hold);
            }
            return events;
        }
    }
}
