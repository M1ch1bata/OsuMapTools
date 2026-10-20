using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Slim
{
    public partial class SongPicker : Window
    {
        private List<string> _allFolders = new List<string>();

        public string SelectedPath { get; private set; }

        public SongPicker()
        {
            InitializeComponent();
            Loaded += (_, __) => LoadFolders();
        }

        private void LoadFolders()
        {
            var songs = SongsLocator.SongsDir;
            if (string.IsNullOrEmpty(songs) || !Directory.Exists(songs))
            {
                StatusText.Text = "找不到 osu! Songs 目录";
                return;
            }

            try
            {
                _allFolders = Directory.GetDirectories(songs).OrderBy(x => x).ToList();
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取 Songs 失败：" + ex.Message;
                return;
            }

            ApplyFilter("");
            StatusText.Text = songs + " · " + _allFolders.Count + " 个谱面集";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(SearchBox.Text);
        }

        private void ApplyFilter(string text)
        {
            var items = string.IsNullOrWhiteSpace(text)
                ? _allFolders
                : _allFolders.Where(f => Path.GetFileName(f).IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            FolderList.ItemsSource = items.Select(Path.GetFileName).ToList();
            FolderList.Tag = items;
            FileList.ItemsSource = null;
            OkButton.IsEnabled = false;
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var folders = FolderList.Tag as List<string>;
            if (folders == null || FolderList.SelectedIndex < 0 || FolderList.SelectedIndex >= folders.Count) return;

            var dir = folders[FolderList.SelectedIndex];
            try
            {
                var files = Directory.GetFiles(dir, "*.osu").OrderBy(x => x).Select(Path.GetFileName).ToList();
                FileList.ItemsSource = files;
                FileList.Tag = files.Select(f => Path.Combine(dir, f)).ToList();
            }
            catch
            {
                FileList.ItemsSource = null;
                FileList.Tag = null;
            }
            OkButton.IsEnabled = false;
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OkButton.IsEnabled = FileList.SelectedIndex >= 0 && FileList.Tag != null;
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            TryAccept();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            TryAccept();
        }

        private void TryAccept()
        {
            var paths = FileList.Tag as List<string>;
            if (paths == null || FileList.SelectedIndex < 0 || FileList.SelectedIndex >= paths.Count) return;

            SelectedPath = paths[FileList.SelectedIndex];
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
