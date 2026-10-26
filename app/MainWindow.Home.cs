namespace AStoryWealth.Installer;

public partial class MainWindow
{
    // ---------- A StoryWealth page ----------

    /// <summary>Install button wording and the status line, which depend on whether the list is already installed.</summary>
    private void RefreshDetailHeader()
    {
        var installed = Mo2Launcher.IsInstalled(InstallState.GetInstallDir());
        BtnDetailInstall.Content = !installed ? "Install A StoryWealth →" : _baseUpdate ? "Update A StoryWealth →" : "Reinstall / repair →";
        TxtDetailSummary.Text = installed && _baseUpdate ? "Update available: the latest list differs from the one you installed." : TxtCollectionSummary.Text;
        PnlDetailPick.IsVisible = PnlPickCollection.IsVisible;
    }

    private bool _baseUpdate;
    private bool _addonUpdates;

    /// <summary>The green dots next to Modlist and Add-Ons.</summary>
    private void UpdateNavBadges()
    {
        var forced = Environment.GetEnvironmentVariable("ASW_UI_DOTS") == "1";   // for screenshots
        DotModlist.IsVisible = _baseUpdate || forced;
        DotAddons.IsVisible = _addonUpdates || forced;
    }

    /// <summary>Compares the list the install was made from with the latest one, off the UI thread.</summary>
    private async Task CheckBaseUpdateAsync()
    {
        var dir = InstallState.GetInstallDir();
        var latest = _collectionPath;
        if (dir is null || latest is null) { _baseUpdate = false; UpdateNavBadges(); return; }
        var differs = await Task.Run(() => InstallHealth.BaseUpdateAvailable(dir, latest));
        _baseUpdate = differs == true;
        UpdateNavBadges();
        RefreshDetailHeader();
    }

    // ---------- home ----------

    /// <summary>The Home tab shows once the base list is installed.</summary>
    private void UpdateHomeTab()
    {
        var dir = InstallState.GetInstallDir();
        var installed = Mo2Launcher.IsInstalled(dir);
        NavItemHome.IsVisible = installed;
        CardHomePlay.IsVisible = installed;
        if (installed) RefreshRail(dir!);
    }

    private string _railSize = "…";
    private bool _railSizeBusy;

