using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Slim
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsuTrainerXSlim", "error.log");
        private static System.Threading.Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
            DispatcherUnhandledException += (_, a) =>
            {
                Log(a.Exception);
                try
                {
                    MessageBox.Show("发生未处理错误：\n" + a.Exception.Message + "\n\n详细日志：" + LogPath,
                        "OsuTrainerX Slim", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                a.Handled = true;
            };

            bool createdNew;
            _mutex = new System.Threading.Mutex(true, "OsuTrainerXSlim_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("OsuTrainerXSlim 已经在运行，请先关闭已有窗口。", "OsuTrainerX Slim",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                Run(e);
            }
            catch (Exception ex)
            {
                Log(ex);
                throw;
            }
        }

        private void Run(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;

            var hasMapArg = e.Args.Length > 0 && !e.Args[0].StartsWith("--", StringComparison.Ordinal);
            if (hasMapArg && !File.Exists(e.Args[0]))
            {
                MessageBox.Show("找不到谱面文件：" + e.Args[0], "OsuTrainerX Slim",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }

            var loaded = hasMapArg && File.Exists(e.Args[0]);
            if (loaded)
            {
                window.LoadFilePublic(e.Args[0]);

                double? rate = ParseDouble(e.Args, "--rate");
                double? od = ParseDouble(e.Args, "--od");
                double? hp = ParseDouble(e.Args, "--hp");
                int? mode = ParseMode(e.Args);
                int? strength = ParseStrength(e.Args);
                window.ApplyOptions(rate, od, hp, mode, strength);

                var outRoot = ParseString(e.Args, "--out");
                if (!string.IsNullOrWhiteSpace(outRoot)) window.SetOutputRoot(outRoot);

                var bpm = ParseDouble(e.Args, "--bpm");
                if (bpm.HasValue) window.SetBpmMode(bpm.Value);

                if (e.Args.Any(a => a.Equals("--no-pitch", StringComparison.OrdinalIgnoreCase)))
                    window.SetKeepPitch(false);
            }

            window.Show();

            if (e.Args.Length > 0 && File.Exists(e.Args[0]) &&
                e.Args.Any(a => a.Equals("--generate", StringComparison.OrdinalIgnoreCase)))
            {
                var task = window.GeneratePublicAsync();
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, System.Threading.Tasks.TaskScheduler.Default);
                Dispatcher.PushFrame(frame);

                var ok = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && task.Result;
                Shutdown(ok ? 0 : 1);
            }
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("O") + "] " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static double? ParseDouble(string[] args, string key)
        {
            int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            double v;
            if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            return v;
        }

        private static string ParseString(string[] args, string key)
        {
            int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            return args[i + 1];
        }

        private static int? ParseStrength(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--strength", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var v = args[i + 1].ToLowerInvariant();
            if (v == "loose") return 0;
            if (v == "std") return 1;
            if (v == "dense") return 2;
            return null;
        }

        private static int? ParseMode(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--mode", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var v = args[i + 1].ToLowerInvariant();
            if (v == "keep") return 0;
            if (v == "invert") return 1;
            if (v == "holdoff") return 2;
            if (v == "release") return 3;
            return null;
        }

        private static int? ParsePreset(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--preset", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var v = args[i + 1].ToLowerInvariant();
            if (v == "lite") return 1;
            if (v == "dense") return 2;
            return 0;
        }
    }
}

