using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OsuMapTools
{
    public partial class ReleaseSettingsWindow : Window
    {
        private bool _loading;

        public int A { get; private set; }
        public int B { get; private set; }
        public int C { get; private set; }

        public ReleaseSettingsWindow(int a, int b, int c)
        {
            InitializeComponent();
            _loading = true;
            A = Clamp(a);
            B = Clamp(b);
            C = Clamp(c);
            ABox.Text = A.ToString(CultureInfo.InvariantCulture);
            BBox.Text = B.ToString(CultureInfo.InvariantCulture);
            CBox.Text = C.ToString(CultureInfo.InvariantCulture);
            StyleBox.SelectedIndex = A == 2 ? 0 : A == 5 ? 1 : A == 8 ? 2 : 3;
            _loading = false;
        }

        private static int Clamp(int value)
        {
            return Math.Min(10, Math.Max(0, value));
        }

        private static int Read(TextBox box, int fallback)
        {
            int value;
            return int.TryParse(box.Text.Trim(), out value) ? Clamp(value) : fallback;
        }

        private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (var ch in e.Text)
            {
                if (!char.IsDigit(ch))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void Style_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (StyleBox.SelectedIndex == 0) ABox.Text = "2";
            else if (StyleBox.SelectedIndex == 1) ABox.Text = "5";
            else if (StyleBox.SelectedIndex == 2) ABox.Text = "8";
        }

        private void Spin_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;
            var parts = button.Tag.ToString().Split(':');
            if (parts.Length != 2) return;
            int delta;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out delta)) return;
            var box = parts[0] == "a" ? ABox : parts[0] == "b" ? BBox : CBox;
            var value = Read(box, parts[0] == "a" ? 5 : parts[0] == "b" ? 0 : 5);
            box.Text = Clamp(value + delta).ToString(CultureInfo.InvariantCulture);
            if (parts[0] == "a") StyleBox.SelectedIndex = 3;
        }

        private void Box_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null) return;
            box.Text = Read(box, box == ABox ? 5 : box == BBox ? 0 : 5).ToString(CultureInfo.InvariantCulture);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            A = Read(ABox, 5);
            B = Read(BBox, 0);
            C = Read(CBox, 5);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
