using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace StrixMonitor
{
    public enum Status { Unknown, Ok, Warm, Hot }
    public enum Kind { Cpu, Gpu, Memory, Storage, Board }

    /// <summary>Seuils "normal / à surveiller / trop chaud" propres à chaque type de composant.</summary>
    public sealed class Range
    {
        public double Warm, Hot;
        public string Note;

        public static Range Cpu(string name) => name.Contains("Intel")
            ? new Range { Warm = 85, Hot = 95, Note = "Intel : jusqu'à 85 °C en charge, c'est normal. Il ralentit vers 100 °C." }
            : new Range { Warm = 80, Hot = 90, Note = "Ryzen : 40–55 °C au repos, jusqu'à 80 °C en charge, c'est normal. Il ralentit vers 95 °C." };
        public static readonly Range GpuCore = new() { Warm = 76, Hot = 86, Note = "GPU : 30–45 °C au repos, 60–75 °C en jeu. Au-delà de 85 °C, vérifier le flux d'air." };
        public static readonly Range GpuHotspot = new() { Warm = 90, Hot = 102, Note = "Point chaud : souvent 10–15 °C de plus que le cœur. Limite vers 105–110 °C." };
        public static readonly Range GpuMemory = new() { Warm = 90, Hot = 100, Note = "Mémoire vidéo : jusqu'à 90 °C, c'est normal. Ralentit vers 105 °C." };
        public static readonly Range Ram = new() { Warm = 60, Hot = 75, Note = "RAM DDR5 : normal sous 60 °C, ralentit vers 85 °C." };
        public static readonly Range Storage = new() { Warm = 70, Hot = 80, Note = "SSD NVMe : 30–50 °C au repos, jusqu'à 70 °C en charge c'est normal. Il ralentit vers 80 °C." };
        public static readonly Range Board = new() { Warm = 65, Hot = 85, Note = "Carte mère / VRM : normal sous 65 °C." };
    }

    public sealed class TempItem
    {
        public const int Capacity = 120; // 2 minutes à 1 mesure/s
        public ISensor Sensor;
        public string Label;
        public Kind Kind;
        public Range Range;
        public double Value = double.NaN, Min = double.NaN, Max = double.NaN;
        public readonly List<double> History = new();

        public Status Status => double.IsNaN(Value) ? Status.Unknown
            : Value >= Range.Hot ? Status.Hot
            : Value >= Range.Warm ? Status.Warm : Status.Ok;

        public void Sample()
        {
            float? v = Sensor.Value;
            if (v is > 0 and < 125)
            {
                Value = v.Value;
                if (double.IsNaN(Min) || Value < Min) Min = Value;
                if (double.IsNaN(Max) || Value > Max) Max = Value;
                History.Add(Value);
                if (History.Count > Capacity) History.RemoveAt(0);
            }
            else Value = double.NaN;
        }
    }

    public sealed class TempGroup
    {
        public Kind Kind;
        public string Title, Hardware;
        public TempItem Primary;
        public readonly List<TempItem> Items = new();
    }

    public enum FanMode { Auto, Curve, Fixed }
    public enum FanSource { Cpu, Gpu, Both }

    public sealed class FanItem
    {
        public string Name, Hardware, Key;
        public ISensor Rpm, ControlSensor;
        public IControl Control => ControlSensor?.Control;
        public bool CanControl => Control != null;
        public bool IsPump => Name.StartsWith("Pompe");
        public double MinPercent => IsPump ? 60 : 20;

        public FanMode Mode = FanMode.Auto;
        public int FixedPercent = 50;
        public string Preset = FanCurves.Default;
        public FanSource Source = FanSource.Both;

        public double SourceTemp = double.NaN; // température suivie (mode Courbe)
        public double Applied = double.NaN;    // dernier % envoyé (NaN = géré par le BIOS)
    }

    /// <summary>Profils de ventilation : points (température → %), interpolés linéairement.</summary>
    public static class FanCurves
    {
        public const string Default = "Équilibré";
        public static readonly string[] Names = { "Silencieux", "Équilibré", "Performance" };

        static readonly (double t, double p)[][] Points =
        {
            new[] { (40.0, 20.0), (60.0, 30.0), (72.0, 50.0), (85.0, 100.0) },
            new[] { (35.0, 30.0), (55.0, 40.0), (68.0, 60.0), (80.0, 100.0) },
            new[] { (30.0, 45.0), (50.0, 60.0), (62.0, 80.0), (75.0, 100.0) },
        };

        /// <summary>Un GPU tourne naturellement plus chaud qu'un CPU : sa courbe est décalée de +5 °C.</summary>
        public static double Offset(FanSource s) => s == FanSource.Gpu ? 5 : 0;

        public static (double t, double p)[] Of(string preset, FanSource s)
        {
            int i = Math.Max(0, Array.IndexOf(Names, preset));
            double o = Offset(s);
            return Points[i].Select(x => (x.t + o, x.p)).ToArray();
        }

        public static double Eval(string preset, FanSource s, double t)
        {
            var pts = Of(preset, s);
            if (t <= pts[0].t) return pts[0].p;
            for (int i = 1; i < pts.Length; i++)
                if (t <= pts[i].t)
                    return pts[i - 1].p + (pts[i].p - pts[i - 1].p) * (t - pts[i - 1].t) / (pts[i].t - pts[i - 1].t);
            return 100;
        }

        public static string SourceLabel(FanSource s) => s switch
        {
            FanSource.Cpu => "le CPU",
            FanSource.Gpu => "le GPU",
            _ => "le plus chaud (CPU / GPU)",
        };
    }

    sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer c) => c.Traverse(this);
        public void VisitHardware(IHardware h) { h.Update(); foreach (var s in h.SubHardware) s.Accept(this); }
        public void VisitSensor(ISensor s) { }
        public void VisitParameter(IParameter p) { }
    }

    public sealed class SensorService : IDisposable
    {
        readonly Computer _pc;
        readonly ConcurrentQueue<Action> _pending = new();
        readonly object _lock = new();
        public readonly List<TempGroup> Groups = new();
        public readonly List<FanItem> Fans = new();
        /// <summary>Charge (%) et consommation (W) du CPU et du GPU : enregistrées pendant les sessions, pour l'analyse.</summary>
        public readonly List<(string group, string name, string unit, ISensor sensor)> Extras = new();

        public SensorService()
        {
            _pc = NewComputer();
            _pc.Open();
            _pc.Accept(new UpdateVisitor());
            Discover();
        }

        internal static Computer NewComputer() => new()
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
            IsMotherboardEnabled = true, IsStorageEnabled = true, IsControllerEnabled = true,
        };

        /// <summary>Appelé hors du thread UI : applique les commandes ventilateur en attente puis relit les capteurs.</summary>
        public void Update()
        {
            lock (_lock)
            {
                while (_pending.TryDequeue(out var a))
                {
                    try { a(); } catch (Exception ex) { Program.Log("Fan: " + ex.Message); }
                }
                _pc.Accept(new UpdateVisitor());
            }
        }

        public void Sample()
        {
            foreach (var g in Groups) foreach (var i in g.Items) i.Sample();
        }

        public void SetMode(FanItem f, FanMode mode)
        {
            f.Mode = mode;
            if (mode == FanMode.Auto) GiveBackToBios(f);
        }

        void GiveBackToBios(FanItem f)
        {
            var c = f.Control;
            if (c == null) return;
            f.Applied = double.NaN;
            _pending.Enqueue(() => c.SetDefault());
        }

        double Primary(Kind k) => Groups.FirstOrDefault(g => g.Kind == k)?.Primary?.Value ?? double.NaN;

        /// <summary>Calcule et envoie la consigne de chaque ventilateur (à appeler après Sample, une fois par seconde).</summary>
        public void TickFans()
        {
            double cpu = Primary(Kind.Cpu), gpu = Primary(Kind.Gpu);
            foreach (var f in Fans.Where(f => f.CanControl && f.Mode != FanMode.Auto))
            {
                double target;
                if (f.Mode == FanMode.Fixed) target = f.FixedPercent;
                else
                {
                    f.SourceTemp = f.Source switch
                    {
                        FanSource.Cpu => cpu,
                        FanSource.Gpu => gpu,
                        _ => double.IsNaN(gpu) ? cpu : double.IsNaN(cpu) ? gpu - 5 : Math.Max(cpu, gpu - 5),
                    };
                    if (double.IsNaN(f.SourceTemp))
                    {
                        // Sonde perdue : on rend la main au BIOS plutôt que de rester bloqué sur une vieille valeur.
                        if (!double.IsNaN(f.Applied)) GiveBackToBios(f);
                        continue;
                    }
                    target = FanCurves.Eval(f.Preset, f.Source, f.SourceTemp);
                    // Monte tout de suite, redescend doucement (3 %/s) pour éviter le "pompage".
                    if (!double.IsNaN(f.Applied) && target < f.Applied) target = Math.Max(target, f.Applied - 3);
                }
                target = Math.Round(Math.Clamp(target, f.MinPercent, 100));
                if (double.IsNaN(f.Applied) || Math.Abs(target - f.Applied) >= 1)
                {
                    var c = f.Control;
                    float v = (float)target;
                    _pending.Enqueue(() => c.SetSoftware(v));
                    f.Applied = target;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var f in Fans.Where(f => f.CanControl))
                {
                    try { f.Control.SetDefault(); } catch { }
                }
                try { _pc.Close(); } catch { }
            }
        }

        static IEnumerable<IHardware> Tree(IHardware h)
        {
            yield return h;
            foreach (var s in h.SubHardware)
                foreach (var x in Tree(s)) yield return x;
        }

        static List<ISensor> Temps(IHardware h) => Tree(h).SelectMany(x => x.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125).ToList();

        static TempItem Make(ISensor s, string label, Kind k, Range r) => new() { Sensor = s, Label = label, Kind = k, Range = r };

        void Discover()
        {
            TempGroup ram = null, disks = null;
            foreach (var hw in _pc.Hardware)
            {
                var temps = Temps(hw);
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu when temps.Count > 0:
                    {
                        var p = temps.FirstOrDefault(s => s.Name.Contains("Tctl"))
                             ?? temps.FirstOrDefault(s => s.Name.Contains("Package"))
                             ?? temps.OrderByDescending(s => s.Value).First();
                        var range = Range.Cpu(hw.Name);
                        var g = new TempGroup { Kind = Kind.Cpu, Title = "Processeur", Hardware = hw.Name };
                        g.Primary = Make(p, "Température", Kind.Cpu, range);
                        g.Items.Add(g.Primary);
                        foreach (var s in temps.Where(s => s.Name.StartsWith("CCD") && s != p).Take(2))
                            g.Items.Add(Make(s, s.Name.Replace(" (Tdie)", "") + " (puce des cœurs)", Kind.Cpu, range));
                        Groups.Add(g);
                        break;
                    }
                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when temps.Count > 0:
                    {
                        var core = temps.FirstOrDefault(s => s.Name == "GPU Core");
                        bool integrated = hw.HardwareType != HardwareType.GpuNvidia && hw.Name.Contains("(TM) Graphics");
                        if (core == null && integrated) break; // GPU intégré sans vraie sonde de cœur : sans intérêt
                        core ??= temps[0];
                        var g = new TempGroup { Kind = Kind.Gpu, Title = integrated ? "GPU intégré" : "Carte graphique", Hardware = hw.Name };
                        g.Primary = Make(core, "Cœur", Kind.Gpu, Range.GpuCore);
                        g.Items.Add(g.Primary);
                        var hot = temps.FirstOrDefault(s => s.Name.Contains("Hot Spot"));
                        if (hot != null) g.Items.Add(Make(hot, "Point chaud", Kind.Gpu, Range.GpuHotspot));
                        var mem = temps.FirstOrDefault(s => s.Name.Contains("Memory"));
                        if (mem != null) g.Items.Add(Make(mem, "Mémoire vidéo", Kind.Gpu, Range.GpuMemory));
                        Groups.Add(g);
                        break;
                    }
                    case HardwareType.Memory when temps.Count > 0:
                    {
                        var model = hw.Name.Split(" (#")[0].Replace(" - ", " ");
                        if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9 \-]+$")) model = "";
                        ram ??= new TempGroup { Kind = Kind.Memory, Title = "Mémoire vive", Hardware = model };
                        var t = temps.FirstOrDefault(s => s.Name.StartsWith("DIMM")) ?? temps[0];
                        ram.Items.Add(Make(t, t.Name.Replace("DIMM #", "Barrette "), Kind.Memory, Range.Ram));
                        break;
                    }
                    case HardwareType.Storage when temps.Count > 0:
                    {
                        disks ??= new TempGroup { Kind = Kind.Storage, Title = "Stockage", Hardware = "" };
                        var t = temps.FirstOrDefault(s => s.Name.Contains("Composite"))
                             ?? temps.FirstOrDefault(s => s.Name == "Temperature")
                             ?? temps.First(s => !s.Name.Contains("Warning") && !s.Name.Contains("Critical"));
                        disks.Items.Add(Make(t, hw.Name, Kind.Storage, Range.Storage));
                        break;
                    }
                    case HardwareType.Motherboard when temps.Count > 0:
                    {
                        var g = new TempGroup { Kind = Kind.Board, Title = "Carte mère", Hardware = hw.Name };
                        // "CPU" doublonne la sonde du processeur ; PCIe/M2 sont souvent des sondes non branchées ou fantaisistes.
                        foreach (var s in temps.Where(s => s.Name != "CPU" && !s.Name.StartsWith("PCIe") && !s.Name.StartsWith("M2")).Take(5))
                            g.Items.Add(Make(s, BoardLabel(s.Name), Kind.Board, BoardRange(s.Name)));
                        if (g.Items.Count > 0) Groups.Add(g);
                        break;
                    }
                }
                DiscoverFans(hw);
            }
            // Connecteurs de carte mère sans ventilateur branché (0 tr/min au démarrage) : masqués.
            HiddenFans = Fans.RemoveAll(f => f.Rpm != null && (f.Rpm.Value ?? 0) < 1 && !f.Hardware.Contains("GeForce") && !f.Hardware.Contains("Radeon"));
            if (ram != null) Groups.Add(ram);
            if (disks != null) Groups.Add(disks);
            Groups.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind)
                : (a.Title == "GPU intégré").CompareTo(b.Title == "GPU intégré"));
            foreach (var g in Groups) g.Primary ??= g.Items.OrderByDescending(i => i.Sensor.Value).First();

            foreach (var hw in _pc.Hardware)
            {
                ISensor Find(SensorType t, params string[] names) =>
                    names.Select(n => hw.Sensors.FirstOrDefault(s => s.SensorType == t && s.Name == n)).FirstOrDefault(s => s != null);
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    if (Find(SensorType.Load, "CPU Total") is { } l) Extras.Add(("Processeur", "Charge", "%", l));
                    if (Find(SensorType.Power, "Package", "CPU Package") is { } p) Extras.Add(("Processeur", "Consommation", "W", p));
                }
                else if (Groups.Any(g => g.Kind == Kind.Gpu && g.Hardware == hw.Name && g.Title != "GPU intégré"))
                {
                    if (Find(SensorType.Load, "GPU Core") is { } l) Extras.Add(("Carte graphique", "Charge", "%", l));
                    if (Find(SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power") is { } p) Extras.Add(("Carte graphique", "Consommation", "W", p));
                }
            }
        }

        public int HiddenFans;

        static string BoardLabel(string n) => n switch
        {
            "System" => "Boîtier (air ambiant)",
            "VRM MOS" => "VRM (alim. du CPU)",
            "PCH" => "Chipset",
            "CPU Socket" => "Socket CPU",
            "Motherboard" => "Carte mère",
            _ => n.Replace("Temperature #", "Sonde ")
        };

        static Range BoardRange(string n) => n switch
        {
            "System" => new Range { Warm = 45, Hot = 55, Note = "Air dans le boîtier : 25–40 °C est normal. Au-delà, le flux d'air est insuffisant." },
            "VRM MOS" => new Range { Warm = 85, Hot = 105, Note = "VRM (étage d'alimentation du CPU) : jusqu'à 85 °C c'est normal, ils supportent ~110 °C." },
            "PCH" => new Range { Warm = 70, Hot = 85, Note = "Chipset : 35–60 °C est normal." },
            "CPU Socket" => new Range { Warm = 70, Hot = 85, Note = "Dessous du socket CPU : normalement 10–20 °C de moins que le CPU." },
            _ => Range.Board,
        };

        void DiscoverFans(IHardware root)
        {
            foreach (var hw in Tree(root))
            {
                var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
                var controls = hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null).ToList();
                var used = new HashSet<ISensor>();
                foreach (var c in controls)
                {
                    var f = fans.FirstOrDefault(x => x.Index == c.Index && !used.Contains(x));
                    if (f != null) used.Add(f);
                    Fans.Add(new FanItem
                    {
                        Name = FanLabel(f?.Name ?? c.Name), Hardware = hw.Name, Rpm = f, ControlSensor = c,
                        Key = c.Identifier.ToString(),
                        Source = root.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ? FanSource.Gpu
                               : (f?.Name ?? c.Name) is "CPU Fan" or "Pump Fan" ? FanSource.Cpu : FanSource.Both,
                    });
                }
                foreach (var f in fans.Where(x => !used.Contains(x)))
                    Fans.Add(new FanItem { Name = FanLabel(f.Name), Hardware = hw.Name, Rpm = f, Key = f.Identifier.ToString() });
            }
        }

        static string FanLabel(string n) => n switch
        {
            "CPU Fan" => "Ventilateur CPU",
            "Pump Fan" => "Pompe (AIO)",
            "GPU Fan" => "Ventilateurs GPU",
            _ when n.StartsWith("System Fan #") => "Ventilateur boîtier " + n[12..],
            _ when n.StartsWith("GPU Fan ") => "Ventilateur GPU " + n[8..],
            _ => n.Replace("Fan Control #", "Ventilateur ").Replace("Fan #", "Ventilateur "),
        };
    }

    /// <summary>StrixMonitor.exe --dump [fichier] : écrit tous les capteurs détectés (diagnostic).</summary>
    static class Dump
    {
        public static int Run(string path)
        {
            var sb = new StringBuilder();
            try
            {
                var pc = SensorService.NewComputer();
                pc.Open();
                pc.Accept(new UpdateVisitor());
                void Walk(IHardware h, int d)
                {
                    sb.AppendLine($"{new string(' ', d * 2)}[{h.HardwareType}] {h.Name}");
                    foreach (var s in h.Sensors)
                        sb.AppendLine($"{new string(' ', d * 2 + 2)}{s.SensorType,-12} #{s.Index,-2} {s.Name,-30} = {s.Value}{(s.Control != null ? $"   (pilotable, mode {s.Control.ControlMode})" : "")}");
                    foreach (var sub in h.SubHardware) Walk(sub, d + 1);
                }
                foreach (var h in pc.Hardware) Walk(h, 0);
                pc.Close();
            }
            catch (Exception ex) { sb.AppendLine("ERREUR: " + ex); }
            File.WriteAllText(path, sb.ToString());
            return 0;
        }
    }
}
