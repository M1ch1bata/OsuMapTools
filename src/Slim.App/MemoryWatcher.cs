using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Security.Principal;
using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;

namespace Slim
{
    /// <summary>Locates the osu! Songs folder from registry, running process or common paths.</summary>
    public static class SongsLocator
    {
        private static string _songs;

        public static string SongsDir
        {
            get { return _songs ?? (_songs = Find()); }
        }

        private static string Find()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"osu\shell\open\command"))
                {
                    var cmd = key == null ? null : key.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        var m = Regex.Match(cmd, "\"([^\"]*osu!\\.exe)\"", RegexOptions.IgnoreCase);
                        if (m.Success && File.Exists(m.Groups[1].Value))
                        {
                            var dir = Path.Combine(Path.GetDirectoryName(m.Groups[1].Value), "Songs");
                            if (Directory.Exists(dir)) return dir;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var proc = Process.GetProcessesByName("osu!").FirstOrDefault();
                if (proc != null)
                {
                    var exe = proc.MainModule.FileName;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        var dir = Path.Combine(Path.GetDirectoryName(exe), "Songs");
                        if (Directory.Exists(dir)) return dir;
                    }
                }
            }
            catch { }

            var common = new[]
            {
                @"D:\APPs\Games\osu!\Songs",
                @"C:\Program Files\osu!\Songs",
                @"C:\Program Files (x86)\osu!\Songs",
                @"D:\osu!\Songs",
                @"C:\osu!\Songs",
            };
            foreach (var dir in common)
            {
                if (Directory.Exists(dir)) return dir;
            }
            return null;
        }
    }

    /// <summary>Reads the currently selected / playing beatmap straight from osu! memory (no tosu required).</summary>
    public sealed class MemoryWatcher : IDisposable
    {
        private readonly Action<string> _onBeatmap;
        private readonly Action<string> _onStatus;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private string _lastPath;
        private StructuredOsuMemoryReader _boundReader;
        private bool _triedWindowBinding;
        private string _lastStatus;
        private int _failCount;

        private static readonly string[] AcceptedStatuses =
        {
            "SongSelect", "SongSelectEdit", "Playing", "ResultsScreen", "MultiplayerSongSelect",
        };

        public MemoryWatcher(Action<string> onBeatmap, Action<string> onStatus = null)
        {
            _onBeatmap = onBeatmap;
            _onStatus = onStatus;
        }

        public void Start()
        {
            Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var reader = _boundReader ?? StructuredOsuMemoryReader.Instance;
                    if (reader != null && _boundReader == null && !_triedWindowBinding)
                    {
                        _triedWindowBinding = true;
                        var title = FindOsuWindowTitle();
                        if (!string.IsNullOrEmpty(title))
                        {
                            try
                            {
                                _boundReader = reader.GetInstanceForWindowTitleHint(title);
                            }
                            catch { }
                        }
                    }

                    if (reader != null)
                    {
                        var general = new GeneralData();
                        bool readGeneral = reader.TryRead(general);
                        var status = general.OsuStatus.ToString();

                        if (!readGeneral)
                        {
                            _failCount++;
                            if (_failCount == 3 && _onStatus != null)
                            {
                                bool running;
                                int visible;
                                try
                                {
                                    var all = Process.GetProcesses();
                                    visible = all.Length;
                                    running = all.Any(p => string.Equals(SafeName(p), "osu!", StringComparison.OrdinalIgnoreCase));
                                }
                                catch { running = false; visible = -1; }
                                _onStatus((running ? "读取被拒绝" : "未找到 osu!（可见 " + visible + " 个进程）") +
                                          (visible >= 0 && visible < 100 ? "（受限环境）" : ""));
                            }
                            try { await Task.Delay(1000, token); }
                            catch (OperationCanceledException) { break; }
                            continue;
                        }

                        _failCount = 0;
                        if (status != _lastStatus)
                        {
                            _lastStatus = status;
                            if (_onStatus != null) _onStatus(status);
                        }

                        if (AcceptedStatuses.Contains(status))
                        {
                            var beatmap = new CurrentBeatmap();
                            bool readBeatmap = reader.TryRead(beatmap);

                            if (readBeatmap &&
                                !string.IsNullOrEmpty(beatmap.FolderName) &&
                                !string.IsNullOrEmpty(beatmap.OsuFileName))
                            {
                                var songs = SongsLocator.SongsDir;
                                if (!string.IsNullOrEmpty(songs))
                                {
                                    var path = Path.Combine(songs, beatmap.FolderName, beatmap.OsuFileName);
                                    if (File.Exists(path) && !string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _lastPath = path;
                                        _onBeatmap(path);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                    }
                }
                catch (Exception ex)
                {
                }

                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public int Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, int len, out int ret);

        [DllImport("advapi32.dll")]
        private static extern int GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private static string SelfIntegrity()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) return "?";
                var label = new TOKEN_MANDATORY_LABEL();
                int ret;
                if (!GetTokenInformation(token, 25, ref label, Marshal.SizeOf(typeof(TOKEN_MANDATORY_LABEL)), out ret))
                {
                    CloseHandle(token);
                    return "?";
                }
                int count = GetSidSubAuthorityCount(label.Label.Sid);
                IntPtr sub = GetSidSubAuthority(label.Label.Sid, count - 1);
                int rid = Marshal.ReadInt32(sub);
                CloseHandle(token);
                return "0x" + rid.ToString("X4");
            }
            catch
            {
                return "?";
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken2(IntPtr process, uint access, out IntPtr token);

        private static bool IsRestrictedToken()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken2(GetCurrentProcess(), 0x0008, out token)) return false;
                IntPtr buf = Marshal.AllocHGlobal(4);
                int ret;
                bool restricted = false;
                if (GetTokenInformationRaw(token, 21, buf, 4, out ret) && Marshal.ReadInt32(buf) != 0)
                    restricted = true;
                Marshal.FreeHGlobal(buf);
                CloseHandle(token);
                return restricted;
            }
            catch { return false; }
        }

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
        private static extern bool GetTokenInformationRaw(IntPtr token, int cls, IntPtr buf, int len, out int ret);

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return ""; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        private static string FindOsuWindowTitle()
        {
            string found = null;
            try
            {
                EnumWindows((h, l) =>
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(h, sb, sb.Capacity);
                    var t = sb.ToString();
                    if (string.Equals(t, "osu!", StringComparison.OrdinalIgnoreCase)) { found = t; return false; }
                    if (found == null && t.StartsWith("osu!", StringComparison.OrdinalIgnoreCase)) found = t;
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
    }
}
