using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Slim
{
    public enum NoteMode
    {
        Keep = 0,
        Invert = 1,
        HoldOff = 2,
        Release = 3,
    }

    public static class Transforms
    {
        /// <summary>Lazer ManiaModInvert: every object becomes a hold, gap = min(d/2, beat/4), last object dropped.</summary>
        public static void ApplyInvert(OsuFile map, int division)
        {
            if (division < 1) division = 4;
            int keys = map.KeyCount <= 0 ? 4 : map.KeyCount;
            var result = new List<HitObject>();

            foreach (var column in map.Objects.GroupBy(o => o.Column(keys)).OrderBy(g => g.Key))
            {
                var objects = column.OrderBy(o => o.Time).ThenBy(o => o.SourceIndex).ToList();
                for (int i = 0; i < objects.Count - 1; i++)
                {
                    var current = objects[i];
                    var next = objects[i + 1];

                    double d = next.Time - current.Time;
                    double beat = map.BeatLengthAt(next.Time);
                    double duration = Math.Max(d / 2.0, d - beat / division);

                    int end = current.Time + (int)Math.Floor(duration + 0.5);
                    if (end <= current.Time) continue;

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

        /// <summary>Beat divisions used by the Release generator (gap = beat / f).</summary>
        public static readonly int[] ReleaseDivisions = { 2, 3, 4, 5, 6, 8 };

        /// <summary>
        /// Release: rebuild every column from object heads, each note becomes a hold whose release-to-next-press
        /// gap is a random beat fraction, clamped to half the distance. The last object in each column stays a note.
        /// </summary>
        public static void ApplyRelease(OsuFile map, Random rng)
        {
            int keys = map.KeyCount <= 0 ? 4 : map.KeyCount;
            var result = new List<HitObject>();

            foreach (var column in map.Objects.GroupBy(o => o.Column(keys)).OrderBy(g => g.Key))
            {
                var objects = column.OrderBy(o => o.Time).ThenBy(o => o.SourceIndex).ToList();
                for (int i = 0; i < objects.Count; i++)
                {
                    var current = objects[i];
                    if (i == objects.Count - 1)
                    {
                        var tail = current.Clone();
                        tail.MakeTap();
                        result.Add(tail);
                        continue;
                    }

                    var next = objects[i + 1];
                    double d = next.Time - current.Time;
                    double beat = map.BeatLengthAt(next.Time);

                    int division = ReleaseDivisions[rng.Next(ReleaseDivisions.Length)];
                    double gap = Math.Min(d / 2.0, beat / division);
                    double duration = d - gap;

                    int end = current.Time + (int)Math.Floor(duration + 0.5);
                    if (end <= current.Time) continue;

                    result.Add(MakeHold(current, end));
                }
            }

            map.Objects = result;
            map.SortObjects();
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
            else map.HeadLines.Add(text);
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

