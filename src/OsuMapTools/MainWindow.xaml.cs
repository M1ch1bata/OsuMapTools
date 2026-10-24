using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OsuMapTools
{
    public partial class MainWindow : Window
    {
        private readonly AnalysisWindow _analysis = new AnalysisWindow();
        private readonly DispatcherTimer _debounce;
        private string _sourceText;
        private string _sourcePath;
        private string _outputRoot;
        private OsuFile _sourceMap;
        private int _previewVersion;
        private MemoryWatcher _memory;
        private TosuWatcher _tosu;
        private bool _isMania = true;
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                var caption = unchecked((int)0x00201610); // #1a1630 in BGR
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
                var text = unchecked((int)0x00f0e8f4); // #f4f0ff in BGR
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
            }
            catch { }
        }

        private string _memStatus = "";
        private bool _isGenerating;
        private string _infoBase = "";
        private string _pattern;
        private string _modeTag;
        private bool _closed;
        private readonly System.Threading.CancellationTokenSource _lifetime =
            new System.Threading.CancellationTokenSource();
        private double _tosuStars;

        public MainWindow()
        {
            InitializeComponent();
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _debounce.Tick += async (_, __) => { _debounce.Stop(); await UpdatePreviewAsync(); };
            Loaded += MainWindow_Loaded;
            Closed += (_, __) =>
            {
                _closed = true;
                try { _lifetime.Cancel(); } catch { }
                try { if (_memory != null) _memory.Dispose(); } catch { }
                try { if (_tosu != null) _tosu.Dispose(); } catch { }
                try { if (_analysis != null) _analysis.Close(); } catch { }
                _debounce.Stop();
                Application.Current.Shutdown();
            };
            OutputPathText.Text = "—";
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _analysis.Show();
                var ok = await _analysis.EnsureInitializedAsync();
                if (_closed) return;
                if (!ok) OutputPathText.Text = "分析引擎初始化失败";

                _tosu = new TosuWatcher(OnExternalMap, OnTosuStatus, OnTosuStars);
                _tosu.Start();

                _memory = new MemoryWatcher(OnExternalMap, OnMemoryStatus);
                _memory.Start();
            }
            catch (Exception ex)
            {
                OutputPathText.Text = "初始化失败：" + ex.Message;
            }
        }

        private void OnExternalMap(string path)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FollowCheck == null || FollowCheck.IsChecked != true) return;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase)) return;
                LoadFile(path);
            }));
        }

        private string _tosuStatus = "";

        private void OnMemoryStatus(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _memStatus = status ?? "";
                UpdateFollowStatus();
            }));
        }

        private void OnTosuStars(double stars)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FollowCheck == null || FollowCheck.IsChecked != true) return;
                if (stars <= 0 || Math.Abs(stars - _tosuStars) < 0.005) return;
                _tosuStars = stars;
                if (StarText != null && _isMania) StarText.Text = stars.ToString("0.00", CultureInfo.InvariantCulture) + "星";
            }));
        }

        private void OnTosuStatus(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _tosuStatus = status ?? "";
                UpdateFollowStatus();
            }));
        }

        private void UpdateFollowStatus()
        {
            if (TosuStatus == null) return;
            var tosuOk = !string.IsNullOrEmpty(_tosuStatus) && _tosuStatus.IndexOf("未连接", StringComparison.Ordinal) < 0;
            if (tosuOk)
            {
                TosuStatus.Text = _tosuStatus;
                return;
            }
            if (!string.IsNullOrEmpty(_memStatus))
                TosuStatus.Text = "内存 " + _memStatus + (string.IsNullOrEmpty(_tosuStatus) ? "" : " · " + _tosuStatus);
            else
                TosuStatus.Text = _tosuStatus;
        }

        private void FollowCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (FollowCheck != null && FollowCheck.IsChecked != true)
            {
                _tosuStars = 0;
                SchedulePreview();
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasOsu(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!HasOsu(e)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            LoadFile(files[0]);
        }

        private static bool HasOsu(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files != null && files.Length > 0 && File.Exists(files[0]) &&
                   Path.GetExtension(files[0]).Equals(".osu", StringComparison.OrdinalIgnoreCase);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var picker = new SongPicker { Owner = this };
            if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedPath))
                LoadFile(picker.SelectedPath);
        }

        public void LoadFilePublic(string path) { LoadFile(path); }

        public System.Threading.Tasks.Task<bool> GeneratePublicAsync() { return GenerateAsync(); }

        public void SetOutputRoot(string root)
        {
            _outputRoot = string.IsNullOrWhiteSpace(root) ? null : root;
            UpdateOutputBox();
        }

        public void SetBpmMode(double bpm)
        {
            if (bpm <= 1) return;
            BpmCheck.IsChecked = true;
            BpmBox.Text = bpm.ToString("0.#", CultureInfo.InvariantCulture);
            UpdateBpmEnabled();
        }

        public void SetKeepPitch(bool keep)
        {
            PitchCheck.IsChecked = keep;
        }

        public void ApplyOptions(double? rate, double? od, double? hp, int? mode, int? strength)
        {
            if (rate.HasValue) RateBox.Text = rate.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (od.HasValue) OdBox.Text = od.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (hp.HasValue) HpBox.Text = hp.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (mode.HasValue) ModeBox.SelectedIndex = mode.Value;
            if (strength.HasValue) StrengthBox.SelectedIndex = strength.Value;
            UpdateBpmEnabled();
            UpdateEffectiveRate();
            UpdateGenerateState();
            SchedulePreview();
        }

        private void LoadFile(string path)
        {
            if (_isGenerating) return;

            _previewVersion++;
            _debounce.Stop();

            try
            {
                var text = File.ReadAllText(path);
                var map = OsuFile.Parse(text);

                // Commit only after both read and parse succeeded.
                _sourcePath = path;
                _sourceText = text;
                _sourceMap = map;
                _tosuStars = 0;

                UpdateBanner(_sourceMap, path);
                UpdateNumericColumn(_sourceMap.KeyCount, null);
                _pattern = null;
                _modeTag = null;
                UpdatePattern(null, null);
                if (PatternInfoText != null) PatternInfoText.Visibility = Visibility.Collapsed;
                StarText.Text = "—";
                DanText.Text = "分析中…";
                LnText.Visibility = Visibility.Collapsed;
                MsdText.Text = "—";

                RateBox.Text = "1.0";
                BpmBox.Text = GetBaseBpm().ToString("0.#", CultureInfo.InvariantCulture);

                var od = _sourceMap.Number("OverallDifficulty");
                var hp = _sourceMap.Number("HPDrainRate");
                OdBox.Text = od.HasValue ? od.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
                HpBox.Text = hp.HasValue ? hp.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";

                _isMania = _sourceMap.Mode == 3;
                UpdateBpmEnabled();
                UpdateEffectiveRate();
                UpdateOutputBox();

                if (!_isMania)
                {
                    GenerateButton.IsEnabled = false;
                    StarText.Text = "—";
                    DanText.Text = "非Mania谱面";
                    LnText.Visibility = Visibility.Collapsed;
                    UpdateNumericColumn(0, null);
                    MsdText.Text = "—";
                    OutputPathText.Text = "非Mania谱面";
                    return;
                }

                UpdateMsdLabel(false);
                UpdateGenerateState();
                SchedulePreview();
            }
            catch (Exception ex)
            {
                _previewVersion++;
                OutputPathText.Text = "读取失败：" + ex.Message;
                UpdateGenerateState();
            }
        }

        private static bool SameDirectory(string a, string b)
        {
            try
            {
                var pa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var pb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateMsdLabel(bool lv)
        {
            if (MsdLabel != null) MsdLabel.Text = lv ? "LV" : "MSD";
        }

        /// <summary>
        /// 4K shows Etterna MSD; 6K shows the xxy level (LV); every other key count has no
        /// agreed numeric metric, so the whole column is removed.
        /// </summary>
        private void UpdateNumericColumn(int keys, double? lv)
        {
            var show = keys == 4 || keys == 6;
            if (MsdLabel != null)
            {
                MsdLabel.Text = keys == 6 ? "LV" : "MSD";
                MsdLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MsdText != null)
                MsdText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (MsdColumn != null)
                MsdColumn.Width = show ? new GridLength(0.85, GridUnitType.Star) : new GridLength(0);

            if (keys == 6 && lv.HasValue && MsdText != null)
                MsdText.Text = lv.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void RenderInfo()
        {
            if (InfoText != null)
                InfoText.Text = _infoBase;

            if (PatternInfoText == null) return;
            if (string.IsNullOrEmpty(_pattern))
            {
                PatternInfoText.Text = "";
                PatternInfoText.Visibility = Visibility.Collapsed;
                return;
            }

            PatternInfoText.Text = "键型 " + _pattern +
                                   (string.IsNullOrEmpty(_modeTag) ? "" : " " + _modeTag);
            PatternInfoText.Visibility = Visibility.Visible;
        }

        private void UpdatePattern(string pattern, string modeTag)
        {
            _pattern = pattern;
            _modeTag = modeTag;
            RenderInfo();
            if (PatternText != null)
            {
                PatternText.Text = string.IsNullOrEmpty(pattern) ? "—" : pattern;
                PatternText.ToolTip = string.IsNullOrEmpty(pattern) ? null : pattern;
            }
        }

        private void UpdateBanner(OsuFile map, string path)
        {
            TitleText.Text = string.IsNullOrEmpty(map.Title) ? Path.GetFileNameWithoutExtension(path) : map.Title;
            ArtistText.Text = string.IsNullOrEmpty(map.Artist) ? "" : map.Artist;
            if (VersionText != null) VersionText.Text = map.Text("Version") ?? "";

            var od = map.Number("OverallDifficulty");
            var hp = map.Number("HPDrainRate");
            _infoBase = map.KeyCount + "K · " + map.Objects.Count + " notes · LN " +
                            (map.LnRatio * 100).ToString("0") + "% · " + FormatDuration(map.DurationMs) +
                            " · BPM " + GetBaseBpm().ToString("0.#") +
                            (od.HasValue ? " · OD " + od.Value.ToString("0.#") : "") +
                            (hp.HasValue ? " · HP " + hp.Value.ToString("0.#") : "");
            RenderInfo();

            try
            {
                var bg = map.BackgroundFile();
                CoverImage.Source = string.IsNullOrEmpty(bg)
                    ? null
                    : LoadCover(Path.GetDirectoryName(path), bg);
            }
            catch
            {
                CoverImage.Source = null;
            }
        }

        private static BitmapImage LoadCover(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return null;
            var full = Path.Combine(dir, file.Replace('/', '\\'));
            if (!File.Exists(full)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(full);
                bmp.EndInit();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private double GetBaseBpm()
        {
            if (_sourceMap == null) return 120;
            return _sourceMap.DominantBpm();
        }

        private static string FormatDuration(int ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            UpdateBpmEnabled();
            UpdateEffectiveRate();
            UpdateStrengthVisibility();
            UpdateGenerateState();
            SchedulePreview();
        }

        /// <summary>True when at least one transformation (mode / speed / OD / HP / output location) was requested.</summary>
        private bool HasChanges()
        {
            if (_sourceMap == null) return false;

            if (GetMode() != NoteMode.Keep) return true;
            if (Math.Abs(GetRate() - 1.0) > 1e-9) return true;

            var baseOd = _sourceMap.Number("OverallDifficulty");
            var baseHp = _sourceMap.Number("HPDrainRate");
            var od = GetOptional(OdBox.Text, 0, 10);
            var hp = GetOptional(HpBox.Text, 0, 10);
            if (od.HasValue && (!baseOd.HasValue || Math.Abs(od.Value - baseOd.Value) > 1e-6)) return true;
            if (hp.HasValue && (!baseHp.HasValue || Math.Abs(hp.Value - baseHp.Value) > 1e-6)) return true;

            if (!string.IsNullOrEmpty(_outputRoot)) return true;
            return false;
        }

        private void UpdateGenerateState()
        {
            if (GenerateButton == null) return;
            GenerateButton.IsEnabled = !_isGenerating && _isMania && _sourceMap != null && HasChanges();
        }

        private void UpdateEffectiveRate()
        {
            if (EffectiveRate == null || BpmCheck == null) return;
            if (BpmCheck.IsChecked == true)
                EffectiveRate.Text = "→ " + GetRate().ToString("0.00", CultureInfo.InvariantCulture) + "x";
            else
                EffectiveRate.Text = "";
        }

        private void UpdateBpmEnabled()
        {
            if (RateBox == null || BpmBox == null || BpmCheck == null) return;
            var byBpm = BpmCheck.IsChecked == true;
            RateBox.IsEnabled = !byBpm;
            BpmBox.IsEnabled = byBpm;
        }

        private void UpdateStrengthVisibility()
        {
            if (StrengthBox == null || StrengthLabel == null || ModeBox == null) return;
            var visible = ModeBox.SelectedIndex == (int)NoteMode.Invert;
            StrengthBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            StrengthLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (CustomGapPanel != null)
                CustomGapPanel.Visibility = visible && StrengthBox.SelectedIndex == 4
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SchedulePreview()
        {
            if (_sourceMap == null || _debounce == null || !_isMania) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private double GetRate()
        {
            if (BpmCheck.IsChecked == true)
            {
                double bpm;
                var baseBpm = GetBaseBpm();
                if (!double.TryParse(BpmBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bpm) || bpm <= 0)
                    return 1.0;
                var r = bpm / baseBpm;
                return Math.Min(5.0, Math.Max(0.01, r));
            }

            double v;
            if (!double.TryParse(RateBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return 1.0;
            return Math.Min(5.0, Math.Max(0.01, v));
        }

        private string GetSpeedTag()
        {
            var rate = GetRate();
            if (Math.Abs(rate - 1.0) < 1e-9) return "";
            if (BpmCheck.IsChecked == true)
                return (GetBaseBpm() * rate).ToString("0.#", CultureInfo.InvariantCulture) + "bpm";
            return rate.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        }

        private bool GetKeepPitch()
        {
            return PitchCheck.IsChecked == true;
        }

        private static double? GetOptional(string text, double min, double max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            double v;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        private NoteMode GetMode()
        {
            return (NoteMode)Math.Max(0, ModeBox.SelectedIndex);
        }

        private int GetStrengthDivision()
        {
            if (StrengthBox.SelectedIndex == 0) return 2;
            if (StrengthBox.SelectedIndex == 2) return 8;
            if (StrengthBox.SelectedIndex == 3) return 0; // seamless
            if (StrengthBox.SelectedIndex == 4) return GetCustomGap();
            return 4;
        }

        private int GetCustomGap()
        {
            int value;
            if (CustomGapBox == null || !int.TryParse(CustomGapBox.Text.Trim(), out value)) return 3;
            return Math.Min(16, Math.Max(1, value));
        }

        private void CustomGap_LostFocus(object sender, RoutedEventArgs e)
        {
            if (CustomGapBox == null) return;
            CustomGapBox.Text = GetCustomGap().ToString(CultureInfo.InvariantCulture);
        }

        public void SetFollow(bool follow)
        {
            if (FollowCheck != null) FollowCheck.IsChecked = follow;
        }

        public void SetCustomGap(int gap)
        {
            gap = Math.Min(16, Math.Max(1, gap));
            if (StrengthBox != null) StrengthBox.SelectedIndex = 4;
            if (CustomGapBox != null) CustomGapBox.Text = gap.ToString(CultureInfo.InvariantCulture);
            UpdateStrengthVisibility();
            UpdateGenerateState();
            SchedulePreview();
        }

        private string GetStrengthVariant()
        {
            if (ModeBox.SelectedIndex != (int)NoteMode.Invert) return "";
            if (StrengthBox.SelectedIndex == 0) return "loose";
            if (StrengthBox.SelectedIndex == 2) return "dense";
            if (StrengthBox.SelectedIndex == 3) return "seamless";
            if (StrengthBox.SelectedIndex == 4) return "1-" + GetCustomGap().ToString(CultureInfo.InvariantCulture);
            return "";
        }

        private string GetOutputDirectory()
        {
            var originDir = Path.GetDirectoryName(Path.GetFullPath(_sourcePath));
            if (string.IsNullOrEmpty(_outputRoot)) return originDir;
            return Path.Combine(_outputRoot, Path.GetFileName(originDir.TrimEnd('\\', '/')));
        }

        private void UpdateOutputBox()
        {
            if (OutputBox == null) return;
            OutputBox.Text = string.IsNullOrEmpty(_sourcePath) ? "—" : GetOutputDirectory();
        }

        private string PredictOutputPath(string suffix)
        {
            var originDir = Path.GetDirectoryName(Path.GetFullPath(_sourcePath));
            var outDir = GetOutputDirectory();
            bool truncated;
            return Naming.BuildOutputPath(outDir, Path.GetFileNameWithoutExtension(_sourcePath), suffix,
                out truncated, originDir == outDir ? _sourcePath : null);
        }

        private OsuFile BuildTransformed(bool scaleTimes, out string suffix)
        {
            var map = OsuFile.Parse(_sourceText);
            map.StripDynamicEvents();

            var mode = GetMode();
            if (mode == NoteMode.Invert)
            {
                Transforms.ApplyInvert(map, GetStrengthDivision());
            }
            else if (mode == NoteMode.HoldOff)
            {
                Transforms.ApplyHoldOff(map);
            }
            else if (mode == NoteMode.Release)
            {
                Transforms.ApplyRelease(map, new Random());
            }

            var od = GetOptional(OdBox.Text, 0, 10);
            var hp = GetOptional(HpBox.Text, 0, 10);
            var baseOd = _sourceMap.Number("OverallDifficulty");
            var baseHp = _sourceMap.Number("HPDrainRate");
            var odChanged = od.HasValue && (!baseOd.HasValue || Math.Abs(baseOd.Value - od.Value) > 1e-6) ? od : null;
            var hpChanged = hp.HasValue && (!baseHp.HasValue || Math.Abs(baseHp.Value - hp.Value) > 1e-6) ? hp : null;
            if (odChanged.HasValue || hpChanged.HasValue) Transforms.SetDifficulty(map, odChanged, hpChanged);

            var rate = GetRate();
            suffix = Naming.BuildSuffix(mode, GetStrengthVariant(), GetSpeedTag(), odChanged, hpChanged);
            if (scaleTimes && Math.Abs(rate - 1.0) > 1e-9) map.ScaleTimes(rate);

            return map;
        }

        private async Task UpdatePreviewAsync()
        {
            if (_sourceMap == null || !_isMania) return;
            int version = ++_previewVersion;

            try
            {
                UpdateNumericColumn(_sourceMap == null ? 0 : _sourceMap.KeyCount, null);
                StarText.Text = "…";
                DanText.Text = "…";
                LnText.Text = "";
                LnText.Visibility = Visibility.Collapsed;
                MsdText.Text = "…";

                _analysis.CancelPending();
                string suffix;
                var map = BuildTransformed(false, out suffix);
                OutputPathText.Text = PredictOutputPath(suffix);

                var result = await _analysis.AnalyzeAsync(map.Serialize(), GetRate());

                if (version != _previewVersion) return;

                if (!result.Ok)
                {
                    UpdateNumericColumn(result.Keys, result.Lv);
                    UpdatePattern(result.Pattern, result.ModeTag);
                    StarText.Text = "—";
                    DanText.Text = "无法分析";
                    LnText.Visibility = Visibility.Collapsed;
                    MsdText.Text = "—";
                    OutputPathText.ToolTip = result.Error;
                    return;
                }

                var starValue = _tosuStars > 0
                    ? _tosuStars
                    : (result.OsuStar > 0 ? result.OsuStar : result.Star);
                var starFormat = (_tosuStars > 0 || result.OsuStar > 0) ? "0.00" : "0.0";
                UpdatePattern(result.Pattern, result.ModeTag);
                UpdateNumericColumn(result.Keys, result.Lv);
                StarText.Text = starValue.ToString(starFormat, CultureInfo.InvariantCulture) + "星";
                DanText.Text = FormatDan(result.RcDifficulty, result.DanLabel);

                var hasLn = !string.IsNullOrEmpty(result.LnDifficulty);
                LnText.Text = hasLn ? FormatDan(result.LnDifficulty, null) : "";
                LnText.Visibility = hasLn ? Visibility.Visible : Visibility.Collapsed;

                if (result.Keys == 4)
                    MsdText.Text = result.Overall.ToString("0.00", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                if (version == _previewVersion)
                {
                    StarText.Text = "—";
                    DanText.Text = "分析失败";
                    LnText.Visibility = Visibility.Collapsed;
                    MsdText.Text = "—";
                    OutputPathText.ToolTip = ex.Message;
                }
            }
        }

        private static string FormatDan(string difficulty, string fallback)
        {
            var text = string.IsNullOrEmpty(difficulty) ? fallback : difficulty;
            if (string.IsNullOrEmpty(text)) return "—";

            var parts = text.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                var sub = parts[i].Split('/');
                for (int j = 0; j < sub.Length; j++)
                {
                    if (sub[j].Length > 0)
                        sub[j] = char.ToUpperInvariant(sub[j][0]) + sub[j].Substring(1);
                }
                parts[i] = string.Join("/", sub);
            }
            return string.Join(" ", parts);
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择输出根目录（会在其中新建与原谱面同名的文件夹）";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _outputRoot = dlg.SelectedPath;
                    UpdateOutputBox();
                    UpdateGenerateState();
                    SchedulePreview();
                }
            }
        }

        private void ResetOutput_Click(object sender, RoutedEventArgs e)
        {
            _outputRoot = null;
            UpdateOutputBox();
            UpdateGenerateState();
            SchedulePreview();
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            await GenerateAsync();
        }

        private async System.Threading.Tasks.Task<bool> GenerateAsync()
        {
            if (_isGenerating) return false;
            if (_sourceMap == null) return false;
            if (!_isMania)
            {
                OutputPathText.Text = "非Mania谱面，无法生成";
                return false;
            }
            if (!HasChanges())
            {
                OutputPathText.Text = "未做任何修改，无需生成";
                return false;
            }
            _isGenerating = true;
            UpdateGenerateState();
            var sourcePath = _sourcePath;
            try
            {
                string suffix;
                var map = BuildTransformed(true, out suffix);
                var rate = GetRate();

                var originDir = Path.GetDirectoryName(Path.GetFullPath(_sourcePath));
                bool external = !string.IsNullOrEmpty(_outputRoot);
                string outDir = GetOutputDirectory();
                if (external && SameDirectory(outDir, originDir)) external = false;
                if (external) Directory.CreateDirectory(outDir);

                var audioName = map.AudioFilename;
                if (string.IsNullOrEmpty(audioName)) throw new InvalidOperationException("谱面没有 AudioFilename");
                var srcAudio = Path.Combine(originDir, audioName.Replace('/', '\\'));

                if (Math.Abs(rate - 1.0) > 1e-9)
                {
                    if (!File.Exists(srcAudio)) throw new FileNotFoundException("找不到音频文件：" + srcAudio);

                    var speedTag = GetSpeedTag();
                    var keepPitch = GetKeepPitch();
                    var pitchTag = keepPitch ? "" : " nopitch";
                    var newName = Naming.Sanitize(Path.GetFileNameWithoutExtension(audioName) + " [" + speedTag + pitchTag + "].mp3");
                    var dstAudio = Path.Combine(outDir, newName);

                    await Task.Run(() => AudioProcessor.CreateRateAdjustedMp3(srcAudio, dstAudio, rate, keepPitch, _lifetime.Token), _lifetime.Token);
                    map.AudioFilename = newName;
                }
                else if (external)
                {
                    if (!File.Exists(srcAudio)) throw new FileNotFoundException("找不到音频文件：" + srcAudio);
                    File.Copy(srcAudio, Path.Combine(outDir, Path.GetFileName(audioName.Replace('/', '\\'))), true);
                }

                if (external) CopyReferencedAssets(map, originDir, outDir);

                map.DifficultyName = map.DifficultyName + " [" + suffix + "]";

                bool truncated;
                var outPath = Naming.BuildOutputPath(outDir, Path.GetFileNameWithoutExtension(sourcePath),
                    suffix, out truncated, originDir == outDir ? sourcePath : null);
                map.Save(outPath);

                OutputPathText.Text = outPath;
                OutputPathText.ToolTip = truncated ? "文件名过长，已截断" : null;
                return true;
            }
            catch (OperationCanceledException)
            {
                OutputPathText.Text = "已取消生成";
                return false;
            }
            catch (Exception ex)
            {
                try
                {
                    var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuMapTools");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), "[" + DateTime.Now.ToString("O") + "] generation: " + ex + Environment.NewLine);
                }
                catch { }
                OutputPathText.Text = "生成失败：" + ex.Message;
                OutputPathText.ToolTip = ex.ToString();
                return false;
            }
            finally
            {
                _isGenerating = false;
                UpdateGenerateState();
            }
        }

        /// <summary>Copies images / custom hit sounds referenced by the beatmap (excluding video/storyboard).</summary>
        private static void CopyReferencedAssets(OsuFile map, string originDir, string outDir)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string section = "";
            foreach (var line in map.HeadLines)
            {
                var t = line.Trim();
                if (t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']') { section = t; continue; }
                if (section != "[Events]") continue;
                var q1 = t.IndexOf('"');
                var q2 = t.LastIndexOf('"');
                if (q1 >= 0 && q2 > q1)
                {
                    var name = t.Substring(q1 + 1, q2 - q1 - 1);
                    if (name.Length > 0) files.Add(name);
                }
            }

            foreach (var o in map.Objects)
            {
                var parts = (o.SampleData ?? "").Split(':');
                if (parts.Length >= 5)
                {
                    var name = parts[parts.Length - 1].Trim();
                    if (name.Length > 0) files.Add(name);
                }
            }

            foreach (var name in files)
            {
                var rel = name.Replace('/', '\\');
                var src = ResolveCaseInsensitive(originDir, rel);
                if (src == null) continue;

                var dst = Path.Combine(outDir, rel);
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(src, dst, true);
            }
        }

        private static string ResolveCaseInsensitive(string root, string relative)
        {
            var current = root;
            foreach (var segment in relative.Split('\\'))
            {
                if (segment.Length == 0) continue;
                var exact = Path.Combine(current, segment);
                if (File.Exists(exact)) return exact;

                var dir = Directory.Exists(current) ? Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly)) : Enumerable.Empty<string>();
                var match = dir.FirstOrDefault(p => string.Equals(Path.GetFileName(p), segment, StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;
                current = match;
            }
            return File.Exists(current) ? current : null;
        }

        // ---- numeric input helpers ----

        private static bool IsNumericText(string s)
        {
            return s.Length == 0 || Regex.IsMatch(s, @"^\d*\.?\d*$");
        }

        private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var box = (TextBox)sender;
            var text = box.Text;
            int start = box.SelectionStart;
            int length = box.SelectionLength;
            if (start > text.Length) start = text.Length;
            if (start + length > text.Length) length = text.Length - start;
            var next = text.Remove(start, length).Insert(start, e.Text);
            e.Handled = !IsNumericText(next);
        }

        private void Number_LostFocus(object sender, RoutedEventArgs e)
        {
            ClampBox((TextBox)sender);
        }

        private void ClampBox(TextBox box)
        {
            var tag = box.Tag as string ?? "";
            double v;
            bool parsed = double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

            switch (tag)
            {
                case "rate":
                    if (!parsed) { box.Text = "1.0"; return; }
                    v = Math.Min(5.0, Math.Max(0.01, v));
                    box.Text = v.ToString("0.##", CultureInfo.InvariantCulture);
                    break;
                case "bpm":
                    if (!parsed) { box.Text = GetBaseBpm().ToString("0.#", CultureInfo.InvariantCulture); return; }
                    var baseBpm = GetBaseBpm();
                    v = Math.Min(baseBpm * 5.0, Math.Max(baseBpm * 0.01, v));
                    box.Text = v.ToString("0.#", CultureInfo.InvariantCulture);
                    break;
                case "od":
                case "hp":
                    if (box.Text.Trim().Length == 0) return;
                    if (!parsed) { box.Text = ""; return; }
                    v = Math.Min(10.0, Math.Max(0.0, v));
                    box.Text = v.ToString("0.0", CultureInfo.InvariantCulture);
                    break;
            }
        }

        private void Spin_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((string)((Button)sender).Tag).Split(':');
            var field = tag[0];
            var direction = int.Parse(tag[1]);

            TextBox box;
            double step;
            switch (field)
            {
                case "rate": box = RateBox; step = 0.01; break;
                case "bpm": box = BpmBox; step = 1; break;
                case "od": box = OdBox; step = 0.1; break;
                default: box = HpBox; step = 0.1; break;
            }

            double current;
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out current))
            {
                current = field == "rate" ? 1.0 : field == "bpm" ? GetBaseBpm() : 0.0;
            }

            current += direction * step;
            box.Text = current.ToString(field == "bpm" ? "0.#" : "0.##", CultureInfo.InvariantCulture);
            ClampBox(box);
            SchedulePreview();
        }
    }
}
