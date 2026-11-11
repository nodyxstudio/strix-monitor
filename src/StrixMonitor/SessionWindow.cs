using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StrixMonitor
{
    /// <summary>Analyse d'une session enregistrée : un graphique par composant et par unité, avec min / moyenne / max.</summary>
    public sealed class SessionWindow : Window
    {
        static readonly Color[] Palette =
        {
            Color.FromRgb(0xEC, 0xEA, 0xE6), Color.FromRgb(0x8A, 0xB4, 0xE8), Color.FromRgb(0xB9, 0xA3, 0xE3),
            Color.FromRgb(0x7C, 0xC4, 0xB4), Color.FromRgb(0xE3, 0xA0, 0xB0), Color.FromRgb(0xC8, 0xC0, 0x8A),
        };

        public string PreviewPath; // --session-preview
        List<SessionInfo> _sessions = new();
        int _index;
        readonly TextBlock _title = new() { FontFamily = Theme.FontDisplay, FontSize = 18, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Theme.Brush(Theme.Muted), Margin = new Thickness(0, 2, 0, 0) };
        readonly StackPanel _content = new();
        readonly TextBlock _prev, _next;

        public SessionWindow(string file = null)
        {
            Title = "Strix Monitor · Analyse";
            Width = 780; Height = 720; MinWidth = 560; MinHeight = 400;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Theme.Brush(Color.FromRgb(0x17, 0x19, 0x1C));
            BorderBrush = Theme.Brush(Theme.Line);
            BorderThickness = new Thickness(1);
            FontFamily = Theme.Font;
            Foreground = Theme.Brush(Theme.Text);
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var dock = new DockPanel();
            Content = dock;

            // En-tête
            var header = new Grid { Margin = new Thickness(22, 16, 14, 10), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel();
            titles.Children.Add(_title);
            titles.Children.Add(_subtitle);
            header.Children.Add(titles);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            _prev = Tool("", "Session précédente (plus ancienne)", () => Show(_index + 1));
            _next = Tool("", "Session suivante (plus récente)", () => Show(_index - 1));
            tools.Children.Add(_prev);
            tools.Children.Add(_next);
            tools.Children.Add(Tool("", "Ouvrir le dossier des sessions", () =>
            {
                Directory.CreateDirectory(Recorder.Dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Recorder.Dir}\"") { UseShellExecute = true });
            }));
            tools.Children.Add(Tool("", "Supprimer cette session", DeleteCurrent));
            tools.Children.Add(Tool("", "Fermer", Close));
            Grid.SetColumn(tools, 1);
            header.Children.Add(tools);
            header.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else try { DragMove(); } catch { }
            };
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            var sep = new Border { Height = 1, Background = Theme.Brush(Theme.Line) };
            DockPanel.SetDock(sep, Dock.Top);
            dock.Children.Add(sep);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content, Padding = new Thickness(22, 6, 22, 22) };
            dock.Children.Add(scroll);

            Loaded += async (_, _) =>
            {
                _sessions = Recorder.List();
                int i = file == null ? 0 : _sessions.FindIndex(s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase));
                Show(Math.Max(0, i));
                if (PreviewPath != null)
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    SavePng(PreviewPath);
                    Close();
                }
            };
        }

        static TextBlock Tool(string glyph, string tip, Action onClick)
        {
            var b = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12,
                Foreground = Theme.Brush(Theme.Muted), Padding = new Thickness(9, 7, 9, 7), Cursor = Cursors.Hand,
                ToolTip = tip, Background = Brushes.Transparent,
            };
            b.MouseEnter += (_, _) => { if (b.IsEnabled) b.Foreground = Theme.Brush(Theme.Text); };
            b.MouseLeave += (_, _) => b.Foreground = Theme.Brush(b.IsEnabled ? Theme.Muted : Theme.Line);
            b.IsEnabledChanged += (_, _) => b.Foreground = Theme.Brush(b.IsEnabled ? Theme.Muted : Theme.Line);
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        void DeleteCurrent()
        {
            if (_sessions.Count == 0) return;
            var s = _sessions[_index];
            if (MessageBox.Show(this, $"Supprimer la session du {s.Label} ?", "Strix Monitor", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try { File.Delete(s.File); } catch { }
            _sessions = Recorder.List();
            Show(Math.Min(_index, Math.Max(0, _sessions.Count - 1)));
        }

        void Show(int index)
        {
            _content.Children.Clear();
            _prev.IsEnabled = index < _sessions.Count - 1;
            _next.IsEnabled = index > 0;
            if (_sessions.Count == 0)
            {
                _title.Text = "Aucune session";
                _subtitle.Text = "Clique sur ● dans le widget pour enregistrer une session (benchmark, partie, test de charge…).";
                return;
            }
            _index = Math.Clamp(index, 0, _sessions.Count - 1);
            var info = _sessions[_index];
            _title.Text = "Session du " + info.Start.ToString("d MMMM yyyy, HH:mm", Theme.Culture);
            _subtitle.Text = $"Durée {Recorder.FormatDuration(info.Duration)} · une mesure par seconde · {_index + 1} / {_sessions.Count}";

            List<Recorder.Series> series;
            try { series = Recorder.Load(info.File); }
            catch (Exception ex)
            {
                _content.Children.Add(new TextBlock { Text = "Lecture impossible : " + ex.Message, Foreground = Theme.Brush(Theme.Hot) });
                return;
            }
            if (series.Count == 0 || series[0].T.Length < 2)
            {
                _content.Children.Add(new TextBlock { Text = "Session trop courte pour être analysée.", Foreground = Theme.Brush(Theme.Muted), Margin = new Thickness(0, 14, 0, 0) });
                return;
            }

            _content.Children.Add(Highlights(series));
            foreach (var group in series.GroupBy(s => s.Col.Group))
                _content.Children.Add(GroupCard(group.Key, group.ToList()));
        }

        /// <summary>Résumé en haut : pic de chaque composant principal, avec son état.</summary>
        UIElement Highlights(List<Recorder.Series> series)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
            foreach (var g in series.Where(s => s.Col.Unit == "°C").GroupBy(s => s.Col.Group))
            {
                var main = g.First();
                var c = !double.IsNaN(main.Col.Warm) && main.Max >= main.Col.Warm ? Theme.Warm : Theme.Ok;
                var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 10) };
                sp.Children.Add(new TextBlock { Text = g.Key, FontSize = 11.5, Foreground = Theme.Brush(Theme.Muted) });
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = Theme.Deg(main.Max, "0"), FontFamily = Theme.FontDisplay, FontSize = 24, FontWeight = FontWeights.Light });
                line.Children.Add(new TextBlock { Text = " max", FontSize = 11.5, Foreground = Theme.Brush(Theme.Faint), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 5) });
                sp.Children.Add(line);
                var status = new StackPanel { Orientation = Orientation.Horizontal };
                status.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Theme.Brush(c), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                status.Children.Add(new TextBlock { Text = c == Theme.Ok ? "Resté normal" : "A dépassé le seuil", FontSize = 11.5, Foreground = Theme.Brush(c) });
                sp.Children.Add(status);
                wrap.Children.Add(sp);
            }
            return wrap;
        }

        UIElement GroupCard(string group, List<Recorder.Series> list)
        {
            var root = new StackPanel();
            var card = new Border
            {
                Background = Theme.Brush(Theme.Surface), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 12, 0, 0), Child = root,
            };
            root.Children.Add(new TextBlock { Text = group, FontSize = 13.5, FontWeight = FontWeights.SemiBold });

            foreach (var unitGroup in list.GroupBy(s => s.Col.Unit))
            {
                var items = unitGroup.ToList();
                string unit = unitGroup.Key;
                var colored = items.Select((s, i) => (s, color: Palette[i % Palette.Length])).ToList();

                var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
                foreach (var (s, color) in colored)
                {
                    var l = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
                    l.Children.Add(new Border { Width = 10, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Brush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                    l.Children.Add(new TextBlock { Text = s.Col.Name + "  ", FontSize = 12 });
                    l.Children.Add(new TextBlock
                    {
                        Text = $"min {Fmt(s.Min, unit)} · moy {Fmt(s.Mean, unit)} · max {Fmt(s.Max, unit)}",
                        FontSize = 11.5, Foreground = Theme.Brush(Theme.Muted),
                    });
                    legend.Children.Add(l);
                }
                root.Children.Add(legend);

                var chart = new SessionChart
                {
                    Height = unit == "°C" ? 170 : 96, Unit = unit,
                    Series = colored.Select(x => (x.s.Col.Name, x.s.T, x.s.V, x.color)).ToList(),
                    Threshold = unit == "°C" ? items[0].Col.Warm : double.NaN,
                };
                root.Children.Add(chart);
            }
            return card;
        }

        public static string Fmt(double v, string unit) => double.IsNaN(v) ? "—" : unit switch
        {
            "°C" => Theme.Deg(v, "0"),
            "tr/min" => v.ToString("N0", Theme.Culture),
            "%" => v.ToString("0", Theme.Culture) + " %",
            "W" => v.ToString("0", Theme.Culture) + " W",
            _ => v.ToString("0.#", Theme.Culture) + " " + unit,
        };

        void SavePng(string path)
        {
            var el = (FrameworkElement)Content;
            el.UpdateLayout();
            const double k = 1.5;
            var rtb = new RenderTargetBitmap((int)(el.ActualWidth * k), (int)(el.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
            // Le fond appartient à la fenêtre, pas au contenu : on le peint d'abord.
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    /// <summary>Graphique temporel multi-séries, avec axes, seuil et lecture des valeurs au survol.</summary>
    public sealed class SessionChart : FrameworkElement
    {
        public List<(string name, double[] t, double[] v, Color color)> Series = new();
        public string Unit = "";
        public double Threshold = double.NaN;
        double _hoverX = double.NaN;

        const double L = 44, R = 10, T = 8, B = 20;

        protected override void OnMouseMove(MouseEventArgs e) { _hoverX = e.GetPosition(this).X; InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { _hoverX = double.NaN; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            if (Series.Count == 0 || w < 100) return;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tf = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText Txt(string s, Color c, double size = 10) => new(s, Theme.Culture, FlowDirection.LeftToRight, tf, size, Theme.Brush(c), dip);

            double t0 = Series.Min(s => s.t[0]), t1 = Math.Max(t0 + 1, Series.Max(s => s.t[^1]));
            var all = Series.SelectMany(s => s.v).Where(v => !double.IsNaN(v)).ToList();
            if (all.Count == 0) return;
            double lo = all.Min(), hi = all.Max();
            if (!double.IsNaN(Threshold) && Threshold - hi < 12 && Threshold > lo) hi = Math.Max(hi, Threshold + 1);
            if (Unit == "%") { lo = 0; hi = Math.Max(hi, 100); }
            double step = NiceStep((hi - lo) / 4);
            lo = Math.Floor(lo / step) * step; hi = Math.Ceiling(hi / step) * step;
            if (hi - lo < step) hi = lo + step;

            double PW = w - L - R, PH = h - T - B;
            double X(double t) => L + (t - t0) / (t1 - t0) * PW;
            double Y(double v) => T + (1 - (v - lo) / (hi - lo)) * PH;

            // Grille horizontale + valeurs
            var grid = new Pen(Theme.Brush(Theme.Line), 1);
            for (double v = lo; v <= hi + step / 2; v += step)
            {
                double y = Math.Round(Y(v)) + 0.5;
                dc.DrawLine(grid, new Point(L, y), new Point(w - R, y));
                var ft = Txt(SessionWindow.Fmt(v, Unit), Theme.Faint);
                dc.DrawText(ft, new Point(L - 6 - ft.Width, y - ft.Height / 2));
            }
            // Graduations de temps
            double ts = NiceTime((t1 - t0) / 6);
            for (double t = Math.Ceiling(t0 / ts) * ts; t <= t1; t += ts)
            {
                var ft = Txt(FormatTime(t), Theme.Faint);
                double x = X(t);
                if (x - ft.Width / 2 < L - 4 || x + ft.Width / 2 > w) continue;
                dc.DrawText(ft, new Point(x - ft.Width / 2, h - ft.Height));
            }
            // Seuil
            if (!double.IsNaN(Threshold) && Threshold >= lo && Threshold <= hi)
            {
                double y = Math.Round(Y(Threshold)) + 0.5;
                var dash = new Pen(Theme.Brush(Theme.Warm, 0x80), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                dc.DrawLine(dash, new Point(L, y), new Point(w - R, y));
            }
            // Courbes (coupées là où il manque des mesures)
            foreach (var (_, t, v, color) in Series)
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    bool open = false;
                    for (int i = 0; i < v.Length; i++)
                    {
                        if (double.IsNaN(v[i])) { open = false; continue; }
                        var p = new Point(X(t[i]), Y(v[i]));
                        if (!open) { c.BeginFigure(p, false, false); open = true; }
                        else c.LineTo(p, true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(Theme.Brush(color), 1.4) { LineJoin = PenLineJoin.Round }, geo);
            }
            // Survol : ligne verticale + valeurs
            if (!double.IsNaN(_hoverX) && _hoverX >= L && _hoverX <= w - R)
            {
                double th = t0 + (_hoverX - L) / PW * (t1 - t0);
                var lines = new List<(string text, Color color)> { (FormatTime(th), Theme.Muted) };
                dc.DrawLine(new Pen(Theme.Brush(Theme.Text, 0x50), 1), new Point(Math.Round(_hoverX) + 0.5, T), new Point(Math.Round(_hoverX) + 0.5, T + PH));
                foreach (var (name, t, v, color) in Series)
                {
                    int i = Array.BinarySearch(t, th);
                    if (i < 0) i = Math.Clamp(~i, 0, t.Length - 1);
                    if (i > 0 && Math.Abs(t[i - 1] - th) < Math.Abs(t[i] - th)) i--;
                    if (double.IsNaN(v[i])) continue;
                    dc.DrawEllipse(Theme.Brush(color), new Pen(Theme.Brush(Theme.Surface), 1.5), new Point(X(t[i]), Y(v[i])), 3.5, 3.5);
                    lines.Add(($"{name}  {SessionWindow.Fmt(v[i], Unit)}", color));
                }
                var fts = lines.Select(l => Txt(l.text, l.color == Theme.Muted ? Theme.Muted : Theme.Text, 11)).ToList();
                double bw = fts.Max(f => f.Width) + 22, bh = fts.Sum(f => f.Height) + 10;
                double bx = _hoverX + 12 + bw > w ? _hoverX - 12 - bw : _hoverX + 12, by = T + 4;
                dc.DrawRoundedRectangle(Theme.Brush(Color.FromRgb(0x2A, 0x2E, 0x33), 0xF0), new Pen(Theme.Brush(Theme.Line), 1), new Rect(bx, by, bw, bh), 6, 6);
                double yy = by + 5;
                for (int k = 0; k < fts.Count; k++)
                {
                    if (k > 0) dc.DrawRectangle(Theme.Brush(lines[k].color), null, new Rect(bx + 8, yy + fts[k].Height / 2 - 1, 6, 2));
                    dc.DrawText(fts[k], new Point(bx + (k > 0 ? 18 : 8), yy));
                    yy += fts[k].Height;
                }
            }
        }

        static double NiceStep(double raw)
        {
            foreach (var s in new double[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 })
                if (s >= raw) return s;
            return 10000;
        }

        static double NiceTime(double raw)
        {
            foreach (var s in new double[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 })
                if (s >= raw) return s;
            return 7200;
        }

        static string FormatTime(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }
}
