using System;
using System.Collections.Generic;

#nullable enable

namespace ADOFAIMacro.Technique
{
    internal enum NoteKind { Tap, HoldStart, HoldEnd }

    internal readonly struct TechniqueNote(double time, int floor, double speed, NoteKind kind, double followingTurn = 0)
    {
        internal readonly double Time = time;
        internal readonly int Floor = floor;
        internal readonly double Speed = speed;
        internal readonly NoteKind Kind = kind;
        internal readonly double FollowingTurn = followingTurn;
    }

    internal readonly struct TechniqueTile(double time, double speed, bool auto, bool midSpin, bool hold, double turn = 0)
    {
        internal readonly double Time = time;
        internal readonly double Speed = speed;
        internal readonly bool Auto = auto;
        internal readonly bool MidSpin = midSpin;
        internal readonly bool Hold = hold;
        internal readonly double Turn = turn;
        internal bool Playable => !Auto && (!MidSpin || Hold);
    }

    /// <summary>Maps game tiles into a linear, source-floor-indexed judgment timeline.</summary>
    internal static class TechniqueTimeline
    {
        internal static TechniqueNote[] Build(TechniqueTile?[] tiles)
        {
            if (tiles.Length == 0) return [];
            var tail = tiles[tiles.Length - 1];
            var notes = new List<TechniqueNote>(tiles.Length);
            int source = -1;
            bool holdDown = false;
            for (int next = 0; next < tiles.Length; next++)
            {
                if (tiles[next] is not { } target || !target.Playable || !IsFinite(target.Time)) continue;
                if (source >= 0)
                {
                    var from = tiles[source]!.Value;
                    // A hold chain re-articulates on the next hold tile. A non-hold target
                    // finishes the chain without an extra press, except at the end of the chart.
                    bool finish = holdDown && (!target.Hold || next == tiles.Length - 1);
                    var kind = finish ? NoteKind.HoldEnd
                        : target.Hold && next != tiles.Length - 1 ? NoteKind.HoldStart : NoteKind.Tap;
                    notes.Add(new TechniqueNote(target.Time, source, from.Speed, kind, next == source + 1 ? target.Turn : 0));
                    if (finish && next == tiles.Length - 1)
                        notes.Add(new TechniqueNote(target.Time, next, target.Speed, NoteKind.Tap));
                    holdDown = kind == NoteKind.HoldStart;
                }
                else if (next == tiles.Length - 1)
                {
                    notes.Add(new TechniqueNote(target.Time, next, target.Speed, NoteKind.Tap));
                }
                source = next;
            }
            if (holdDown && tail is { } end && IsFinite(end.Time) && source >= 0)
                notes.Add(new TechniqueNote(end.Time, source, tiles[source]!.Value.Speed, NoteKind.HoldEnd));
            return notes.ToArray();
        }

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // Include the actual planner inputs, not just the file path and tile count:
        // editor changes at fixed length and different speed variants invalidate the same cache.
        internal static int Fingerprint(TechniqueTile?[] tiles, double bpm, double speed,
                                       bool includeTempo = true, bool includeTileSpeed = true, bool includeTurns = false)
        {
            var hash = new TechniqueHash();
            if (includeTempo)
            {
                hash.Add(bpm);
                hash.Add(speed);
            }
            hash.Add(tiles.Length);
            foreach (var tile in tiles)
            {
                if (tile is not { } t) { hash.Add(0); continue; }
                hash.Add(1 | (t.Auto ? 2 : 0) | (t.MidSpin ? 4 : 0) | (t.Hold ? 8 : 0));
                hash.Add(t.Time);
                if (includeTileSpeed) hash.Add(t.Speed);
                if (includeTurns) hash.Add(t.Turn);
            }
            return hash.Value;
        }
    }

    internal struct TechniqueHash
    {
        private uint _value;

        public TechniqueHash() => _value = 2166136261u;
        internal int Value => unchecked((int)_value);
        internal void Add(int value) => _value = unchecked((_value ^ (uint)value) * 16777619u);
        internal void Add(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            Add(unchecked((int)bits));
            Add(unchecked((int)(bits >> 32)));
        }
    }
}
