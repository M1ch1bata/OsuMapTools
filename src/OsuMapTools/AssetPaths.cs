using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace OsuMapTools
{
    /// <summary>
    /// Extracts the embedded web assets (ManiaMapAnalyser + rosu-pp) into a content-addressed
    /// cache folder, so the single .exe can ship with its front-end resources embedded.
    /// The cache key is the SHA-256 of the embedded payload: whenever app.js / wasm changes,
    /// a fresh folder is used and stale files can never be served.
    /// </summary>
    public static class AssetPaths
    {
        private const string Prefix = "webassets/";
        private const string Marker = "_complete";

        private static string _webRoot;

        public static string WebRoot
        {
            get { return _webRoot ?? (_webRoot = EnsureWebRoot()); }
        }

        public static string UserDataRoot
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OsuMapTools");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string EnsureWebRoot()
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Folder release: a complete `web` folder next to the exe wins over the extracted cache.
            var loose = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
            if (File.Exists(Path.Combine(loose, "index.html")))
                return loose;

            var hash = ComputePayloadHash(assembly);
            var root = Path.Combine(UserDataRoot, "web", hash);

            if (IsComplete(root, hash)) return root;

            Extract(assembly, root, hash);
            return root;
        }

        private static bool IsComplete(string root, string hash)
        {
            try
            {
                var marker = Path.Combine(root, Marker);
                if (!File.Exists(marker)) return false;
                if (!File.Exists(Path.Combine(root, "index.html"))) return false;
                return string.Equals(File.ReadAllText(marker).Trim(), hash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void Extract(Assembly assembly, string root, string hash)
        {
            // Multiple sessions/processes may start at once; serialise the extraction.
            using (var mutex = new Mutex(false, @"Global\OsuMapTools-WebAssets"))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                    catch (AbandonedMutexException) { acquired = true; }

                    if (IsComplete(root, hash)) return;

                    var temp = root + ".tmp-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);

                    foreach (var name in assembly.GetManifestResourceNames())
                    {
                        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

                        var relative = name.Substring(Prefix.Length).Replace('\\', '/');
                        var target = Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar));
                        var dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        using (var stream = assembly.GetManifestResourceStream(name))
                        {
                            if (stream == null) continue;
                            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                                stream.CopyTo(file);
                        }
                    }

                    // Marker written last: a partially extracted folder can never be considered complete.
                    File.WriteAllText(Path.Combine(temp, Marker), hash);

                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(root)) Directory.Delete(root, true);
                            Directory.Move(temp, root);
                            return;
                        }
                        catch (IOException) when (attempt < 5)
                        {
                            Thread.Sleep(200);
                        }
                        catch (UnauthorizedAccessException) when (attempt < 5)
                        {
                            Thread.Sleep(200);
                        }
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        private static string ComputePayloadHash(Assembly assembly)
        {
            using (var sha = SHA256.Create())
            using (var buffer = new MemoryStream())
            {
                foreach (var name in assembly.GetManifestResourceNames()
                             .Where(n => n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(n => n, StringComparer.Ordinal))
                {
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    buffer.Write(nameBytes, 0, nameBytes.Length);
                    using (var stream = assembly.GetManifestResourceStream(name))
                    {
                        if (stream != null) stream.CopyTo(buffer);
                    }
                }

                buffer.Position = 0;
                var hash = sha.ComputeHash(buffer);
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }
    }
}
