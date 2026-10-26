using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OsuMapTools
{
    public partial class BatchWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly double _baseBpm;
        private bool _loading;
        private bool _running;

        public BatchWindow(MainWindow owner, double baseBpm)
        {
            InitializeComponent();
            _owner = owner;
            _baseBpm = baseBpm > 0 ? baseBpm : 120;
            _loading = true;
            ApplyDefaults();
            _loading = false;
            UpdatePreview();
        }

        private void ApplyDefaults()
        {
            if (LowBox == null) return;
            if (TypeBox.SelectedIndex == 0)
            {
                LowBox.Text = "1.0";
                HighBox.Text = "1.25";
                StepBox.Text = "0.05";
                AlignCheck.IsEnabled = false;
            }
            else
            {
                LowBox.Text = _baseBpm.ToString("0.##", CultureInfo.InvariantCulture);
                HighBox.Text = (_baseBpm + 5 * 5).ToString("0.##", CultureInfo.InvariantCulture);
                StepBox.Text = "5";
                AlignCheck.IsEnabled = true;
            }
        }

        private static double Parse(string text, double fallback)
        {
            double v;
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static int Clamp(int v) { return Math.Min(10, Math.Max(0, v)); }

        private void Type_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || LowBox == null) return;
            _loading = true;
            ApplyDefaults();
            _loading = false;
            UpdatePreview();
        }

        private void Value_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdatePreview();
        }

        private void Spin_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;
            var parts = button.Tag.ToString().Split(':');
            if (parts.Length != 2) return;
            int delta;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta)) return;
            var box = parts[0] == "low" ? LowBox : HighBox;
            var step = TypeBox.SelectedIndex == 0 ? 0.05 : 1.0;
            var value = Parse(box.Text, TypeBox.SelectedIndex == 0 ? 1.0 : _baseBpm);
            box.Text = (value + delta * step).ToString("0.##", CultureInfo.InvariantCulture);
            UpdatePreview();
        }

        private void Box_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null) return;
            var fallback = box == LowBox ? (TypeBox.SelectedIndex == 0 ? 1.0 : _baseBpm) : (TypeBox.SelectedIndex == 0 ? 1.25 : _baseBpm + 25);
            box.Text = Parse(box.Text, fallback).ToString("0.##", CultureInfo.InvariantCulture);
            UpdatePreview();
        }

        private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (var ch in e.Text)
            {
                if (!char.IsDigit(ch) && ch != '.')
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private List<MainWindow.BatchVariant> BuildVariants(out string error)
        {
            error = null;
            var list = new List<MainWindow.BatchVariant>();
            double low = Parse(LowBox.Text, double.NaN);
            double high = Parse(HighBox.Text, double.NaN);
            double step = Parse(StepBox.Text, double.NaN);
            if (double.IsNaN(low) || double.IsNaN(high) || double.IsNaN(step) || step <= 0)
            {
                error = "请填写有效的低值、高值和步长";
                return list;
            }
            if (low > high) { var t = low; low = high; high = t; }

            if (TypeBox.SelectedIndex == 0)
            {
                for (double v = low; v <= high + 1e-9; v += step)
                {
                    var rounded = Math.Round(v, 3);
                    if (Math.Abs(rounded - 1.0) < 1e-9) continue;
                    if (rounded < 0.01 || rounded > 5.0) continue;
                    list.Add(new MainWindow.BatchVariant { Rate = rounded, Tag = rounded.ToString("0.##", CultureInfo.InvariantCulture) + "x" });
                    if (list.Count > 500) break;
                }
            }
            else
            {
                bool align = AlignCheck.IsChecked == true;
                double first = low;
                if (align) first = Math.Ceiling(low / 5.0) * 5.0;
                for (double t = first; t <= high + 1e-9; t += step)
                {
                    var target = Math.Round(t, 3);
                    if (align && Math.Abs(target % 5.0) > 1e-9) continue;
                    if (Math.Abs(target - _baseBpm) < 1e-9) continue;
                    var rate = target / _baseBpm;
                    if (rate < 0.01 || rate > 5.0) continue;
                    list.Add(new MainWindow.BatchVariant { Rate = rate, Tag = "BPM" + target.ToString("0.#", CultureInfo.InvariantCulture) });
                    if (list.Count > 500) break;
                }
            }

            if (UpRadio.IsChecked != true) list.Reverse();
            if (list.Count == 0) error = "当前范围/步长没有可生成的版本";
            if (list.Count > 200) error = "版本过多（超过 200 个），请调整范围或步长";
            return list;
        }

        private void UpdatePreview()
        {
            if (PreviewText == null) return;
            string error;
            var list = BuildVariants(out error);
            if (error != null)
            {
                PreviewText.Text = error;
                if (GenerateButton != null) GenerateButton.IsEnabled = false;
                return;
            }
            var text = string.Join("  ", list.Take(24).Select(v => v.Tag));
            if (list.Count > 24) text += " …";
            PreviewText.Text = "共 " + list.Count + " 个：" + text;
            if (GenerateButton != null) GenerateButton.IsEnabled = !_running;
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            string error;
            var variants = BuildVariants(out error);
            if (error != null)
            {
                PreviewText.Text = error;
                return;
            }

            _running = true;
            GenerateButton.IsEnabled = false;
            Progress.Maximum = variants.Count;
            Progress.Value = 0;
            try
            {
                var results = await _owner.GenerateBatchAsync(variants, (index, message) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Progress.Value = index;
                        PreviewText.Text = message + "（" + (index + 1) + "/" + variants.Count + "）";
                    }));
                });
                var ok = results.Count(x => !string.IsNullOrEmpty(x));
                PreviewText.Text = "完成：" + ok + "/" + variants.Count + " 个成功";
                Progress.Value = variants.Count;
            }
            catch (Exception ex)
            {
                PreviewText.Text = "批量失败：" + ex.Message;
            }
            finally
            {
                _running = false;
                GenerateButton.IsEnabled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (!_running) Close();
        }
    }
}
