using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace OsuMapTools
{
    /// <summary>
    /// Persisted user preferences, stored as "key=value" lines in
    /// %LOCALAPPDATA%\OsuMapTools\settings.txt.
    ///
    /// Keys are namespaced by their owner so the store can grow with new tools without a
    /// schema rewrite: "global.*" (shared by every tab), "edit.*" (the beatmap editor tab)
    /// and "convert.*" (reserved for the Malody/Etterna converter tab). Unknown keys are
    /// ignored on read, so an older build can open a file written by a newer one.
    ///
    /// Writes are debounced and atomic, and are skipped entirely in headless runs
    /// (--generate), which must never rewrite the user's interactive preferences.
    /// </summary>
    public static class Settings
    {
        /// <summary>Bumped when the meaning of existing keys changes; unknown keys survive.</summary>
        public const int Schema = 1;

        private const string SchemaKey = "schema";
        private const int SaveDelayMs = 600;

        private static readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static DispatcherTimer _timer;
        private static bool _readOnly;
        private static bool _dirty;
        private static string _lastError;

        public static string FilePath
        {
            get { return Path.Combine(AssetPaths.UserDataRoot, "settings.txt"); }
        }

        /// <summary>Last save/load failure, or null. Failures never surface as dialogs.</summary>
        public static string LastError { get { return _lastError; } }

        public static bool IsReadOnly { get { return _readOnly; } }

        /// <summary>
        /// Loads the file once. <paramref name="readOnly"/> is set for headless CLI runs,
        /// which read the settings (so automation sees the same defaults) but never write.
        /// </summary>
        public static void Init(bool readOnly)
        {
            _readOnly = readOnly;
            _dirty = false;
            Values.Clear();
            _lastError = null;

            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = line.Substring(0, eq).Trim();
                        var value = line.Substring(eq + 1).Trim();
                        if (key.Length > 0) Values[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt or unreadable file must not stop the app: fall back to defaults.
                _lastError = "读取设置失败：" + ex.Message;
                Values.Clear();
            }
        }

        public static string Get(string key, string fallback)
        {
            string value;
            if (key != null && Values.TryGetValue(key, out value)) return value;
            return fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            var value = Get(key, null);
            if (string.IsNullOrEmpty(value)) return fallback;
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            int value;
            var text = Get(key, null);
            if (text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        public static double? GetDouble(string key)
        {
            double value;
            var text = Get(key, null);
            if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value))
                return value;
            return null;
        }

        public static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            value = value ?? "";
            string existing;
            if (Values.TryGetValue(key, out existing) && existing == value) return;
            Values[key] = value;
            if (_readOnly) return;
            _dirty = true;
            ScheduleSave();
        }

        public static void SetBool(string key, bool value)
        {
            Set(key, value ? "1" : "0");
        }

        public static void SetInt(string key, int value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public static void SetDouble(string key, double? value)
        {
            Set(key, value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Writes immediately when something changed. Safe to call on shutdown.</summary>
        public static void Flush()
        {
            if (_timer != null) _timer.Stop();
            if (_readOnly || !_dirty) return;
            SaveNow();
        }

        public static void Dispose()
        {
            Flush();
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        private static void ScheduleSave()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDelayMs) };
                _timer.Tick += (_, __) => { _timer.Stop(); if (_dirty) SaveNow(); };
            }
            _timer.Stop();
            _timer.Start();
        }

        private static void SaveNow()
        {
            lock (Gate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var sb = new StringBuilder();
                    sb.Append("# OsuMapTools settings (schema ").Append(Schema).Append(") — written by the app\n");
                    sb.Append(SchemaKey).Append('=').Append(Schema).Append('\n');
                    foreach (var pair in Values.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (pair.Key.Equals(SchemaKey, StringComparison.OrdinalIgnoreCase)) continue;
                        sb.Append(pair.Key).Append('=')
                          .Append((pair.Value ?? "").Replace("\r", " ").Replace("\n", " "))
                          .Append('\n');
                    }

                    // Same atomic pattern as OsuFile.Save: temp file, then replace.
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);

                    _dirty = false;
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _dirty = false;
                    _lastError = "保存设置失败：" + ex.Message;
                }
            }
        }
    }
}
