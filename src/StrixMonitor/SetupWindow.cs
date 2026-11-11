using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StrixMonitor
{
    /// <summary>Assistant d'installation : affiché quand l'exe est lancé hors de son dossier d'installation.</summary>
    public sealed class SetupWindow : Window
    {
        public string PreviewPath; // --setup-preview : enregistre une image puis quitte

        readonly StackPanel _options = new();
        readonly StackPanel _progress = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 18, 0, 0) };
        readonly TextBlock _step = new() { FontSize = 12.5 };
        readonly Border _barFill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        readonly Grid _bar = new() { Margin = new Thickness(0, 8, 0, 0) };
        readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Func<bool> _pawnio, _desktop, _startup;

        public SetupWindow()
        {
            Title = "Installation de Strix Monitor";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            Foreground = Theme.Brush(Theme.Text);
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var root = new StackPanel();
            Content = new Border
            {
                CornerRadius = new CornerRadius(12), Background = Theme.Brush(Color.FromRgb(0x17, 0x19, 0x1C)),
                BorderBrush = Theme.Brush(Theme.Line), BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 22), Child = root,
            };
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            root.Children.Add(Header());
            root.Children.Add(new Border { Height = 1, Background = Theme.Brush(Theme.Line), Margin = new Thickness(0, 18, 0, 16) });

            bool pawnOk = Setup.PawnIOInstalled;
            _pawnio = pawnOk
                ? AddDone("PawnIO est déjà installé", "Le pilote qui permet de lire le processeur et de piloter les ventilateurs.")
                : AddCheck("Installer PawnIO (recommandé)", "Pilote nécessaire pour lire la température du processeur et piloter les ventilateurs. Téléchargé depuis sa source officielle, puis vérifié.", true);
            _desktop = AddCheck("Raccourci sur le bureau", null, true);
            _startup = AddCheck("Lancer au démarrage de Windows", "Démarre discrètement à l'ouverture de session, sans demande d'autorisation.", false);
            root.Children.Add(_options);

            root.Children.Add(new TextBlock
            {
                // Chemin affiché sans le nom d'utilisateur (pratique pour les captures d'écran partagées).
                Text = "Dossier : " + Setup.InstallDir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
                ToolTip = "Installation pour ton compte uniquement, sans droits sur les autres utilisateurs du PC.",
                FontSize = 11, Foreground = Theme.Brush(Theme.Faint),
                Margin = new Thickness(0, 14, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });

            _bar.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(Theme.Line) });
            _barFill.Background = Theme.Brush(Theme.Text);
            _bar.Children.Add(_barFill);
            _progress.Children.Add(_step);
            _progress.Children.Add(_bar);
            root.Children.Add(_progress);
            root.Children.Add(_message);

            ShowStartButtons();
            root.Children.Add(_buttons);

            if (PreviewPath == null) Loaded += async (_, _) =>
            {
                if (PreviewPath == null) return;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SavePng(PreviewPath);
                Close();
            };
        }

        UIElement Header()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var logo = new Image { Width = 52, Height = 52, Margin = new Thickness(0, 0, 14, 0) };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/owl.png")); } catch { }
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            g.Children.Add(logo);

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "Strix Monitor", FontFamily = Theme.FontDisplay, FontSize = 22, FontWeight = FontWeights.SemiBold });
            titles.Children.Add(new TextBlock
            {
                Text = $"Températures et ventilateurs en direct · version {Setup.Version}",
                FontSize = 12, Foreground = Theme.Brush(Theme.Muted), Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(titles, 1);
            g.Children.Add(titles);

            var close = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Foreground = Theme.Brush(Theme.Faint), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
            };
            close.MouseEnter += (_, _) => close.Foreground = Theme.Brush(Theme.Text);
            close.MouseLeave += (_, _) => close.Foreground = Theme.Brush(Theme.Faint);
            close.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (_buttons.IsEnabled) Close(); };
            Grid.SetColumn(close, 2);
            g.Children.Add(close);
            return g;
        }

        // ───────────── Cases à cocher plates ─────────────

        Func<bool> AddCheck(string label, string hint, bool initial)
        {
            bool value = initial;
            var box = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            var tick = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            box.Child = tick;
            void Paint()
            {
                box.Background = value ? Theme.Brush(Theme.Text) : Brushes.Transparent;
                box.BorderBrush = Theme.Brush(value ? Theme.Text : Theme.Faint);
                tick.Foreground = Theme.Brush(Theme.Bg);
                tick.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            }
            Paint();
            var row = Row(box, label, hint);
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_buttons.IsEnabled) return; value = !value; Paint(); };
            _options.Children.Add(row);
            return () => value;
        }

        Func<bool> AddDone(string label, string hint)
        {
            var mark = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
                Foreground = Theme.Brush(Theme.Ok), Width = 18, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            _options.Children.Add(Row(mark, label, hint));
            return () => false;
        }

        static Grid Row(UIElement mark, string label, string hint)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(mark);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, FontSize = 13.5 });
            if (hint != null)
                text.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Theme.Brush(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            return g;
        }

        // ───────────── Boutons ─────────────

        static Border Button(string text, bool primary, Action onClick)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(18, 8, 18, 9), Margin = new Thickness(10, 0, 0, 0),
                Background = primary ? Theme.Brush(Theme.Text) : Theme.Brush(Theme.Surface),
                BorderBrush = Theme.Brush(primary ? Theme.Text : Theme.Line), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(primary ? Theme.Bg : Theme.Text) },
            };
            b.MouseEnter += (_, _) => b.Opacity = 0.88;
            b.MouseLeave += (_, _) => b.Opacity = 1;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        void ShowStartButtons()
        {
            _buttons.Children.Clear();
            _buttons.Children.Add(Button("Annuler", false, Close));
            _buttons.Children.Add(Button(Setup.IsInstalled ? "Mettre à jour" : "Installer", true, () => _ = Install()));
        }

        void SetProgress(string step, double fraction)
        {
            _step.Text = step;
            _bar.UpdateLayout();
            _barFill.Width = Math.Max(0, Math.Min(1, fraction)) * _bar.ActualWidth;
        }

        void ShowMessage(string text, Color color)
        {
            _message.Text = text;
            _message.Foreground = Theme.Brush(color);
            _message.Visibility = Visibility.Visible;
        }

        // ───────────── Installation ─────────────

        async Task Install()
        {
            _buttons.IsEnabled = false;
            _buttons.Opacity = 0.4;
            _message.Visibility = Visibility.Collapsed;
            _progress.Visibility = Visibility.Visible;
            bool wantPawn = _pawnio(), wantDesktop = _desktop(), wantStartup = _startup();
            string warning = null;
            bool reboot = false;

            try
            {
                SetProgress("Fermeture de la version en cours…", 0.05);
                bool legacyStartup = await Task.Run(() =>
                {
                    Setup.StopOtherInstances();
                    return Setup.RemoveLegacy();
                });

                SetProgress("Copie des fichiers…", 0.12);
                await Task.Run(Setup.CopySelf);

                SetProgress("Création des raccourcis…", 0.25);
                await Task.Run(() =>
                {
                    Setup.CreateShortcuts(wantDesktop);
                    Setup.RegisterUninstall();
                    if (wantStartup || legacyStartup) Startup.Set(true, Setup.InstalledExe);
                });

                if (wantPawn)
                {
                    SetProgress("Téléchargement de PawnIO…", 0.3);
                    var progress = new Progress<double>(p => SetProgress($"Téléchargement de PawnIO… {p * 100:0} %", 0.3 + p * 0.5));
                    var download = Setup.InstallPawnIO(progress);
                    // Le téléchargement terminé, l'installateur PawnIO prend quelques secondes.
                    while (!download.IsCompleted)
                    {
                        await Task.WhenAny(download, Task.Delay(300));
                        if (_step.Text.EndsWith("100 %")) SetProgress("Installation de PawnIO…", 0.88);
                    }
                    (warning, reboot) = await download;
                }

                SetProgress("Terminé", 1);
            }
            catch (Exception ex)
            {
                Program.Log("Installation : " + ex);
                _progress.Visibility = Visibility.Collapsed;
                ShowMessage("L'installation a échoué : " + ex.Message, Theme.Hot);
                _buttons.IsEnabled = true;
                _buttons.Opacity = 1;
                return;
            }

            _buttons.IsEnabled = true;
            _buttons.Opacity = 1;
            _options.IsEnabled = false;
            _options.Opacity = 0.5;

            if (warning != null)
                ShowMessage("Strix Monitor est installé, mais PawnIO n'a pas pu l'être : " + warning +
                            "\nSans lui, la température du processeur et le contrôle des ventilateurs ne seront pas disponibles. " +
                            "Tu peux l'installer à la main depuis pawnio.eu.", Theme.Warm);
            else if (reboot)
                ShowMessage("Strix Monitor est installé. Windows demande un redémarrage pour finir l'installation de PawnIO.", Theme.Warm);
            else
                ShowMessage("Strix Monitor est installé. Tu le retrouveras dans le menu Démarrer" + (wantDesktop ? " et sur le bureau." : "."), Theme.Ok);

            _buttons.Children.Clear();
            _buttons.Children.Add(Button("Fermer", false, Close));
            _buttons.Children.Add(Button("Lancer Strix Monitor", true, () =>
            {
                try { Setup.Launch(); } catch (Exception ex) { Program.Log("Lancement : " + ex.Message); }
                Close();
            }));
        }

        void SavePng(string path)
        {
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
    }
}
