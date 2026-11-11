using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using D = System.Drawing;
using D2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace StrixMonitor
{
    /// <summary>
    /// Dessine les températures dans l'icône du bouton de barre des tâches (et, en option, dans la zone de notification).
    /// Windows 11 ne permet plus d'écrire du texte dans la barre : on redessine donc l'icône à chaque changement.
    /// </summary>
    public sealed class LiveIcons : IDisposable
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1, GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000;

        readonly Window _w;
        IntPtr _hwnd, _big, _small, _trayIcon;
        WF.NotifyIcon _tray;
        string _lastKey;

        public event Action TrayClicked;
        public event Action<System.Drawing.Point> TrayRightClicked;

        public LiveIcons(Window w) => _w = w;

        /// <summary>À appeler sur SourceInitialized : ajoute WS_MINIMIZEBOX pour que le clic sur le bouton de la barre réduise/restaure.</summary>
        public void Attach()
        {
            _hwnd = new WindowInteropHelper(_w).Handle;
            SetWindowLong(_hwnd, GWL_STYLE, GetWindowLong(_hwnd, GWL_STYLE) | WS_MINIMIZEBOX);
        }

        public bool TrayVisible
        {
            get => _tray?.Visible == true;
            set
            {
                if (value && _tray == null)
                {
                    _tray = new WF.NotifyIcon();
                    _tray.MouseUp += (_, e) =>
                    {
                        if (e.Button == WF.MouseButtons.Left) TrayClicked?.Invoke();
                        else if (e.Button == WF.MouseButtons.Right) TrayRightClicked?.Invoke(WF.Cursor.Position);
                    };
                }
                if (_tray != null) _tray.Visible = value;
                _lastKey = null;
            }
        }

        public void Invalidate() => _lastKey = null;

        /// <param name="values">1 ou 2 valeurs (haut / bas) à afficher, avec leur état.</param>
        /// <summary>Met à jour l'icône de la zone de notification (près de l'horloge). Celle-ci se met à jour sur place.
        /// (Le bouton de barre des tâches, lui, ne peut pas changer d'icône sous Windows 11 sans "sauter" :
        /// les températures y sont affichées par <see cref="TaskbarBox"/>.)</summary>
        public void Update((double value, Status status)[] values, string tooltip)
        {
            if (_hwnd == IntPtr.Zero || values.Length == 0) return;
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(_w).DpiScaleX;
            string key = string.Join("|", Array.ConvertAll(values, v => $"{Round(v.value)}{v.status}")) + $"|{scale}|{TrayVisible}";
            if (_tray != null) _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
            if (key == _lastKey) return;
            _lastKey = key;

            if (TrayVisible)
            {
                var h = Make((int)Math.Round(16 * scale), new[] { values[0] });
                var old = _tray.Icon;
                _tray.Icon = D.Icon.FromHandle(h);
                old?.Dispose();
                Swap(ref _trayIcon, h);
            }
        }

        /// <summary>Diagnostic : enregistre l'icône telle qu'elle serait dessinée.</summary>
        public static void SavePreview(string path, (double value, Status status)[] values, int n)
        {
            if (values.Length == 0) return;
            var h = Make(n, values);
            using (var ic = D.Icon.FromHandle(h))
            using (var bmp = ic.ToBitmap())
                bmp.Save(path, D.Imaging.ImageFormat.Png);
            DestroyIcon(h);
        }

        static string Round(double v) => double.IsNaN(v) ? "-" : Math.Round(v).ToString("0");

        static void Swap(ref IntPtr field, IntPtr next)
        {
            if (field != IntPtr.Zero) DestroyIcon(field);
            field = next;
        }

        static D.Color ColorOf(Status s)
        {
            var c = s == Status.Ok || s == Status.Unknown ? Theme.Text : Theme.Of(s);
            return D.Color.FromArgb(c.R, c.G, c.B);
        }

        static IntPtr Make(int n, (double value, Status status)[] values)
        {
            using var bmp = MakeBitmap(n, values);
            return bmp.GetHicon();
        }

        static D.Bitmap MakeBitmap(int n, (double value, Status status)[] values)
        {
            var bmp = new D.Bitmap(n, n, D.Imaging.PixelFormat.Format32bppArgb);
            using var g = D.Graphics.FromImage(bmp);
            g.SmoothingMode = D2.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = D2.PixelOffsetMode.HighQuality;
            g.Clear(D.Color.Transparent);

            float r = n * 0.2f;
            using (var bg = RoundRect(new D.RectangleF(0, 0, n, n), r))
            using (var brush = new D.SolidBrush(D.Color.FromArgb(0x20, 0x23, 0x27)))
                g.FillPath(brush, bg);

            using var family = new D.FontFamily("Segoe UI");
            if (values.Length == 1)
                DrawCentered(g, family, Round(values[0].value), ColorOf(values[0].status), new D.RectangleF(0, 0, n, n), n * 0.56f, n * 0.86f);
            else
            {
                float half = n / 2f;
                DrawCentered(g, family, Round(values[0].value), ColorOf(values[0].status), new D.RectangleF(0, 0.5f, n, half), half * 0.74f, n * 0.86f);
                DrawCentered(g, family, Round(values[1].value), ColorOf(values[1].status), new D.RectangleF(0, half - 0.5f, n, half), half * 0.74f, n * 0.86f);
            }
            return bmp;
        }

        /// <summary>Texte converti en tracé puis centré au pixel près sur la hauteur des chiffres (et non de la police).</summary>
        static void DrawCentered(D.Graphics g, D.FontFamily family, string text, D.Color color, D.RectangleF slot, float digitHeight, float maxWidth)
        {
            using var path = new D2.GraphicsPath();
            path.AddString(text, family, (int)D.FontStyle.Bold, 100, D.PointF.Empty, D.StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float k = Math.Min(digitHeight / b.Height, maxWidth / b.Width);
            using var m = new D2.Matrix();
            m.Translate(slot.X + (slot.Width - b.Width * k) / 2f, slot.Y + (slot.Height - b.Height * k) / 2f);
            m.Scale(k, k);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
            using var brush = new D.SolidBrush(color);
            g.FillPath(brush, path);
        }

        static D2.GraphicsPath RoundRect(D.RectangleF rc, float r)
        {
            var p = new D2.GraphicsPath();
            float d = r * 2;
            p.AddArc(rc.X, rc.Y, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Swap(ref _big, IntPtr.Zero);
            Swap(ref _small, IntPtr.Zero);
            Swap(ref _trayIcon, IntPtr.Zero);
        }
    }
}
