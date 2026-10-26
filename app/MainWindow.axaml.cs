namespace AStoryWealth.Installer;

public partial class MainWindow : Window
{
    private string? _collectionPath;
    private AddonManifest? _manifest;
    private const string AddonId = "a-storywealth";
    private const string NexusCollectionUrl = "https://www.nexusmods.com/games/fallout4/collections/5atq9t";
    private const string DiscordUrl = "https://discord.gg/storywealth";
    private NexusCredential? _cred;
    private NexusSession _session = null!;
    private CancellationTokenSource? _signInCts;
    private InstallState.ResumeInfo? _resume;
    private bool _installing;
    private string? _resumeJsonPath;
    private string? _resumeAddonId;
    private CancellationTokenSource? _cts;
    private bool _authPremium;
    private bool _signedIn;

    public MainWindow()
    {
        InitializeComponent();
        BuildOptionalGroups();
        try { SecretStore.Delete("nexus-api-key"); } catch { }   // a saved personal key from an older build: not used any more
        _session = new NexusSession(NexusOAuthConfig.Load(), new DpapiTokenStore());
        _cred = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY") is { Length: > 0 } devKey ? new ApiKeyCredential(devKey) : _session.IsSignedIn ? _session : null;
        InstallRunner.UiLogLine += OnLogLine;
        _logTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        Closed += (_, _) => { _cts?.Cancel(); InstallRunner.UiLogLine -= OnLogLine; _logTimer?.Stop(); };
        RbFresh.IsCheckedChanged += (_, _) => OnSourceChanged();
        RbVortex.IsCheckedChanged += (_, _) => OnSourceChanged();
        if (Environment.GetEnvironmentVariable("ASW_MAKE_ICON") is { Length: > 0 } iconDir)
            Opened += async (_, _) => { await MakeIconAsync(iconDir); Close(); };
        if (Environment.GetEnvironmentVariable("ASW_UI_SHOT") is { Length: > 0 } shotDir)
            Opened += (_, _) => RunScreenshotTour(shotDir);
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0];
        _version = string.IsNullOrEmpty(version) ? "0.0.0" : version;
        TxtVersion.Text = string.IsNullOrEmpty(version) ? "Installer" : $"Installer  v{version}";
        TxtWatchDir.Text = ManualDownloader.DefaultWatchDir();
        TxtGameDir.Text = FindFallout4Path();
        var threads = InstallState.GetThreads();
        NudDownloadThreads.Value = threads.Download;
        NudExtractThreads.Value = threads.Extract;
        _resume = InstallState.GetResume();
        _ = RefreshAuthAsync();
        TxtCollectionSummary.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) RefreshDetailHeader(); };
        PnlPickCollection.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Visual.IsVisibleProperty) PnlDetailPick.IsVisible = PnlPickCollection.IsVisible; };
        InitUpdates();
        ShowExistingInstall();
        RefreshInstallCheck();
        UpdateHomeTab();
        var last = InstallState.GetLastPath();
        if (_resume is { } r)
        {
            // An install was interrupted — pre-fill its exact settings and tell
            // the user that starting it again continues where it left off.
            _resumeJsonPath = r.JsonPath;
            _resumeAddonId = r.AddonId;
            TxtInstallDir.Text = r.InstallDir;
            TxtDownloadsDir.Text = r.DownloadsDir ?? Path.Combine(r.InstallDir, "Downloads");
            if (r.ReuseDir is { } rr && Directory.Exists(rr))
            {
                RbVortex.IsChecked = true;
                TxtReuseDir.Text = rr;
            }
            if (r.StockGame) RbStockGame.IsChecked = true;
            TxtDirStatus.Text = "Your last install was interrupted. Start it again to pick up where it stopped.";
            ApplyResumeGroups();
        }
        else if (last is { } ld2 && ld2.Dir is { } ld && Directory.Exists(ld))
        {
            TxtInstallDir.Text = ld;
            if (ld2.StockGame) RbStockGame.IsChecked = true;
        }
        // Persist the game-mode radio the moment it changes — an app restart
        // must not silently fall back to the attached-install default.
        RbStockGame.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(RadioButton.IsChecked))
                RememberGameMode(RbStockGame.IsChecked == true);
        };
        // downloads location: remembered, else inside the install folder
        if (last is { } llast)
        {
            var installText = TxtInstallDir.Text ?? "";
            TxtDownloadsDir.Text = llast.DownloadsDir is { } dd && dd.Length > 0
                ? dd : Path.Combine(installText.Length > 0 ? installText : ".", "Downloads");
            if (RbVortex.IsChecked == true)
            {
                if (llast.ReuseDir is { } lr && Directory.Exists(lr)) TxtReuseDir.Text = lr;
                else
                {
                    var vortexDefault = DownloadReuse.DefaultVortexDir();
                    if (vortexDefault is not null) TxtReuseDir.Text = vortexDefault;
                }
            }
        }
    }

    // ---------- shared ----------

    /// <summary>One checkbox per optional install group the collection ships (Sim Settlements 2, HD textures, ...).</summary>
    private void BuildOptionalGroups()
    {
        var groups = AStoryWealth.Core.OptionalGroups.Load(AddonId);
        CardOptional.IsVisible = groups.Count > 0;
        foreach (var g in groups)
        {
            var box = new CheckBox { Tag = g.Id, IsChecked = g.DefaultOn };
            box.IsCheckedChanged += (_, _) => { UpdateStats(); TxtSetupError.Text = ""; };
            PanelOptional.Children.Add(box);
        }
        UpdateOptionalLabels();
    }

    private IEnumerable<CheckBox> OptionalBoxes => PanelOptional.Children.OfType<CheckBox>();
    private List<string> SelectedGroups() => OptionalBoxes.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    /// <summary>Checkbox text with each group's mod count and download size (once the collection is loaded).</summary>
    private void UpdateOptionalLabels()
    {
        var groups = AStoryWealth.Core.OptionalGroups.Load(AddonId);
        foreach (var box in OptionalBoxes)
        {
            var g = groups.FirstOrDefault(x => x.Id == (string)box.Tag!);
            if (g is null) continue;
            // first line: name and how much it adds (computed from the list); second line: what it is
            var title = g.Name;
            if (_manifest is not null)
            {
                var only = AStoryWealth.Core.OptionalGroups.Excluded(_manifest.Mods, groups, groups.Where(x => x.Id != g.Id).Select(x => x.Id).ToList());
                var bytes = _manifest.Mods.Where(m => only.Contains(m.InstallAs)).Sum(m => m.Nexus?.FileSize ?? 0);
                title += $"  ·  {only.Count} mods, {SizeText(bytes)}";
            }
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = title });
            if (!string.IsNullOrWhiteSpace(g.Description))
                panel.Children.Add(new TextBlock { Text = g.Description, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9AA3AE")), TextWrapping = TextWrapping.Wrap });
            box.Content = panel;
        }
    }

    /// <summary>Mod count and download size of what the current optional-group ticks will actually install.</summary>
    private (int Mods, double Gb) SelectionTotals()
    {
        if (_manifest is null) return (0, 0);
        var groups = AStoryWealth.Core.OptionalGroups.Load(AddonId);
        var skip = AStoryWealth.Core.OptionalGroups.Excluded(_manifest.Mods, groups, SelectedGroups());
        var kept = _manifest.Mods.Where(m => !skip.Contains(m.InstallAs)).ToList();
        return (kept.Count, kept.Sum(m => m.Nexus?.FileSize ?? 0) / 1_000_000_000.0);
    }

    private void UpdateStats()
    {
        if (_manifest is null) return;
        var (mods, gb) = SelectionTotals();
        TxtStatMods.Text = mods.ToString("N0");
        TxtStatSize.Text = $"{gb:N0} GB";
        TxtStatStorage.Text = $"{gb * 2:N0} GB";
    }

    /// <summary>An unfinished install keeps the groups it started with; the boxes mirror them and are locked.</summary>
    private void ApplyResumeGroups()
    {
        var locked = _resume?.OptionalGroups is not null;
        if (locked)
            foreach (var box in OptionalBoxes)
                box.IsChecked = _resume!.OptionalGroups!.Contains((string)box.Tag!, StringComparer.OrdinalIgnoreCase);
        foreach (var box in OptionalBoxes) box.IsEnabled = !locked;
        TxtOptionalNote.IsVisible = locked;
        TxtOptionalNote.Text = locked ? "An earlier install is unfinished, so it keeps the choices it started with." : "";
        UpdateStats();
    }

    // Log lines arrive from worker threads by the thousand; they are queued and shown in batches (last 2,000 lines).
    // The complete log is always in install.log next to the install.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private readonly Queue<string> _logLines = new();
    private Avalonia.Threading.DispatcherTimer? _logTimer;
    private const int LogKeep = 2000;

    private void OnLogLine(string line) => _logQueue.Enqueue(line);

    private void FlushLog()
    {
        if (_logQueue.IsEmpty) return;
        while (_logQueue.TryDequeue(out var l)) { _logLines.Enqueue(l); if (_logLines.Count > LogKeep) _logLines.Dequeue(); }
        TxtLog.Text = string.Join(Environment.NewLine, _logLines);
    }

    private void ClearLog()
    {
        while (_logQueue.TryDequeue(out _)) { }
        _logLines.Clear();
        TxtLog.Text = "";
    }

    private void OnLogChanged(object? sender, TextChangedEventArgs e)
        => TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;

    private void ShowPage(int page)
    {
        if (page == 0) page = 5;   // the Modlist tab opens the A StoryWealth page directly
        Control[] pages = [PageModlist, PageSetup, PageNexus, PageInstall, PageDone, PageDetail, PageAddons, PageSettings, PageAddonDetail, PageOptionalTab, PageHome];
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == page;

        // left rail: Modlist(0) / Add-Ons(6) / Settings(7)
        Nav.SelectionChanged -= OnNavChanged;
        var navTag = page is 6 or 8 ? 6 : page is 9 or 7 or 10 ? page : 0;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == navTag.ToString());
        Nav.SelectionChanged += OnNavChanged;

        // wizard step bar only during an install flow.
        WizBar.IsVisible = page is >= 1 and <= 4;
        Avalonia.Controls.Primitives.ToggleButton[] steps = [Wiz0, Wiz1, Wiz2, Wiz3];
        var step = page switch { 0 => 0, 1 => 1, 2 or 3 => 2, 4 => 3, _ => -1 };
        for (var i = 0; i < steps.Length; i++) steps[i].IsChecked = i == step;
        if (page == 6) { StartAddonGrab(); RefreshAddons(); }
        if (page == 9) RefreshOptionalTab();
        if (page == 5) RefreshDetailHeader();
        if (page == 10) RefreshHome();
    }
}