    /// <summary>The Play block in the left rail: the launch buttons and a few numbers about the install, on every page.</summary>
    private void RefreshRail(string dir)
    {
        var title = Mo2Launcher.FindF4seTitle(dir);
        BtnHomeF4se.IsEnabled = title is not null;
        BtnHomeF4se.Content = title is null ? "F4SE not found" : "Launch with F4SE";
        void Show()
        {
            try
            {
                var sum = InstallHealth.Summary(dir);
                TxtHomeSummary.Text = $"{sum.Mods:N0} mods ({sum.EnabledMods:N0} enabled)\n{sum.Plugins:N0} plugins ({sum.EnabledPlugins:N0} enabled)\n"
                    + $"{sum.AddOns} add-on{(sum.AddOns == 1 ? "" : "s")}  ·  {_railSize}";
            }
            catch { TxtHomeSummary.Text = ""; }
        }
        Show();
        if (_railSizeBusy) return;
        _railSizeBusy = true;
        _ = Task.Run(() => InstallHealth.FolderSize(Path.Combine(dir, "mods"))).ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _railSizeBusy = false; if (t.IsCompletedSuccessfully) { _railSize = SizeText(t.Result); Show(); } }));
    }

    private int _homeRun;

    private void RefreshHome()
    {
        var dir = InstallState.GetInstallDir();
        if (dir is null || !Mo2Launcher.IsInstalled(dir)) return;
        RefreshRail(dir);
        var run = ++_homeRun;
        var display = MainDisplay();
        PnlHealth.Children.Clear();
        PnlHealth.Children.Add(new TextBlock { Text = "Checking…", Foreground = new SolidColorBrush(Color.Parse("#B8BEC9")) });
        _ = Task.Run(() =>
        {
            var health = InstallHealth.Check(dir, display);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (run != _homeRun) return;   // the page was refreshed again meanwhile
                ShowHealth(health);
            });
        });
    }

    private void ShowHealth(List<HealthItem> items)
    {
        PnlHealth.Children.Clear();
        foreach (var it in items)
        {
            var (glyph, color) = it.Level switch
            {
                HealthLevel.Ok => ("✓", "#6FCF83"), HealthLevel.Warn => ("!", "#F2C48D"), _ => ("✗", "#EB6B6B"),
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
            row.Children.Add(new TextBlock { Text = glyph, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)) });
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = it.Label, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FDF6EC")) });
            text.Children.Add(new TextBlock { Text = it.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B8BEC9")) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (it.Action is "match-resolution" or "use-recommended")
            {
                var fix = new Button { Content = it.Action == "use-recommended" ? "Use recommended INIs" : "Match display", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
                fix.Click += it.Action == "use-recommended" ? OnHomeRecommendedIni : OnHomeMatchRes;
                Grid.SetColumn(fix, 2);
                row.Children.Add(fix);
            }
            PnlHealth.Children.Add(row);
        }
        var bad = items.Count(i => i.Level == HealthLevel.Fail);
        PnlHealth.Children.Add(new TextBlock
        {
            Margin = new Avalonia.Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = bad == 0 ? "Everything checks out."
                : $"Needs attention: {string.Join(", ", items.Where(i => i.Level == HealthLevel.Fail).Select(i => i.Label))}. Save a report from Settings and send it with a screenshot of this.",
            Foreground = new SolidColorBrush(Color.Parse(bad == 0 ? "#6FCF83" : "#EB6B6B")),
        });
    }

    /// <summary>The main display's size in real pixels (not scaled), or null when Windows doesn't say.</summary>
    private (int W, int H)? MainDisplay() =>
        Screens.Primary is { } sc && sc.Bounds.Width >= 640 && sc.Bounds.Height >= 480 ? (sc.Bounds.Width, sc.Bounds.Height) : null;

    /// <summary>The game rewrites its INIs when it closes, so changing them while it or MO2 runs would be undone.</summary>
    private string? IniBlocker() =>
        _installing ? "Wait for the current install to finish."
        : Process.GetProcesses().Any(p => p.ProcessName.StartsWith("ModOrganizer", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("Fallout4", StringComparison.OrdinalIgnoreCase))
            ? "Close Mod Organizer 2 and Fallout 4 first. The game rewrites these files when it closes and would undo the change."
        : null;

    private void OnHomeRecommendedIni(object? sender, RoutedEventArgs e)
    {
        if (InstallState.GetInstallDir() is not { } dir) return;
        if (IniBlocker() is { } why) { TxtHomeIniStatus.Text = why; return; }
        if (sender is not Button btn) return;
        if (!Arm("home-ini", btn, "Replace my INIs?", "Use recommended INIs"))
        {
            TxtHomeIniStatus.Text = "This replaces your Fallout4.ini, Fallout4Prefs.ini and Fallout4Custom.ini with the recommended ones. Your current files are kept as .asw-backup. Click again to do it.";
            return;
        }
        try
        {
            var done = GameDisplay.UseRecommended(dir, MainDisplay());
            TxtHomeIniStatus.Text = done.Count == 0 ? "This build has no recommended INIs to apply."
                : $"Done: {string.Join(", ", done)} replaced, and the resolution set to your display. Your old files are kept next to them as .asw-backup.";
            if (done.Count > 0) InstallLog.Info("config", $"recommended INIs applied: {string.Join(", ", done)}");
        }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); TxtHomeIniStatus.Text = $"Couldn't replace them: {ex.Message}"; }
        RefreshHome();
    }

    private void OnHomeMatchRes(object? sender, RoutedEventArgs e)
    {
        if (InstallState.GetInstallDir() is not { } dir || MainDisplay() is not { } d) return;
        if (IniBlocker() is { } why) { TxtHomeStatus.Text = why; return; }
        try
        {
            var ok = GameDisplay.ApplyResolution(GameDisplay.ProfilePrefs(dir), d.W, d.H);
            TxtHomeStatus.Text = ok ? $"The game's resolution is now {d.W}x{d.H}. The old file is kept as Fallout4Prefs.ini{GameDisplay.BackupSuffix}."
                : "Couldn't find the profile's Fallout4Prefs.ini.";
            if (ok) InstallLog.Info("config", $"game resolution set to {d.W}x{d.H} (your main display)");
        }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); TxtHomeStatus.Text = $"Couldn't change it: {ex.Message}"; }
        RefreshHome();
    }

    private void OnHomeRecheck(object? sender, RoutedEventArgs e) => RefreshHome();

    private static void OpenFolder(string path)
    {
        try { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); }
    }

    private void OnHomeFolderInstall(object? sender, RoutedEventArgs e) { if (InstallState.GetInstallDir() is { } d) OpenFolder(d); }
    private void OnHomeFolderMods(object? sender, RoutedEventArgs e) { if (InstallState.GetInstallDir() is { } d) OpenFolder(Path.Combine(d, "mods")); }
    private void OnHomeFolderDownloads(object? sender, RoutedEventArgs e) { if (InstallState.GetInstallDir() is { } d) OpenFolder(Mo2Launcher.DownloadsDir(d)); }
    private void OnHomeNexus(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/games/fallout4/collections/{CollectionSync.BaseSlug}") { UseShellExecute = true }); }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); }
    }

    /// <summary>Fills a "latest changes" card from a collection's newest revision; hides it when Nexus has none.</summary>
    private static void ShowChangelog(Control card, TextBlock title, TextBlock body, NexusApiClient.CollectionStats? s)
    {
        var text = ChangelogFormat.ToPlain(s?.Changelog);
        card.IsVisible = text.Length > 0;
        if (text.Length == 0) return;
        var when = s!.ChangelogDate is { } d ? $" · {d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" : "";
        title.Text = (s.Revision > 0 ? $"What's new in revision {s.Revision}" : "What's new") + when;
        body.Text = text;
    }

    private void OnHomeMo2(object? sender, RoutedEventArgs e)
    {
        if (InstallState.GetInstallDir() is not { } dir || !Mo2Launcher.IsInstalled(dir)) return;
        try { Mo2Launcher.StartMo2(dir); TxtHomeStatus.Text = "Opening Mod Organizer 2."; }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); TxtHomeStatus.Text = $"Couldn't open Mod Organizer 2: {ex.Message}"; }
    }

    private void OnHomeF4se(object? sender, RoutedEventArgs e)
    {
        if (InstallState.GetInstallDir() is not { } dir || Mo2Launcher.FindF4seTitle(dir) is not { } title) return;
        try { Mo2Launcher.StartF4se(dir, title); TxtHomeStatus.Text = $"Starting {title} through Mod Organizer 2."; }
        catch (Exception ex) { InstallLog.Warn("home", ex.Message); TxtHomeStatus.Text = $"Couldn't start F4SE: {ex.Message}"; }
    }

    /// <summary>Auto-discovers add-on collections (GraphQL listing only, no archive downloads).</summary>
    private bool _addonGrabbed;
    private List<NexusApiClient.UserCollection>? _liveAddons;
    private Dictionary<string, string> _liveSummary = new();
    private AddonCard? _addonDetailCard;
    private string? _addonJsonPath;
    private bool _addonInstall;
    private async void StartAddonGrab()
    {
        if (_addonGrabbed || _cred is null) return;
        _addonGrabbed = true;
        TxtAddonsStatus.Text = "Checking for add-ons…";
        TxtAddonsStatus.IsVisible = true;
        try
        {
            var cred = _cred;
            var prog = new Progress<string>(s => TxtAddonsStatus.Text = s);
            var list = await AddonGrabber.ListAsync(cred, prog);
            if (!ReferenceEquals(cred, _cred)) { _addonGrabbed = false; return; }   // signed out (or in as someone else) meanwhile
            if (list.Count == 0)
            {
                TxtAddonsStatus.Text = "Couldn't load the add-on list. Check your internet connection, then open this page again.";
                _addonGrabbed = false;
                RefreshAddons();
                return;
            }
            _liveAddons = list;
            _liveSummary = _liveAddons.ToDictionary(c => c.Slug, c => c.Summary);
            TxtAddonsStatus.IsVisible = false;
        }
        catch (Exception ex)
        {
            TxtAddonsStatus.Text = ex.Message;
            _addonGrabbed = false;
        }
        RefreshAddons();
    }

    private void OnNavChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageModlist is null) return; // still in InitializeComponent
        ShowPage(int.TryParse((Nav.SelectedItem as ListBoxItem)?.Tag as string, out var t) ? t : 0);
    }

    private void EnableNav()
    {
        foreach (var item in Nav.Items.OfType<ListBoxItem>()) item.IsEnabled = true;
    }

    private void SetStatus(string s) => TxtNavStatus.Text = s;
}
