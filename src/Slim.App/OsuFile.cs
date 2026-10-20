using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Slim
{
    public sealed class HitObject
    {
        public int X;
        public int Y;
        public int Time;
        public int Type;
        public int HitSound;
        public string SampleData;
        public int? EndTime;
        public int SourceIndex;

        public bool IsHold { get { return (Type & 128) != 0; } }

        public int Column(int keyCount)
        {
            if (keyCount <= 0) keyCount = 4;
            long col = (long)X * keyCount / 512;
            if (col < 0) col = 0;
            if (col > keyCount - 1) col = keyCount - 1;
            return (int)col;
        }

        public HitObject Clone()
        {
            return new HitObject
            {
                X = X, Y = Y, Time = Time, Type = Type, HitSound = HitSound,
                SampleData = SampleData, EndTime = EndTime, SourceIndex = SourceIndex
            };
        }

        public static HitObject Parse(string line, int sourceIndex)
        {
            var f = line.Split(',');
            if (f.Length < 5) throw new FormatException("bad hit object: " + line);

            var o = new HitObject
            {
                X = int.Parse(f[0].Trim(), CultureInfo.InvariantCulture),
                Y = int.Parse(f[1].Trim(), CultureInfo.InvariantCulture),
                Time = int.Parse(f[2].Trim(), CultureInfo.InvariantCulture),
                Type = int.Parse(f[3].Trim(), CultureInfo.InvariantCulture),
                HitSound = int.Parse(f[4].Trim(), CultureInfo.InvariantCulture),
                SourceIndex = sourceIndex,
                SampleData = f.Length >= 6 ? string.Join(",", f.Skip(5)) : null
            };

            if (o.IsHold && !string.IsNullOrEmpty(o.SampleData))
            {
                var head = o.SampleData.Split(':')[0];
                int end;
                if (int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
                    o.EndTime = end;
            }
            return o;
        }

        public string ToLine()
        {
            var head = X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture) + "," +
                       Time.ToString(CultureInfo.InvariantCulture) + "," + Type.ToString(CultureInfo.InvariantCulture) + "," +
                       HitSound.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(SampleData) ? head : head + "," + SampleData;
        }

        public string SampleTail()
        {
            if (string.IsNullOrEmpty(SampleData)) return "0:0:0:0:";
            if (!IsHold) return SampleData;
            var parts = SampleData.Split(new[] { ':' }, 2);
            return parts.Length > 1 ? parts[1] : "0:0:0:0:";
        }

        public void MakeHold(int endTime)
        {
            Type = (Type & ~7) | 128;
            EndTime = endTime;
            SampleData = endTime.ToString(CultureInfo.InvariantCulture) + ":" + SampleTail();
        }

        public void MakeTap()
        {
            var tail = IsHold ? SampleTail() : SampleData;
            Type = (Type & ~135) | 1;
            EndTime = null;
            SampleData = tail;
        }
    }

    public struct TimingPoint
    {
        public int Time;
        public double BeatLength;
    }

    public sealed class OsuFile
    {
        public List<string> HeadLines = new List<string>();
        public List<string> TailLines = new List<string>();
        public List<HitObject> Objects = new List<HitObject>();
        public List<TimingPoint> TimingPoints = new List<TimingPoint>();
        public int KeyCount = 4;
        public string SourcePath = "";

        public static OsuFile ParseFile(string path)
        {
            var map = Parse(File.ReadAllText(path, Encoding.UTF8));
            map.SourcePath = path;
            return map;
        }

        public static OsuFile Parse(string text)
        {
            var map = new OsuFile();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            string section = "";
            bool inObjects = false;
            int idx = 0;

            foreach (var raw in lines)
            {
                var line = raw;
                var t = line.Trim();

                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']')
                {
                    section = t;
                    if (section == "[HitObjects]")
                    {
                        inObjects = true;
                        map.HeadLines.Add(line);
                        continue;
                    }
                }

                if (!inObjects)
                {
                    map.HeadLines.Add(line);
                    if (section == "[TimingPoints]" && t.Length > 0 && !t.StartsWith("//"))
                    {
                        TimingPoint tp;
                        if (TimingPointTryParse(t, out tp)) map.TimingPoints.Add(tp);
                    }
                    else if (section == "[Difficulty]" && t.StartsWith("CircleSize:", StringComparison.OrdinalIgnoreCase))
                    {
                        int keys;
                        if (int.TryParse(t.Substring(t.IndexOf(':') + 1).Trim(), out keys)) map.KeyCount = keys;
                    }
                    continue;
                }

                if (section != "[HitObjects]")
                {
                    map.TailLines.Add(line);
                    continue;
                }

                if (t.Length == 0 || t.StartsWith("//")) continue;
                map.Objects.Add(HitObject.Parse(t, idx++));
            }

            map.TimingPoints.Sort((a, b) => a.Time.CompareTo(b.Time));
            return map;
        }

        private static bool TimingPointTryParse(string line, out TimingPoint point)
        {
            point = default(TimingPoint);
            var p = line.Split(',');
            if (p.Length < 2) return false;

            double t, beat;
            if (!double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out t)) return false;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out beat)) return false;

            bool uninherited = true;
            int u;
            if (p.Length > 6 && int.TryParse(p[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out u))
                uninherited = u != 0;
            if (!uninherited || beat <= 0) return false;

            point = new TimingPoint { Time = (int)Math.Truncate(t), BeatLength = beat };
            return true;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var l in HeadLines) sb.Append(l).Append("\r\n");
            foreach (var o in Objects) sb.Append(o.ToLine()).Append("\r\n");
            foreach (var l in TailLines) sb.Append(l).Append("\r\n");
            return sb.ToString();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
        }

        /// <summary>BPM of the timing segment that covers the most time (matches osu!'s displayed main BPM).</summary>
        public double DominantBpm()
        {
            var points = TimingPoints
                .Where(tp => tp.BeatLength > 0 && !double.IsNaN(tp.BeatLength) && !double.IsInfinity(tp.BeatLength))
                .OrderBy(tp => tp.Time)
                .ToList();
            if (points.Count == 0) return 120;

            int endTime = 0;
            foreach (var o in Objects)
            {
                if (o.Time > endTime) endTime = o.Time;
                if (o.EndTime.HasValue && o.EndTime.Value > endTime) endTime = o.EndTime.Value;
            }

            var weights = new Dictionary<double, double>();
            for (int i = 0; i < points.Count; i++)
            {
                var start = Math.Max(0, points[i].Time);
                var end = i + 1 < points.Count ? points[i + 1].Time : Math.Max(start + 1, endTime);
                var weight = Math.Max(0, end - start);
                if (weight <= 0) continue;

                var bpm = Math.Round(60000.0 / points[i].BeatLength, 1);
                double current;
                weights.TryGetValue(bpm, out current);
                weights[bpm] = current + weight;
            }

            if (weights.Count == 0) return 60000.0 / points[0].BeatLength;
            return weights.OrderByDescending(kv => kv.Value).First().Key;
        }

        public double BeatLengthAt(int timeMs)
        {
            if (TimingPoints.Count == 0) return 500.0;
            double beat = TimingPoints[0].BeatLength;
            foreach (var tp in TimingPoints)
            {
                if (tp.Time > timeMs) break;
                beat = tp.BeatLength;
            }
            return beat;
        }

        public double? Number(string key)
        {
            var line = HeadLines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (line == null) return null;
            double value;
            if (!double.TryParse(line.Substring(line.IndexOf(':') + 1).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value)) return null;
            return value;
        }

        public string Text(string key)
        {
            var line = HeadLines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            return line == null ? "" : line.Substring(line.IndexOf(':') + 1).Trim();
        }

        public string Title { get { return Text("Title"); } }
        public string Artist { get { return Text("Artist"); } }

        public int Mode
        {
            get
            {
                int value;
                return int.TryParse(Text("Mode"), out value) ? value : 0;
            }
        }

        public string BackgroundFile()
        {
            string section = "";
            foreach (var line in HeadLines)
            {
                var t = line.Trim();
                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']') { section = t; continue; }
                if (section != "[Events]") continue;
                if (!t.StartsWith("0,0,", StringComparison.Ordinal)) continue;
                var q1 = t.IndexOf('"');
                var q2 = t.LastIndexOf('"');
                if (q1 >= 0 && q2 > q1) return t.Substring(q1 + 1, q2 - q1 - 1);
            }
            return null;
        }

        /// <summary>Inserts a key/value line into the given section (before [HitObjects] if the section is missing).</summary>
        public void InsertHeadLine(string section, string text)
        {
            int start = -1, end = HeadLines.Count;
            for (int i = 0; i < HeadLines.Count; i++)
            {
                var t = HeadLines[i].Trim();
                if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') continue;
                if (start < 0)
                {
                    if (string.Equals(t, section, StringComparison.OrdinalIgnoreCase)) start = i;
                }
                else
                {
                    end = i;
                    break;
                }
            }

            if (start < 0)
            {
                int obj = HeadLines.FindIndex(l => l.Trim().Equals("[HitObjects]", StringComparison.OrdinalIgnoreCase));
                if (obj >= 0) HeadLines.Insert(obj, text);
                else HeadLines.Add(text);
                return;
            }

            int pos = start + 1;
            for (int i = end - 1; i > start; i--)
            {
                if (HeadLines[i].Trim().Length > 0) { pos = i + 1; break; }
            }
            HeadLines.Insert(pos, text);
        }

        public string DifficultyName
        {
            get
            {
                var line = HeadLines.FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
                return line == null ? "" : line.Substring(line.IndexOf(':') + 1).Trim();
            }
            set
            {
                int i = HeadLines.FindIndex(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
                var text = "Version:" + value;
                if (i >= 0) HeadLines[i] = text; else InsertHeadLine("[Metadata]", text);
            }
        }

        public string AudioFilename
        {
            get
            {
                var line = HeadLines.FirstOrDefault(l => l.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase));
                return line == null ? "" : line.Substring(line.IndexOf(':') + 1).Trim();
            }
            set
            {
                int i = HeadLines.FindIndex(l => l.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase));
                var text = "AudioFilename: " + value;
                if (i >= 0) HeadLines[i] = text; else HeadLines.Add(text);
            }
        }

        public int DurationMs
        {
            get
            {
                int end = 0;
                foreach (var o in Objects)
                {
                    int e = o.IsHold && o.EndTime.HasValue ? Math.Max(o.Time, o.EndTime.Value) : o.Time;
                    if (e > end) end = e;
                }
                return end;
            }
        }

        public double LnRatio
        {
            get
            {
                if (Objects.Count == 0) return 0;
                int holds = 0;
                foreach (var o in Objects) if (o.IsHold) holds++;
                return (double)holds / Objects.Count;
            }
        }

        public void SortObjects()
        {
            Objects.Sort((a, b) =>
            {
                int c = a.Time.CompareTo(b.Time);
                if (c != 0) return c;
                c = a.X.CompareTo(b.X);
                if (c != 0) return c;
                return a.SourceIndex.CompareTo(b.SourceIndex);
            });
        }

        /// <summary>Removes video, storyboard commands and breaks from [Events]. Background image is kept.</summary>
        public void StripDynamicEvents()
        {
            var result = new List<string>(HeadLines.Count);
            string section = "";
            foreach (var line in HeadLines)
            {
                var t = line.Trim();
                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']') section = t;

                if (section == "[Events]")
                {
                    if (t.StartsWith("Video,", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.StartsWith("Sprite,", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.StartsWith("Animation,", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.StartsWith("Sample,", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.StartsWith("//Storyboard", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.StartsWith("1,", StringComparison.Ordinal)) continue; // legacy video event
                    if (t.StartsWith("2,", StringComparison.Ordinal)) continue; // breaks
                }
                result.Add(line);
            }
            HeadLines = result;
        }

        /// <summary>Scales every time value by 1/rate (used when the audio is time-stretched).</summary>
        public void ScaleTimes(double rate)
        {
            if (Math.Abs(rate - 1.0) < 1e-9) return;

            var head = new List<string>(HeadLines.Count);
            string section = "";
            foreach (var line in HeadLines)
            {
                var t = line.Trim();
                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']') section = t;

                if (section == "[TimingPoints]" && t.Length > 0 && !t.StartsWith("//"))
                {
                    head.Add(ScaleTimingLine(t, rate));
                    continue;
                }
                if (section == "[General]" && (t.StartsWith("AudioLeadIn:", StringComparison.OrdinalIgnoreCase)
                                              || t.StartsWith("PreviewTime:", StringComparison.OrdinalIgnoreCase)))
                {
                    head.Add(ScaleIntLine(t, rate));
                    continue;
                }
                head.Add(line);
            }
            HeadLines = head;

            foreach (var o in Objects)
            {
                o.Time = (int)Math.Round(o.Time / rate);
                if (o.EndTime.HasValue) o.EndTime = (int)Math.Round(o.EndTime.Value / rate);
                if (o.IsHold && o.EndTime.HasValue) o.SampleData = o.EndTime.Value.ToString(CultureInfo.InvariantCulture) + ":" + o.SampleTail();
            }
        }

        private static string ScaleTimingLine(string line, double rate)
        {
            var p = line.Split(',');
            if (p.Length < 2) return line;
            double time, beat;
            if (!double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out time)) return line;
            if (!double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out beat)) return line;

            p[0] = ((int)Math.Round(time / rate)).ToString(CultureInfo.InvariantCulture);
            if (beat > 0)
                p[1] = (beat / rate).ToString("0.######", CultureInfo.InvariantCulture);
            return string.Join(",", p);
        }

        private static string ScaleIntLine(string line, double rate)
        {
            int c = line.IndexOf(':');
            if (c < 0) return line;
            int v;
            if (!int.TryParse(line.Substring(c + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return line;
            if (v <= 0) return line;
            return line.Substring(0, c) + ":" + ((int)Math.Round(v / rate)).ToString(CultureInfo.InvariantCulture);
        }
    }
}
