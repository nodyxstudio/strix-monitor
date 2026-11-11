using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace StrixMonitor
{
    public sealed class WidgetWindow : Window
    {
        static readonly Color Neutral = Color.FromRgb(0xA9, 0xAE, 0xB5); // couleur des courbes quand tout va bien

        readonly Settings _cfg = Settings.Load();
        readonly List<Action> _refresh = new();
        readonly List<Action> _fanSync = new();
        readonly LiveIcons _icons;
        readonly TaskbarBox _box;
        Recorder _rec;
        TextBlock _recButton;
        SensorService _svc;
        bool _closing;
        public string PreviewPath; // mode --preview : enregistre des PNG puis quitte
        int _ticks;

        readonly StackPanel _tempsPanel = new();
        readonly StackPanel _fansPanel = new() { Visibility = Visibility.Collapsed };
        readonly TextBlock _summary = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Ellipse _summaryDot = new() { Width = 7, Height = 7, Margin = new Thickness(1, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _failsafe = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        TextBlock _minimize;
        (TextBlock label, Border line) _tabTemps, _tabFans;

        public WidgetWindow()
        {
            Title = "Strix Monitor";
            Width = 344;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanMinimize;
            ShowInTaskbar = false; // les températures sont dans l'encart de la barre, pas dans un bouton
            Topmost = _cfg.Topmost;
            FontFamily = Theme.Font;
            Foreground = Theme.Brush(Theme.Text);
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.Manual;

            var wa = SystemParameters.WorkArea;
            bool saved = !double.IsNaN(_cfg.Left) && _cfg.Left >= SystemParameters.VirtualScreenLeft
                         && _cfg.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
                         && _cfg.Top >= SystemParameters.VirtualScreenTop
                         && _cfg.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
            Left = saved ? _cfg.Left : wa.Right - Width - 24;
            Top = saved ? _cfg.Top : wa.Top + 24;

            var root = new StackPanel();
            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = Theme.Brush(Theme.Bg),
                BorderBrush = Theme.Brush(Theme.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 10, 16, 16),
                Child = root,
            };
            root.Children.Add(BuildHeader());
            root.Children.Add(BuildSummary());
            root.Children.Add(_tempsPanel);
            root.Children.Add(_fansPanel);
            _tempsPanel.Children.Add(Note("Lecture des capteurs…", Theme.Muted, 14));

            _box = new TaskbarBox(_cfg);
            _box.Clicked += ToggleWidget;
            _icons = new LiveIcons(this);
            _icons.TrayClicked += ToggleWidget;
            _icons.TrayRightClicked += _ => { ContextMenu.Placement = PlacementMode.MousePoint; ContextMenu.IsOpen = true; };
            SourceInitialized += (_, _) =>
            {
                _icons.Attach();
                _icons.TrayVisible = _cfg.Tray;
            };

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch { }
                _cfg.Left = Left; _cfg.Top = Top; _cfg.Save();
            };
            ContextMenu = BuildMenu();
            Loaded += OnLoaded;
            Closing += (_, _) =>
            {
                _closing = true;
                if (PreviewPath == null) { _cfg.Left = Left; _cfg.Top = Top; _cfg.Save(); }
                _rec?.Stop();
                _icons.Dispose();
                _box.Dispose();
                _svc?.Dispose(); // remet tous les ventilateurs en Auto
            };
        }

        // ───────────────────────── Structure ─────────────────────────

        UIElement BuildHeader()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            _tabTemps = Tab("Températures", tabs, () => SelectTab(false));
            _tabFans = Tab("Ventilateurs", tabs, () => SelectTab(true));
            g.Children.Add(tabs);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            _recButton = IconButton("\uE7C8", "Enregistrer une session (benchmark, partie…)", ToggleRecording);
            _recButton.Visibility = Visibility.Collapsed; // visible une fois les capteurs lus
            right.Children.Add(_recButton);
            _minimize = IconButton("\uE921", "Cacher (clic sur l'encart de la barre des tâches pour le réafficher)", ToggleWidget);
            right.Children.Add(_minimize);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
            UpdateMinimizeButton();

            var close = IconButton("", "Fermer", Close);
            Grid.SetColumn(close, 3);
            g.Children.Add(close);
            SelectTab(false);
            return g;
        }

        static TextBlock IconButton(string glyph, string tip, Action onClick)
        {
            var b = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Foreground = Theme.Brush(Theme.Faint), Padding = new Thickness(8, 6, 4, 6),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = tip,
                Background = Brushes.Transparent,
            };
            b.MouseEnter += (_, _) => { if (b.Tag == null) b.Foreground = Theme.Brush(Theme.Text); };
            b.MouseLeave += (_, _) => { if (b.Tag == null) b.Foreground = Theme.Brush(Theme.Faint); };
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        void UpdateMinimizeButton() =>
            _minimize.Visibility = _cfg.Taskbar || _cfg.Tray ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Clic sur le bouton de la barre, l'icône de notification ou "—" : affiche / cache le widget.</summary>
        void ToggleWidget()
        {
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
            else Hide();
        }

        // ───────────── Sessions d'enregistrement ─────────────

        void ToggleRecording()
        {
            if (_rec == null) return;
            if (_rec.IsRecording)
            {
                string file = _rec.Stop();
                UpdateRecordUi();
                if (file != null) new SessionWindow(file).Show();
            }
            else
            {
                try { _rec.Start(); }
                catch (Exception ex) { Program.Log("Session : " + ex.Message); }
                UpdateRecordUi();
            }
        }

        void UpdateRecordUi()
        {
            bool on = _rec?.IsRecording == true;
            _recButton.Text = on ? "\uE71A" : "\uE7C8"; // carré "stop" / rond "enregistrer"
            _recButton.Tag = on ? "rec" : null;
            _recButton.Foreground = Theme.Brush(on ? Theme.Hot : Theme.Faint);
            _recButton.ToolTip = on ? "Arrêter et voir l'analyse" : "Enregistrer une session (benchmark, partie…)";
            UpdateSummary();
        }

        (TextBlock, Border) Tab(string text, Panel host, Action onClick)
        {
            var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 6, 0, 5) };
            var line = new Border { Height = 2, CornerRadius = new CornerRadius(1) };
            var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            sp.Children.Add(label);
            sp.Children.Add(line);
            sp.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            host.Children.Add(sp);
            return (label, line);
        }

        void SelectTab(bool fans)
        {
            if (_tabTemps.label == null || _tabFans.label == null) return;
            void Style((TextBlock label, Border line) t, bool on)
            {
                t.label.Foreground = Theme.Brush(on ? Theme.Text : Theme.Faint);
                t.line.Background = on ? Theme.Brush(Theme.Text) : Brushes.Transparent;
            }
            Style(_tabTemps, !fans);
            Style(_tabFans, fans);
            _tempsPanel.Visibility = fans ? Visibility.Collapsed : Visibility.Visible;
            _fansPanel.Visibility = fans ? Visibility.Visible : Visibility.Collapsed;
        }

        UIElement BuildSummary()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
            _summaryDot.Fill = Theme.Brush(Theme.Faint);
            _summary.Foreground = Theme.Brush(Theme.Muted);
            sp.Children.Add(_summaryDot);
            sp.Children.Add(_summary);
            return sp;
        }

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();

            var record = new MenuItem { Header = "Enregistrer une session" };
            record.Click += (_, _) => ToggleRecording();
            var sessions = new MenuItem { Header = "Sessions enregistrées…" };
            sessions.Click += (_, _) => new SessionWindow().Show();

            var taskbar = new MenuItem { Header = "Températures dans la barre des tâches", IsCheckable = true, IsChecked = _cfg.Taskbar };
            taskbar.Click += (_, _) =>
            {
                _cfg.Taskbar = taskbar.IsChecked;
                _cfg.Save();
                UpdateMinimizeButton();
                RefreshIcons();
            };
            var tray = new MenuItem { Header = "Icône près de l'horloge", IsCheckable = true, IsChecked = _cfg.Tray };
            tray.Click += (_, _) =>
            {
                _cfg.Tray = _icons.TrayVisible = tray.IsChecked;
                _cfg.Save();
                UpdateMinimizeButton();
                RefreshIcons();
            };

            var shows = new MenuItem { Header = "Afficher dans la barre" };
            foreach (var (key, label) in new[] { ("both", "CPU en haut, GPU en bas"), ("cpu", "CPU seulement"), ("gpu", "GPU seulement") })
            {
                var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = _cfg.IconShows == key, Tag = key };
                it.Click += (_, _) =>
                {
                    _cfg.IconShows = key;
                    _cfg.Save();
                    foreach (MenuItem o in shows.Items) o.IsChecked = (string)o.Tag == key;
                    _icons.Invalidate();
                    RefreshIcons();
                };
                shows.Items.Add(it);
            }

            var top = new MenuItem { Header = "Toujours au premier plan", IsCheckable = true, IsChecked = _cfg.Topmost };
            top.Click += (_, _) => { Topmost = _cfg.Topmost = top.IsChecked; _cfg.Save(); };
            var boot = new MenuItem { Header = "Lancer au démarrage de Windows", IsCheckable = true };
            m.Opened += (_, _) => boot.IsChecked = Startup.IsEnabled();
            boot.Click += (_, _) => boot.IsChecked = Startup.Set(boot.IsChecked) ? boot.IsChecked : !boot.IsChecked;
            var quit = new MenuItem { Header = "Quitter" };
            quit.Click += (_, _) => Close();

            m.Opened += (_, _) =>
            {
                record.Header = _rec?.IsRecording == true ? "Arrêter l'enregistrement" : "Enregistrer une session";
                record.IsEnabled = _rec != null;
            };
            m.Items.Add(record);
            m.Items.Add(sessions);
            m.Items.Add(new Separator());
            m.Items.Add(taskbar);
            m.Items.Add(tray);
            m.Items.Add(shows);
            m.Items.Add(new Separator());
            m.Items.Add(top);
            m.Items.Add(boot);
            m.Items.Add(new Separator());
            m.Items.Add(quit);
            return m;
        }

        // ───────────────────────── Cycle ─────────────────────────

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (PreviewPath == null) _ = Task.Run(Startup.RefreshPath);
            try
            {
                _svc = await Task.Run(() => new SensorService());
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                _tempsPanel.Children.Clear();
                _tempsPanel.Children.Add(Note("Impossible de lire les capteurs : " + ex.Message, Theme.Hot, 14));
                return;
            }

            if (PreviewPath == null)
                foreach (var f in _svc.Fans.Where(f => f.CanControl)) _cfg.Restore(f);
            else
            {
                // Aperçu uniquement : montre les modes Courbe et Fixe sans jamais toucher au matériel.
                var c = _svc.Fans.Where(f => f.CanControl).ToList();
                if (c.Count > 0) { c[0].Mode = FanMode.Curve; c[0].SourceTemp = CpuItemValue(); c[0].Applied = FanCurves.Eval(c[0].Preset, c[0].Source, c[0].SourceTemp); }
                if (c.Count > 2) { c[2].Mode = FanMode.Fixed; c[2].FixedPercent = 45; }
            }

            _svc.Sample();
            if (PreviewPath == null)
            {
                _svc.TickFans();
                _rec = new Recorder(_svc);
                _recButton.Visibility = Visibility.Visible;
            }
            BuildTemps();
            BuildFans();
            RefreshAll();

            while (!_closing)
            {
                await Task.Delay(1000);
                if (_closing) break;
                try
                {
                    await Task.Run(_svc.Update);
                    if (_closing) break;
                    _svc.Sample();
                    if (PreviewPath == null)
                    {
                        Failsafe();
                        _svc.TickFans();
                        _rec?.Tick();
                        if (_rec?.IsRecording == true && _rec.Elapsed > Recorder.MaxDuration) ToggleRecording();
                    }
                    RefreshAll();
                    if (PreviewPath != null && ++_ticks == 10)
                    {
                        await SavePng(PreviewPath + "_temps.png");
                        SelectTab(true);
                        await SavePng(PreviewPath + "_fans.png");
                        LiveIcons.SavePreview(PreviewPath + "_icon24.png", IconValues(), 24);
                        LiveIcons.SavePreview(PreviewPath + "_icon30.png", IconValues(), 30);
                        Close();
                    }
                }
                catch (Exception ex) { Program.Log(ex.ToString()); }
            }
        }

        async Task SavePng(string path)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var el = (FrameworkElement)Content;
            el.UpdateLayout();
            const double k = 2;
            var rtb = new RenderTargetBitmap((int)(el.ActualWidth * k), (int)(el.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
        }

        void RefreshAll()
        {
            if (WindowState != WindowState.Minimized && IsVisible)
                foreach (var r in _refresh) r();
            UpdateSummary();
            RefreshIcons();
        }

        double CpuItemValue() => (double?)CpuItem?.Sensor.Value ?? 50;

        TempItem CpuItem => _svc?.Groups.FirstOrDefault(g => g.Kind == Kind.Cpu)?.Primary;
        TempItem GpuItem => _svc?.Groups.FirstOrDefault(g => g.Kind == Kind.Gpu)?.Primary;

        (double, Status)[] IconValues()
        {
            var list = new List<(double, Status)>();
            if (_cfg.IconShows != "gpu" && CpuItem != null) list.Add((CpuItem.Value, CpuItem.Status));
            if (_cfg.IconShows != "cpu" && GpuItem != null) list.Add((GpuItem.Value, GpuItem.Status));
            if (list.Count == 0 && CpuItem != null) list.Add((CpuItem.Value, CpuItem.Status));
            return list.ToArray();
        }

        void RefreshIcons()
        {
            if (_svc == null) return;
            var parts = new List<string>();
            if (CpuItem != null) parts.Add("CPU " + Theme.Deg(CpuItem.Value, "0"));
            if (GpuItem != null) parts.Add("GPU " + Theme.Deg(GpuItem.Value, "0"));
            string tip = parts.Count > 0 ? string.Join(" · ", parts) : "Strix Monitor";
            Title = tip;
            var rows = new List<(string, double, Status)>();
            if (_cfg.IconShows != "gpu" && CpuItem != null) rows.Add(("CPU", CpuItem.Value, CpuItem.Status));
            if (_cfg.IconShows != "cpu" && GpuItem != null) rows.Add(("GPU", GpuItem.Value, GpuItem.Status));
            if (rows.Count == 0 && CpuItem != null) rows.Add(("CPU", CpuItem.Value, CpuItem.Status));
            _box.Update(new WindowInteropHelper(this).Handle, rows.ToArray(), _cfg.Taskbar && PreviewPath == null);
            _icons.Update(IconValues(), tip);
        }

        /// <summary>Si le CPU ou le GPU devient trop chaud, tout ventilateur en % fixe repasse en Auto.
        /// (Les courbes, elles, montent déjà à 100 % dans ce cas.)</summary>
        void Failsafe()
        {
            var hot = new[] { CpuItem, GpuItem }.FirstOrDefault(i => i?.Status == Status.Hot);
            var fixedFans = _svc.Fans.Where(f => f.Mode == FanMode.Fixed).ToList();
            if (hot == null || fixedFans.Count == 0) return;
            foreach (var f in fixedFans)
            {
                _svc.SetMode(f, FanMode.Auto);
                _cfg.Store(f);
            }
            foreach (var s in _fanSync) s();
            _failsafe.Text = $"Remis en Auto à {DateTime.Now:HH:mm} : {GroupOf(hot).Title.ToLower()} à {Theme.Deg(hot.Value, "0")}.";
            _failsafe.Foreground = Theme.Brush(Theme.Warm);
            _failsafe.Visibility = Visibility.Visible;
        }

        static string LowerFirst(string s) => s.Length > 1 && char.IsLower(s[1]) ? char.ToLower(s[0]) + s[1..] : s;

        TempGroup GroupOf(TempItem i) => _svc.Groups.First(g => g.Items.Contains(i));

        void UpdateSummary()
        {
            UpdateSummaryText();
            if (_rec?.IsRecording != true) return;
            var text = _summary.Text;
            var color = _summary.Foreground;
            _summary.Inlines.Clear();
            _summary.Inlines.Add(new Run($"● REC {Recorder.FormatDuration(_rec.Elapsed)}") { Foreground = Theme.Brush(Theme.Hot), FontWeight = FontWeights.SemiBold });
            _summary.Inlines.Add(new Run("  ·  " + text) { Foreground = color });
        }

        void UpdateSummaryText()
        {
            var worst = _svc.Groups.SelectMany(g => g.Items)
                .Where(i => i.Status is Status.Warm or Status.Hot)
                .OrderByDescending(i => i.Status).ThenByDescending(i => i.Value - i.Range.Warm)
                .FirstOrDefault();
            if (worst == null)
            {
                bool any = _svc.Groups.Count > 0;
                _summaryDot.Fill = Theme.Brush(any ? Theme.Ok : Theme.Faint);
                _summary.Text = any ? "Tout est normal" : "Aucun capteur de température trouvé";
                _summary.Foreground = Theme.Brush(Theme.Muted);
                return;
            }
            var c = Theme.Of(worst.Status);
            _summaryDot.Fill = Theme.Brush(c);
            _summary.Foreground = Theme.Brush(c);
            string what = worst.Label is "Température" or "Cœur" ? GroupOf(worst).Title : $"{GroupOf(worst).Title}, {LowerFirst(worst.Label)}";
            _summary.Text = $"{Theme.Word(worst.Status)} · {what} {Theme.Deg(worst.Value, "0")}";
        }

        // ───────────────────────── Températures ─────────────────────────

        void BuildTemps()
        {
            _tempsPanel.Children.Clear();
            if (_svc.Groups.All(g => g.Kind != Kind.Cpu))
                _tempsPanel.Children.Add(Note("Température CPU indisponible : le pilote PawnIO est nécessaire (pawnio.eu).", Theme.Warm, 12));

            foreach (var g in _svc.Groups)
            {
                _tempsPanel.Children.Add(new Border { Height = 1, Background = Theme.Brush(Theme.Line), Margin = new Thickness(0, 12, 0, 10) });
                _tempsPanel.Children.Add(SectionTitle(g.Title, g.Hardware));
                bool hero = g.Kind is Kind.Cpu || (g.Kind is Kind.Gpu && g.Title != "GPU intégré");
                foreach (var i in g.Items)
                    _tempsPanel.Children.Add(hero && i == g.Primary ? Hero(i) : Row(i));
            }
        }

        static UIElement SectionTitle(string title, string hw)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            var h = new TextBlock
            {
                Text = Shorten(hw), FontSize = 11, Foreground = Theme.Brush(Theme.Faint), Margin = new Thickness(12, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(h, 1);
            g.Children.Add(h);
            return g;
        }

        static string Shorten(string hw) => (hw ?? "")
            .Replace("NVIDIA GeForce ", "").Replace(" Processor", "").Replace("6-Core", "")
            .Replace("8-Core", "").Replace("12-Core", "").Replace("16-Core", "").Replace("  ", " ").Trim();

        UIElement Hero(TempItem item)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            var left = new StackPanel();
            var value = new TextBlock { FontFamily = Theme.FontDisplay, FontSize = 32, FontWeight = FontWeights.Light, Margin = new Thickness(-1, -4, 0, 0) };
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            var (statusPanel, dot, word) = StatusLine(12);
            var minmax = new TextBlock { FontSize = 11, Foreground = Theme.Brush(Theme.Faint), Margin = new Thickness(0, 3, 0, 0) };
            left.Children.Add(value);
            left.Children.Add(statusPanel);
            left.Children.Add(minmax);
            g.Children.Add(left);

            var chart = new LineChart { Height = 62, Data = item.History, Threshold = item.Range.Warm, ShowThreshold = true, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(chart, 1);
            g.Children.Add(chart);

            var tip = Tip(g);
            _refresh.Add(() =>
            {
                var c = Theme.Of(item.Status);
                value.Text = Theme.Deg(item.Value);
                dot.Fill = Theme.Brush(c);
                word.Text = Theme.Word(item.Status);
                word.Foreground = Theme.Brush(c);
                minmax.Text = $"min {Theme.Deg(item.Min, "0")} · max {Theme.Deg(item.Max, "0")}";
                chart.Color = item.Status == Status.Ok ? Neutral : c;
                chart.InvalidateVisual();
                tip.Text = TipText(item);
            });
            return g;
        }

        UIElement Row(TempItem item)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = item.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            var (statusPanel, dot, word) = StatusLine(11);
            left.Children.Add(statusPanel);
            g.Children.Add(left);

            var chart = new LineChart { Height = 22, Data = item.History, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chart, 1);
            g.Children.Add(chart);

            var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            Grid.SetColumn(value, 2);
            g.Children.Add(value);

            var tip = Tip(g);
            _refresh.Add(() =>
            {
                var c = Theme.Of(item.Status);
                value.Text = Theme.Deg(item.Value);
                dot.Fill = Theme.Brush(c);
                word.Text = Theme.Word(item.Status);
                word.Foreground = Theme.Brush(c);
                chart.Color = item.Status == Status.Ok ? Neutral : c;
                chart.InvalidateVisual();
                tip.Text = TipText(item);
            });
            return g;
        }

        static (StackPanel, Ellipse, TextBlock) StatusLine(double size)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var word = new TextBlock { FontSize = size };
            sp.Children.Add(dot);
            sp.Children.Add(word);
            return (sp, dot, word);
        }

        static string TipText(TempItem i) =>
            $"{i.Range.Note}\n\nÀ surveiller dès {i.Range.Warm:0} °C, trop chaud dès {i.Range.Hot:0} °C.\n" +
            $"Depuis le lancement : min {Theme.Deg(i.Min)}  ·  max {Theme.Deg(i.Max)}";

        static TextBlock Tip(FrameworkElement target)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 250, FontSize = 12, Foreground = Theme.Brush(Theme.Text) };
            target.ToolTip = new ToolTip
            {
                Content = tb, Background = Theme.Brush(Theme.Surface), BorderBrush = Theme.Brush(Theme.Line),
                Padding = new Thickness(10, 8, 10, 8), HasDropShadow = false,
            };
            ToolTipService.SetInitialShowDelay(target, 350);
            ToolTipService.SetShowDuration(target, 20000);
            return tb;
        }

        static TextBlock Note(string text, Color c, double top) => new()
        {
            Text = text, FontSize = 12, Foreground = Theme.Brush(c), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
        };

        // ───────────────────────── Ventilateurs ─────────────────────────

        void BuildFans()
        {
            _fansPanel.Children.Clear();
            if (_svc.Fans.Count == 0)
            {
                _fansPanel.Children.Add(Note("Aucun ventilateur détecté. Ceux de la carte mère n'apparaissent que si sa puce de contrôle est reconnue.", Theme.Muted, 14));
                return;
            }

            foreach (var f in _svc.Fans.OrderByDescending(f => f.CanControl))
                _fansPanel.Children.Add(FanCard(f));

            if (_svc.Fans.Any(f => f.CanControl))
                _fansPanel.Children.Add(Note(
                    "Courbe : la vitesse suit la température (Silencieux, Équilibré ou Performance). " +
                    "Fixe : vitesse constante, jamais sous 20 % (60 % pour la pompe), et retour en Auto si le CPU ou le GPU devient trop chaud. " +
                    "Tout repasse en Auto à la fermeture du widget.", Theme.Faint, 12));
            if (_svc.HiddenFans > 0)
                _fansPanel.Children.Add(Note($"{_svc.HiddenFans} connecteur(s) sans ventilateur branché, masqué(s).", Theme.Faint, 6));
            _fansPanel.Children.Add(_failsafe);
        }

        UIElement FanCard(FanItem f)
        {
            var root = new StackPanel();
            var card = new Border
            {
                Background = Theme.Brush(Theme.Surface), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 10, 0, 0), Child = root,
            };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var names = new StackPanel();
            names.Children.Add(new TextBlock { Text = f.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            names.Children.Add(new TextBlock { Text = Shorten(f.Hardware), FontSize = 11, Foreground = Theme.Brush(Theme.Faint), TextTrimming = TextTrimming.CharacterEllipsis });
            top.Children.Add(names);
            var rpm = new TextBlock { FontSize = 15, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(rpm, FontNumeralAlignment.Tabular);
            Grid.SetColumn(rpm, 1);
            top.Children.Add(rpm);
            root.Children.Add(top);

            if (!f.CanControl)
            {
                root.Children.Add(new TextBlock { Text = "Lecture seule : pas pilotable par logiciel", FontSize = 11, Foreground = Theme.Brush(Theme.Faint), Margin = new Thickness(0, 6, 0, 0) });
                _refresh.Add(() => SetRpm(rpm, f));
                return card;
            }

            // Ligne de mode : [Auto | Courbe | Fixe]  ………  38 %
            var modeRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition());
            var seg = new Border { Background = Theme.Brush(Theme.Bg), CornerRadius = new CornerRadius(6), Padding = new Thickness(2) };
            var segPanel = new StackPanel { Orientation = Orientation.Horizontal };
            seg.Child = segPanel;
            var modeBtns = new Dictionary<FanMode, Border>
            {
                [FanMode.Auto] = SegButton("Auto"),
                [FanMode.Curve] = SegButton("Courbe"),
                [FanMode.Fixed] = SegButton("Fixe"),
            };
            foreach (var b in modeBtns.Values) segPanel.Children.Add(b);
            modeRow.Children.Add(seg);
            var pct = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
            Grid.SetColumn(pct, 1);
            modeRow.Children.Add(pct);
            root.Children.Add(modeRow);

            // Mode Fixe : curseur
            var slider = new FlatSlider
            {
                Minimum = f.MinPercent, Maximum = 100, Margin = new Thickness(0, 10, 0, 0),
                Value = Math.Max(f.MinPercent, f.Mode == FanMode.Fixed ? f.FixedPercent : f.ControlSensor.Value ?? 50),
            };
            root.Children.Add(slider);

            // Mode Courbe : profils + graphe
            var curvePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var presetRow = new Grid();
            presetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presetRow.ColumnDefinitions.Add(new ColumnDefinition());
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            var chipBtns = FanCurves.Names.ToDictionary(n => n, n => Chip(n));
            foreach (var c in chipBtns.Values) chips.Children.Add(c);
            presetRow.Children.Add(chips);
            curvePanel.Children.Add(presetRow);
            var follows = new TextBlock { FontSize = 11, Foreground = Theme.Brush(Theme.Faint), Margin = new Thickness(0, 8, 0, 4) };
            curvePanel.Children.Add(follows);
            var chart = new FanCurveChart { Height = 58, MinPercent = f.MinPercent };
            curvePanel.Children.Add(chart);
            root.Children.Add(curvePanel);

            void Sync()
            {
                foreach (var (mode, b) in modeBtns) StyleSeg(b, f.Mode == mode);
                foreach (var (name, b) in chipBtns) StyleChip(b, f.Preset == name);
                slider.Visibility = f.Mode == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
                curvePanel.Visibility = f.Mode == FanMode.Curve ? Visibility.Visible : Visibility.Collapsed;
                if (f.Mode == FanMode.Fixed) slider.Value = f.FixedPercent;
                chart.Points = FanCurves.Of(f.Preset, f.Source);
                chart.InvalidateVisual();
            }
            void Changed()
            {
                _cfg.Store(f);
                _failsafe.Visibility = Visibility.Collapsed;
                Sync();
                _svc.TickFans();
                Live();
            }
            void Live()
            {
                SetRpm(rpm, f);
                if (Mouse.Captured is FlatSlider) return;
                float? duty = f.ControlSensor.Value;
                pct.Text = f.Mode == FanMode.Auto
                    ? (duty == null ? "auto" : $"auto · {duty.Value:0} %")
                    : !double.IsNaN(f.Applied) ? $"{f.Applied:0} %" : duty == null ? "—" : $"{duty.Value:0} %";
                pct.Foreground = Theme.Brush(f.Mode == FanMode.Auto ? Theme.Muted : Theme.Text);
                if (f.Mode == FanMode.Curve)
                {
                    follows.Text = $"Suit {FanCurves.SourceLabel(f.Source)} · {Theme.Deg(f.SourceTemp, "0")}";
                    chart.CurrentTemp = f.SourceTemp;
                    chart.CurrentPercent = f.Applied;
                    chart.InvalidateVisual();
                }
            }

            foreach (var (mode, b) in modeBtns)
                b.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if (mode == FanMode.Fixed && f.Mode != FanMode.Fixed) f.FixedPercent = (int)slider.Value;
                    _svc.SetMode(f, mode);
                    Changed();
                };
            foreach (var (name, b) in chipBtns)
                b.MouseLeftButtonDown += (_, e) => { e.Handled = true; f.Preset = name; Changed(); };
            slider.ValueChanged += v => pct.Text = $"{v:0} %";
            slider.ValueCommitted += v => { f.FixedPercent = (int)v; Changed(); };

            _fanSync.Add(() => { Sync(); Live(); });
            _refresh.Add(Live);
            Sync();
            return card;
        }

        static void SetRpm(TextBlock rpm, FanItem f)
        {
            float? r = f.Rpm?.Value;
            rpm.Text = r == null ? "—" : r.Value.ToString("N0", Theme.Culture) + " tr/min";
            rpm.Foreground = Theme.Brush(r is > 0 ? Theme.Text : Theme.Muted);
        }

        static Border SegButton(string text) => new()
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 3, 9, 4), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 11.5 },
        };

        static void StyleSeg(Border b, bool on)
        {
            b.Background = on ? Theme.Brush(Theme.Line) : Brushes.Transparent;
            ((TextBlock)b.Child).Foreground = Theme.Brush(on ? Theme.Text : Theme.Muted);
        }

        static Border Chip(string text) => new()
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 11 },
        };

        static void StyleChip(Border b, bool on)
        {
            b.Background = on ? Theme.Brush(Theme.Text) : Brushes.Transparent;
            b.BorderBrush = Theme.Brush(on ? Theme.Text : Theme.Line);
            ((TextBlock)b.Child).Foreground = Theme.Brush(on ? Theme.Surface : Theme.Muted);
        }
    }
}
