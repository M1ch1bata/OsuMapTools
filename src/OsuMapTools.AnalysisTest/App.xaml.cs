using System;
using System.Windows;

namespace OsuMapTools.AnalysisTest
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Length < 2)
            {
                Console.Error.WriteLine("usage: OsuMapTools.AnalysisTest <map.osu> <out.json>");
                Shutdown(2);
                return;
            }

            var window = new MainWindow(e.Args[0], e.Args[1]);
            MainWindow = window;
            window.Show();
        }
    }
}
