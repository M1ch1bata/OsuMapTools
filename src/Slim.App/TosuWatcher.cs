using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Slim
{
    /// <summary>
    /// tosu v2 watcher (flat schema): state + folders.songs/beatmap + files.beatmap give the
    /// exact .osu path even on song select, without reading osu! process memory.
    /// </summary>
    public sealed class TosuWatcher : IDisposable
    {
        private readonly Action<string> _onMap;
        private readonly Action<string> _onStatus;
        private readonly Action<double> _onStars;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private string _lastPath;

        public TosuWatcher(Action<string> onMap, Action<string> onStatus = null, Action<double> onStars = null)
        {
            _onMap = onMap;
            _onStatus = onStatus;
            _onStars = onStars;
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
                    using (var ws = new ClientWebSocket())
                    {
                        await ws.ConnectAsync(new Uri("ws://127.0.0.1:24050/websocket/v2"), token);
                        if (_onStatus != null) _onStatus("tosu 已连接");

                        var buffer = new byte[1 << 16];
                        var sb = new StringBuilder();

                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close) break;

                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            if (!result.EndOfMessage) continue;

                            var json = sb.ToString();
                            sb.Length = 0;
                            Handle(json);
                        }
                    }
                }
                catch
                {
                    if (_onStatus != null) _onStatus("tosu 未连接");
                }

                try { await Task.Delay(3000, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void Handle(string json)
        {
            var stateObj = ExtractObject(json, "state");
            var stateName = stateObj == null ? "" : ExtractString(stateObj, "name");

            var folders = ExtractObject(json, "folders");
            var files = ExtractObject(json, "files");
            var beatmap = ExtractObject(json, "beatmap");

            var songs = folders == null ? null : ExtractString(folders, "songs");
            var relFolder = folders == null ? null : ExtractString(folders, "beatmap");
            var file = files == null ? null : ExtractString(files, "beatmap");

            var mode = -1;
            var setId = -1;
            var id = -1;
            double stars = 0;
            if (beatmap != null)
            {
                var modeObj = ExtractObject(beatmap, "mode");
                if (modeObj != null) mode = ExtractInt(modeObj, "number");
                setId = ExtractInt(beatmap, "set");
                id = ExtractInt(beatmap, "id");

                var stats = ExtractObject(beatmap, "stats");
                var starObj = stats == null ? null : ExtractObject(stats, "stars");
                if (starObj != null)
                {
                    var live = ExtractDouble(starObj, "live");
                    var total = ExtractDouble(starObj, "total");
                    stars = live > 0.5 ? live : total;
                }
            }

            if (_onStars != null && stars > 0) _onStars(stars);

            if (_onStatus != null)
            {
                var text = "tosu " + (string.IsNullOrEmpty(stateName) ? "?" : stateName);
                if (mode >= 0) text += " · mode " + mode;
                _onStatus(text);
            }

            if (string.IsNullOrEmpty(songs) || string.IsNullOrEmpty(relFolder) || string.IsNullOrEmpty(file))
                return;

            var path = Path.Combine(songs, relFolder, file);
            if (!File.Exists(path)) return;
            if (string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase)) return;

            _lastPath = path;
            _onMap(path);
        }

        private static string ExtractObject(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return null;

            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '{') return null;

            int depth = 0;
            bool inString = false;
            for (int p = start; p < json.Length; p++)
            {
                char c = json[p];
                if (inString)
                {
                    if (c == (char)92) p++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, p - start + 1);
                }
            }
            return null;
        }

        private static string ExtractString(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return null;

            int q = json.IndexOf('"', colon + 1);
            if (q < 0) return null;

            var sb = new StringBuilder();
            for (int p = q + 1; p < json.Length; p++)
            {
                char c = json[p];
                if (c == (char)92 && p + 1 < json.Length)
                {
                    char n = json[++p];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case (char)92: sb.Append((char)92); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (p + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(p + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                p += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static double ExtractDouble(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return 0;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return 0;

            int p = colon + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '	')) p++;
            int start = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '.' || json[p] == '-' || json[p] == '+' || json[p] == 'e' || json[p] == 'E')) p++;
            double value;
            return double.TryParse(json.Substring(start, p - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static int ExtractInt(string json, string key)
        {
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return -1;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return -1;

            int p = colon + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int start = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '-')) p++;
            int value;
            return int.TryParse(json.Substring(start, p - start), out value) ? value : -1;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
    }
}
