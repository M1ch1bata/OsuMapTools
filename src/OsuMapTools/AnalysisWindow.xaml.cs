using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace OsuMapTools
{
    public sealed class AnalysisResult
    {
        public bool Ok;
        public string Error;
        public int Keys;
        public string Engine;
        public float[] Msd = new float[8];
        public string DanLabel;
        public double? DanNumeric;
        public string Difficulty;
        public string RcDifficulty;
        public string LnDifficulty;
        public double? DanNumber;
        public double Star;
        public double OsuStar;
        public string Pattern;
        public string ModeTag;
        public string SourcePattern;
        public string SourceModeTag;
        public double? Lv;

        public float Overall { get { return Msd.Length > 0 ? Msd[0] : 0f; } }
    }

    /// <summary>Offscreen WebView2 hosting the ManiaMapAnalyser JS (MinaCalc + daniel/sunny).</summary>
    public partial class AnalysisWindow : Window
    {
        private readonly Dictionary<int, TaskCompletionSource<string>> _pending =
            new Dictionary<int, TaskCompletionSource<string>>();
        private readonly System.Threading.SemaphoreSlim _initLock = new System.Threading.SemaphoreSlim(1, 1);
        private bool _initialized;
        private bool _messageAttached;
        private bool _processFailedAttached;
        private volatile bool _needsReinit;
        private string _lastStage = "";
        private TaskCompletionSource<bool> _ready;
        private int _nextId;
        private string _initError;

        public AnalysisWindow()
        {
            InitializeComponent();
            Loaded += async (_, __) => { await EnsureInitializedAsync(); };
        }

        private async Task InitCoreAsync()
        {
            var userData = Path.Combine(AssetPaths.UserDataRoot, "wv2data");
            if (Web.CoreWebView2 == null)
            {
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await Web.EnsureCoreWebView2Async(env);
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local",
                    AssetPaths.WebRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            if (!_processFailedAttached)
            {
                Web.CoreWebView2.ProcessFailed += OnProcessFailed;
                _processFailedAttached = true;
            }
            if (!_messageAttached)
            {
                Web.CoreWebView2.WebMessageReceived += OnMessage;
                _messageAttached = true;
            }

            _ready = new TaskCompletionSource<bool>();
            Web.CoreWebView2.Navigate("https://app.local/index.html");

            var done = await Task.WhenAny(_ready.Task, Task.Delay(20000));
            if (done != _ready.Task) throw new TimeoutException("analysis page did not become ready");
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _needsReinit = true;
            _initialized = false;
            _ready = null;
            _initError = "分析引擎崩溃，正在恢复";
            FailPending(new InvalidOperationException("analysis engine crashed (" + e.ProcessFailedKind + ")"));
        }

        private void FailPending(Exception error)
        {
            TaskCompletionSource<string>[] pending;
            lock (_pending)
            {
                pending = _pending.Values.ToArray();
                _pending.Clear();
            }
            foreach (var tcs in pending) tcs.TrySetException(error);
        }

        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var json = e.WebMessageAsJson;
            if (json.IndexOf("\"type\":\"stage\"", StringComparison.Ordinal) >= 0)
            {
                var st = Regex.Match(json, "\"stage\"\\s*:\\s*\"([^\"]*)\"");
                if (st.Success) _lastStage = st.Groups[1].Value;
                return;
            }
            if (json.IndexOf("\"type\":\"ready\"", StringComparison.Ordinal) >= 0)
            {
                if (_ready != null) _ready.TrySetResult(true);
                return;
            }
            var m = Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
            if (!m.Success) return;
            int id = int.Parse(m.Groups[1].Value);
            TaskCompletionSource<string> tcs;
            lock (_pending)
            {
                if (!_pending.TryGetValue(id, out tcs)) return;
                _pending.Remove(id);
            }
            tcs.TrySetResult(json);
        }

        public async Task<bool> EnsureInitializedAsync()
        {
            if (_initialized && !_needsReinit) return true;

            await _initLock.WaitAsync();
            try
            {
                if (_initialized && !_needsReinit) return true;
                _needsReinit = false;
                await InitCoreAsync();
                _initialized = true;
                _initError = null;
                return true;
            }
            catch (Exception ex)
            {
                _initialized = false;
                _initError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public void CancelPending()
        {
            TaskCompletionSource<string>[] pending;
            int[] ids;
            lock (_pending)
            {
                ids = _pending.Keys.ToArray();
                pending = _pending.Values.ToArray();
                _pending.Clear();
            }
            foreach (var tcs in pending) tcs.TrySetCanceled();

            try
            {
                var core = Web == null ? null : Web.CoreWebView2;
                if (core == null) return;
                foreach (var id in ids)
                    core.PostWebMessageAsJson("{\"type\":\"cancel\",\"id\":" + id + "}");
            }
            catch { }
        }

        public async Task<AnalysisResult> AnalyzeAsync(string osuText, double rate, string sourceText = null)
        {
            if (!await EnsureInitializedAsync())
                return new AnalysisResult { Ok = false, Error = _initError ?? "分析引擎初始化超时" };

            int id = ++_nextId;
            var tcs = new TaskCompletionSource<string>();
            lock (_pending) _pending[id] = tcs;

            var payload = "{\"type\":\"analyze\",\"id\":" + id +
                          ",\"rate\":" + rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                          ",\"text\":\"" + EscapeJson(osuText) + "\"" +
                          (string.IsNullOrEmpty(sourceText) ? "" : ",\"sourceText\":\"" + EscapeJson(sourceText) + "\"") +
                          "}";

            try
            {
                Web.CoreWebView2.PostWebMessageAsJson(payload);
            }
            catch (Exception ex)
            {
                _pending.Remove(id);
                return new AnalysisResult { Ok = false, Error = "分析请求发送失败：" + ex.Message };
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(60000));
            if (done != tcs.Task)
            {
                lock (_pending) _pending.Remove(id);
                return new AnalysisResult { Ok = false, Error = "分析超时（最后阶段：" + _lastStage + "）" };
            }
            if (tcs.Task.IsCanceled)
                return new AnalysisResult { Ok = false, Error = "分析已取消" };
            lock (_pending) _pending.Remove(id);
            return Parse(tcs.Task.Result);
        }

        private static AnalysisResult Parse(string json)
        {
            var r = new AnalysisResult { Ok = true };
            try
            {
                var err = Regex.Match(json, "\"ok\"\\s*:\\s*(true|false)");
                var patternMatch = Regex.Match(json, "\"pattern\"\\s*:\\s*\"([^\"]*)\"");
                var modeTagMatch = Regex.Match(json, "\"modeTag\"\\s*:\\s*\"([^\"]*)\"");
                if (err.Success && err.Groups[1].Value == "false")
                {
                    var em = Regex.Match(json, "\"error\"\\s*:\\s*\"([^\"]*)\"");
                    var esp = Regex.Match(json, "\"sourcePattern\"\\s*:\\s*\"([^\"]*)\"");
                    var esm = Regex.Match(json, "\"sourceModeTag\"\\s*:\\s*\"([^\"]*)\"");
                    return new AnalysisResult
                    {
                        Ok = false,
                        Error = em.Success ? em.Groups[1].Value : "分析失败",
                        Pattern = patternMatch.Success ? patternMatch.Groups[1].Value : null,
                        ModeTag = modeTagMatch.Success ? modeTagMatch.Groups[1].Value : null,
                        SourcePattern = esp.Success ? esp.Groups[1].Value : null,
                        SourceModeTag = esm.Success ? esm.Groups[1].Value : null,
                    };
                }

                var keys = Regex.Match(json, "\"keys\"\\s*:\\s*(\\d+)");
                if (keys.Success) r.Keys = int.Parse(keys.Groups[1].Value);
                var engine = Regex.Match(json, "\"etternaVersion\"\\s*:\\s*\"([^\"]*)\"");
                if (engine.Success) r.Engine = engine.Groups[1].Value;

                string[] names = { "Overall", "Stream", "Jumpstream", "Handstream", "Stamina", "JackSpeed", "Chordjack", "Technical" };
                for (int i = 0; i < names.Length; i++)
                {
                    var v = Regex.Match(json, "\"" + names[i] + "\"\\s*:\\s*(-?[0-9.eE+]+)");
                    if (v.Success) float.TryParse(v.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out r.Msd[i]);
                }

                var label = Regex.Match(json, "\"label\"\\s*:\\s*\"([^\"]*)\"");
                if (label.Success) r.DanLabel = label.Groups[1].Value;
                var numeric = Regex.Match(json, "\"numeric\"\\s*:\\s*(null|-?[0-9.eE+]+)");
                if (numeric.Success && numeric.Groups[1].Value != "null")
                    r.DanNumeric = double.Parse(numeric.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var diff = Regex.Match(json, "\"difficulty\"\\s*:\\s*\"([^\"]*)\"");
                if (diff.Success)
                {
                    r.Difficulty = diff.Groups[1].Value;
                    var parts = r.Difficulty.Split(new[] { " || " }, StringSplitOptions.None);
                    r.RcDifficulty = parts[0].Trim();
                    if (parts.Length > 1) r.LnDifficulty = parts[1].Trim();
                    r.DanNumber = ParseDanNumber(r.RcDifficulty);
                }

                var osuStar = Regex.Match(json, "\"osuStar\"\\s*:\\s*(null|-?[0-9.eE+]+)");
                if (osuStar.Success && osuStar.Groups[1].Value != "null")
                    r.OsuStar = double.Parse(osuStar.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                if (patternMatch.Success) r.Pattern = patternMatch.Groups[1].Value;
                if (modeTagMatch.Success) r.ModeTag = modeTagMatch.Groups[1].Value;
                var sourcePatternMatch = Regex.Match(json, "\"sourcePattern\"\\s*:\\s*\"([^\"]*)\"");
                var sourceModeTagMatch = Regex.Match(json, "\"sourceModeTag\"\\s*:\\s*\"([^\"]*)\"");
                if (sourcePatternMatch.Success) r.SourcePattern = sourcePatternMatch.Groups[1].Value;
                if (sourceModeTagMatch.Success) r.SourceModeTag = sourceModeTagMatch.Groups[1].Value;
                var lv = Regex.Match(json, "\"lv\"\\s*:\\s*(null|-?[0-9.eE+]+)");
                if (lv.Success && lv.Groups[1].Value != "null")
                    r.Lv = double.Parse(lv.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                var star = Regex.Match(json, "\"star\"\\s*:\\-?([0-9.eE+]+)");
                if (star.Success)
                    r.Star = double.Parse(star.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                return new AnalysisResult { Ok = false, Error = ex.Message };
            }
            return r;
        }

        private static readonly string[] NamedBands =
        {
            "Alpha", "Beta", "Gamma", "Delta", "Epsilon",
            "Emik Zeta", "Thaumiel Eta", "CloverWisp Theta",
        };

        private static double BandOffset(string band)
        {
            switch (band)
            {
                case "low": return 0.1;
                case "mid/low": return 0.3;
                case "mid": return 0.5;
                case "mid/high": return 0.7;
                case "high": return 0.9;
                default: return 0.5;
            }
        }

        /// <summary>Turns "Reform 3 mid" / "Gamma mid/low" into a numeric dan (e.g. 3.5 / 13.3).</summary>
        private static double? ParseDanNumber(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            var text = label.Trim();
            if (text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith(">", StringComparison.Ordinal))
                text = text.Substring(1).Trim();

            var reform = Regex.Match(text, @"^Reform\s+(\d+)\s+(low|mid/low|mid|mid/high|high)$");
            if (reform.Success)
                return int.Parse(reform.Groups[1].Value) + BandOffset(reform.Groups[2].Value);

            for (int i = 0; i < NamedBands.Length; i++)
            {
                var prefix = NamedBands[i] + " ";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var band = text.Substring(prefix.Length).Trim();
                    return 11 + i + BandOffset(band);
                }
            }
            return null;
        }

        private static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
