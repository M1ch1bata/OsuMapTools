using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OsuMapTools
{
    /// <summary>
    /// tosu v2 watcher: structured JSON parsing (flat schema), incremental UTF-8 decoding and
    /// a cancellable socket. Provides the current beatmap path and osu!'s live star rating.
    /// </summary>
    public sealed class TosuWatcher : IDisposable
    {
        private readonly Action<string> _onMap;
        private readonly Action<string> _onStatus;
        private readonly Action<double> _onStars;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private ClientWebSocket _ws;
        private string _lastPath;

        public string CurrentPath => _lastPath;

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
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri("ws://127.0.0.1:24050/websocket/v2"), token);
                    if (_onStatus != null) _onStatus("tosu 已连接");

                    var buffer = new byte[1 << 16];
                    var decoder = Encoding.UTF8.GetDecoder();
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
                    var sb = new StringBuilder();

                    while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var count = decoder.GetChars(buffer, 0, result.Count, chars, 0, false);
                        sb.Append(chars, 0, count);
                        if (!result.EndOfMessage) continue;

                        var json = sb.ToString();
                        sb.Length = 0;
                        decoder.Reset();
                        Handle(json);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) break;
                }
                catch
                {
                    if (!token.IsCancellationRequested && _onStatus != null) _onStatus("tosu 未连接");
                }
                finally
                {
                    try { _ws?.Abort(); } catch { }
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;
                }

                try { await Task.Delay(3000, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void Handle(string json)
        {
            Dictionary<string, object> root;
            try { root = SimpleJson.Parse(json) as Dictionary<string, object>; }
            catch { root = null; }
            if (root == null) return;

            var state = GetObject(root, "state");
            var beatmap = GetObject(root, "beatmap");
            var folders = GetObject(root, "folders");
            var files = GetObject(root, "files");

            var stateName = GetString(state, "name");
            var mode = (int)GetDouble(GetObject(beatmap, "mode"), "number", -1);

            double stars = 0;
            if (beatmap != null)
            {
                var stats = GetObject(beatmap, "stats");
                var starObj = GetObject(stats, "stars");
                if (starObj != null)
                {
                    var live = GetDouble(starObj, "live", 0);
                    var total = GetDouble(starObj, "total", 0);
                    stars = live > 0.5 ? live : total;
                }
                else if (stats != null)
                {
                    stars = GetDouble(stats, "stars", 0);
                }
            }

            if (_onStatus != null)
                _onStatus("tosu " + (string.IsNullOrEmpty(stateName) ? "?" : stateName) +
                          (mode >= 0 ? " · mode " + mode : ""));
            if (_onStars != null && stars > 0) _onStars(stars);

            var songs = GetString(folders, "songs");
            var rel = GetString(folders, "beatmap");
            var file = GetString(files, "beatmap");
            if (string.IsNullOrEmpty(songs) || string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(file)) return;

            var path = Path.Combine(songs, rel, file);
            if (string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(path)) return;

            _lastPath = path;
            if (_onMap != null) _onMap(path);
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            if (!source.TryGetValue(key, out value)) return null;
            return value as string;
        }

        private static double GetDouble(Dictionary<string, object> source, string key, double fallback)
        {
            if (source == null) return fallback;
            object value;
            if (!source.TryGetValue(key, out value) || value == null) return fallback;
            if (value is double d) return d;
            var text = value as string;
            double parsed;
            if (text != null && double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _cts.Dispose();
        }
    }
}
