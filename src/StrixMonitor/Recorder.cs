using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StrixMonitor
{
    /// <summary>Description d'une colonne enregistrée (stockée dans l'en-tête du fichier).</summary>
    public sealed class Column
    {
        public string Group, Name, Unit;
        public double Warm = double.NaN;

        public string Header => string.Join("|", Group, Name, Unit, double.IsNaN(Warm) ? "" : Warm.ToString(CultureInfo.InvariantCulture));

        public static Column Parse(string h)
        {
            var p = h.Split('|');
            return new Column
            {
                Group = p.ElementAtOrDefault(0) ?? "", Name = p.ElementAtOrDefault(1) ?? h, Unit = p.ElementAtOrDefault(2) ?? "",
                Warm = double.TryParse(p.ElementAtOrDefault(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : double.NaN,
            };
        }
    }

    /// <summary>Une session enregistrée (un fichier CSV).</summary>
    public sealed class SessionInfo
    {
        public string File;
        public DateTime Start;
        public TimeSpan Duration;
        public string Label => $"{Start:dddd d MMMM, HH:mm}".ToLower(Theme.Culture) + $" · {Recorder.FormatDuration(Duration)}";
    }

    /// <summary>
    /// Sessions d'enregistrement manuelles (benchmark, test de charge…) : une mesure par seconde de toutes les
    /// températures, des ventilateurs, et de la charge / consommation CPU et GPU, dans
    /// %APPDATA%\StrixMonitor\sessions\2026-11-07_23-45-10.csv. S'arrête seule au bout de 3 h.
    /// </summary>
    public sealed class Recorder
    {
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(3);
        public static string Dir => Path.Combine(Program.DataDir, "sessions");

        readonly List<(Column col, Func<double> read)> _cols = new();
        string _file;
        DateTime _start;
        StreamWriter _writer;

        public bool IsRecording => _writer != null;
        public TimeSpan Elapsed => IsRecording ? DateTime.Now - _start : TimeSpan.Zero;

        public Recorder(SensorService svc)
        {
            foreach (var g in svc.Groups)
                foreach (var i in g.Items)
                {
                    var item = i;
                    _cols.Add((new Column { Group = g.Title, Name = i.Label, Unit = "°C", Warm = i.Range.Warm }, () => item.Value));
                }
            foreach (var (group, name, unit, sensor) in svc.Extras)
            {
                var s = sensor;
                _cols.Add((new Column { Group = group, Name = name, Unit = unit }, () => s.Value is float v ? v : double.NaN));
            }
            foreach (var f in svc.Fans.Where(f => f.Rpm != null))
            {
                var fan = f;
                _cols.Add((new Column { Group = "Ventilateurs", Name = f.Name, Unit = "tr/min" }, () => fan.Rpm.Value is float v ? v : double.NaN));
            }
        }

        public void Start()
        {
            if (IsRecording) return;
            Directory.CreateDirectory(Dir);
            _start = DateTime.Now;
            _file = Path.Combine(Dir, _start.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".csv");
            _writer = new StreamWriter(new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _writer.Write("secondes;" + string.Join(";", _cols.Select(c => c.col.Header)) + "\n");
            Tick();
        }

        /// <summary>Arrête la session ; renvoie le fichier enregistré.</summary>
        public string Stop()
        {
            if (!IsRecording) return null;
            try { _writer.Dispose(); } catch { }
            _writer = null;
            return _file;
        }

        /// <summary>À appeler une fois par seconde, après la lecture des capteurs.</summary>
        public void Tick()
        {
            if (!IsRecording) return;
            try
            {
                var sb = new StringBuilder(Math.Round((DateTime.Now - _start).TotalSeconds, 1).ToString("0.#", CultureInfo.InvariantCulture));
                foreach (var (_, read) in _cols)
                {
                    sb.Append(';');
                    double v = read();
                    if (!double.IsNaN(v)) sb.Append(v.ToString("0.#", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
                _writer.Write(sb.ToString());
                _writer.Flush();
            }
            catch (Exception ex) { Program.Log("Session : " + ex.Message); Stop(); }
        }

        public static string FormatDuration(TimeSpan d) =>
            d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00}" : $"{d.Seconds} s";

        // ───────────── Lecture (fenêtre d'analyse) ─────────────

        public static List<SessionInfo> List()
        {
            if (!Directory.Exists(Dir)) return new List<SessionInfo>();
            var list = new List<SessionInfo>();
            foreach (var f in Directory.GetFiles(Dir, "*.csv"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    continue;
                double last = 0;
                try
                {
                    string lastLine = ReadLinesShared(f).LastOrDefault();
                    if (lastLine != null) double.TryParse(lastLine.Split(';')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out last);
                }
                catch { }
                list.Add(new SessionInfo { File = f, Start = start, Duration = TimeSpan.FromSeconds(last) });
            }
            return list.OrderByDescending(s => s.Start).ToList();
        }

        public sealed class Series
        {
            public Column Col;
            public double[] T, V; // secondes depuis le début, valeur
            public double Min = double.PositiveInfinity, Max = double.NegativeInfinity, Mean;
        }

        public static List<Series> Load(string file)
        {
            string[] header = null;
            var t = new List<double>();
            List<double>[] cols = null;
            foreach (var line in ReadLinesShared(file))
            {
                var parts = line.Split(';');
                if (header == null)
                {
                    header = parts;
                    cols = Enumerable.Range(0, header.Length).Select(_ => new List<double>()).ToArray();
                    continue;
                }
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) continue;
                t.Add(sec);
                for (int i = 1; i < header.Length; i++)
                    cols[i].Add(i < parts.Length && parts[i].Length > 0 && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN);
            }
            var result = new List<Series>();
            if (header == null) return result;
            var times = t.ToArray();
            for (int i = 1; i < header.Length; i++)
            {
                var s = new Series { Col = Column.Parse(header[i]), T = times, V = cols[i].ToArray() };
                var valid = s.V.Where(v => !double.IsNaN(v)).ToList();
                if (valid.Count == 0) continue;
                s.Min = valid.Min(); s.Max = valid.Max(); s.Mean = valid.Average();
                result.Add(s);
            }
            return result;
        }

        static IEnumerable<string> ReadLinesShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            string line;
            while ((line = r.ReadLine()) != null)
                if (line.Length > 0) yield return line;
        }
    }
}
