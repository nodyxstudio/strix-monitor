using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace StrixMonitor
{
    /// <summary>
    /// Petit encart "CPU / GPU" posé sur la barre des tâches. Les valeurs se mettent à jour sur place : rien ne bouge.
    /// Sous Windows 11, une fenêtre placée *dans* la barre passe sous son effet de transparence (invisible) :
    /// c'est donc une petite fenêtre "toujours au premier plan" alignée sur la barre, qui se cache
    /// quand une application plein écran (un jeu) occupe le même écran.
    /// Un clic affiche / cache le widget ; on peut la faire glisser horizontalement ; clic droit = position par défaut.
    /// </summary>
    public sealed class TaskbarBox : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        static readonly IntPtr HWND_TOPMOST = new(-1);
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, MONITOR_DEFAULTTONEAREST = 2;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        const double BoxWidth = 76; // en DIP

        readonly Settings _cfg;
        readonly Window _win;
        readonly Border _root;
        readonly Grid _grid;
        readonly TextBlock[] _labels = new TextBlock[2], _values = new TextBlock[2];
        readonly WinEventProc _hookProc; // gardé en champ : sinon le ramasse-miettes le libère
        IntPtr _hwnd, _bar, _hook, _widget;
        bool _enabled, _dark = true, _visible;
        DateTime _lastSample;

        // Glisser-déposer
        bool _down, _dragging;
        int _downX;
        double _downOffset;

        public event Action Clicked;

        public TaskbarBox(Settings cfg)
        {
            _cfg = cfg;
            _grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 2; i++)
            {
                _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _labels[i] = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                _values[i] = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Typography.SetNumeralAlignment(_values[i], FontNumeralAlignment.Tabular);
                Grid.SetRow(_labels[i], i); Grid.SetRow(_values[i], i); Grid.SetColumn(_values[i], 1);
                _grid.Children.Add(_labels[i]);
                _grid.Children.Add(_values[i]);
            }
            _root = new Border
            {
                Child = _grid, Cursor = Cursors.Hand, SnapsToDevicePixels = true,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                ToolTip = "Strix Monitor : clic pour afficher / cacher, glisser pour déplacer, clic droit pour remettre en place",
            };

            _win = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                ShowActivated = false, Content = _root, Width = BoxWidth, Height = 40, Left = -10000, Top = -10000,
                FontFamily = Theme.Font, UseLayoutRounding = true, Title = "Strix Monitor – barre",
            };
            TextOptions.SetTextFormattingMode(_win, TextFormattingMode.Display);
            _win.SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(_win).Handle;
                // Pas dans Alt+Tab, ne prend jamais le focus.
                SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };

            _root.MouseLeftButtonDown += (_, e) =>
            {
                _down = true; _dragging = false;
                GetCursorPos(out var p); _downX = p.X;
                _downOffset = CurrentOffsetDip();
                _root.CaptureMouse();
                e.Handled = true;
            };
            _root.MouseMove += (_, _) =>
            {
                if (!_down) return;
                GetCursorPos(out var p);
                int dx = p.X - _downX;
                if (!_dragging && Math.Abs(dx) < 4) return;
                _dragging = true;
                _cfg.BarOffset = Math.Max(0, _downOffset - dx / Scale);
                Place();
            };
            _root.MouseLeftButtonUp += (_, e) =>
            {
                if (!_down) return;
                _down = false;
                _root.ReleaseMouseCapture();
                if (_dragging) _cfg.Save(); else Clicked?.Invoke();
                e.Handled = true;
            };
            _root.MouseRightButtonUp += (_, e) => { _cfg.BarOffset = double.NaN; _cfg.Save(); Place(); e.Handled = true; };

            // Quand une autre fenêtre passe au premier plan (clic sur la barre, lancement d'un jeu…) : on se repositionne tout de suite.
            _hookProc = (_, _, _, _, _, _, _) => { if (_enabled) Place(); };
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        double Scale => _bar == IntPtr.Zero ? 1 : Math.Max(1, GetDpiForWindow(_bar)) / 96.0;

        /// <param name="widget">Fenêtre du widget : l'encart va sur la barre de l'écran où elle se trouve.</param>
        public void Update(IntPtr widget, (string label, double value, Status status)[] rows, bool enabled)
        {
            _enabled = enabled && rows.Length > 0;
            _widget = widget;
            if (!_enabled) { HideBox(); return; }
            try
            {
                if (_hwnd == IntPtr.Zero) { _win.Show(); }
                _bar = FindBar(widget);

                for (int i = 0; i < 2; i++)
                {
                    bool on = i < rows.Length;
                    _labels[i].Visibility = _values[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (!on) continue;
                    _labels[i].Text = rows[i].label;
                    _values[i].Text = Theme.Deg(rows[i].value, "0");
                    var c = rows[i].status is Status.Warm or Status.Hot ? Theme.Of(rows[i].status)
                          : _dark ? Theme.Text : Color.FromRgb(0x1A, 0x1A, 0x1A);
                    _values[i].Foreground = Theme.Brush(c);
                    _labels[i].Foreground = Theme.Brush(_dark ? Color.FromRgb(0x9A, 0xA0, 0xA6) : Color.FromRgb(0x5A, 0x5F, 0x66));
                    _values[i].FontSize = rows.Length == 1 ? 15 : 12.5;
                }

                Place();
                if (_visible && (DateTime.Now - _lastSample).TotalSeconds > 20) SampleBackground();
            }
            catch (Exception ex) { Program.Log("Encart barre : " + ex.Message); }
        }

        static IntPtr FindBar(IntPtr widget)
        {
            var mon = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr h = IntPtr.Zero;
                while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                    if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == mon) return h;
            }
            return FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        }

        double CurrentOffsetDip()
        {
            if (!double.IsNaN(_cfg.BarOffset)) return _cfg.BarOffset;
            // Par défaut : juste à gauche de la zone de notification (barre principale) ou de l'horloge (barres secondaires).
            var tray = FindWindowEx(_bar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var t) && GetWindowRect(_bar, out var b) && t.Right > t.Left)
                return (b.Right - t.Left) / Scale + 6;
            return 150;
        }

        /// <summary>Une application occupe-t-elle tout l'écran de la barre (jeu, vidéo plein écran…) ?</summary>
        bool FullscreenAppOnBarMonitor()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd || fg == _bar) return false;
            var sb = new System.Text.StringBuilder(64);
            GetClassName(fg, sb, 64);
            string cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            var mon = MonitorFromWindow(_bar, MONITOR_DEFAULTTONEAREST);
            if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != mon) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out var r)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        void Place()
        {
            if (_hwnd == IntPtr.Zero || _bar == IntPtr.Zero || !IsWindow(_bar) || !GetWindowRect(_bar, out var r)) { HideBox(); return; }
            if (!_enabled || FullscreenAppOnBarMonitor()) { HideBox(); return; }
            // Barre masquée automatiquement (hors de l'écran) : on se cache aussi.
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(_bar, MONITOR_DEFAULTTONEAREST), ref mi) && (r.Bottom > mi.rcMonitor.Bottom + 2 || r.Top < mi.rcMonitor.Top - 2))
            { HideBox(); return; }

            int barW = r.Right - r.Left, barH = r.Bottom - r.Top;
            int w = (int)Math.Round(BoxWidth * Scale);
            int pad = (int)Math.Round(4 * Scale);
            int x = r.Left + Math.Clamp(barW - (int)Math.Round(CurrentOffsetDip() * Scale) - w, 0, Math.Max(0, barW - w));
            SetWindowPos(_hwnd, HWND_TOPMOST, x, r.Top + pad, w, barH - 2 * pad, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _visible = true;
        }

        void HideBox()
        {
            if (_hwnd != IntPtr.Zero && _visible) SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_HIDEWINDOW | 0x1 | 0x2 | 0x4);
            _visible = false;
        }

        /// <summary>Reprend la couleur de la barre juste à gauche de l'encart (thème clair / sombre, transparence…).</summary>
        void SampleBackground()
        {
            _lastSample = DateTime.Now;
            if (!GetWindowRect(_hwnd, out var me)) return;
            int x = me.Left - 3, y = (me.Top + me.Bottom) / 2;
            var dc = GetDC(IntPtr.Zero);
            uint px = GetPixel(dc, x, y);
            ReleaseDC(IntPtr.Zero, dc);
            if (px == 0xFFFFFFFF) return;
            byte R = (byte)(px & 0xFF), G = (byte)((px >> 8) & 0xFF), B = (byte)((px >> 16) & 0xFF);
            int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            if (max - min > 40) return; // couleur vive : sûrement une fenêtre devant, on garde l'ancienne
            double lum = (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255;
            if (lum > 0.35 && lum < 0.7) return;
            _dark = lum < 0.5;
            ((SolidColorBrush)_root.Background).Color = Color.FromRgb(R, G, B);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            try { _win.Close(); } catch { }
        }
    }
}
