using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ProjectOpti;

public partial class MainWindow : Window
{
    private SystemSpecs? _specs;

    // last numbers shown in the FPS tiles (so they count up from the previous value)
    private double _shownCurrent, _shownPotential, _shownGain;
    private readonly Dictionary<TextBlock, DispatcherTimer> _counters = new();

    public MainWindow()
    {
        InitializeComponent();
        // maximize to the work area (above the taskbar) instead of behind it
        SourceInitialized += (_, _) => MaximizeFix.Attach(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // staggered fade-in of the four cards
        FadeIn(CardSpecs, 0);
        FadeIn(CardLogs, 90);
        FadeIn(CardFps, 180);
        FadeIn(CardTweaks, 270);

        BuildSwatches();

        Logger.Init();
        Logger.LineLogged += line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        AdminText.Text = Admin.IsAdmin ? "Administrator" : "Standard user";
        ShowTweakPlaceholder("Press Calculate to see which tweaks you can apply.");
        SetStatus("Scanning hardware...", busy: true);

        Logger.Log($"Logs folder: {AppPaths.Logs}");
        Logger.Log($"Running as administrator: {Admin.IsAdmin}");
        Logger.Log("Scanning hardware...");
        CalculateButton.IsEnabled = false;

        try
        {
            _specs = await Task.Run(SpecScanner.Scan);
            Tweaks.CurrentSpecs = _specs;
            SpecsBox.Text = _specs.ToDisplayString();
            Logger.Log("Hardware scan done.");
        }
        catch (Exception ex)
        {
            Logger.Log("ERROR during hardware scan: " + ex);
        }

        SetBusy(false);
    }

    // ================= title bar =================

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "\uE923" : "\uE922";
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var a in Theme.Accents)
        {
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(a.Color)),
                Tag = a.Name,
                ToolTip = a.Name
            };
            b.Click += Accent_Click;
            SwatchPanel.Children.Add(b);
        }
        MarkSelectedSwatch();
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            Theme.Apply(name);
            MarkSelectedSwatch();
            Logger.Log($"Accent color: {name}");
        }
    }

    private void MarkSelectedSwatch()
    {
        foreach (var b in SwatchPanel.Children.OfType<Button>())
            b.BorderBrush = (string)b.Tag == Theme.Current ? Brushes.White : Brushes.Transparent;
    }

    // ================= animations =================

    private static void FadeIn(FrameworkElement el, int delayMs, double fromY = 14)
    {
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);

        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
    }

    private void CountUp(TextBlock tb, double from, double to, Func<double, string> format, int ms = 750)
    {
        if (_counters.TryGetValue(tb, out var old)) old.Stop();

        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, sw.ElapsedMilliseconds / (double)ms);
            double eased = 1 - Math.Pow(1 - t, 3); // ease-out
            tb.Text = format(from + (to - from) * eased);
            if (t >= 1) timer.Stop();
        };
        _counters[tb] = timer;
        timer.Start();
    }

    // ================= small UI helpers =================

    private void SetStatus(string text, bool busy)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(busy ? "WarnBrush" : "SuccessBrush");

        if (busy)
        {
            StatusDot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(650))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

            if (BusyBar.Visibility != Visibility.Visible)
            {
                BusyBar.Visibility = Visibility.Visible;
                BusyMove.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-BusyBar.Width, Math.Max(ActualWidth, 600), TimeSpan.FromMilliseconds(1100))
                    { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else
        {
            StatusDot.BeginAnimation(OpacityProperty, null);
            StatusDot.Opacity = 1;
            BusyMove.BeginAnimation(TranslateTransform.XProperty, null);
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private void SetBusy(bool busy, string status = "Working...")
    {
        CalculateButton.IsEnabled = !busy && _specs != null;
        RunTweaksButton.IsEnabled = !busy && TweaksPanel.Children.OfType<CheckBox>().Any();
        SetStatus(busy ? status : "Ready", busy);
    }

    private TextBlock MutedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("MutedBrush"),
        FontSize = 12.5
    };

    private void ShowTweakPlaceholder(string text)
    {
        TweaksPanel.Children.Clear();
        TweaksPanel.Children.Add(MutedText(text));
        RunTweaksButton.IsEnabled = false;
    }

    private CheckBox BuildCheckBox(Tweak t)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = t.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap
        });
        var desc = MutedText(t.Description);
        desc.Margin = new Thickness(0, 2, 0, 0);
        desc.FontSize = 12;
        content.Children.Add(desc);

        return new CheckBox
        {
            Content = content,
            IsChecked = t.DefaultOn,
            Tag = t,
            Margin = new Thickness(0, 0, 0, 12)
        };
    }

    // Checkboxes only for tweaks that would change something right now.
    private async Task RefreshTweaksAsync()
    {
        Logger.Log("Checking which tweaks are still needed...");
        var needed = await Task.Run(Tweaks.GetNeeded);

        TweaksPanel.Children.Clear();
        if (needed.Count == 0)
        {
            ShowTweakPlaceholder("Nothing to tweak: all supported settings are already optimal.");
        }
        else
        {
            int i = 0;
            foreach (var t in needed)
            {
                var cb = BuildCheckBox(t);
                TweaksPanel.Children.Add(cb);
                FadeIn(cb, i++ * 45, fromY: 8);
            }
            RunTweaksButton.IsEnabled = true;
        }

        if (!Admin.IsAdmin && Tweaks.AnyNeedAdmin)
        {
            var note = MutedText("Some tweaks and checks need administrator rights (Defender exclusion, CPU temperature, machine-wide startup items). Restart the app as administrator to see them.");
            note.FontStyle = FontStyles.Italic;
            note.Margin = new Thickness(0, 6, 0, 0);
            TweaksPanel.Children.Add(note);
        }
    }

    // ================= buttons =================

    private async void CalculateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_specs == null) return;

        SetBusy(true, "Calculating...");
        ResultBox.Text = "Calculating...";

        try
        {
            var specs = _specs;
            var (text, result) = await Task.Run(() =>
            {
                Logger.Log("=== Calculate started (game: Rust) ===");

                // network driver check runs while the process scan samples CPU
                var driverTask = Task.Run(() => DriverChecker.Check(specs));
                var procs = ProcessScanner.Scan(specs);
                var driver = driverTask.GetAwaiter().GetResult();

                var findings = OptimizationScanner.Scan(specs, procs, driver);
                int? quality = RustConfig.ReadGraphicsQuality();
                var r = RustEstimator.Estimate(specs, findings, quality);
                Logger.Log("=== Calculate finished ===");

                string nl = Environment.NewLine;
                string details = r.ToDisplayString() + nl + nl + driver.ToDisplayString() + nl + nl + procs.ToDisplayString();
                return (details, r);
            });

            double current = result.CurrentFps;
            double potential = result.AfterAllFps;
            double gain = current > 0 ? (potential / current - 1) * 100 : 0;

            CountUp(CurrentFpsText, _shownCurrent, current, v => $"~{v:0}");
            CountUp(PotentialFpsText, _shownPotential, potential, v => $"~{v:0}");
            CountUp(GainText, _shownGain, gain, v => $"+{v:0.#}%");
            (_shownCurrent, _shownPotential, _shownGain) = (current, potential, gain);

            ResultBox.Text = text;

            await RefreshTweaksAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("ERROR during calculation: " + ex);
            ResultBox.Text = "Error, see logs.";
        }

        SetBusy(false);
    }

    private async void RunTweaksButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = TweaksPanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (Tweak)c.Tag)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Tick at least one tweak first.", "Project Opti");
            return;
        }

        string list = string.Join(Environment.NewLine, selected.Select(t => "- " + t.Name));
        var answer = MessageBox.Show($"Run these tweaks?{Environment.NewLine}{Environment.NewLine}{list}",
            "Project Opti", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SetBusy(true, "Running tweaks...");
        Logger.Log($"=== Running {selected.Count} tweak(s) ===");
        await Task.Run(() => Tweaks.Run(selected));
        Logger.Log("=== Tweaks finished. Press Calculate again to see the new estimate. ===");
        await RefreshTweaksAsync();
        SetBusy(false);
    }
}
