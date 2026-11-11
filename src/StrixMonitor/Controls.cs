using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StrixMonitor
{
    /// <summary>Palette sobre : fond graphite, texte cassé, trois couleurs d'état désaturées.</summary>
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0xF4, 0x17, 0x19, 0x1C);
        public static readonly Color Surface = Color.FromRgb(0x20, 0x23, 0x27);
        public static readonly Color Line = Color.FromRgb(0x2D, 0x31, 0x36);
        public static readonly Color Text = Color.FromRgb(0xEC, 0xEA, 0xE6);
        public static readonly Color Muted = Color.FromRgb(0x8C, 0x91, 0x97);
        public static readonly Color Faint = Color.FromRgb(0x5A, 0x5F, 0x66);
        public static readonly Color Ok = Color.FromRgb(0x86, 0xB8, 0x8A);
        public static readonly Color Warm = Color.FromRgb(0xDB, 0xA9, 0x5C);
        public static readonly Color Hot = Color.FromRgb(0xE0, 0x6E, 0x5E);

        public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily FontDisplay = new("Segoe UI Variable Display, Segoe UI");
        public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");

        public static SolidColorBrush Brush(Color c, byte alpha = 0xFF)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static Color Of(Status s) => s switch
        {
            Status.Hot => Hot,
            Status.Warm => Warm,
            Status.Ok => Ok,
            _ => Faint,
        };

        public static string Word(Status s) => s switch
        {
            Status.Hot => "Trop chaud",
            Status.Warm => "À surveiller",
            Status.Ok => "Normal",
            _ => "Indisponible",
        };

        public static string Deg(double v, string fmt = "0.0") =>
            double.IsNaN(v) ? "—" : v.ToString(fmt, Culture) + "°";
    }

    /// <summary>Courbe d'historique simple, avec ligne pointillée au seuil "à surveiller".</summary>
    public sealed class LineChart : FrameworkElement
    {
        public IList<double> Data;
        public double Threshold = double.NaN;
        public bool ShowThreshold;
        public Color Color = Theme.Ok;
        public int Capacity = TempItem.Capacity;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            if (Data == null || Data.Count < 2)
            {
                var p = new Pen(Theme.Brush(Theme.Line), 1);
                dc.DrawLine(p, new Point(0, h - 1), new Point(w, h - 1));
                return;
            }

            double lo = Data.Min(), hi = Data.Max();
            bool thr = ShowThreshold && !double.IsNaN(Threshold) && Threshold - hi < 15;
            if (thr) hi = Math.Max(hi, Threshold + 1);
            double span = Math.Max(hi - lo, 6), mid = (hi + lo) / 2;
            lo = mid - span / 2 - 1; hi = mid + span / 2 + 1;

            const double padT = 4, padB = 3;
            double H = h - padT - padB;
            double Y(double v) => padT + (1 - (v - lo) / (hi - lo)) * H;
            double step = w / (Math.Min(Capacity, Data.Count) - 1);
            double x0 = w - (Data.Count - 1) * step;

            if (thr)
            {
                double ty = Math.Round(Y(Threshold)) + 0.5;
                var dash = new Pen(Theme.Brush(Theme.Warm, 0x70), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                dc.DrawLine(dash, new Point(0, ty), new Point(w, ty));
                var ft = new FormattedText(Theme.Deg(Threshold, "0"), Theme.Culture, FlowDirection.LeftToRight,
                    new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9.5,
                    Theme.Brush(Theme.Warm, 0xB0), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(0, Math.Max(0, ty - ft.Height - 1)));
            }

            var pts = Data.Select((v, i) => new Point(x0 + i * step, Y(v))).ToList();

            var fill = new StreamGeometry();
            using (var c = fill.Open())
            {
                c.BeginFigure(new Point(pts[0].X, h), true, true);
                c.PolyLineTo(pts, false, false);
                c.LineTo(new Point(pts[^1].X, h), false, false);
            }
            fill.Freeze();
            dc.DrawGeometry(Theme.Brush(Color, 0x1C), null, fill);

            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(pts[0], false, false);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(Theme.Brush(Color), 1.4) { LineJoin = PenLineJoin.Round }, line);
            dc.DrawEllipse(Theme.Brush(Color), null, pts[^1], 2.2, 2.2);
        }
    }

    /// <summary>Petit graphe de courbe de ventilation : % en fonction de la température, avec le point de fonctionnement actuel.</summary>
    public sealed class FanCurveChart : FrameworkElement
    {
        const double TMin = 25, TMax = 95;
        public (double t, double p)[] Points;
        public double MinPercent = 20;
        public double CurrentTemp = double.NaN, CurrentPercent = double.NaN;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || Points == null) return;
            const double padB = 13; // place pour les graduations
            double H = h - padB - 2;
            double X(double t) => (Math.Clamp(t, TMin, TMax) - TMin) / (TMax - TMin) * w;
            double Y(double p) => 2 + (1 - p / 100) * H;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tf = new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var grid = new Pen(Theme.Brush(Theme.Line), 1);
            foreach (double t in new double[] { 40, 60, 80 })
            {
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(grid, new Point(x, 2), new Point(x, 2 + H));
                var ft = new FormattedText($"{t:0}°", Theme.Culture, FlowDirection.LeftToRight, tf, 9.5, Theme.Brush(Theme.Faint), dip);
                dc.DrawText(ft, new Point(x - ft.Width / 2, h - ft.Height + 1));
            }

            var pts = new List<Point> { new(0, Y(Math.Max(MinPercent, Points[0].p))) };
            pts.AddRange(Points.Select(p => new Point(X(p.t), Y(Math.Max(MinPercent, p.p)))));
            pts.Add(new Point(w, Y(100)));

            var fill = new StreamGeometry();
            using (var c = fill.Open())
            {
                c.BeginFigure(new Point(0, 2 + H), true, true);
                c.PolyLineTo(pts, false, false);
                c.LineTo(new Point(w, 2 + H), false, false);
            }
            fill.Freeze();
            dc.DrawGeometry(Theme.Brush(Theme.Text, 0x10), null, fill);

            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(pts[0], false, false);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(Theme.Brush(Theme.Muted), 1.3) { LineJoin = PenLineJoin.Round }, line);

            if (!double.IsNaN(CurrentTemp) && !double.IsNaN(CurrentPercent))
            {
                var p = new Point(X(CurrentTemp), Y(CurrentPercent));
                var dash = new Pen(Theme.Brush(Theme.Text, 0x60), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
                dc.DrawLine(dash, new Point(Math.Round(p.X) + 0.5, p.Y), new Point(Math.Round(p.X) + 0.5, 2 + H));
                dc.DrawEllipse(Theme.Brush(Theme.Text), new Pen(Theme.Brush(Theme.Surface), 2), p, 4, 4);
            }
        }
    }

    /// <summary>Curseur plat dessiné à la main (le Slider WPF par défaut jure sur fond sombre).</summary>
    public sealed class FlatSlider : FrameworkElement
    {
        public double Minimum = 0, Maximum = 100;
        double _value;
        public bool Active = true;
        public event Action<double> ValueChanged;   // pendant le glissement
        public event Action<double> ValueCommitted; // au relâchement

        public FlatSlider()
        {
            Height = 22;
            Cursor = Cursors.Hand;
            Focusable = false;
        }

        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, Minimum, Maximum); InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, cy = h / 2;
            const double r = 7;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double x = r + (w - 2 * r) * (_value - Minimum) / (Maximum - Minimum);
            dc.DrawRoundedRectangle(Theme.Brush(Theme.Line), null, new Rect(r, cy - 2, Math.Max(0, w - 2 * r), 4), 2, 2);
            dc.DrawRoundedRectangle(Theme.Brush(Active ? Theme.Text : Theme.Faint), null, new Rect(r, cy - 2, Math.Max(0, x - r), 4), 2, 2);
            dc.DrawEllipse(Theme.Brush(Active ? Theme.Text : Theme.Muted), new Pen(Theme.Brush(Theme.Bg), 2), new Point(x, cy), r, r);
        }

        void SetFromMouse(MouseEventArgs e)
        {
            const double r = 7;
            double t = (e.GetPosition(this).X - r) / Math.Max(1, ActualWidth - 2 * r);
            Value = Math.Round(Minimum + t * (Maximum - Minimum));
            ValueChanged?.Invoke(_value);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            CaptureMouse();
            SetFromMouse(e);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured) SetFromMouse(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            ValueCommitted?.Invoke(_value);
            e.Handled = true;
        }
    }
}
