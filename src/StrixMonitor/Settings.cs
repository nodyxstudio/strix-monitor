using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace StrixMonitor
{
    public sealed class Settings
    {
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public bool Topmost { get; set; } = true;
        /// <summary>Encart CPU / GPU intégré dans la barre des tâches.</summary>
        public bool Taskbar { get; set; } = true;
        /// <summary>Icône dans la zone de notification (près de l'horloge).</summary>
        public bool Tray { get; set; } = false;
        /// <summary>Ce que montre l'icône : "both" (CPU en haut, GPU en bas), "cpu" ou "gpu".</summary>
        public string IconShows { get; set; } = "both";
        /// <summary>Position de l'encart dans la barre des tâches : distance depuis le bord droit (DIP). NaN = automatique.</summary>
        public double BarOffset { get; set; } = double.NaN;
        /// <summary>Réglage par ventilateur : "auto", "fixed:45" ou "curve:Équilibré".</summary>
        public Dictionary<string, string> FanModes { get; set; } = new();

        static string FilePath => Path.Combine(Program.DataDir, "settings.json");
        static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts) ?? new Settings(); }
            catch { return new Settings(); }
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); }
            catch (Exception ex) { Program.Log("Settings: " + ex.Message); }
        }

        public void Store(FanItem f)
        {
            FanModes[f.Key] = f.Mode switch
            {
                FanMode.Fixed => "fixed:" + f.FixedPercent,
                FanMode.Curve => "curve:" + f.Preset,
                _ => "auto",
            };
            Save();
        }

        public void Restore(FanItem f)
        {
            if (!FanModes.TryGetValue(f.Key, out var s)) return;
            if (s.StartsWith("fixed:") && int.TryParse(s[6..], out int p)) { f.Mode = FanMode.Fixed; f.FixedPercent = p; }
            else if (s.StartsWith("curve:")) { f.Mode = FanMode.Curve; f.Preset = s[6..]; }
        }
    }

    /// <summary>Démarrage auto via le Planificateur de tâches (seul moyen de lancer une app admin sans UAC à l'ouverture de session).</summary>
    static class Startup
    {
        const string Name = "StrixMonitor";

        public static bool IsEnabled() => Run($"/Query /TN {Name}") == 0;

        public static bool Set(bool on, string exe = null) => on
            ? Run($"/Create /F /TN {Name} /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe ?? Environment.ProcessPath}\\\"\"") == 0
            : Run($"/Delete /F /TN {Name}") == 0;

        /// <summary>Supprime la tâche de démarrage d'une ancienne version. Renvoie true si elle existait.</summary>
        public static bool RemoveLegacy(string oldName)
        {
            if (Run($"/Query /TN {oldName}") != 0) return false;
            Run($"/Delete /F /TN {oldName}");
            return true;
        }

        /// <summary>Si la tâche existe, la recrée pour qu'elle pointe vers l'exe actuel (utile si l'app a été déplacée).</summary>
        public static void RefreshPath()
        {
            if (IsEnabled()) Set(true);
        }

        static int Run(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                p.WaitForExit(5000);
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}
