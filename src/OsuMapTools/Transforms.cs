using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OsuMapTools
{
    public enum NoteMode
    {
        Keep = 0,
        Invert = 1,
        HoldOff = 2,
        Release = 3,
        ShortLn = 4,
    }

    public static class Transforms
    {
        /// <summary>
        /// FULL LN (NOODLES rule): every object becomes a hold that ends `beat / division` before the
        /// next object in the same column. If there is not enough room (gap &lt;= beat/division) or the
        /// object is the last one in its column, it stays a tap.
        /// division &lt;= 0 means "seamless": the hold ends exactly on the next object's head (0 ms gap).
        /// </summary>
        public static void ApplyInvert(OsuFile map, int division)
        {
            int keys = map.KeyCount <= 0 ? 4 : map.KeyCount;
            bool seamless = division <= 0;
            if (!seamless && division < 1) division = 4;
            var result = new List<HitObject>();

            foreach (var column in map.Objects.GroupBy(o => o.Column(keys)).OrderBy(g => g.Key))
            {
                var objects = column.OrderBy(o => o.Time).ThenBy(o => o.SourceIndex).ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var current = objects[i];

                    // Last object in the column: no next object to measure against, keep it as a tap.
                    if (i == objects.Count - 1)
                    {
                        current.MakeTap();
                        result.Add(current);
                        continue;
                    }

                    var next = objects[i + 1];
                    double d = next.Time - current.Time;
                    double beat = map.BeatLengthAt(current.Time);
                    double duration = seamless ? d : d - beat / division;

                    int end = current.Time + (int)Math.Floor(duration + 0.5);
                    // Sub-millisecond remainders (e.g. 87ms note on a 86.207ms quarter grid) are not
                    // playable as holds; NOODLES-style maps keep them as taps.
                    if (duration < 1.0 || end <= current.Time)
                    {
                        current.MakeTap();
                        result.Add(current);
                        continue;
                    }

                    result.Add(MakeHold(current, end));
                }
            }

            map.Objects = result;
            map.SortObjects();
        }

        /// <summary>Lazer ManiaModHoldOff: hold notes become notes at their head, everything else untouched.</summary>
        public static void ApplyHoldOff(OsuFile map)
        {
            foreach (var o in map.Objects)
            {
                if (o.IsHold) o.MakeTap();
            }
        }

        /// <summary>
        /// SHORT LN: convert every tap into an LN of a fixed length (beat / lengthDivision).
        /// The release-to-next-head gap is kept at least beat / minGapDivision; existing LNs are
        /// left untouched. The last tap in each column also becomes a short LN.
        /// </summary>
        public static void ApplyShortLn(OsuFile map, int lengthDivision, int minGapDivision)
        {
            if (lengthDivision < 1) lengthDivision = 4;
            if (minGapDivision < 1) minGapDivision = 4;
            int keys = map.KeyCount <= 0 ? 4 : map.KeyCount;
            var result = new List<HitObject>();

            foreach (var column in map.Objects.GroupBy(o => o.Column(keys)).OrderBy(g => g.Key))
            {
                var objects = column.OrderBy(o => o.Time).ThenBy(o => o.SourceIndex).ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var current = objects[i];
                    if (current.IsHold)
                    {
                        result.Add(current);
                        continue;
                    }

                    double beat = map.BeatLengthAt(current.Time);
                    double duration = beat / lengthDivision;
                    if (i < objects.Count - 1)
                    {
                        double d = objects[i + 1].Time - current.Time;
                        double room = d - beat / minGapDivision;
                        if (room < duration) duration = room;
                    }

                    int end = current.Time + (int)Math.Floor(duration + 0.5);
                    if (duration < 1.0 || end <= current.Time)
                    {
                        current.MakeTap();
                        result.Add(current);
                        continue;
                    }

                    result.Add(MakeHold(current, end));
                }
            }

            map.Objects = result;
            map.SortObjects();
        }

        /// <summary>
        /// Release (0.3): a = 0..10 controls the long-note probability (a/10). Existing LNs are
        /// preserved. Long notes choose a clearance in [0, beat*f(c)]; short notes choose a length in
        /// [beat*f(c), d - beat*f(c)], so their clearance is never smaller than the long-note maximum.
        /// b = 0..10 moves the tail from the next global event (b=0) to the point farthest from any
        /// event (b=10). c = 0..10 indexes ReleaseFractions.
        /// </summary>
        public static void ApplyRelease(OsuFile map, Random rng, int a, int b, int c)
        {
            int keys = map.KeyCount <= 0 ? 4 : map.KeyCount;
            a = Math.Min(10, Math.Max(0, a));
            b = Math.Min(10, Math.Max(0, b));
            c = Math.Min(10, Math.Max(0, c));
            double longChance = a / 10.0;
            double fraction = ReleaseFractions[c];

            var events = map.Objects
                .SelectMany(o => o.EndTime.HasValue ? new[] { o.Time, o.EndTime.Value } : new[] { o.Time })
                .Distinct()
                .OrderBy(t => t)
                .ToArray();

            var result = new List<HitObject>();
            foreach (var column in map.Objects.GroupBy(o => o.Column(keys)).OrderBy(g => g.Key))
            {
                var objects = column.OrderBy(o => o.Time).ThenBy(o => o.SourceIndex).ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var current = objects[i];
                    if (current.IsHold)
                    {
                        result.Add(current);
                        continue;
                    }
                    if (i == objects.Count - 1)
                    {
                        current.MakeTap();
                        result.Add(current);
                        continue;
                    }

                    var next = objects[i + 1];
                    double d = next.Time - current.Time;
                    if (d < 2)
                    {
                        current.MakeTap();
                        result.Add(current);
                        continue;
                    }

                    double beat = map.BeatLengthAt(current.Time);
                    double maxGap = beat * fraction;
                    int end;

                    if (rng.NextDouble() < longChance)
                    {
                        // Long: clearance from 0 to maxGap, tail stays before the next head.
                        double tailMin = Math.Max(current.Time + 1.0, next.Time - maxGap);
                        double tailMax = next.Time;
                        if (tailMax <= current.Time + 1.0)
                        {
                            current.MakeTap();
                            result.Add(current);
                            continue;
                        }
                        end = ChooseReleaseTail(current.Time, tailMin, tailMax, b, events);
                    }
                    else
                    {
                        // Short: length must leave at least maxGap before the next head.
                        double maxLen = d - maxGap;
                        double minLen = beat * fraction;
                        if (maxLen < 1.0 || maxLen < minLen)
                        {
                            current.MakeTap();
                            result.Add(current);
                            continue;
                        }
                        end = ChooseReleaseTail(current.Time, current.Time + minLen, current.Time + maxLen, b, events);
                    }

                    if (end - current.Time < 1)
                    {
                        current.MakeTap();
                        result.Add(current);
                    }
                    else
                    {
                        result.Add(MakeHold(current, end));
                    }
                }
            }

            map.Objects = result;
            map.SortObjects();
        }

        /// <summary>c table: 0 = 1/16 beat, 5 = 1/4 beat, 10 = 1 beat.</summary>
        public static readonly double[] ReleaseFractions =
        {
            1.0 / 16, 1.0 / 12, 1.0 / 8, 1.0 / 6, 1.0 / 5,
            1.0 / 4, 1.0 / 3, 1.0 / 2, 2.0 / 3, 3.0 / 4, 1.0
        };

        /// <summary>
        /// Move the tail from the first global event after the head (b=0) to the point in the
        /// allowed interval farthest from any global event (b=10).
        /// </summary>
        private static int ChooseReleaseTail(int start, double minPos, double maxPos, int b, int[] events)
        {
            double lo = Math.Max(start + 1.0, Math.Min(minPos, maxPos));
            double hi = Math.Max(lo, Math.Max(minPos, maxPos));

            double aligned = hi;
            foreach (var t in events)
            {
                if (t > start && t >= lo && t <= hi)
                {
                    aligned = t;
                    break;
                }
            }

            double far = lo;
            double best = -1;
            var candidates = new List<double> { lo, hi };
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i] > lo && events[i] < hi) candidates.Add(events[i]);
                if (i + 1 < events.Length && events[i] < hi && events[i + 1] > lo)
                {
                    double mid = (Math.Max(events[i], lo) + Math.Min(events[i + 1], hi)) / 2.0;
                    candidates.Add(mid);
                }
            }
            foreach (var candidate in candidates)
            {
                if (candidate < lo || candidate > hi) continue;
                double nearest = double.MaxValue;
                foreach (var t in events)
                {
                    double dist = Math.Abs(t - candidate);
                    if (dist < nearest) nearest = dist;
                    if (t > candidate && dist > nearest) break;
                }
                if (nearest > best)
                {
                    best = nearest;
                    far = candidate;
                }
            }

            double pos = aligned + (far - aligned) * (b / 10.0);
            return (int)Math.Floor(pos + 0.5);
        }

        public static void SetDifficulty(OsuFile map, double? od, double? hp)
        {
            if (od.HasValue) SetLine(map, "OverallDifficulty", od.Value);
            if (hp.HasValue) SetLine(map, "HPDrainRate", hp.Value);
        }

        private static HitObject MakeHold(HitObject source, int end)
        {
            var head = new HitObject
            {
                X = source.X,
                Y = source.Y,
                Time = source.Time,
                HitSound = source.HitSound,
                Type = 128,
                EndTime = end,
                SourceIndex = source.SourceIndex,
            };
            var samples = source.IsHold ? source.SampleTail() : (string.IsNullOrEmpty(source.SampleData) ? "0:0:0:0:" : source.SampleData);
            head.SampleData = end.ToString(CultureInfo.InvariantCulture) + ":" + samples;
            return head;
        }

        private static void SetLine(OsuFile map, string key, double value)
        {
            var text = key + ":" + value.ToString("0.##", CultureInfo.InvariantCulture);
            int i = map.HeadLines.FindIndex(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (i >= 0) map.HeadLines[i] = text;
            else map.InsertHeadLine("[Difficulty]", text);
        }
    }

    public static class Naming
    {
        public const int MaxFileNameLength = 120;
        public const int MaxFullPathLength = 250;

        public static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                var bad = c == '<' || c == '>' || c == ':' || c == '"' || c == '/' ||
                          c == '\\' || c == '|' || c == '?' || c == '*';
                sb.Append(bad ? '_' : c);
            }
            var r = sb.ToString().Trim().TrimEnd('.');
            return r.Length == 0 ? "map" : r;
        }

        public static string ModeTag(NoteMode mode)
        {
            switch (mode)
            {
                case NoteMode.Invert: return "FULL LN";
                case NoteMode.HoldOff: return "RICE";
                case NoteMode.Release: return "Release";
                case NoteMode.ShortLn: return "SHORT LN";
                default: return "";
            }
        }

        public static string BuildSuffix(NoteMode mode, string variant, string speedTag, double? od, double? hp)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(speedTag)) parts.Add(speedTag);
            var tag = ModeTag(mode);
            if (tag.Length > 0) parts.Add(tag);
            if (!string.IsNullOrEmpty(variant)) parts.Add(variant);
            if (hp.HasValue) parts.Add("HP" + hp.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (od.HasValue) parts.Add("OD" + od.Value.ToString("0.##", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? "practice" : string.Join(" ", parts);
        }

        public static string BuildOutputPath(string sourcePath, string suffix, out bool truncated)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            return BuildOutputPath(dir, stem, suffix, out truncated, sourcePath);
        }

        public static string BuildOutputPath(string dir, string stem, string suffix, out bool truncated, string sourcePath = null)
        {
            truncated = false;
            var clean = Sanitize(suffix).Trim();
            var tail = clean.Length == 0 ? "" : " [" + clean + "]";

            int pathBudget = MaxFullPathLength - dir.TrimEnd('\\', '/').Length - 1 - tail.Length - 5;
            int budget = Math.Min(MaxFileNameLength - tail.Length - 5, pathBudget);
            budget = Math.Max(12, budget);

            var cleanStem = Sanitize(stem);
            if (cleanStem.Length > budget)
            {
                cleanStem = cleanStem.Substring(0, budget).TrimEnd();
                truncated = true;
            }

            var basePath = Path.Combine(dir, cleanStem + tail + ".osu");
            var path = basePath;
            int n = 2;
            while (File.Exists(path) && (sourcePath == null || !string.Equals(path, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)))
            {
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(basePath) + " (" + n++ + ").osu");
            }
            return path;
        }
    }
}

