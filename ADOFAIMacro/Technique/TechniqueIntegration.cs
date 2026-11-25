using System;
using System.Collections.Generic;
using ADOFAIMacro.Core;

#nullable enable

namespace ADOFAIMacro.Technique
{
    internal static partial class MacroEngine
    {
        private readonly struct ConfiguredSegment(int start, int end, TechniqueProfile profile)
        {
            internal readonly int Start = start;
            internal readonly int End = end;
            internal readonly TechniqueProfile Profile = profile;
        }

        private sealed class TechniqueSource(TechniqueTile?[] tiles, double bpm, double speed, bool startRight,
                                            TechniquePlanner.IGroupingStrategy groupingStrategy,
                                            bool lazyRelease,
                                            TechniqueProfile profile, ConfiguredSegment[] segments, int fingerprint)
        {
            internal readonly int Fingerprint = fingerprint;
            internal int FloorCount => tiles.Length;

            private TechniqueProfile Resolve(int floor)
            {
                // Overlap precedence is the original first-match rule in profile order.
                foreach (var segment in segments)
                    if (floor >= segment.Start && floor <= segment.End) return segment.Profile;
                return profile;
            }

            internal TechniquePlan Build() => TechniquePlanner.Build(TechniqueTimeline.Build(tiles), bpm, speed,
                startRight, Resolve, groupingStrategy, lazyRelease);

            internal void PublishKeyDiagnostics()
            {
                var left = new HashSet<byte>(profile.Layout(false).Keys);
                var right = new HashSet<byte>(profile.Layout(true).Keys);
                foreach (var segment in segments)
                {
                    left.UnionWith(segment.Profile.Layout(false).Keys);
                    right.UnionWith(segment.Profile.Layout(true).Keys);
                }
                _techLeftKeys = [.. left];
                _techRightKeys = [.. right];
            }
        }

        private static void BuildTechniqueHitEvents(TechniqueSource source)
        {
            try
            {
                var plan = source.Build();
                var table = TechniquePlayback.PlanToHitEvents(plan);
                _hitEvents = table;
                _hitEventCount = table.Length;
                Log($"[Macro-Tech] 手法事件 {table.Length} · 分片 {plan.Pieces} · 单音片 {plan.Fragments} · 最长同手 {plan.LongestRun}");
            }
            catch (ArgumentException ex)
            {
                _hitEvents = [];
                _hitEventCount = 0;
                Main.Log($"[Macro-Tech] 谱面时间线无效：{ex.Message}");
            }
        }
    }
}
