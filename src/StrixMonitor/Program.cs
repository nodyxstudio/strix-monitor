using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace StrixMonitor
{
    public static class Program
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrixMonitor");

        [STAThread]
        public static int Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            if (args.Length > 0 && args[0] == "--dump")
                return Dump.Run(args.Length > 1 ? args[1] : Path.Combine(DataDir, "sensors.txt"));

            if (args.Length > 1 && args[0] == "--preview")
            {
                var w = new WidgetWindow { PreviewPath = args[1], ShowActivated = false };
                w.Left = -4000;
                return new Application().Run(w);
            }

            if (args.Length > 1 && args[0] == "--setup-preview")
                return new Application().Run(new SetupWindow { PreviewPath = args[1], ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000 });

            if (args.Length > 1 && args[0] == "--session-preview")
                return new Application().Run(new SessionWindow { PreviewPath = args[1], ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000 });

            if (args.Contains("--uninstall"))
                return Setup.Uninstall(quiet: args.Contains("--quiet"));

            // Lancé hors de son dossier d'installation (téléchargé, reçu par un ami…) : on affiche l'assistant d'installation.
            if (!args.Contains("--widget") && !Setup.IsInstalledCopy)
            {
                var setupApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                setupApp.DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); e.Handled = true; };
                return setupApp.Run(new SetupWindow());
            }

            using var mutex = new Mutex(true, "StrixMonitor_SingleInstance", out bool first);
            if (!first) return 0;

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log(e.Exception.ToString());
                e.Handled = true;
            };
            return app.Run(new WidgetWindow());
        }

        public static void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(DataDir, "log.txt"), $"[{DateTime.Now:G}] {msg}\n"); } catch { }
        }
    }
}
