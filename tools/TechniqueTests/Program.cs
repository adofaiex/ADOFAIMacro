using ADOFAIMacro.Technique;

internal static class Program
{
    private static readonly TechniqueProfile TwoHands = new(120, Hand([65]), Hand([74]));

    private static async Task<int> Main()
    {
        Action[] tests =
        [
            HalfBeatGridAndEmptySlices,
            IndependentPhrasePrefersConfiguredLead,
            ContinuousSlowFlowDoesNotResetLead,
            ContinuousCountedRestDoesNotResetLead,
            ContinuousCountedRestWithDoubledNeighborDoesNotResetLead,
            UnconfirmedPickupDoesNotResetLead,
            IndependentPhraseFallsBackWhenPhaseMovesRapidReuse,
            IndependentPhraseKeepsCapacityAndActiveHold,
            AssignmentsExposeHandAndKeySlot,
            PhysicalKeyMappingCanResolveSlotAliases,
            InjectedGroupingStrategyControlsHandAssignments,
            RunHandPolicyIsIndependentOfWindowAndBuildHistory,
            PreferredHandPolicyHandlesSparseAndDenseRhythms,
            InvalidStrategyPartitionIsRejected,
            KpsGroupingUsesTempoRunsAndAlternatesHands,
            KpsGroupingUsesTotalRunSpan,
            KpsGroupingSplitsAtHandCapacity,
            TempoWindowGroupsBySpeedAndCapsAtHandCapacity,
            TempoWindowTightensWithinRunWhenTempoRises,
            KpsThresholdDoesNotChangePressDuration,
            KpsOutputIgnoresTempoOnlyInputs,
            KpsVarianceUsesObservedRunSpan,
            GroupingStrategyFingerprintTracksPolicy,
            PolicyFingerprintOnlyTracksConsumedInputs,
            TempoWindowStrategyFingerprintDiffers,
            StrategyFactorySelectsAndDefaultsPolicies,
            KpsGroupingPreservesHoldReleaseAndProfileBoundaries,
            KpsGroupingOnlySplitsOnRelevantLayoutChanges,
            DenseRunReleasesEveryPress,
            ReleaseTimingChecks.NativeReleaseBranchesAndCaps,
            ReleaseTimingChecks.EqualCellsUseTwoCellHorizonAndConfiguredRatios,
            ReleaseTimingChecks.RatiosRemainPerKeyAndOffsetAware,
            ReleaseTimingChecks.EmptyCellsDoNotHoldAcrossLongRests,
            ReleaseTimingChecks.Folded170BpmRestoresRatioMeaning,
            ReleaseTimingChecks.MergedFlamKeepsFullReleaseWindow,
            GridBoundaryChecks.Replay2140MovesFlamOnlyAndKeepsConfiguredOrder,
            BoundaryCadenceChecks.DenseSameHandRowsSwitchWithoutMovingTheirBoundary,
            BoundaryCadenceChecks.NarrowToWideBoundaryCanSwitchWholeRow,
            BoundaryCadenceChecks.SafeSeparatedRowsAndTightSameHandEdgesStayPut,
            BoundaryCadenceChecks.ActiveHoldAndInsufficientCapacityPreventSwitch,
            BoundaryCadenceChecks.JointWidthBoundaryRemovesUnneededZigzag,
            BoundaryCadenceChecks.JointWidthBoundaryKeepsNecessaryAlternation,
            BoundaryCadenceChecks.SteadyPulsesSpanAttachedShortPair,
            BoundaryCadenceChecks.SteadyPulseRebuildsAfterClosedHoldAndKeepsTripletAndSubdivision,
            BoundaryCadenceChecks.LocalPulsesTrackTempoWithRestsWithoutSwallowingSubdivisions,
            BoundaryCadenceChecks.RebuiltPulseCarriesBeatParityAndKeepsExitPair,
            BoundaryCadenceChecks.SteadyMiddlePassageDoesNotChangeSurroundingHands,
            BoundaryCadenceChecks.SteadyPulsePreservesAlignedMixedBeatsAndRejectsHoldOrDrift,
            BoundaryCadenceChecks.SteadyPulseBeforeDistantHoldStillAlternates,
            BoundaryCadenceChecks.RestAfterHoldUsesPreferredHand,
            BoundaryCadenceChecks.CompactSeamBeatAlternatesBetweenMeasuredRuns,
            ChartRhythmChecks.FourQuarterTurnsPerHand,
            ChartRhythmChecks.SlowSquaresUseFourKeys,
            ChartRhythmChecks.PostHoldTripletAndMeasuredPairsKeepTheirHands,
            ChartRhythmChecks.SlowerPhraseStartsOnPreferredHand,
            ChartRhythmChecks.OffbeatPickupDoesNotChooseLeadForThePhrase,
            ChartRhythmChecks.HalfPulseEntryAlternatesAcrossTheSeam,
            ChartRhythmChecks.ShortHalfBeatPhraseKeepsArticulation,
            ChartRhythmChecks.ReturnFromTripletsKeepsSubdivisionOnOneHand,
            ChartRhythmChecks.RepeatedTripletsKeepBeatPhaseAcrossTempoAndLayout,
            ChartRhythmChecks.QuarterTurnRunsDoNotUseFiveOrSixKeyRows,
            ChartRhythmChecks.QuarterTurnRunsKeepFourKeyOrderAcrossTempoSteps,
            ChartRhythmChecks.QuarterTurnRunsKeepFourKeysAndPhaseAcrossRests,
            ChartRhythmChecks.RepeatedTripletsCarryPhaseThroughMeasuredTempoStep,
            ChartRhythmChecks.EvenTripletsUseTheSurroundingBeatClock,
            ChartRhythmChecks.EvenTripletsUseAvailableBeatAnchorsAtTempoAndChartEdges,
            ChartRhythmChecks.EvenTripletsKeepTheirPhaseAcrossRests,
            ChartRhythmChecks.EvenTripletsKeepTheBeatThroughChangesAtBothSeams,
            ChartRhythmChecks.MixedSubdivisionsUseOneClockAcrossRests,
            ChartRhythmChecks.ConfirmedPhraseKeepsPhaseAtEmptyBeatEntry,
            ChartRhythmChecks.PickupCanStartOnEitherHandToMeetTheNextPhrase,
            ChartRhythmChecks.DensePhraseCarriesPhaseThroughMixedSizesAndRests,
            BoundaryCadenceChecks.LocalPhrasesKeepDonorFragmentsAtConfirmedEdges,
            BoundaryCadenceChecks.LocalEvidenceAcceptsOneConfirmedAccent,
            BoundaryCadenceChecks.LocalEvidenceAcceptsMeasuredTempoBoundary,
            BoundaryCadenceChecks.LocalEvidenceAcceptsSingleAccentInShortRun,
            BoundaryCadenceChecks.LocalEvidenceRejectsUncorroboratedFastSubdivisions,
            BoundaryCadenceChecks.FoldedLocalPhrasesCountRestsAndCarryThroughTempoSteps,
            BoundaryCadenceChecks.RecurringWideBurstsUseCompleteRowsAndStablePhase,
            BoundaryCadenceChecks.MixedPulseUsesConfiguredLeadAcrossABoundary,
            GridBoundaryChecks.TempoBoundaryWindowsShareAnOrderedBoundary,
            GridBoundaryChecks.PartialTempoCellsAdvancePhaseWithoutRestartingHand,
            GridBoundaryChecks.TempoChangeCountsEmptyCellParity,
            GridBoundaryChecks.SmallIsolatedCrossTempoFlamStaysTogether,
            GridBoundaryChecks.CrossTempoPrefixTripletMovesAsACluster,
            GridBoundaryChecks.UniformCrossTempoStreamIsNotAbsorbed,
            GridBoundaryChecks.CrossTempoRefinementRespectsProfileBoundaries,
            GridBoundaryChecks.AdjustedSlicesStillUseEachConfiguredRatio,
            GridBoundaryChecks.Isolated14msPairUsesConfiguredTwoKeyRow,
            GridBoundaryChecks.IsolatedPairCanLeaveOneNoteInDonor,
            GridBoundaryChecks.IsolatedPairBetweenTwoGroupsDoesNotMergeBothGroups,
            GridBoundaryChecks.Uniform14msStreamIsNotAnIsolatedPair,
            GridBoundaryChecks.IsolatedPairRespectsOneKeyHandCapacity,
            GridBoundaryChecks.Isolated14msPairRespectsLayoutBoundary,
            GridBoundaryChecks.TempoHandCorrectionSurvivesPairIsolation,
            GridBoundaryChecks.SameGridHandCorrectionSurvivesPairIsolation,
            GridBoundaryChecks.DistantPrefixIsNotAbsorbedIntoPair,
            RhythmicPairChecks.Atohs695UsesEightAlternatingTwoKeyPieces,
            RhythmicPairChecks.Atohs719AndFollowing25msPairsKeepWholeRows,
            RhythmicPairChecks.AtohsLaterPairsUseConfiguredRowsWithoutFastRepresses,
            RhythmicPairChecks.AtohsEarlyPairsSeparatedBySinglesPreserveHands,
            RhythmicPairChecks.EquivalentAtohsAngleAndSpeedEncodingsGiveIdenticalPieces,
            RhythmicPairChecks.RhythmicPairShapeSurvivesTempoAndEpochScaling,
            RhythmicPairChecks.SlowRhythmicPairsAdvanceOccupiedPulsePhase,
            RhythmicPairChecks.RhythmicRefinementDoesNotTreatUniformStreamAsPairs,
            RhythmicPairChecks.RhythmicPairRefinementRespectsOneKeyCapacity,
            RepeatedPhraseExitChecks.AlignsBothPairsByMergingAdjacentRows,
            RepeatedPhraseExitChecks.KeepsExistingRowsWhenJointCandidateIsUnsafe,
            SpeedAndSegmentChanges,
            InvalidTimingParameters,
            VeryLongRest,
            SameMicrosecondAndSharedPhysicalKey,
            OrdinaryTailUsesSourceFloor,
            TimelineSkipsDecorationsAndClosesHolds,
            HoldAtTailReleasesBeforeTerminalTap,
            TimelineFingerprintTracksInputs,
            LongChartIsDeterministic,
            RepositoryChartPreservesEveryPress,
            RepositoryIsolatedPairsStayOnOneHand,
            DefaultStrategyOutputMatchesGolden,
            PieceFallbackBacktracksOffHandOverflow,
            PieceFallbackSurvivesCoincidentBurst,
            PieceFallbackKeepsEveryRepositoryPress,
            LazyReleaseOnlyShiftsReleaseTiming,
            LazyReleaseKeepsRepositoryPresses,
            StrategyIdentityAndFingerprintsForNewOptions,
            PieceFallbackGroupsFormValidPartitions,
            PieceFallbackIsDeterministic,
            PieceFallbackTerminatesOnDenseBursts,
            TraceFileChecks.PortableTraceAndValidation,
            MacroEngine.PauseInvalidatesDequeuedEdge,
            MacroEngine.SaturationPreservesFifoAndTail,
            MacroEngine.TimedOutStopDoesNotStartAnotherWorker,
            MacroEngine.TimeWorkerCSharpReleasesHoldAfterTechniqueToggle,
            MacroEngine.TimeWorkerRustBranchReleasesHoldAfterTechniqueToggle,
            MacroEngine.TimeWorkerCSharpPreservesHoldAcrossStaleAnchor,
            MacroEngine.TimeWorkerRustFallbackPreservesHoldAcrossStaleAnchor,
            MacroEngine.TimeWorkerStopReleasesHeldKeys,
            MacroEngine.FocusBlocksNewDownButAllowsOwnedUp,
            MacroEngine.TimeWorkerUnfocusedScheduledRelease,
            MacroEngine.TimeWorkerUnfocusedStopReleasesLazyKeys,
            MacroEngine.AngleWorkerFocusLossOnlyReleasesOwnedKeys,
        ];
        int failures = 0;
        foreach (var test in tests)
        {
            string name = test.Method.Name;
            try
            {
                // A hung planner must fail the console run instead of hanging CI.
                await Task.Run(test).WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"PASS {name}");
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine($"FAIL {name}: did not finish within 15 seconds");
                return 1;
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static void HalfBeatGridAndEmptySlices()
    {
        // 120 BPM => 250 ms per half beat. Start off the absolute grid so the
        // phase is relative to the first note. Empty slices still switch hands.
        TechniqueNote[] notes = [Tap(.125, 0), Tap(.375, 1), Tap(.875, 2), Tap(1.625, 3), Tap(1.875, 4)];
        var leftFirst = Build(notes);
        var rightFirst = Build(notes, startRight: true);
        ExpectSequence<byte>([65, 74, 74, 65, 74], Presses(leftFirst).Select(e => e.Key), "left-first half-beat phase");
        ExpectSequence<byte>([74, 65, 65, 74, 65], Presses(rightFirst).Select(e => e.Key), "right-first half-beat phase");
        VerifyPlan(notes, leftFirst);
        VerifyPlan(notes, rightFirst);
    }

    private static void IndependentPhrasePrefersConfiguredLead()
    {
        // The re-entry falls off the prior clock; the rest inside each run does not.
        TechniqueNote[] notes = [Tap(0, 0), Tap(.25, 1), Tap(.75, 2),
            Tap(2.29, 3), Tap(2.54, 4), Tap(3.04, 5)];
        var strategy = TechniquePlanner.CreateGroupingStrategy("beatGrid", 6.5);
        foreach (bool startRight in new[] { false, true })
        foreach (bool lazyRelease in new[] { false, true })
        {
            var plan = TechniquePlanner.Build(notes, 120, 1, startRight, _ => TwoHands, strategy, lazyRelease);
            byte main = startRight ? (byte)74 : (byte)65;
            byte other = startRight ? (byte)65 : (byte)74;
            ExpectSequence<byte>([main, other, other, main, other, other],
                plan.Assignments.Select(a => a.Key), "independent phrase lead; internal empty beats keep parity");
            VerifyPlan(notes, plan.Events);
        }
    }

    private static void ContinuousSlowFlowDoesNotResetLead()
    {
        // Long inter-onset gaps in a steady flow do not prove new phrases.
        TechniqueNote[] notes = Enumerable.Range(0, 7).Select(i => Tap(i * 1.25, i)).ToArray();
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => TwoHands);
        ExpectSequence<byte>([74, 74, 74, 74, 74, 74, 74], plan.Assignments.Select(a => a.Key),
            "slow continuous pulse keeps parity");
        VerifyPlan(notes, plan.Events);
    }

    private static void ContinuousCountedRestDoesNotResetLead()
    {
        TechniqueNote[] notes = [Tap(0, 0), Tap(.25, 1), Tap(.5, 2),
            Tap(1.75, 3), Tap(2, 4), Tap(2.25, 5)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => TwoHands);
        ExpectSequence<byte>([74, 65, 74, 65, 74, 65], plan.Assignments.Select(a => a.Key),
            "counted empty beats on the same pulse must not reset the lead");
        VerifyPlan(notes, plan.Events);
    }

    private static void ContinuousCountedRestWithDoubledNeighborDoesNotResetLead()
    {
        TechniqueNote[] notes = [Tap(0, 0), Tap(.25, 1), Tap(.75, 2),
            Tap(2.25, 3), Tap(2.5, 4), Tap(3, 5)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => TwoHands);
        ExpectSequence<byte>([74, 65, 65, 65, 74, 74], plan.Assignments.Select(a => a.Key),
            "a double interval beside a counted rest still belongs to the same clock");
        VerifyPlan(notes, plan.Events);
    }

    private static void UnconfirmedPickupDoesNotResetLead()
    {
        TechniqueNote[] notes = [Tap(0, 0), Tap(.25, 1), Tap(.5, 2),
            Tap(1.77, 3), Tap(2, 4), Tap(2.25, 5), Tap(2.5, 6)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => TwoHands);
        ExpectSequence<byte>([74, 65, 74, 65, 74, 65, 74], plan.Assignments.Select(a => a.Key),
            "a single off-grid pickup does not establish an independent clock");
        VerifyPlan(notes, plan.Events);
    }

    private static void IndependentPhraseFallsBackWhenPhaseMovesRapidReuse()
    {
        // Either phase yields exactly one rapid reuse, but on a different physical key.
        // The right hand therefore cannot lead this separated passage safely.
        TechniqueNote[] notes = [Tap(0, 0), Tap(.1, 1), Tap(.2, 2),
            Tap(.94, 3), Tap(.95, 4), Tap(1.04, 5), Tap(1.14, 6)];
        var profile = new TechniqueProfile(300,
            new HandLayout([65, 66], [[0], [0, 0]], [.8, 1.2]),
            new HandLayout([74, 75], [[0], [0, 0]], [1.2, .8]));
        var attacks = notes.Select((_, i) => new TechniquePlanner.Attack(i, profile, .1)).ToList();
        List<TechniquePlanner.Slice> rows = [Row(0, 1, true), Row(1, 2, false), Row(2, 3, true),
            Row(3, 5, false), Row(5, 6, true), Row(6, 7, false)];
        TechniquePlanner.PreferIndependentPhraseLead(notes, attacks, rows, preferredRight: true);
        Check(!rows[3].Right, "do not transfer a fast repress to the other hand's key");
        var safe = new TechniqueProfile(300, Hand([65, 66]), Hand([74, 75]));
        var safeAttacks = notes.Select((_, i) => new TechniquePlanner.Attack(i, safe, .1)).ToList();
        TechniquePlanner.PreferIndependentPhraseLead(notes, safeAttacks, rows, preferredRight: true);
        Check(rows[3].Right, "the same separated phrase must rephase when its key rows are safe");

        TechniquePlanner.Slice Row(int a, int b, bool right) =>
            new(a, b, .1, .1, right, notes[a].Time, notes[a].Time + .1);
    }

    private static void IndependentPhraseKeepsCapacityAndActiveHold()
    {
        var profile = new TechniqueProfile(120,
            new HandLayout([65, 74], [[0], [1, 0]], [.7, 1.3]), Hand([74]));
        TechniqueNote[] taps = [Tap(0, 0), Tap(.25, 1), Tap(.5, 2),
            Tap(2.29, 3), Tap(2.31, 4), Tap(2.54, 5), Tap(2.79, 6), Tap(3.04, 7)];
        TechniqueNote[] holds = [new(0, 0, 1, NoteKind.HoldStart), Tap(.25, 1), Tap(.5, 2),
            Tap(2.29, 3), Tap(2.54, 4), Tap(2.79, 5), new(3, 6, 1, NoteKind.HoldEnd)];
        var strategy = TechniquePlanner.CreateGroupingStrategy("beatGrid", 6.5);
        foreach (bool lazyRelease in new[] { false, true })
        {
            var tooWide = TechniquePlanner.Build(taps, 120, 1, true, _ => profile, strategy, lazyRelease);
            ExpectSequence<byte>([74, 65], tooWide.Assignments.Skip(3).Take(2).Select(a => a.Key),
                "wide entry must keep the other hand's configured order");
            Check(tooWide.Assignments[3].Hand == TechniqueHand.Left && tooWide.Assignments[4].Hand == TechniqueHand.Left,
                "do not force a two-key row onto the one-key preferred hand");
            VerifyPlan(taps, tooWide.Events);

            var holdLayout = new TechniqueProfile(120, Hand([66]), Hand([75]));
            var active = TechniquePlanner.Build(holds, 120, 1, true,
                floor => floor == 0 ? holdLayout : TwoHands, strategy, lazyRelease);
            Check(active.Assignments[3].Hand == TechniqueHand.Left,
                "a gap inside an active chart hold cannot establish independent re-entry");
            Check(active.Events.Any(e => e.Hold && e.Release && e.Time == 3), "preserve the authored hold tail");
            VerifyPlan(holds, active.Events);
        }
    }

    private static void AssignmentsExposeHandAndKeySlot()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.1, 1)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile);

        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "assignment hands");
        ExpectSequence<int>([0, 1], plan.Assignments.Select(a => a.KeySlot), "configured key slots");
        ExpectSequence<byte>([65, 66], plan.Assignments.Select(a => a.Key), "mapped physical keys");
        ExpectSequence<int>([0, 1], plan.Assignments.Select(a => a.Floor), "source floors");

        var downs = plan.Events.Where(e => !e.Release).ToArray();
        Check(downs.Length == plan.Assignments.Length, "each assignment must produce exactly one key-down event");
        for (int i = 0; i < downs.Length; i++)
            Check(downs[i].Time == plan.Assignments[i].Time && downs[i].Floor == plan.Assignments[i].Floor &&
                  downs[i].Key == plan.Assignments[i].Key && downs[i].Hold == plan.Assignments[i].Hold,
                $"assignment {i} does not match its projected key-down event");
    }

    private static void PhysicalKeyMappingCanResolveSlotAliases()
    {
        var left = new HandLayout([65, 66], [[0], [0, 0]], [.8, .8]);
        var profile = new TechniqueProfile(120, left, Hand([74]));
        TechniqueNote[] notes = [Tap(1, 0), Tap(1, 1)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile);

        ExpectSequence<int>([0, 1], plan.Assignments.Select(a => a.KeySlot),
            "physical alias fallback key slots");
        ExpectSequence<int>([0, 0], plan.Assignments.Select(a => a.PreferredKeySlot),
            "preferred slots remain visible after alias fallback");
        ExpectSequence<byte>([65, 66], plan.Assignments.Select(a => a.Key),
            "physical alias fallback keys");
        VerifyPlan(notes, plan.Events);
    }

    private static void InjectedGroupingStrategyControlsHandAssignments()
    {
        var profile = new TechniqueProfile(120, Hand([65]), Hand([74]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.1, 1)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new ForceRightGroupingStrategy());

        ExpectSequence<TechniqueHand>([TechniqueHand.Right, TechniqueHand.Right],
            plan.Assignments.Select(a => a.Hand), "injected grouping strategy hands");
        ExpectSequence<byte>([74, 74], Presses(plan.Events).Select(e => e.Key),
            "injected grouping strategy keys");
        VerifyPlan(notes, plan.Events);
    }

    private sealed class ForceRightGroupingStrategy : TechniquePlanner.IGroupingStrategy
    {
        public bool UsesTempoInputs => true;
        public List<TechniquePlanner.Slice> Group(TechniqueNote[] notes,
            List<TechniquePlanner.Attack> attacks, bool startRight)
        {
            var groups = new List<TechniquePlanner.Slice>(attacks.Count);
            for (int i = 0; i < attacks.Count; i++)
                groups.Add(new TechniquePlanner.Slice(i, i + 1, attacks[i].CellLength,
                    attacks[i].CellLength, right: true));
            return groups;
        }
    }

    private static void InvalidStrategyPartitionIsRejected()
    {
        var profile = new TechniqueProfile(120, Hand([65]), Hand([74]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.1, 1)];
        try
        {
            TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
                new MissingFirstAttackGroupingStrategy());
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("incomplete strategy partitions must be rejected");
    }

    private sealed class MissingFirstAttackGroupingStrategy : TechniquePlanner.IGroupingStrategy
    {
        public bool UsesTempoInputs => true;
        public List<TechniquePlanner.Slice> Group(TechniqueNote[] notes,
            List<TechniquePlanner.Attack> attacks, bool startRight) =>
            [new TechniquePlanner.Slice(1, attacks.Count, attacks[1].CellLength,
                attacks[1].CellLength, right: startRight)];
    }

    private sealed class RightHandPolicy : TechniquePlanner.IRunHandPolicy
    {
        public bool ChooseHand(bool preferredRight, bool? previousRight, double sincePreferredAttack, double window) => true;
    }

    private static void RunHandPolicyIsIndependentOfWindowAndBuildHistory()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66, 67]), Hand([74]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.01, 1), Tap(.30, 2)];
        TechniquePlanner.IGroupingStrategy[] strategies =
        [
            new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5, new RightHandPolicy()),
            new TechniquePlanner.TempoWindowGroupingStrategy(new RightHandPolicy()),
        ];
        foreach (var strategy in strategies)
        {
            var plan = TechniquePlanner.Build(notes, 120, 1, false, _ => profile, strategy);
            ExpectSequence<TechniqueHand>([TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Right],
                plan.Assignments.Select(a => a.Hand), "independent hand-selection policy");
            Check(plan.Pieces == 3, "groups must honor the selected hand's one-key capacity");
            VerifyPlan(notes, plan.Events);
        }

        var reusable = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.KpsAlternatingStrategyId, 6.5,
            TechniquePlanner.RecoveredPreferredHandPolicyId);
        var first = TechniquePlanner.Build(notes, 120, 1, true, _ => profile, reusable);
        _ = TechniquePlanner.Build(notes, 120, 1, false, _ => profile, reusable);
        var again = TechniquePlanner.Build(notes, 120, 1, true, _ => profile, reusable);
        ExpectSequence(Trace(first.Events), Trace(again.Events), "hand history must not leak across builds");
    }

    private static void PreferredHandPolicyHandlesSparseAndDenseRhythms()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        var policy = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.KpsAlternatingStrategyId, 10,
            TechniquePlanner.RecoveredPreferredHandPolicyId);
        TechniqueNote[] sparse = [Tap(0, 0), Tap(.30, 1), Tap(.60, 2)];
        var sparsePlan = TechniquePlanner.Build(sparse, 120, 1, true, _ => profile, policy);
        ExpectSequence<TechniqueHand>([TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Right],
            sparsePlan.Assignments.Select(a => a.Hand), "sparse rhythm prefers the starting hand");
        VerifyPlan(sparse, sparsePlan.Events);

        var dense = Enumerable.Range(0, 5).Select(i => Tap(i * .005, i)).ToArray();
        var densePlan = TechniquePlanner.Build(dense, 120, 1, true, _ => profile, policy);
        ExpectSequence<TechniqueHand>(
            [TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right],
            densePlan.Assignments.Select(a => a.Hand), "dense rhythm alternates groups without a sufficient gap");
        VerifyPlan(dense, densePlan.Events);

        int defaultFp = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.KpsAlternatingStrategyId, 10);
        Check(defaultFp != TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.KpsAlternatingStrategyId, 10,
                TechniquePlanner.RecoveredPreferredHandPolicyId), "hand policy must enter the run strategy fingerprint");
        Check(TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.BeatGridStrategyId, 10) ==
              TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.BeatGridStrategyId, 10,
                  TechniquePlanner.RecoveredPreferredHandPolicyId), "beat-grid phase must ignore the run hand policy");
    }

    private static void KpsGroupingUsesTempoRunsAndAlternatesHands()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66, 67, 68]), Hand([74, 75, 76, 77]));
        TechniqueNote[] notes =
        [
            Tap(0, 0), Tap(.04, 1), Tap(.08, 2),
            Tap(.30, 3), Tap(.34, 4), Tap(.60, 5),
        ];
        var strategy = new TechniquePlanner.KpsAlternatingGroupingStrategy(5);
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile, strategy);

        ExpectSequence<TechniqueHand>(
            [TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "KPS run hands");
        ExpectSequence<int>([0, 1, 2, 0, 1, 0], plan.Assignments.Select(a => a.KeySlot), "KPS run key slots");
        ExpectSequence<byte>([65, 66, 67, 74, 75, 65], plan.Assignments.Select(a => a.Key), "KPS run physical keys");
        VerifyPlan(notes, plan.Events);
    }

    private static void KpsGroupingSplitsAtHandCapacity()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        TechniqueNote[] notes = Enumerable.Range(0, 5).Select(i => Tap(i * .02, i)).ToArray();
        var strategy = new TechniquePlanner.KpsAlternatingGroupingStrategy(10);
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile, strategy);

        ExpectSequence<TechniqueHand>(
            [TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "KPS capacity hand changes");
        VerifyPlan(notes, plan.Events);
    }

    private static void TempoWindowGroupsBySpeedAndCapsAtHandCapacity()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.02, 1), Tap(.04, 2), Tap(.60, 3)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.TempoWindowGroupingStrategy());

        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "tempo-window groups and capacity");
        ExpectSequence<int>([0, 1, 0, 0], plan.Assignments.Select(a => a.KeySlot),
            "tempo-window slot sequence");
        VerifyPlan(notes, plan.Events);
    }

    private static void TempoWindowTightensWithinRunWhenTempoRises()
    {
        var profile = new TechniqueProfile(500, Hand([65, 66, 67, 68]), Hand([74, 75, 76, 77]));
        TechniqueNote[] notes =
        [
            Tap(0, 0, 1), Tap(.10, 1, 1),
            Tap(.15, 2, 4), Tap(.20, 3, 4),
        ];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.TempoWindowGroupingStrategy());

        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right, TechniqueHand.Right],
            plan.Assignments.Select(a => a.Hand), "tempo-window tightened segment hand runs");
        VerifyPlan(notes, plan.Events);
    }

    private static void KpsGroupingUsesTotalRunSpan()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66, 67, 68]), Hand([74, 75, 76, 77]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.03, 1), Tap(.06, 2), Tap(.09, 3), Tap(.12, 4)];
        var strategy = new TechniquePlanner.KpsAlternatingGroupingStrategy(10);
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile, strategy);

        ExpectSequence<TechniqueHand>(
            [TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "KPS total-span hand groups");
        ExpectSequence<int>([0, 1, 0, 1, 0], plan.Assignments.Select(a => a.KeySlot),
            "KPS total-span key slots");
        VerifyPlan(notes, plan.Events);
    }

    private static void KpsThresholdDoesNotChangePressDuration()
    {
        var profile = new TechniqueProfile(120, Hand([65]), Hand([74]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.25, 1)];
        var slowerThreshold = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(5));
        var fasterThreshold = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(10));

        ExpectSequence<double>([.05, .30], slowerThreshold.Events.Where(e => e.Release).Select(e => e.Time),
            "KPS policy nominal key-up times");
        ExpectSequence(Trace(slowerThreshold.Events), Trace(fasterThreshold.Events),
            "KPS grouping threshold must not alter timing for this sparse rhythm");
    }

    private static void KpsOutputIgnoresTempoOnlyInputs()
    {
        var profileA = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        var profileB = new TechniqueProfile(900, Hand([65, 66]), Hand([74, 75]));
        TechniqueNote[] notesA = [Tap(0, 0, 1), Tap(.04, 1, 1), Tap(.30, 2, 1)];
        TechniqueNote[] notesB = [Tap(0, 0, 2), Tap(.04, 1, .5), Tap(.30, 2, 3)];
        var strategyA = new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5);
        var strategyB = new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5);
        var first = TechniquePlanner.Build(notesA, 120, 1, startRight: false, _ => profileA, strategyA);
        var second = TechniquePlanner.Build(notesB, 500, 2, startRight: false, _ => profileB, strategyB);

        ExpectSequence(Trace(first.Events), Trace(second.Events),
            "KPS event output must only depend on its consumed time/layout/KPS inputs");
        ExpectSequence(first.Assignments.Select(a => (a.Hand, a.KeySlot, a.Key)),
            second.Assignments.Select(a => (a.Hand, a.KeySlot, a.Key)),
            "KPS assignments must ignore BPM limits and chart-speed fields");
    }

    private static void KpsVarianceUsesObservedRunSpan()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.04, 1), Tap(.30, 2)];
        var plan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(10));

        Check(plan.Pieces == 2, $"expected two KPS runs, got {plan.Pieces}");
        Check(Math.Abs(plan.Variance - .0004) < 1e-12,
            $"KPS run-span variance should measure [.04, 0], got {plan.Variance:R}");
        VerifyPlan(notes, plan.Events);
    }

    private static void GroupingStrategyFingerprintTracksPolicy()
    {
        int beatGrid = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.BeatGridStrategyId, 5);
        Check(beatGrid == TechniquePlanner.GroupingStrategyFingerprint("unknown-policy", 30),
            "unknown strategies should canonicalize to the legacy beat-grid policy");
        int kpsFive = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.KpsAlternatingStrategyId, 5);
        int kpsTen = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.KpsAlternatingStrategyId, 10);
        Check(kpsFive != kpsTen, "different KPS thresholds must invalidate the technique fingerprint");
        Check(kpsFive == TechniquePlanner.GroupingStrategyFingerprint("KPSALTERNATING", 5),
            "strategy ids should be case insensitive");
    }

    private static void PolicyFingerprintOnlyTracksConsumedInputs()
    {
        TechniqueTile?[] tiles = [Tile(0, 1), Tile(.25, 1)];
        var changedSpeed = (TechniqueTile?[])tiles.Clone();
        changedSpeed[1] = Tile(.25, 2);
        int kpsChart = TechniqueTimeline.Fingerprint(tiles, 120, 1, includeTempo: false, includeTileSpeed: false);
        Check(kpsChart == TechniqueTimeline.Fingerprint(changedSpeed, 240, 2,
                includeTempo: false, includeTileSpeed: false),
            "KPS chart fingerprint should ignore BPM and speed fields that its grouping policy does not read");
        Check(kpsChart != TechniqueTimeline.Fingerprint([Tile(0, 1), Tile(.26, 1)], 120, 1,
                includeTempo: false, includeTileSpeed: false),
            "KPS chart fingerprint must still track attack-time changes");

        var left = Hand([65, 66]);
        var right = Hand([74, 75]);
        var slowProfile = new TechniqueProfile(120, left, right);
        var fastProfile = new TechniqueProfile(900, left, right);
        Check(TechniquePlanner.ProfileFingerprint(slowProfile, TechniquePlanner.KpsAlternatingStrategyId) ==
              TechniquePlanner.ProfileFingerprint(fastProfile, TechniquePlanner.KpsAlternatingStrategyId),
            "KPS profile fingerprint should ignore the unused BPM limit");
        Check(TechniquePlanner.ProfileFingerprint(slowProfile, TechniquePlanner.BeatGridStrategyId) !=
              TechniquePlanner.ProfileFingerprint(fastProfile, TechniquePlanner.BeatGridStrategyId),
            "beat-grid profile fingerprint must include BPM limit");
        Check(TechniquePlanner.ProfileFingerprint(slowProfile, TechniquePlanner.TempoWindowStrategyId) !=
              TechniquePlanner.ProfileFingerprint(fastProfile, TechniquePlanner.TempoWindowStrategyId),
            "tempo-window profile fingerprint must include BPM limit");
        var changedKeys = new TechniqueProfile(120, Hand([65, 67]), right);
        Check(TechniquePlanner.ProfileFingerprint(slowProfile, TechniquePlanner.KpsAlternatingStrategyId) !=
              TechniquePlanner.ProfileFingerprint(changedKeys, TechniquePlanner.KpsAlternatingStrategyId),
            "KPS profile fingerprint must retain relevant key layout changes");
        Check(TechniqueTimeline.Fingerprint(tiles, 120, 1) !=
              TechniqueTimeline.Fingerprint(changedSpeed, 120, 1),
            "tempo-aware policies must fingerprint per-floor speed changes");
    }

    private static void TempoWindowStrategyFingerprintDiffers()
    {
        int beatGrid = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.BeatGridStrategyId, 6.5);
        int kps = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.KpsAlternatingStrategyId, 6.5);
        int tempoWindow = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.TempoWindowStrategyId, 6.5);
        Check(tempoWindow != beatGrid && tempoWindow != kps,
            "tempo-window traces must have their own strategy fingerprint");
        Check(tempoWindow == TechniquePlanner.GroupingStrategyFingerprint("TEMPOWINDOW", 2),
            "tempo-window policy id should normalize case and ignore unused KPS values");
    }

    private static void StrategyFactorySelectsAndDefaultsPolicies()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66, 67]), Hand([74, 75, 76]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.04, 1), Tap(.08, 2), Tap(.30, 3)];
        var kpsStrategy = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.KpsAlternatingStrategyId, 5);
        var selected = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile, kpsStrategy);
        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right],
            selected.Assignments.Select(a => a.Hand), "factory-selected KPS grouping");

        var tempoStrategy = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.TempoWindowStrategyId, 5);
        var tempoSelected = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile, tempoStrategy);
        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right],
            tempoSelected.Assignments.Select(a => a.Hand), "factory-selected tempo-window grouping");

        var legacy = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile);
        var unknown = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            TechniquePlanner.CreateGroupingStrategy("unrecognized", 5));
        ExpectSequence(Trace(legacy.Events), Trace(unknown.Events),
            "unknown saved policy ids should fall back to the default strategy");
    }

    private static void KpsGroupingPreservesHoldReleaseAndProfileBoundaries()
    {
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        var notes = TechniqueTimeline.Build(
        [
            Tile(0, 1.25), null, Tile(2, auto: true), Tile(3, midSpin: true),
            Tile(4, 1.5, midSpin: true, hold: true), Tile(5, auto: true), Tile(6, 1.75), Tile(7),
        ]);
        var holdPlan = TechniquePlanner.Build(notes, 120, 1, startRight: false, _ => profile,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5));
        VerifyPlan(notes, holdPlan.Events);
        var held = holdPlan.Assignments.Single(a => a.Hold);
        Check(holdPlan.Events.Any(e => e.Release && e.Hold && e.Key == held.Key && e.Time == 6),
            "KPS grouping must release a hold at its source timeline endpoint");

        var before = new TechniqueProfile(120, Hand([65, 67]), Hand([74, 76]));
        var after = new TechniqueProfile(120, Hand([66, 68]), Hand([75, 77]));
        TechniqueNote[] boundaryNotes = [Tap(0, 0), Tap(.01, 1), Tap(.02, 2)];
        TechniqueProfile Resolve(int floor) => floor < 2 ? before : after;
        var boundaryPlan = TechniquePlanner.Build(boundaryNotes, 120, 1, startRight: false, Resolve,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(10));
        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right],
            boundaryPlan.Assignments.Select(a => a.Hand), "KPS profile boundary groups");
        ExpectSequence<byte>([65, 67, 75], boundaryPlan.Assignments.Select(a => a.Key),
            "KPS profile-specific layout mapping");
        VerifyPlan(boundaryNotes, boundaryPlan.Events);
    }

    private static void KpsGroupingOnlySplitsOnRelevantLayoutChanges()
    {
        var sameKeysSlowGrid = new TechniqueProfile(120, Hand([65, 66, 67]), Hand([74, 75, 76]));
        var sameKeysFastGrid = new TechniqueProfile(900, Hand([65, 66, 67]), Hand([74, 75, 76]));
        TechniqueNote[] notes = [Tap(0, 0), Tap(.01, 1), Tap(.02, 2)];
        TechniqueProfile Resolve(int floor) => floor < 2 ? sameKeysSlowGrid : sameKeysFastGrid;
        var kps = new TechniquePlanner.KpsAlternatingGroupingStrategy(10);

        var equivalent = TechniquePlanner.Build(notes, 120, 1, startRight: false, Resolve, kps);
        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Left],
            equivalent.Assignments.Select(a => a.Hand), "unused BPM profile change should preserve the KPS run");
        ExpectSequence<int>([0, 1, 2], equivalent.Assignments.Select(a => a.KeySlot),
            "equivalent layout should keep one key-order sequence");

        var changedLayout = new TechniqueProfile(120,
            HandLayoutWithRatio([65, 66, 67], [.8, 1.1, .8]), Hand([74, 75, 76]));
        TechniqueProfile ResolveChanged(int floor) => floor < 2 ? sameKeysSlowGrid : changedLayout;
        var split = TechniquePlanner.Build(notes, 120, 1, startRight: false, ResolveChanged, kps);
        ExpectSequence<TechniqueHand>([TechniqueHand.Left, TechniqueHand.Left, TechniqueHand.Right],
            split.Assignments.Select(a => a.Hand), "changed press ratios should split the KPS run");
        VerifyPlan(notes, equivalent.Events);
        VerifyPlan(notes, split.Events);
    }

    private static void DenseRunReleasesEveryPress()
    {
        // 32 taps per base slice, but every 7.8125 ms tap can be split further.
        var notes = Enumerable.Range(0, 48).Select(i => Tap(1 + i / 128.0, i)).ToArray();
        var plan = Build(notes);
        VerifyPlan(notes, plan);
        Check(plan[^1].Release && plan[^1].Time > notes[^1].Time,
            "the final tap must still receive a release after its down");
    }

    private static void SpeedAndSegmentChanges()
    {
        // At 1.5x, 180 effective BPM folds to 90 under the 120 BPM limit.
        // The new third-second half-beats must alternate on the re-anchored grid.
        TechniqueNote[] speedOnly =
        [
            Tap(0, 0), Tap(.25, 1), Tap(.5, 2, 1.5), Tap(.5 + 1.0 / 3, 3, 1.5),
            Tap(.5 + 2.0 / 3, 4, 1.5), Tap(1.5, 5, 1.5),
        ];
        var speedPlan = Build(speedOnly);
        VerifyPlan(speedOnly, speedPlan);
        ExpectSequence<byte>([65, 74, 65, 74, 65, 74], Presses(speedPlan).Select(e => e.Key), "half-beat phase after a speed change");

        var before = new TechniqueProfile(120, Hand([65]), Hand([74, 75]));
        var after = new TechniqueProfile(200, Hand([68, 69, 70]), Hand([76]));
        TechniqueNote[] notes =
        [
            Tap(0, 0), Tap(.25, 1), Tap(.375, 2, 1.5), Tap(.375 + 1.0 / 6, 3, 1.5),
            Tap(.60, 4, 1.5), Tap(.63, 5, 1.5), Tap(.66, 6, 1.5),
            Tap(.80, 7, 1.5), Tap(1.04, 8, 1.25), Tap(1.24, 9, 1.25),
        ];
        TechniqueProfile Resolve(int floor) => floor < 4 ? before : after;
        byte[] oldKeys = [65, 74, 75];
        byte[] newKeys = [68, 69, 70, 76];
        foreach (bool startRight in new[] { false, true })
        {
            var plan = Build(notes, startRight: startRight, resolve: Resolve);
            VerifyPlan(notes, plan);
            foreach (var e in Presses(plan))
                Check((e.Floor < 4 ? oldKeys : newKeys).Contains(e.Key), $"floor {e.Floor} used key {e.Key} from the wrong segment");
        }
    }

    private static void InvalidTimingParameters()
    {
        var left = Hand([65, 66, 67], [double.NaN, -2, double.PositiveInfinity]);
        var right = Hand([74], [0]);
        var notes = new[]
        {
            Tap(0, 0, double.NaN), Tap(.02, 1, 0), Tap(.04, 2, double.PositiveInfinity),
            Tap(.075, 3, -2), Tap(.30, 4), Tap(.5, 5, double.NegativeInfinity),
        };
        foreach (double bad in new[] { 0.0, -120, double.NaN, double.PositiveInfinity })
        {
            var profile = new TechniqueProfile(bad, left, right);
            var plan = Build(notes, bpm: bad, fallbackSpeed: bad, resolve: _ => profile);
            VerifyPlan(notes, plan);
            Check(plan.All(e => e.Key is 65 or 66 or 67 or 74), "timing fallbacks must preserve the configured keys");
        }
    }

    private static void VeryLongRest()
    {
        // Billions of empty slices: stepping through them one-by-one is not viable.
        TechniqueNote[] notes = [Tap(0, 0), Tap(1_000_000_000.25, 1), Tap(1_000_000_000.5, 2)];
        var plan = Build(notes);
        VerifyPlan(notes, plan);
        ExpectSequence<byte>([65, 74, 65], Presses(plan).Select(e => e.Key), "phase after a multi-billion-slice rest");
    }

    private static void SameMicrosecondAndSharedPhysicalKey()
    {
        // All keys in both layouts alias one physical key. Equal timestamps and
        // sub-microsecond distances must not drop taps or move an up before its down.
        var repeatedKey = new TechniqueProfile(120, Hand([65, 65]), Hand([65]));
        TechniqueNote[] notes = [Tap(1, 0), Tap(1, 1), Tap(1.0000002, 2), Tap(1.0000004, 3), Tap(1.25, 4)];
        var plan = Build(notes, resolve: _ => repeatedKey);
        VerifyPlan(notes, plan);
        Check(plan.All(e => e.Key == 65), "repeated layout entries must still use the configured physical key");
        ExpectSequence(Trace(plan), Trace(Build(notes, resolve: _ => repeatedKey)), "stable event ordering at equal times");
    }

    private static void OrdinaryTailUsesSourceFloor()
    {
        TechniqueTile?[] tiles = [Tile(2, 1.25), Tile(2.25, 1.5), Tile(2.5, 1.75), Tile(2.75, 2)];
        var notes = TechniqueTimeline.Build(tiles);
        ExpectTimeline(
            [(2.25, 0, 1.25, NoteKind.Tap), (2.5, 1, 1.5, NoteKind.Tap), (2.75, 2, 1.75, NoteKind.Tap)],
            notes);
        VerifyPlan(notes, Build(notes));
    }

    private static void TimelineSkipsDecorationsAndClosesHolds()
    {
        TechniqueTile?[] tiles =
        [
            Tile(0, 1.25), null, Tile(2, auto: true), Tile(3, midSpin: true),
            Tile(4, 1.5, midSpin: true, hold: true), Tile(5, auto: true), Tile(6, 1.75), Tile(7),
        ];
        var notes = TechniqueTimeline.Build(tiles);
        ExpectTimeline([(4, 0, 1.25, NoteKind.HoldStart), (6, 4, 1.5, NoteKind.HoldEnd), (7, 6, 1.75, NoteKind.Tap)], notes);
        var plan = Build(notes);
        VerifyPlan(notes, plan);
        var hold = Presses(plan).Single(e => e.Hold);
        Check(plan.Any(e => e.Release && e.Key == hold.Key && e.Time == 6), "an interior hold must end on its destination tile");
    }

    private static void HoldAtTailReleasesBeforeTerminalTap()
    {
        var sameKey = new TechniqueProfile(120, Hand([65]), Hand([65]));
        foreach (var tail in new[] { Tile(4), Tile(4, auto: true), Tile(4, midSpin: true) })
        {
            var notes = TechniqueTimeline.Build([Tile(0, 1.25), Tile(1, 1.5, hold: true), Tile(2, auto: true), tail]);
            bool terminalTap = !tail.Auto && !tail.MidSpin;
            if (terminalTap)
                ExpectTimeline([(1, 0, 1.25, NoteKind.HoldStart), (4, 1, 1.5, NoteKind.HoldEnd), (4, 3, 1, NoteKind.Tap)], notes);
            else
                ExpectTimeline([(1, 0, 1.25, NoteKind.HoldStart), (4, 1, 1.5, NoteKind.HoldEnd)], notes);

            var plan = Build(notes, resolve: _ => sameKey);
            VerifyPlan(notes, plan);
            var holdRelease = plan.First(e => e.Release);
            Check(holdRelease.Time == 4 && holdRelease.Key == 65,
                $"held key must release at the 4s tail, got key {holdRelease.Key} at {holdRelease.Time:R}s");
            Check(plan.First(e => e.Time >= 4).Release, "release must precede any same-time terminal tap");
        }
    }

    private static void TimelineFingerprintTracksInputs()
    {
        TechniqueTile?[] tiles = [Tile(0), Tile(.25), null, Tile(.75)];
        int fingerprint = TechniqueTimeline.Fingerprint(tiles, 120, 1);
        Check(fingerprint == TechniqueTimeline.Fingerprint((TechniqueTile?[])tiles.Clone(), 120, 1), "equal tile values should hash the same");
        Check(fingerprint != TechniqueTimeline.Fingerprint(tiles, 121, 1), "BPM must enter the fingerprint");
        Check(fingerprint != TechniqueTimeline.Fingerprint(tiles, 120, 1.25), "global speed must enter the fingerprint");
        foreach (var changed in new TechniqueTile?[]
        {
            Tile(.26), Tile(.25, 1.25), Tile(.25, auto: true), Tile(.25, midSpin: true), Tile(.25, hold: true), null,
        })
        {
            var edited = (TechniqueTile?[])tiles.Clone();
            edited[1] = changed;
            Check(fingerprint != TechniqueTimeline.Fingerprint(edited, 120, 1), "same-length tile edit did not invalidate the fingerprint");
        }
    }

    private static void LongChartIsDeterministic()
    {
        // Cross the old 65,536-entry pool boundary without losing the chart tail.
        const int count = 70_000;
        var tiles = new TechniqueTile?[count + 1];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = Tile(i / 8.0, 1 + i / 1000 % 3 * .25);
        var notes = TechniqueTimeline.Build(tiles);
        Check(notes.Length == count && notes[^1].Floor == count - 1 && notes[^1].Time == tiles[^1]!.Value.Time,
            "long timeline must preserve all ordinary notes and the terminal press");
        var first = Build(notes);
        var second = Build(TechniqueTimeline.Build((TechniqueTile?[])tiles.Clone()));
        VerifyPlan(notes, first);
        ExpectSequence(Trace(first), Trace(second), "full event stream deterministic across builds");
    }

    private static void RepositoryChartPreservesEveryPress()
    {
        // A checked-in judgment timeline from the historical A/B tooling, not a synth chart.
        var notes = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "main.timedata"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(','))
            .Select(row => Tap(long.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture) / 1e6,
                int.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        Check(notes.Length > 3000, "real-chart fixture unexpectedly short");
        var profile = new TechniqueProfile(550, Hand([65, 83, 68, 70], [.8, 1, 1.2, 1.4]),
            Hand([74, 75, 76, 186], [.8, 1, 1.2, 1.4]));
        var gridPlan = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile);
        VerifyPlan(notes, gridPlan.Events);
        Console.WriteLine($"  Real chart: {notes.Length} onsets, every press and release verified");
        var (gridRuns, gridLongest) = HandRunStats(gridPlan.Assignments);
        Console.WriteLine($"  Half-beat baseline: {gridPlan.Pieces} groups, {gridRuns} contiguous hand runs, longest {gridLongest}");

        var kpsPlan = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile,
            new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5));
        VerifyPlan(notes, kpsPlan.Events);
        var (kpsRuns, kpsLongest) = HandRunStats(kpsPlan.Assignments);
        Console.WriteLine($"  KPS prototype: {kpsPlan.Pieces} groups, {kpsRuns} contiguous hand runs, longest {kpsLongest}");

        var tempoPlan = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile,
            new TechniquePlanner.TempoWindowGroupingStrategy());
        VerifyPlan(notes, tempoPlan.Events);
        var (tempoRuns, tempoLongest) = HandRunStats(tempoPlan.Assignments);
        Console.WriteLine($"  Tempo-window prototype: {tempoPlan.Pieces} groups, {tempoRuns} contiguous hand runs, longest {tempoLongest}");

        foreach (var strategyId in new[] { TechniquePlanner.KpsAlternatingStrategyId, TechniquePlanner.TempoWindowStrategyId })
        {
            var preferredPlan = TechniquePlanner.Build(notes, 454, 1, startRight: true, _ => profile,
                TechniquePlanner.CreateGroupingStrategy(strategyId, 6.5, TechniquePlanner.RecoveredPreferredHandPolicyId));
            VerifyPlan(notes, preferredPlan.Events);
            Check(preferredPlan.Assignments.Length == notes.Length,
                $"preferred-hand {strategyId} must preserve all chart attacks");
        }

        byte[] referenceSizedLeft = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[] referenceSizedRight = Enumerable.Range(33, 16).Select(value => (byte)value).ToArray();
        var referenceSizedProfile = new TechniqueProfile(500,
            Hand(referenceSizedLeft), Hand(referenceSizedRight));
        var fixedKps = TechniquePlanner.Build(notes, 454, 1, startRight: true,
            _ => referenceSizedProfile, new TechniquePlanner.KpsAlternatingGroupingStrategy(7));
        VerifyPlan(notes, fixedKps.Events);
        var (fixedRuns, fixedLongest) = HandRunStats(fixedKps.Assignments);
        Console.WriteLine($"  Current fixed-window KPS (16 slots/hand): {fixedRuns} contiguous hand runs, longest {fixedLongest}");
        Console.WriteLine($"  Current fixed-window KPS first 24 hands: {string.Concat(fixedKps.Assignments.Take(24).Select(a => a.Hand == TechniqueHand.Right ? 'R' : 'L'))}");
    }

    private static (int Runs, int Longest) HandRunStats(IEnumerable<TechniqueAssignment> assignments)
    {
        int runs = 0, longest = 0, current = 0;
        TechniqueHand? previous = null;
        foreach (var assignment in assignments)
        {
            if (previous != assignment.Hand)
            {
                runs++;
                current = 0;
                previous = assignment.Hand;
            }
            current++;
            longest = Math.Max(longest, current);
        }
        return (runs, longest);
    }

    private static TechniqueEvent[] Build(TechniqueNote[] notes, double bpm = 120, double fallbackSpeed = 1,
        bool startRight = false, Func<int, TechniqueProfile>? resolve = null) =>
        TechniquePlanner.Build(notes, bpm, fallbackSpeed, startRight, resolve ?? (_ => TwoHands)).Events;

    private static void RepositoryIsolatedPairsStayOnOneHand()
    {
        var notes = RepositoryTapNotes();
        var profile = new TechniqueProfile(550, Hand([65,83,68,70],[.8,1,1.2,1.4]),
            Hand([74,75,76,186],[.8,1,1.2,1.4]));
        var plan = TechniquePlanner.Build(notes,454,1,false,_=>profile);
        int pairs = 0;
        for (int i=1;i<notes.Length;i++)
        {
            double gap = notes[i].Time-notes[i-1].Time;
            if (gap < .003 || gap >= .0167 ||
                i > 1 && notes[i-1].Time-notes[i-2].Time < .0167 ||
                i+1 < notes.Length && notes[i+1].Time-notes[i].Time < .0167) continue;
            pairs++;
            Check(plan.Assignments[i-1].Hand==plan.Assignments[i].Hand,
                $"isolated repository pair {notes[i-1].Floor}/{notes[i].Floor} was split");
        }
        Check(pairs>0,"repository isolated-pair acceptance must inspect actual cases");
        Console.WriteLine($"  Repository isolated pairs: {pairs}, all on one hand");
        VerifyPlan(notes,plan.Events);
    }

    private static void DefaultStrategyOutputMatchesGolden()
    {
        var notes = RepositoryTapNotes();
        var profile = new TechniqueProfile(550, Hand([65, 83, 68, 70], [.8, 1, 1.2, 1.4]),
            Hand([74, 75, 76, 186], [.8, 1, 1.2, 1.4]));
        var cases = new (string Name, int Hash)[]
        {
            ("beatGrid", PlanHash(TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile))),
            ("kpsAlternating", PlanHash(TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile,
                new TechniquePlanner.KpsAlternatingGroupingStrategy(6.5)))),
            ("tempoWindow", PlanHash(TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile,
                new TechniquePlanner.TempoWindowGroupingStrategy()))),
            ("kpsRecoveredPreferred", PlanHash(TechniquePlanner.Build(notes, 454, 1, startRight: true, _ => profile,
                TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.KpsAlternatingStrategyId, 6.5,
                    TechniquePlanner.RecoveredPreferredHandPolicyId)))),
            ("tempoRecoveredPreferred", PlanHash(TechniquePlanner.Build(notes, 454, 1, startRight: true, _ => profile,
                TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.TempoWindowStrategyId, 6.5,
                    TechniquePlanner.RecoveredPreferredHandPolicyId)))),
        };
        var expected = new Dictionary<string, int>
        {
            // Revision 21 corroborates sparse accents and measured tempo changes.
            ["beatGrid"] = 756613671,
            ["kpsAlternating"] = -48530905,
            ["tempoWindow"] = -1802184853,
            ["kpsRecoveredPreferred"] = -235997156,
            ["tempoRecoveredPreferred"] = 820843684,
        };
        Check(cases.All(c => expected[c.Name] == c.Hash), "default output changed: " +
            string.Join("; ", cases.Select(c => $"{c.Name}: expected {expected[c.Name]}, got {c.Hash}")));
    }

    private static void PieceFallbackBacktracksOffHandOverflow()
    {
        // One key per hand at 480 BPM: both off-hand attacks overflow the left hand, so the
        // earlier right-hand piece is re-cut at a finer subdivision before any group is emitted.
        var notes = new[] { Tap(0.01, 0), Tap(0.07, 1), Tap(0.075, 2) };
        var profile = new TechniqueProfile(500, Hand([65]), Hand([74]));
        var plan = TechniquePlanner.Build(notes, 480, 1, startRight: true, _ => profile,
            TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.PieceFallbackStrategyId, 6.5));
        VerifyPlan(notes, plan.Events);
        ExpectSequence<TechniqueHand>([TechniqueHand.Right, TechniqueHand.Right, TechniqueHand.Left],
            plan.Assignments.Select(a => a.Hand), "piece fallback re-cuts the earlier main-hand piece");
        Check(plan.Pieces == 3, $"expected three non-empty pieces, got {plan.Pieces}");
    }

    private static void PieceFallbackSurvivesCoincidentBurst()
    {
        // Twelve simultaneous presses exceed every hand at any subdivision. The planner must
        // terminate, assign every press, and keep each release ordered before its key is reused.
        var notes = Enumerable.Range(0, 12).Select(floor => Tap(0.5, floor)).ToArray();
        var profile = new TechniqueProfile(500, Hand([65]), Hand([74]));
        var plan = TechniquePlanner.Build(notes, 480, 1, startRight: true, _ => profile,
            TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.PieceFallbackStrategyId, 6.5));
        VerifyPlan(notes, plan.Events);
        Check(plan.Assignments.Length == notes.Length, "every coincident press needs a hand assignment");
    }

    private static void PieceFallbackKeepsEveryRepositoryPress()
    {
        var notes = RepositoryTapNotes();
        var profile = new TechniqueProfile(550, Hand([65, 83, 68, 70], [.8, 1, 1.2, 1.4]),
            Hand([74, 75, 76, 186], [.8, 1, 1.2, 1.4]));
        var plan = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile,
            TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.PieceFallbackStrategyId, 6.5));
        VerifyPlan(notes, plan.Events);
        Check(plan.Assignments.Length == notes.Length, "piece fallback must preserve all chart attacks");
        var (runs, longest) = HandRunStats(plan.Assignments);
        Console.WriteLine($"  Piece fallback: {plan.Pieces} groups, {runs} contiguous hand runs, longest {longest}");
    }

    private static void LazyReleaseOnlyShiftsReleaseTiming()
    {
        // Lazy release changes only when a plain press lets go: presses and keys match the plain plan,
        // and each release lands just before the next press on the same key.
        var notes = new[] { Tap(0.0, 0), Tap(0.1, 1), Tap(0.2, 2), Tap(0.3, 3) };
        var profile = new TechniqueProfile(120, Hand([65, 66]), Hand([74, 75]));
        var grouping = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.BeatGridStrategyId, 6.5);
        var plain = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => profile, grouping);
        var lazy = TechniquePlanner.Build(notes, 120, 1, startRight: true, _ => profile, grouping, lazyRelease: true);
        VerifyPlan(notes, lazy.Events);
        ExpectSequence(Presses(plain.Events).Select(e => (e.Time, e.Floor, e.Key)),
            Presses(lazy.Events).Select(e => (e.Time, e.Floor, e.Key)), "lazy release keeps press times and keys");

        var events = lazy.Events;
        for (int i = 0; i < events.Length; i++)
        {
            if (events[i].Release) continue;
            int release = -1, next = -1;
            for (int j = i + 1; j < events.Length && (release < 0 || next < 0); j++)
            {
                if (events[j].Key != events[i].Key) continue;
                if (events[j].Release) { if (release < 0) release = j; }
                else if (next < 0) next = j;
            }
            Check(release >= 0, $"press at {events[i].Time:R} has no release");
            double expected = next >= 0 ? events[next].Time - 0.0002 : notes[^1].Time + 0.015;
            Check(release < (next >= 0 ? next : events.Length) && Math.Abs(events[release].Time - expected) < 1e-9,
                $"lazy release for press at {events[i].Time:R} must land at {expected:R}, got {events[release].Time:R}");
        }
    }

    private static void LazyReleaseKeepsRepositoryPresses()
    {
        var notes = RepositoryTapNotes();
        var profile = new TechniqueProfile(550, Hand([65, 83, 68, 70], [.8, 1, 1.2, 1.4]),
            Hand([74, 75, 76, 186], [.8, 1, 1.2, 1.4]));
        var grouping = TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.BeatGridStrategyId, 6.5);
        var plain = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile, grouping);
        var lazy = TechniquePlanner.Build(notes, 454, 1, startRight: false, _ => profile, grouping, lazyRelease: true);
        VerifyPlan(notes, lazy.Events);
        ExpectSequence(Presses(plain.Events).Select(e => (e.Time, e.Floor, e.Key)),
            Presses(lazy.Events).Select(e => (e.Time, e.Floor, e.Key)), "lazy release keeps every press");
    }

    private static void StrategyIdentityAndFingerprintsForNewOptions()
    {
        Check(TechniquePlanner.NormalizeStrategyId("PieceFallback") == TechniquePlanner.PieceFallbackStrategyId,
            "piece fallback id must normalize case-insensitively");
        Check(TechniquePlanner.NormalizeStrategyId("unknown") == TechniquePlanner.BeatGridStrategyId,
            "unknown strategy ids keep the beat-grid default");
        int beat = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.BeatGridStrategyId, 6.5);
        int tempo = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.TempoWindowStrategyId, 6.5);
        int piece = TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.PieceFallbackStrategyId, 6.5);
        Check(piece != beat && piece != tempo, "piece fallback must not share a grouping fingerprint");
        Check(piece == TechniquePlanner.GroupingStrategyFingerprint(TechniquePlanner.PieceFallbackStrategyId, 6.5,
            TechniquePlanner.RecoveredPreferredHandPolicyId), "piece fallback ignores the run hand policy");
        Check(TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.PieceFallbackStrategyId, 6.5).UsesTempoInputs,
            "piece fallback consumes tempo inputs");
    }

    private static void PieceFallbackGroupsFormValidPartitions()
    {
        var random = new Random(20261123);
        for (int trial = 0; trial < 300; trial++)
        {
            var (notes, attacks, startRight) = RandomPieceInput(random);
            CheckPartition(attacks, PieceFallbackGroups(notes, attacks, startRight), $"trial {trial}");
        }
    }

    private static void PieceFallbackIsDeterministic()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 100; trial++)
        {
            var (notes, attacks, startRight) = RandomPieceInput(random);
            var first = PieceFallbackGroups(notes, attacks, startRight);
            var second = PieceFallbackGroups(notes, attacks, startRight);
            Check(first.Count == second.Count, $"trial {trial}: group count changed between runs");
            for (int i = 0; i < first.Count; i++)
                Check(first[i].Start == second[i].Start && first[i].End == second[i].End &&
                    first[i].Length == second[i].Length && first[i].PressWindow == second[i].PressWindow &&
                    first[i].Right == second[i].Right, $"trial {trial}: group {i} changed between runs");
        }
    }

    private static void PieceFallbackTerminatesOnDenseBursts()
    {
        var notes = Enumerable.Range(0, 300).Select(floor => Tap(0.5, floor)).ToArray();
        var profile = new TechniqueProfile(500, Hand([65]), Hand([74]));
        var attacks = notes.Select((note, index) => new TechniquePlanner.Attack(index, profile, 0.0625)).ToList();
        var slices = PieceFallbackGroups(notes, attacks, startRight: true);
        CheckPartition(attacks, slices, "dense burst");
        Check(slices.Sum(slice => slice.Count) == notes.Length, "dense burst must keep every attack");
    }

    private static List<TechniquePlanner.Slice> PieceFallbackGroups(TechniqueNote[] notes,
        List<TechniquePlanner.Attack> attacks, bool startRight) =>
        TechniquePlanner.CreateGroupingStrategy(TechniquePlanner.PieceFallbackStrategyId, 6.5)
            .Group(notes, attacks, startRight);

    private static void CheckPartition(List<TechniquePlanner.Attack> attacks,
        List<TechniquePlanner.Slice> slices, string label)
    {
        Check(slices.Count > 0, $"{label}: no groups");
        int next = 0;
        foreach (var slice in slices)
        {
            Check(slice.Start == next && slice.End > slice.Start && slice.End <= attacks.Count,
                $"{label}: groups must be contiguous and nonempty at attack {next}");
            var profile = attacks[slice.Start].Profile;
            for (int i = slice.Start; i < slice.End; i++)
                Check(ReferenceEquals(attacks[i].Profile, profile), $"{label}: group crosses a profile at attack {i}");
            Check(slice.Count <= Math.Max(1, profile.Layout(slice.Right).Capacity),
                $"{label}: group of {slice.Count} exceeds hand capacity");
            Check(double.IsFinite(slice.Length) && slice.Length > 0 &&
                double.IsFinite(slice.PressWindow) && slice.PressWindow > 0,
                $"{label}: group timing must be finite and positive");
            next = slice.End;
        }
        Check(next == attacks.Count, $"{label}: groups must cover every attack");
    }

    private static (TechniqueNote[] Notes, List<TechniquePlanner.Attack> Attacks, bool StartRight) RandomPieceInput(Random random)
    {
        int count = random.Next(1, 60);
        var profiles = new[]
        {
            new TechniqueProfile(500, Hand([65, 83, 68]), Hand([74, 75])),
            new TechniqueProfile(500, Hand([65]), Hand([74, 75, 76, 186])),
        };
        double[] gaps = [0, 0, 0.0005, 0.01, 0.03, 0.07, 0.2];
        double[] cells = [0.0625, 0.03125, 0.125, 0.25];
        var notes = new TechniqueNote[count];
        var attacks = new List<TechniquePlanner.Attack>(count);
        double time = random.NextDouble() * 0.05;
        int profile = random.Next(profiles.Length);
        double cell = cells[random.Next(cells.Length)];
        for (int i = 0; i < count; i++)
        {
            if (random.Next(6) == 0) profile = random.Next(profiles.Length);
            if (random.Next(6) == 0) cell = cells[random.Next(cells.Length)];
            time += gaps[random.Next(gaps.Length)];
            notes[i] = new TechniqueNote(time, i, 1, NoteKind.Tap);
            attacks.Add(new TechniquePlanner.Attack(i, profiles[profile], cell));
        }
        return (notes, attacks, random.Next(2) == 0);
    }

    private static TechniqueNote[] RepositoryTapNotes() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "main.timedata"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(','))
            .Select(row => Tap(long.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture) / 1e6,
                int.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

    private static int PlanHash(TechniquePlan plan)
    {
        var hash = new TechniqueHash();
        foreach (var e in plan.Events)
        {
            hash.Add(e.Time);
            hash.Add(e.Floor);
            hash.Add((double)e.Fraction);
            hash.Add((int)e.Key);
            hash.Add(e.Release ? 1 : 0);
            hash.Add(e.Hold ? 1 : 0);
        }
        foreach (var a in plan.Assignments)
        {
            hash.Add(a.Time);
            hash.Add(a.Floor);
            hash.Add((int)a.Hand);
            hash.Add(a.PreferredKeySlot);
            hash.Add(a.KeySlot);
            hash.Add((int)a.Key);
            hash.Add(a.Hold ? 1 : 0);
        }
        hash.Add(plan.Pieces);
        hash.Add(plan.Fragments);
        hash.Add(plan.LongestRun);
        hash.Add(plan.Variance);
        return hash.Value;
    }

    private static HandLayout Hand(byte[] keys, double[]? ratios = null) => new(keys,
        Enumerable.Range(1, keys.Length).Select(n => Enumerable.Range(0, n).ToArray()).ToArray(),
        ratios ?? Enumerable.Repeat(.8, keys.Length).ToArray());

    private static HandLayout HandLayoutWithRatio(byte[] keys, double[] ratios) => new(keys,
        Enumerable.Range(1, keys.Length).Select(n => Enumerable.Range(0, n).ToArray()).ToArray(), ratios);

    private static TechniqueNote Tap(double time, int floor, double speed = 1) => new(time, floor, speed, NoteKind.Tap);

    private static TechniqueTile Tile(double time, double speed = 1, bool auto = false, bool midSpin = false, bool hold = false) =>
        new(time, speed, auto, midSpin, hold);

    private static IEnumerable<TechniqueEvent> Presses(TechniqueEvent[] events) => events.Where(e => !e.Release);

    private static IEnumerable<(double, int, float, byte, bool, bool)> Trace(TechniqueEvent[] events) =>
        events.Select(e => (e.Time, e.Floor, e.Fraction, e.Key, e.Release, e.Hold));

    private static void ExpectTimeline((double Time, int Floor, double Speed, NoteKind Kind)[] expected, TechniqueNote[] actual) =>
        ExpectSequence(expected, actual.Select(n => (n.Time, n.Floor, n.Speed, n.Kind)), "timeline (time, source floor, source speed, kind)");

    private static void VerifyPlan(TechniqueNote[] notes, TechniqueEvent[] events)
    {
        var remaining = notes.Where(n => n.Kind != NoteKind.HoldEnd).ToDictionary(n => n.Floor);
        var active = new Dictionary<byte, TechniqueEvent>();
        double previousTime = double.NegativeInfinity;
        for (int i = 0; i < events.Length; i++)
        {
            var e = events[i];
            if (!double.IsFinite(e.Time) || !float.IsFinite(e.Fraction) || e.Time < previousTime)
                throw new InvalidOperationException($"event {i} has non-finite or unordered timing: {e.Time:R}, fraction {e.Fraction:R} (previous {previousTime:R})");
            previousTime = e.Time;
            if (e.Release)
            {
                if (!active.Remove(e.Key, out var down) || e.Time < down.Time)
                    throw new InvalidOperationException($"release {i} for key {e.Key} at {e.Time:R} has no preceding down");
                continue;
            }
            if (!active.TryAdd(e.Key, e))
                throw new InvalidOperationException($"press {i} at floor {e.Floor} repeated physical key {e.Key} before its release");
            if (!remaining.Remove(e.Floor, out var note))
                throw new InvalidOperationException($"extra or duplicate press at floor {e.Floor}");
            if (Math.Abs(e.Time - note.Time) > 1e-6 || e.Hold != (note.Kind == NoteKind.HoldStart))
                throw new InvalidOperationException($"incorrect press at floor {e.Floor}: time {e.Time:R} versus {note.Time:R}, hold={e.Hold} versus {note.Kind}");
        }
        Check(remaining.Count == 0, $"{remaining.Count} missing presses");
        Check(active.Count == 0, $"{active.Count} keys still held at the end of the plan");
    }

    private static void ExpectSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        using var want = expected.GetEnumerator();
        using var got = actual.GetEnumerator();
        for (int i = 0; ; i++)
        {
            bool hasExpected = want.MoveNext();
            bool hasActual = got.MoveNext();
            if (hasExpected != hasActual)
                throw new InvalidOperationException($"{message}: sequence length differs at index {i}");
            if (!hasExpected) return;
            if (!EqualityComparer<T>.Default.Equals(want.Current, got.Current))
                throw new InvalidOperationException($"{message}: index {i}, expected {want.Current}, got {got.Current}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
