using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Slim.AnalysisTest
{
    public partial class MainWindow : Window
    {
        private readonly string _mapPath;
        private readonly string _outPath;
        private TaskCompletionSource<string> _pending;

        public MainWindow(string mapPath, string outPath)
        {
            _mapPath = mapPath;
            _outPath = outPath;
            InitializeComponent();
            Loaded += async (_, __) => await RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                var userData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wv2data");
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await Web.EnsureCoreWebView2Async(env);

                var webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);

                var ready = new TaskCompletionSource<bool>();
                _pending = new TaskCompletionSource<string>();

                Web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    var json = e.WebMessageAsJson;
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wv2.log"), json.Substring(0, Math.Min(200, json.Length)) + Environment.NewLine);
                    if (json.IndexOf("\"type\":\"ready\"", StringComparison.Ordinal) >= 0)
                    {
                        ready.TrySetResult(true);
                    }
                    else if (json.IndexOf("\"type\":\"result\"", StringComparison.Ordinal) >= 0)
                    {
                        _pending.TrySetResult(json);
                    }
                };

                Web.CoreWebView2.Navigate("https://app.local/index.html");

                if (await Task.WhenAny(ready.Task, Task.Delay(20000)) != ready.Task)
                    throw new TimeoutException("WebView2 page did not become ready");

                var text = File.ReadAllText(_mapPath);
                var payload = "{\"type\":\"analyze\",\"id\":1,\"rate\":1.0,\"text\":\"" + EscapeJson(text) + "\"}";
                Web.CoreWebView2.PostWebMessageAsJson(payload);

                var completed = await Task.WhenAny(_pending.Task, Task.Delay(60000));
                if (completed != _pending.Task)
                    throw new TimeoutException("analysis timed out");

                File.WriteAllText(_outPath, _pending.Task.Result, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                File.WriteAllText(_outPath,
                    "{\"ok\":false,\"error\":\"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}",
                    new UTF8Encoding(false));
            }
            finally
            {
                Application.Current.Shutdown();
            }
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

