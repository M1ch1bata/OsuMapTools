using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace OsuMapTools
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsuMapTools", "error.log");
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
                        "OsuMapTools", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                a.Handled = true;
            };

            var headless = e.Args.Any(a => a.Equals("--generate", StringComparison.OrdinalIgnoreCase));
            string cliError;
            if (headless && !ValidateCli(e.Args, out cliError))
            {
                LogText("CLI: " + cliError);
                Shutdown(2);
                return;
            }

            bool createdNew;
            _mutex = new System.Threading.Mutex(true, "OsuMapTools_SingleInstance", out createdNew);
            if (!createdNew)
            {
                if (headless)
                {
                    LogText("CLI: another OsuMapTools instance is already running");
                    Shutdown(2);
                    return;
                }
                MessageBox.Show("OsuMapTools 已经在运行，请先关闭已有窗口。", "OsuMapTools",
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

            if (e.Args.Any(a => a.Equals("--generate", StringComparison.OrdinalIgnoreCase)))
            {
                var mapArg = e.Args.FirstOrDefault(a => a.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)
                                                        && File.Exists(a));
                if (mapArg == null || !File.Exists(mapArg))
                {
                    LogText("CLI: no valid .osu map argument for --generate");
                    Shutdown(2);
                    return;
                }
            }

            var hasMapArg = e.Args.Length > 0 && !e.Args[0].StartsWith("--", StringComparison.Ordinal);
            if (hasMapArg && !File.Exists(e.Args[0]))
            {
                MessageBox.Show("找不到谱面文件：" + e.Args[0], "OsuMapTools",
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

                var gap = ParseGap(e.Args);
                if (gap.HasValue) window.SetCustomGap(gap.Value);

                if (e.Args.Any(a => a.Equals("--no-follow", StringComparison.OrdinalIgnoreCase)))
                    window.SetFollow(false);

                var releaseGap = ParseReleaseGap(e.Args);
                if (releaseGap.HasValue) window.SetReleaseGap(releaseGap.Value);

                var shortLen = ParseIntOption(e.Args, "--short-length", 1, 16);
                var shortGap = ParseIntOption(e.Args, "--short-gap", 1, 16);
                if (shortLen.HasValue || shortGap.HasValue)
                    window.SetShortLnOptions(shortLen ?? 4, shortGap ?? 4);

                var releaseA = ParseIntOption(e.Args, "--release-a", 0, 10);
                var releaseB = ParseIntOption(e.Args, "--release-b", 0, 10);
                var releaseC = ParseIntOption(e.Args, "--release-c", 0, 10);
                var releaseStyle = ParseString(e.Args, "--release-style");
                if (!string.IsNullOrEmpty(releaseStyle))
                {
                    if (releaseStyle.Equals("loose", StringComparison.OrdinalIgnoreCase)) releaseA = 2;
                    else if (releaseStyle.Equals("standard", StringComparison.OrdinalIgnoreCase)
                             || releaseStyle.Equals("std", StringComparison.OrdinalIgnoreCase)) releaseA = 5;
                    else if (releaseStyle.Equals("dense", StringComparison.OrdinalIgnoreCase)) releaseA = 8;
                }
                if (releaseA.HasValue || releaseB.HasValue || releaseC.HasValue || !string.IsNullOrEmpty(releaseStyle))
                    window.SetReleaseOptions(releaseA ?? 5, releaseB ?? 0, releaseC ?? 5);

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

        private static bool ValidateCli(string[] args, out string error)
        {
            error = null;
            string[] valueOptions = { "--rate", "--bpm", "--od", "--hp", "--mode", "--strength", "--gap", "--release-gap", "--out", "--short-length", "--short-gap", "--release-a", "--release-b", "--release-c", "--release-style" };
            foreach (var opt in valueOptions)
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "缺少 " + opt + " 的参数值";
                    return false;
                }
            }

            foreach (var opt in new[] { "--rate", "--bpm", "--od", "--hp" })
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                double v;
                if (!double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                    || double.IsNaN(v) || double.IsInfinity(v))
                {
                    error = opt + " 的参数不是有效数字：" + args[i + 1];
                    return false;
                }
            }

            foreach (var opt in new[] { "--mode", "--strength" })
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                var v = args[i + 1];
                var allowed = opt == "--mode"
                    ? new[] { "keep", "invert", "holdoff", "release", "shortln", "short" }
                    : new[] { "loose", "std", "dense", "seamless" };
                if (!allowed.Contains(v, StringComparer.OrdinalIgnoreCase))
                {
                    error = opt + " 的参数无效：" + v;
                    return false;
                }
            }

            foreach (var opt in new[] { "--gap" })
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                int v;
                if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 1 || v > 16)
                {
                    error = opt + " 需要 1–16 的整数：" + args[i + 1];
                    return false;
                }
            }

            foreach (var opt in new[] { "--short-length", "--short-gap" })
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                int v;
                if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 1 || v > 16)
                {
                    error = opt + " 需要 1–16 的整数：" + args[i + 1];
                    return false;
                }
            }

            foreach (var opt in new[] { "--release-gap", "--release-a", "--release-b", "--release-c" })
            {
                int i = Array.FindIndex(args, a => a.Equals(opt, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                int v;
                if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0 || v > 10)
                {
                    error = opt + " 需要 0–10 的整数：" + args[i + 1];
                    return false;
                }
            }

            return true;
        }

        private static void LogText(string text)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("O") + "] " + text + Environment.NewLine);
            }
            catch { }
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
            var token = args[i + 1];
            if (token.StartsWith("--", StringComparison.Ordinal)) return null;
            double v;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }

        private static string ParseString(string[] args, string key)
        {
            int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var token = args[i + 1];
            if (token.StartsWith("--", StringComparison.Ordinal)) return null;
            return token;
        }

        private static int? ParseReleaseGap(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--release-gap", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            int value;
            if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
            return Math.Min(16, Math.Max(1, value));
        }

        private static int? ParseIntOption(string[] args, string key, int min, int max)
        {
            int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var token = args[i + 1];
            if (token.StartsWith("--", StringComparison.Ordinal)) return null;
            int value;
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
            return Math.Min(max, Math.Max(min, value));
        }

        private static int? ParseGap(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--gap", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            int value;
            if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
            return Math.Min(16, Math.Max(1, value));
        }

        private static int? ParseStrength(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--strength", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return null;
            var v = args[i + 1].ToLowerInvariant();
            if (v == "loose") return 0;
            if (v == "std") return 1;
            if (v == "dense") return 2;
            if (v == "seamless") return 3;
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
            if (v == "shortln" || v == "short") return 4;
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

