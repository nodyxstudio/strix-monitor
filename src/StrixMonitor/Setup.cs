using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace StrixMonitor
{
    /// <summary>Installation / désinstallation : copie de l'exe, raccourcis, entrée "Applications installées", PawnIO.</summary>
    public static class Setup
    {
        public const string Version = "1.3.0";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StrixMonitor";

        // Installateur officiel PawnIO (même URL et même empreinte que le paquet winget namazso.PawnIO 2.2.0).
        const string PawnIOUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        const string PawnIOSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "StrixMonitor");
        public static string InstalledExe => Path.Combine(InstallDir, "StrixMonitor.exe");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Strix Monitor.lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Strix Monitor.lnk");

        public static bool IsInstalledCopy =>
            string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static bool IsInstalled => File.Exists(InstalledExe);

        public static bool PawnIOInstalled
        {
            get
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                    return k != null;
                }
                catch { return false; }
            }
        }

        /// <summary>Anciens noms de l'application (versions de développement), nettoyés à l'installation.</summary>
        static readonly string[] LegacyNames = { "OracleTemp", "TempWidget" };

        /// <summary>Ferme les autres instances (y compris les anciennes versions), sans se fermer soi-même.
        /// On demande d'abord une fermeture propre pour que les ventilateurs repassent en Auto.</summary>
        public static void StopOtherInstances()
        {
            int me = Environment.ProcessId;
            var others = LegacyNames.Prepend("StrixMonitor").SelectMany(Process.GetProcessesByName)
                .Where(p => p.Id != me).ToList();
            foreach (var p in others)
            {
                try
                {
                    if (p.CloseMainWindow() && p.WaitForExit(4000)) continue;
                    p.Kill();
                    p.WaitForExit(4000);
                }
                catch { }
            }
        }

        /// <summary>Retire les anciennes versions (dossier, raccourcis, tâche de démarrage, entrée de désinstallation)
        /// et reprend leurs réglages et sessions. Renvoie true si le démarrage automatique était activé.</summary>
        public static bool RemoveLegacy()
        {
            bool hadStartup = false;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var old in LegacyNames)
            {
                try { hadStartup |= Startup.RemoveLegacy(old); } catch { }
                foreach (var f in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), old + ".lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), old + ".lnk"),
                })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                try { Directory.Delete(Path.Combine(local, "Programs", old), true); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + old, false); } catch { }

                // Reprend les réglages et les sessions enregistrées s'il n'y en a pas encore.
                try
                {
                    string oldDir = Path.Combine(roaming, old);
                    string oldCfg = Path.Combine(oldDir, "settings.json"), newCfg = Path.Combine(Program.DataDir, "settings.json");
                    if (File.Exists(oldCfg) && !File.Exists(newCfg)) File.Copy(oldCfg, newCfg);
                    string oldSessions = Path.Combine(oldDir, "sessions");
                    if (Directory.Exists(oldSessions))
                    {
                        Directory.CreateDirectory(Recorder.Dir);
                        foreach (var s in Directory.GetFiles(oldSessions, "*.csv"))
                        {
                            string target = Path.Combine(Recorder.Dir, Path.GetFileName(s));
                            if (!File.Exists(target)) File.Copy(s, target);
                        }
                    }
                }
                catch { }
            }
            return hadStartup;
        }

        public static void CopySelf()
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath, InstalledExe, overwrite: true);
        }

        public static void CreateShortcuts(bool desktop)
        {
            MakeShortcut(StartMenuLink);
            if (desktop) MakeShortcut(DesktopLink);
            else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);
        }

        static void MakeShortcut(string path)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Description = "Températures et ventilateurs";
            lnk.Save();
        }

        public static void RegisterUninstall()
        {
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "Strix Monitor");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Strix Monitor");
            k.SetValue("DisplayIcon", InstalledExe + ",0");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        /// <summary>Télécharge l'installateur officiel PawnIO, vérifie son empreinte, puis l'installe en silencieux.</summary>
        /// <returns>null si OK, sinon un message d'erreur. needsReboot passe à true si Windows demande un redémarrage.</returns>
        public static async Task<(string error, bool needsReboot)> InstallPawnIO(IProgress<double> progress)
        {
            string file = Path.Combine(Path.GetTempPath(), "PawnIO_setup_" + Guid.NewGuid().ToString("N")[..8] + ".exe");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("StrixMonitor-Setup/" + Version);
                    using var resp = await http.GetAsync(PawnIOUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 3_400_000;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(file);
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        progress.Report(Math.Min(1, (double)done / total));
                    }
                }

                string hash;
                using (var fs = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(fs));
                if (!hash.Equals(PawnIOSha256, StringComparison.OrdinalIgnoreCase))
                    return ("le fichier téléchargé ne correspond pas à la version officielle (empreinte différente).", false);

                using var p = Process.Start(new ProcessStartInfo(file, "-install -silent") { UseShellExecute = false, CreateNoWindow = true });
                await p.WaitForExitAsync();
                return p.ExitCode switch
                {
                    0 => (null, false),
                    3010 => (null, true),
                    _ => ($"l'installateur PawnIO a renvoyé le code {p.ExitCode}.", false),
                };
            }
            catch (Exception ex)
            {
                Program.Log("PawnIO: " + ex);
                return ("téléchargement impossible (" + ex.Message + ").", false);
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        public static void Launch() =>
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });

        public static int Uninstall(bool quiet)
        {
            if (!quiet)
            {
                var r = MessageBox.Show(
                    "Désinstaller Strix Monitor ?\n\nLes ventilateurs repasseront en mode automatique. " +
                    "Le pilote PawnIO reste installé, car d'autres logiciels peuvent s'en servir (il se désinstalle à part depuis « Applications installées »).",
                    "Strix Monitor", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK) return 1;
            }

            StopOtherInstances(); // l'arrêt du widget remet les ventilateurs en Auto côté BIOS au prochain cycle
            try { Startup.Set(false); } catch { }
            foreach (var f in new[] { StartMenuLink, DesktopLink })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Directory.Delete(Program.DataDir, true); } catch { }

            // L'exe en cours ne peut pas s'effacer lui-même : on confie la suppression du dossier à cmd, 3 s plus tard.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });

            if (!quiet) MessageBox.Show("Strix Monitor a été désinstallé.", "Strix Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
    }
}

