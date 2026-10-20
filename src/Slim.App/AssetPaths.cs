using System;
using System.IO;
using System.Reflection;

namespace Slim
{
    /// <summary>
    /// Extracts the embedded web assets (ManiaMapAnalyser + rosu-pp) into a per-version cache
    /// folder so the application can run as a single .exe.
    /// </summary>
    public static class AssetPaths
    {
        private static string _webRoot;

        public static string WebRoot
        {
            get { return _webRoot ?? (_webRoot = EnsureWebRoot()); }
        }

        public static string UserDataRoot
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuTrainerXSlim");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string EnsureWebRoot()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version.ToString();
            var root = Path.Combine(UserDataRoot, "web", version);

            var marker = Path.Combine(root, "index.html");
            if (File.Exists(marker)) return root;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch { }

            Directory.CreateDirectory(root);

            const string prefix = "webassets/";
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = name.Substring(prefix.Length)
                    .Replace('\\', '/')
                    .Replace('/', Path.DirectorySeparatorChar);

                var target = Path.Combine(root, relative);
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var stream = assembly.GetManifestResourceStream(name))
                using (var file = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    if (stream == null) continue;
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        file.Write(buffer, 0, read);
                }
            }

            return root;
        }
    }
}
