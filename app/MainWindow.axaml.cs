using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AStoryWealth.Core;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Installer;

public partial class MainWindow : Window
{
    private string? _collectionPath;
    private AddonManifest? _manifest;
    private const string AddonId = "a-storywealth";
    private const string NexusCollectionUrl = "https://www.nexusmods.com/games/fallout4/collections/5atq9t";
    private const string DiscordUrl = "https://discord.gg/storywealth";
    private string? _apiKey;
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
        _apiKey = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY") ?? SecretStore.Load("nexus-api-key");
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
        InitUpdates();
        ShowExistingInstall();
        RefreshInstallCheck();
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

    // ---------- removing add-ons and optional mods ----------

    private string? _armed;
    private Avalonia.Threading.DispatcherTimer? _armTimer;
    private string _optNote = "";

    /// <summary>First click arms the button (it asks "sure?" for a few seconds); the second click returns true.</summary>
    private bool Arm(string key, Button btn, string armedText, string normalText)
    {
        if (_armed == key) { _armTimer?.Stop(); _armed = null; btn.Content = normalText; return true; }
        _armed = key;
        btn.Content = armedText;
        _armTimer?.Stop();
        _armTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _armTimer.Tick += (_, _) => { _armTimer!.Stop(); if (_armed == key) { _armed = null; btn.Content = normalText; } };
        _armTimer.Start();
        return false;
    }

    /// <summary>Why a removal can't happen right now, or null. Mod Organizer 2 rewrites the profile when it closes.</summary>
    private string? RemovalBlocker() =>
        _installing ? "Wait for the current install to finish."
        : Process.GetProcesses().Any(p => p.ProcessName.StartsWith("ModOrganizer", StringComparison.OrdinalIgnoreCase))
            ? "Close Mod Organizer 2 first. It rewrites your profile when it closes and would undo the removal."
        : null;

    private HashSet<string> BaseModNames(bool requiredOnly) =>
        new((_manifest?.Mods ?? []).Where(m => !requiredOnly || !m.IsOptional).Select(m => m.InstallAs), StringComparer.OrdinalIgnoreCase);

    private async void OnAddDetRemove(object? sender, RoutedEventArgs e)
    {
        if (_addonDetailCard is not { } c || InstallState.GetInstallDir() is not { } dir) return;
        if (RemovalBlocker() is { } why) { TxtAddDetStatus.Text = why; return; }
        var names = BaseModNames(requiredOnly: false);
        var (del, kept) = Removal.PlanAddon(dir, c.Slug, names);
        if (!Arm("addon:" + c.Slug, BtnAddDetRemove, $"Remove {del.Count} mods? Click again", "Remove"))
        {
            TxtAddDetStatus.Text = $"This deletes {del.Count} mod folders" + (kept.Count > 0 ? $" and keeps {kept.Count} that something else needs" : "")
                + ". Downloaded archives stay. Click Remove again to confirm.";
            return;
        }
        BtnAddDetRemove.IsEnabled = false;
        var res = await Task.Run(() => Removal.RemoveAddon(dir, c.Slug, names));
        BtnAddDetRemove.IsEnabled = true;
        RefreshAddons();
        ShowAddonDetail(new AddonCard(c.Slug, c.Name, c.Author, c.Version, false, c.HeaderImageUrl, null, c.Stats));
        TxtAddDetStatus.Text = res.Refused ?? $"Removed. {res.FoldersDeleted} mod folders deleted ({SizeText(res.BytesFreed)})."
            + (res.Kept.Count > 0 ? $" {res.Kept.Count} kept because something else needs them." : "");
    }

    private async void RemoveOptionalAsync(OptionalItem it, Button btn)
    {
        if (RemovalBlocker() is { } why) { _optNote = why; UpdateOptSummary(); return; }
        if (InstallState.GetInstallDir() is not { } dir) return;
        if (!Arm("opt:" + it.SourceId + it.Md5, btn, "Sure?", "Remove")) { _optNote = $"Click Remove again to delete {it.Name}."; UpdateOptSummary(); return; }
        var required = BaseModNames(requiredOnly: true);
        var res = await Task.Run(() => Removal.RemoveOptional(dir, it.SourceId == OptionalCatalog.BaseId ? null : it.SourceId, it.InstallAs, it.Md5, required));
        _optNote = res.Refused ?? $"Removed {it.Name} ({SizeText(res.BytesFreed)}).";
        RefreshOptionalTab();
    }

    // ---------- support report ----------

    private async void OnSaveReport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save report",
            SuggestedFileName = $"aSW-report-{_version}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Zip file") { Patterns = ["*.zip"] }],
        });
        if (file is null) return;
        var path = file.Path.LocalPath;
        var ctx = new ReportContext(_version, InstallState.GetInstallDir(), GameDir() is { Length: > 0 } g ? g : null,
            string.IsNullOrWhiteSpace(TxtDownloadsDir.Text) ? null : TxtDownloadsDir.Text.Trim(),
            RbStockGame.IsChecked, _signedIn ? _authPremium : null, InstallState.GetResume() is not null, _apiKey);
        try
        {
            await Task.Run(() => ReportBuilder.Build(path, ctx));
            TxtReportStatus.Text = $"Saved to {path}. Send that file when you ask for help.";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InstallLog.Warn("report", ex.Message);
            TxtReportStatus.Text = $"Couldn't save the report: {ex.Message}";
        }
    }

    // ---------- existing install ----------

    private void ShowExistingInstall()
    {
        var dir = InstallState.GetInstallDir();
        TxtExistingInstall.Text = dir is null
            ? "No install is recorded. If you've already installed A StoryWealth, choose its folder (the one with ModOrganizer.exe) and the app will pick it up."
            : $"The app is using {dir}. Choose a different folder to switch to another install.";
    }

    private async void OnPickExistingInstall(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose your install folder (the one with ModOrganizer.exe)", AllowMultiple = false });
        if (folders.Count == 0) return;
        var dir = folders[0].Path.LocalPath.TrimEnd('\\', '/');
        if (!File.Exists(Path.Combine(dir, "ModOrganizer.exe")))
        {
            TxtExistingInstall.Text = "That folder doesn't have ModOrganizer.exe in it. Choose the folder that does.";
            return;
        }
        InstallState.SetInstallDir(dir);
        _installedDir = dir;
        TxtInstallDir.Text = dir;
        RefreshInstallCheck();
        ShowExistingInstall();
    }

    // ---------- updates (GitHub releases) ----------

    private static readonly UpdateConfig UpdateCfg = UpdateConfig.Load();
    private string _version = "0.0.0";
    private GitHubUpdater? _updater;
    private UpdateInfo? _updateInfo;
    private string? _stagedUpdate;
    private bool _updateBusy;
    private Avalonia.Threading.DispatcherTimer? _updateTimer;
    private int _updateCountdown;

    private void InitUpdates()
    {
        var settings = UpdateSettings.Load();
        ChkUpdateAuto.IsChecked = settings.CheckAtStartup;
        if (!UpdateCfg.Enabled)
        {
            TxtUpdateStatus.Text = $"You're on version {_version}. Updates aren't set up in this build.";
            ChkUpdateAuto.IsEnabled = BtnUpdateCheck.IsEnabled = false;
            return;
        }
        TxtUpdateStatus.Text = $"You're on version {_version}.";
        _ = Task.Run(() => { try { foreach (var d in Directory.GetDirectories(GitHubUpdater.DefaultStagingRoot())) Directory.Delete(d, true); } catch { } });   // leftovers of an earlier update
        if (settings.CheckAtStartup) _ = Task.Run(async () => { await Task.Delay(4000); await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CheckForUpdateAsync(false)); });
    }

    private void OnUpdateAutoChanged(object? sender, RoutedEventArgs e) => new UpdateSettings { CheckAtStartup = ChkUpdateAuto.IsChecked == true }.Save();

    private async void OnCheckUpdateNow(object? sender, RoutedEventArgs e) => await CheckForUpdateAsync(true);

    /// <summary>Looks for a newer release, downloads and checks it, then offers (and after a short countdown does) the restart.</summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy || !UpdateCfg.Enabled || _stagedUpdate is not null) return;
        _updateBusy = true;
        BtnUpdateCheck.IsEnabled = false;
        try
        {
            _updater ??= new GitHubUpdater(UpdateCfg);
            if (manual) TxtUpdateStatus.Text = "Checking…";
            var info = await _updater.CheckAsync(_version);
            if (info is null)
            {
                if (manual) TxtUpdateStatus.Text = $"You're on the latest version ({_version}).";
                return;
            }
            TxtUpdateStatus.Text = $"Version {info.Version} is available. Downloading…";
            var progress = new Progress<double>(p => TxtUpdateStatus.Text = $"Downloading version {info.Version}… {p:F0}%");
            _stagedUpdate = await _updater.DownloadAndVerifyAsync(info, _version, progress);
            _updateInfo = info;
            TxtUpdateStatus.Text = $"Version {info.Version} is downloaded and checked. Restart to install it.";
            ShowUpdateBar();
        }
        catch (Exception ex)
        {
            InstallLog.Warn("update", ex.Message);
            TxtUpdateStatus.Text = manual ? $"Couldn't check for updates: {ex.Message}" : $"You're on version {_version}.";
        }
        finally { _updateBusy = false; BtnUpdateCheck.IsEnabled = true; }
    }

    private void ShowUpdateBar()
    {
        if (_updateInfo is null) return;
        UpdateBar.IsVisible = true;
        _updateCountdown = 20;
        TxtUpdateBar.Text = $"Version {_updateInfo.Version} is ready. Restarting in {_updateCountdown} s to install it.";
        _updateTimer?.Stop();
        _updateTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += (_, _) =>
        {
            if (_installing)   // never restart in the middle of an install
            {
                _updateCountdown = 20;
                TxtUpdateBar.Text = $"Version {_updateInfo.Version} is ready. It installs once your current install is done.";
                return;
            }
            if (--_updateCountdown <= 0) { ApplyUpdate(); return; }
            TxtUpdateBar.Text = $"Version {_updateInfo.Version} is ready. Restarting in {_updateCountdown} s to install it.";
        };
        _updateTimer.Start();
    }

    private void OnUpdateNow(object? sender, RoutedEventArgs e) => ApplyUpdate();

    private void OnUpdateLater(object? sender, RoutedEventArgs e)
    {
        _updateTimer?.Stop();
        UpdateBar.IsVisible = false;
        TxtUpdateStatus.Text = $"Version {_updateInfo?.Version} is downloaded. It will be offered again next time the app starts.";
        _stagedUpdate = null;   // the staged copy is cleaned up at the next start
    }

    private void ApplyUpdate()
    {
        if (_installing || _stagedUpdate is null) return;
        _updateTimer?.Stop();
        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (!UpdateApplier.CanWrite(appDir))
        {
            UpdateBar.IsVisible = true;
            TxtUpdateBar.Text = "This folder is read-only, so the app can't update itself here. Download the new version from GitHub instead.";
            BtnUpdateNow.IsVisible = false;
            return;
        }
        try
        {
            UpdateApplier.LaunchHelper(_stagedUpdate, appDir);
            Close();
        }
        catch (Exception ex)
        {
            InstallLog.Warn("update", ex.Message);
            TxtUpdateBar.Text = $"Couldn't start the update: {ex.Message}";
        }
    }

    // ---------- icon maker (development only) ----------

    /// <summary>
    /// With ASW_MAKE_ICON=&lt;dir&gt; the window draws the app icon (gold "A" on the dark rail colour, "SW" under it at larger
    /// sizes) at 16–256 px, writes icon-256.png and a multi-size app.ico into that folder, and closes. Replace the files in
    /// app/Assets to change the icon.
    /// </summary>
    private async Task MakeIconAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(500);
        var gold = new SolidColorBrush(Color.Parse("#F2C48D"));
        byte[] Render(int size)
        {
            var root = new Border
            {
                Width = size, Height = size, Background = new SolidColorBrush(Color.Parse("#171A20")),
                CornerRadius = new Avalonia.CornerRadius(size * 0.22), BorderBrush = gold, BorderThickness = new Avalonia.Thickness(Math.Max(1, size / 24.0)),
            };
            var grid = new Grid();
            grid.Children.Add(new TextBlock
            {
                Text = "A", FontSize = size * (size >= 48 ? 0.56 : 0.72), FontWeight = FontWeight.Bold, Foreground = gold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 0, size >= 48 ? size * 0.08 : 0),
            });
            if (size >= 48)
                grid.Children.Add(new TextBlock
                {
                    Text = "SW", FontSize = size * 0.17, FontWeight = FontWeight.SemiBold, Foreground = gold, Opacity = 0.85,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                    Margin = new Avalonia.Thickness(0, 0, 0, size * 0.1),
                });
            root.Child = grid;
            root.Measure(new Avalonia.Size(size, size));
            root.Arrange(new Avalonia.Rect(0, 0, size, size));
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(size, size));
            bmp.Render(root);
            using var ms = new MemoryStream();
            bmp.Save(ms, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            return ms.ToArray();
        }
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var pngs = sizes.Select(Render).ToList();
        File.WriteAllBytes(Path.Combine(dir, "icon-256.png"), pngs[^1]);
        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);                 // ICONDIR
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);                                                 // PNG-compressed images
        File.WriteAllBytes(Path.Combine(dir, "app.ico"), ico.ToArray());
    }

    // ---------- screenshot hook (development only) ----------

    /// <summary>
    /// With ASW_UI_SHOT=&lt;dir&gt; the window renders each page to a PNG in that folder and closes (ASW_UI_SIZE=WxH
    /// picks the window size). Lets the UI be checked without a person clicking through it; does nothing otherwise.
    /// </summary>
    private async void RunScreenshotTour(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (Environment.GetEnvironmentVariable("ASW_UI_SIZE")?.Split('x') is [var w, var h] && double.TryParse(w, out var ww) && double.TryParse(h, out var hh))
            { Width = ww; Height = hh; }
            await Task.Delay(6000); // let the collection sync and cover images arrive
            async Task Shot(string name)
            {
                await Task.Delay(700);
                var size = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bmp.Render(this);
                bmp.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            string[] names = ["modlist", "setup", "nexus", "install", "done", "detail", "addons", "settings", "addon-detail", "optional"];
            for (var i = 0; i < names.Length; i++)
            {
                if (i == 6) { StartAddonGrab(); for (var tries = 0; tries < 60 && _liveAddons is null; tries++) await Task.Delay(500); await Task.Delay(1500); }
                if (i == 8)
                {
                    var c0 = _liveAddons?.FirstOrDefault(c => c.Slug == "vq4q7d") ?? _liveAddons?.FirstOrDefault();
                    if (c0 is not null) ShowAddonDetail(new AddonCard(c0.Slug, c0.Name, c0.Author, $"r{c0.LatestRevision}", false, c0.HeaderImageUrl, (InstallState.GetInstallDir() is { } idir ? AddonRegistry.Load(idir).Addons.FirstOrDefault(a => a.Id == c0.Slug)?.Revision : null), c0.Stats));
                    await Task.Delay(1500);
                }
                if (i == 3) { TxtPhase.Text = "Downloading mod 42 of 911"; TxtItem.Text = "Sample Mod"; TxtSpeed.Text = "12.4 MB/s"; TxtCounts.Text = "41/911"; PbOverall.Value = 4.5; OnLogLine("sample log line"); FlushLog(); }
                if (i == 5) OnOpenDetail(null, new RoutedEventArgs());
                ShowPage(i);
                await Shot(names[i]);
                if (i == 1 && PageSetup.Children.OfType<ScrollViewer>().FirstOrDefault() is { } sv) { sv.ScrollToEnd(); await Shot("setup-bottom"); }
            }
            // install page, free-account wait state and a stopped run
            ShowPage(3);
            TxtPhase.Text = "Your turn: download mod 3 of 911 in your browser"; TxtSpeed.Text = "On the page: Manual download, then Slow download";
            BtnOpenPage.IsVisible = true; await Shot("install-manual");
            BtnOpenPage.IsVisible = false; TxtPhase.Text = "Stopped. Your downloads so far are kept. Press Resume to continue."; OfferContinue(false); await Shot("install-stopped");
            CardManual.IsVisible = true; CardVortex.IsVisible = true; TxtVortexCheck.Text = "880 of 911 mods were found in your Vortex folder. 31 (4.2 GB) still need downloading.";
            ShowPage(2); await Shot("nexus-free-vortex");
            TxtSetupError.Text = "Choose an install folder."; ShowPage(1); await Shot("setup-error");
        }
        catch (Exception ex) { InstallLog.Error("ui", $"screenshot tour failed: {ex}"); }
        Close();
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
        TxtStatSize.Text = $"~{gb:N0} GB";
        TxtStatStorage.Text = $"~{gb * 2:N0} GB";
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
        Control[] pages = [PageModlist, PageSetup, PageNexus, PageInstall, PageDone, PageDetail, PageAddons, PageSettings, PageAddonDetail, PageOptionalTab];
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == page;

        // left rail: Modlist(0) / Add-Ons(6) / Settings(7)
        Nav.SelectionChanged -= OnNavChanged;
        Nav.SelectedIndex = page is 6 or 8 ? 1 : page == 9 ? 2 : page == 7 ? 3 : 0;
        Nav.SelectionChanged += OnNavChanged;

        // wizard step bar only during an install flow.
        WizBar.IsVisible = page is >= 1 and <= 4;
        Avalonia.Controls.Primitives.ToggleButton[] steps = [Wiz0, Wiz1, Wiz2, Wiz3];
        var step = page switch { 0 => 0, 1 => 1, 2 or 3 => 2, 4 => 3, _ => -1 };
        for (var i = 0; i < steps.Length; i++) steps[i].IsChecked = i == step;
        if (page == 6) { StartAddonGrab(); RefreshAddons(); }
        if (page == 9) RefreshOptionalTab();
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
        if (_addonGrabbed || _apiKey is null) return;
        _addonGrabbed = true;
        TxtAddonsStatus.Text = "Checking for add-ons…";
        TxtAddonsStatus.IsVisible = true;
        try
        {
            var prog = new Progress<string>(s => TxtAddonsStatus.Text = s);
            _liveAddons = await AddonGrabber.ListAsync(_apiKey, prog);
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
        ShowPage(Nav.SelectedIndex switch { 1 => 6, 2 => 9, 3 => 7, _ => 0 });
    }

    private void EnableNav(int upTo)
    {
        foreach (var item in Nav.Items.OfType<ListBoxItem>()) item.IsEnabled = true;
    }

    private void SetStatus(string s) => TxtNavStatus.Text = s;

    // ---------- page 0: modlist ----------

    private async void OnPickCollection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a collection file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Collection") { Patterns = ["*.7z", "*.json"] }]
        });
        if (files.Count == 0) return;
        var picked = files[0].Path.LocalPath;
        if (!picked.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            // the archive Nexus gives for a manual collection download: take collection.json out of it
            try
            {
                var outDir = Path.Combine(SyncCacheDir, "manual", Path.GetFileNameWithoutExtension(picked));
                Directory.CreateDirectory(outDir);
                await Task.Run(() => SevenZip.ExtractSingleFile(picked, "collection.json", outDir));
                picked = Path.Combine(outDir, "collection.json");
            }
            catch (Exception ex)
            {
                InstallLog.Error("ui", $"couldn't open collection archive {picked}: {ex.Message}");
                TxtCollectionSummary.Text = "Couldn't read that file. Choose the collection archive (.7z) you downloaded from Nexus.";
                return;
            }
        }
        await LoadCollectionAsync(picked);
    }

    private async Task LoadCollectionAsync(string path)
    {
        try
        {
            var manifest = await Task.Run(() => VortexCollectionMapper.Map(path, AddonId));
            _collectionPath = path;
            _manifest = manifest;
            UpdateOptionalLabels();
            UpdateStats();
            TxtCollectionSummary.Text = "";
            PnlPickCollection.IsVisible = false;
            BtnToInstall.IsEnabled = _signedIn;
            BtnDetailInstall.IsEnabled = true;
            EnableNav(1);
            SetStatus("");
        }
        catch (Exception ex)
        {
            TxtCollectionSummary.Text = "That file doesn't look like a valid collection. Details are in the log.";
            InstallLog.Error("ui", $"manifest load failed for {path}: {ex.Message}");
        }
    }

    private void OnAddonsFilter(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RefreshAddons();

    /// <summary>Renders add-on cards, honoring the update and author filters.</summary>
    private bool _suppressAuthorChanged;
    private void RefreshAddons()
    {
        var installed = InstallState.GetInstallDir() is not null;

        // entries: live GraphQL listing preferred; cached manifests as fallback
        List<AddonCard> cards;
        if (_liveAddons is { } live)
        {
            // what this instance has installed (AddonRegistry) decides "Installed" and "Update available"
            var dir = InstallState.GetInstallDir();
            var have = dir is null ? [] : AddonRegistry.Load(dir).Addons.ToDictionary(a => a.Id, a => a.Revision, StringComparer.OrdinalIgnoreCase);
            cards = live.Select(c =>
            {
                have.TryGetValue(c.Slug, out var haveRev);
                var update = haveRev is { Length: > 0 } && int.TryParse(haveRev.TrimStart('r'), out var hr) && hr < c.LatestRevision;
                return new AddonCard(c.Slug, c.Name, c.Author, $"r{c.LatestRevision}", update, string.IsNullOrWhiteSpace(c.HeaderImageUrl) ? c.TileImageUrl : c.HeaderImageUrl,
                    have.ContainsKey(c.Slug) ? (haveRev is { Length: > 0 } ? haveRev : "installed") : null, c.Stats);
            }).ToList();
        }
        else
        {
            cards = AddonStore.List(null)
                .Select(a => new AddonCard(Path.GetFileNameWithoutExtension(a.Path), a.Manifest.Name, a.Manifest.Author, a.Manifest.Version, a.UpdateAvailable, ""))
                .ToList();
        }
        cards = (CmbAddonsSort.SelectedIndex switch
        {
            1 => cards.OrderByDescending(c => c.Stats?.TotalDownloads ?? 0),
            2 => cards.OrderByDescending(c => c.Stats?.Endorsements ?? 0),
            3 => cards.OrderByDescending(c => c.Stats?.LastPublished ?? DateTime.MinValue),
            _ => cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        }).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // author filter dropdown: All + one entry per author, keep current selection
        var authors = cards.Select(c => c.Author).Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var selected = (CmbAddonsAuthor.SelectedItem as string) is { } s && authors.Contains(s, StringComparer.OrdinalIgnoreCase) ? s : "All authors";
        _suppressAuthorChanged = true;
        CmbAddonsAuthor.ItemsSource = new[] { "All authors" }.Concat(authors).ToList();
        CmbAddonsAuthor.SelectedItem = selected;
        _suppressAuthorChanged = false;

        GridAddons.Children.Clear();
        var rows = cards
            .Where(a => selected == "All authors" || string.Equals(a.Author, selected, StringComparison.OrdinalIgnoreCase))
            .Where(a => TglAddonsUpdate.IsChecked != true || a.UpdateAvailable)
            .ToList();
        var listing = _apiKey != null && _liveAddons is null && _addonGrabbed; // still fetching the list
        TxtAddonsEmpty.Text = cards.Count == 0
            ? (listing ? "" : "No add-ons are available yet.")
            : rows.Count == 0 ? "No add-ons match this filter." : "";
        foreach (var a in rows) GridAddons.Children.Add(BuildAddonCard(a, installed));
    }

    private sealed record AddonCard(string Slug, string Name, string Author, string Version, bool UpdateAvailable, string HeaderImageUrl, string? InstalledRev = null,
        NexusApiClient.CollectionStats? Stats = null);

    private void OnAddonsSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GridAddons is null) return;   // fired while the window is still being built
        RefreshAddons();
    }

    /// <summary>1,234 -> "1.2k", 1,061,268 -> "1.06M".</summary>
    private static string Compact(long n) => n switch
    {
        >= 10_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000_000 => $"{n / 1_000_000.0:F2}M",
        >= 10_000 => $"{n / 1_000.0:F0}k",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString("N0"),
    };

    /// <summary>One short line for a card: downloads, endorsements, mod count and download size.</summary>
    private static string StatsLine(NexusApiClient.CollectionStats s, bool withSize = true) =>
        $"⬇ {Compact(s.TotalDownloads)}   ♥ {Compact(s.Endorsements)}   {s.ModCount:N0} mods" + (withSize ? $"   {SizeText(s.SizeBytes)}" : "");

    /// <summary>The longer description for a detail page.</summary>
    private static string StatsDetail(NexusApiClient.CollectionStats s, string sizeNote = "", bool stacked = false)
    {
        var rating = s.RatingCount > 0 ? $"{s.RatingPercent:F1}% positive ({s.RatingCount:N0} ratings)" : "no ratings yet";
        var updated = s.LastPublished is { } d ? d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "?";
        var facts = new List<string>
        {
            $"Downloads  {s.TotalDownloads:N0} ({s.UniqueDownloads:N0} unique)", $"Endorsements  {s.Endorsements:N0}", $"Rating  {rating}",
            $"{s.ModCount:N0} mods", $"{SizeText(s.SizeBytes)} to download{sizeNote}", $"Last updated  {updated}",
        };
        if (s.AdultContent) facts.Add("Adult content");
        // wide pages: two lines of facts; the narrow base-list column: four short lines
        return stacked
            ? string.Join("\n", facts[0], $"{facts[1]}   ·   {facts[2]}", $"{facts[3]}   ·   {facts[4]}", string.Join("   ·   ", facts.Skip(5)))
            : string.Join("    ·    ", facts.Take(3)) + "\n" + string.Join("    ·    ", facts.Skip(3));
    }

    private void OnAddonsAuthorChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_suppressAuthorChanged) return;
        RefreshAddons();
    }

    private void ShowAddonDetail(AddonCard c)
    {
        _addonDetailCard = c;
        TxtAddDetName.Text = c.Name;
        TxtAddDetBy.Text = $"By {c.Author} - Revision {c.Version}";
        TxtAddDetSummary.Text = _liveSummary.GetValueOrDefault(c.Slug, "");
        TxtAddDetStats.Text = c.Stats is { } stats ? StatsDetail(stats) : "";
        BtnAddDetRemove.IsVisible = c.InstalledRev is not null;
        BtnAddDetRemove.Content = "Remove";
        _addonJsonPath = null;           // belongs to whichever add-on was last fetched
        PnlAddDetManual.IsVisible = false;
        var baseInstalled = InstallState.GetInstallDir() is not null;
        TxtAddDetStatus.Text = !baseInstalled ? "Install the base list first; add-ons are placed on top of it."
            : c.UpdateAvailable ? $"Installed ({c.InstalledRev}). A newer revision is available."
            : c.InstalledRev is not null ? $"Installed ({c.InstalledRev}). Installing again re-applies it." : "";
        BtnAddDetInstall.Content = c.UpdateAvailable ? "Update" : c.InstalledRev is not null ? "Reinstall" : "Install";
        BtnAddDetInstall.IsEnabled = baseInstalled && _apiKey != null && _collectionPath != null;
        _ = LoadImageAsync(ImgAddDet, c.HeaderImageUrl, "addon-" + c.Slug);
        ShowPage(8);
    }

    /// <summary>collection.json from what the user picked: the .json itself, or the .7z Nexus gives for a manual collection download.</summary>
    private async Task<string?> CollectionPathFromPickedAsync(string picked)
    {
        if (picked.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return picked;
        try
        {
            var outDir = Path.Combine(SyncCacheDir, "manual", Path.GetFileNameWithoutExtension(picked));
            Directory.CreateDirectory(outDir);
            await Task.Run(() => SevenZip.ExtractSingleFile(picked, "collection.json", outDir));
            return Path.Combine(outDir, "collection.json");
        }
        catch (Exception ex)
        {
            InstallLog.Error("ui", $"couldn't open collection archive {picked}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Display name without the "aSW <Addon> - " install-folder prefix.</summary>
    private static string ShortModName(string s)
    {
        if (s.StartsWith("aSW ", StringComparison.Ordinal))
            s = s["aSW ".Length..];
        var i = s.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? s[(i + 3)..] : s;
    }

    private string? _addonJsonSlug;

    /// <summary>One button: fetch the add-on's collection (Premium) if needed, then install it on top of the base list.</summary>
    private async void OnAddDetInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_addonDetailCard is not { } card || _collectionPath is null || _apiKey is null) return;
        if (InstallState.GetInstallDir() is null) { TxtAddDetStatus.Text = "Install the base list first."; return; }
        BtnAddDetInstall.IsEnabled = false;
        PnlAddDetManual.IsVisible = false;
        if (_addonJsonPath is null || _addonJsonSlug != card.Slug)
        {
            TxtAddDetStatus.Text = "Fetching the add-on list…";
            try
            {
                var sync = new CollectionSync(SyncCacheDir);
                var addon = await Task.Run(() => sync.EnsureAddonJsonAsync(_apiKey!, card.Slug, card.Name));
                _addonJsonPath = addon.CollectionJsonPath;
                _addonJsonSlug = card.Slug;
            }
            catch (Exception ex)
            {
                InstallLog.Info("addons", $"fetch {card.Slug}: {ex.Message}");
                var premium = ex.Message.Contains("premium", StringComparison.OrdinalIgnoreCase);
                TxtAddDetStatus.Text = premium
                    ? "Free accounts can't fetch this list automatically. Open the collection page, use Manual download, then choose the file you get."
                    : "Couldn't fetch the add-on list. Check your connection and try again; the details are in the logs.";
                PnlAddDetManual.IsVisible = premium;
                BtnAddDetInstall.IsEnabled = true;
                return;
            }
        }
        StartAddonInstall();
    }

    private void StartAddonInstall()
    {
        TxtAddDetStatus.Text = "";
        BtnAddDetInstall.IsEnabled = true;
        _addonInstall = true;
        TxtInstallDir.Text = InstallState.GetLastDir() ?? TxtInstallDir.Text;
        ClearInstallPage();
        ShowPage(3);
        _ = StartInstallAsync();
    }

    private void OnAddDetOpenPage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_addonDetailCard is { } c)
            Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/games/fallout4/collections/{c.Slug}") { UseShellExecute = true });
    }

    private async void OnAddDetPick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_addonDetailCard is not { } card) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the collection you downloaded",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Collection") { Patterns = ["*.7z", "*.json"] }]
        });
        if (files.Count == 0) return;
        var path = await CollectionPathFromPickedAsync(files[0].Path.LocalPath);
        if (path is null) { TxtAddDetStatus.Text = "Couldn't read that file. Choose the collection archive (.7z) you downloaded from Nexus."; return; }
        _addonJsonPath = path;
        _addonJsonSlug = card.Slug;
        PnlAddDetManual.IsVisible = false;
        StartAddonInstall();
    }

    private void OnAddDetBack(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(6);

    private static readonly HttpClient _imgHttp = new();

    /// <summary>Downloads a collection banner once and caches it in %LOCALAPPDATA%\AStoryWealth\Covers.</summary>
    private static async Task LoadImageAsync(Image? img, string? url, string key)
    {
        if (img is null || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "Covers");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var path = Path.Combine(dir, key + ext);
            if (!File.Exists(path))
            {
                var bytes = await _imgHttp.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(path, bytes);
            }
            img.Source = new Avalonia.Media.Imaging.Bitmap(path);
            img.IsVisible = true;
        }
        catch { /* no image: gradient placeholder stays */ }
    }

    /// <summary>Pulls the base collection banner from Nexus for the gallery card and detail page.</summary>
    private async Task LoadBaseImagesAsync()
    {
        if (_apiKey is null) return;
        try
        {
            using var api = new NexusApiClient(_apiKey);
            var info = await api.GetCollectionInfoAsync(CollectionSync.BaseSlug);
            if (info.Stats is { } bs)
            {
                TxtBaseStats.Text = StatsLine(bs, withSize: false);
                TxtBaseStatsDetail.Text = StatsDetail(bs, " (everything, including optional files)", stacked: true);
            }
            await LoadImageAsync(ImgBaseCard, info.HeaderImageUrl, "base");
            await LoadImageAsync(ImgDetailBanner, info.HeaderImageUrl, "base");
        }
        catch { /* offline: gradient stays */ }
    }

    private Border BuildAddonCard(AddonCard c, bool installed)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        _ = LoadImageAsync(img, c.HeaderImageUrl, "addon-" + c.Slug);

        var overlay = new Panel();
        overlay.Background = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#00000000"), 0),
                new GradientStop(Color.Parse("#B3000000"), 1)
            }
        };

        var banner = new Border
        {
            Height = 236,
            ClipToBounds = true,
            Child = new Panel { Children = { img, overlay } }
        };

        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Avalonia.Thickness(14, 0, 0, 12) };
        text.Children.Add(new TextBlock
        { Text = c.Name, FontSize = 17, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#FDF6EC")), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        { Text = $"By {c.Author} - Revision {c.Version}", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#D9CBA8")), TextWrapping = TextWrapping.Wrap });
        if (c.Stats is { } st)
            text.Children.Add(new TextBlock
            { Text = StatsLine(st), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#F2C48D")), TextWrapping = TextWrapping.Wrap });
        if (c.UpdateAvailable)
            text.Children.Add(new TextBlock
            { Text = $"Installed ({c.InstalledRev}) · update available", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#F2C48D")) });
        else if (c.InstalledRev is not null)
            text.Children.Add(new TextBlock
            { Text = c.InstalledRev == "installed" ? "Installed" : $"Installed ({c.InstalledRev})", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9FE0A8")) });
        if (!installed)
            text.Children.Add(new TextBlock
            { Text = "Install the base list first", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B8BEC9")) });

        var card = new Border
        {
            Classes = { "card" },
            Width = 380,
            Padding = new Avalonia.Thickness(0),
            Tag = c.Slug,
            Child = new Panel { Children = { banner, text } }
        };
        card.Tapped += (_, _) => ShowAddonDetail(c);
        return card;
    }

    private void OnOpenLogs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dir = InstallState.GetInstallDir()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    /// <summary>Card click opens the detail page: links + archive list from collection.json.</summary>
    private void OnOpenDetail(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_manifest is null) return;
        const string prefix = "aSW AStorywealth";
        _archiveRows = _manifest.Mods
            .Select(m =>
            {
                var name = m.InstallAs;
                name = name.StartsWith(prefix + " - ", StringComparison.OrdinalIgnoreCase) ? name[(prefix.Length + 3)..]
                     : name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..].TrimStart(' ', '-')
                     : name;
                return new ArchiveRow(name, m.Nexus?.FileSize is long b && b > 0 ? SizeText(b) : "?",
                    m.Nexus?.FileSize ?? 0);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RefreshArchives();
        ShowPage(5);
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        _ => $"{bytes / 1_000.0:F0} KB"
    };

    private List<ArchiveRow>? _archiveRows;

    private void OnArchiveSort(object? sender, SelectionChangedEventArgs e) => RefreshArchives();
    private void OnArchiveFilter(object? sender, TextChangedEventArgs e) => RefreshArchives();

    private void RefreshArchives()
    {
        if (_archiveRows is null) return;
        var rows = _archiveRows.AsEnumerable();
        var filter = TxtArchiveFilter.Text?.Trim();
        if (!string.IsNullOrEmpty(filter))
            rows = rows.Where(r => r.Size.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        rows = CmbSort.SelectedIndex switch
        {
            1 => rows.OrderByDescending(r => r.Bytes),
            2 => rows.OrderBy(r => r.Bytes),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        };
        var list = rows.ToList();
        LstArchives.ItemsSource = list;
        TxtArchiveCount.Text = $"{list.Count:N0} archives";
    }

    private void OnOpenNexus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(NexusCollectionUrl) { UseShellExecute = true });

    private void OnOpenDiscord(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DiscordUrl) { UseShellExecute = true });

    private void OnToSetup(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtInstallDir.Text))
            TxtInstallDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AStoryWealth", "Instance");
        // a fresh visit starts from the defaults (Sim Settlements 2 on, HD textures off); only an unfinished install keeps its own choices
        if (_resume?.OptionalGroups is null && !_installing)
        {
            var defaults = AStoryWealth.Core.OptionalGroups.Load(AddonId).ToDictionary(g => g.Id, g => g.DefaultOn);
            foreach (var box in OptionalBoxes) box.IsChecked = defaults.GetValueOrDefault((string)box.Tag!, false);
        }
        // the downloads box must never hold a relative default (".\Downloads" from before an install folder existed)
        var dl = (TxtDownloadsDir.Text ?? "").Trim();
        if (dl.Length == 0 || !Path.IsPathRooted(dl))
            TxtDownloadsDir.Text = Path.Combine(TxtInstallDir.Text!.Trim(), "Downloads");
        ShowPage(1);
    }

    private void OnBackToModlist(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(0);

    private void OnToggleArchives(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ColArchives.IsVisible = !ColArchives.IsVisible;

    private void OnDetailInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OnToSetup(sender, e); // fills the install/downloads defaults, then opens Setup

    // ---------- page 1: setup ----------

    private void OnSourceChanged()
    {
        var fresh = RbFresh.IsChecked == true;
        PnlFreshDownloads.IsVisible = fresh;
        PnlVortexDownloads.IsVisible = !fresh;
        if (!fresh && string.IsNullOrWhiteSpace(TxtReuseDir.Text))
        {
            var vortexDefault = DownloadReuse.DefaultVortexDir();
            if (vortexDefault is not null) TxtReuseDir.Text = vortexDefault;
        }
        if (fresh && string.IsNullOrWhiteSpace(TxtDownloadsDir.Text))
            TxtDownloadsDir.Text = Path.Combine(string.IsNullOrEmpty(TxtInstallDir.Text) ? "." : TxtInstallDir.Text, "Downloads");
    }

    private async void OnPickDownloads(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose downloads location", AllowMultiple = false });
        if (folders.Count > 0) TxtDownloadsDir.Text = folders[0].Path.LocalPath;
    }

    private async void OnPickDir(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose install location", AllowMultiple = false });
        if (folders.Count > 0) TxtInstallDir.Text = folders[0].Path.LocalPath;
    }

    private async void OnPickReuse(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose your downloads folder", AllowMultiple = false });
        if (folders.Count > 0) TxtReuseDir.Text = folders[0].Path.LocalPath;
    }

    private void OnValidateDir(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dir = TxtInstallDir.Text;
        if (string.IsNullOrWhiteSpace(dir)) { TxtDirStatus.Text = "Pick a folder first."; return; }
        TxtDirStatus.Text = CheckSpace(dir.Trim()) is { } problem ? problem : SpaceSummary(dir.Trim());
    }

    /// <summary>Free space per drive against what the ticked groups need: downloads once, plus about the same again for the installed mods.</summary>
    private (string Install, string Downloads, double InstallNeed, double DownloadsNeed) SpacePlan(string installDir)
    {
        var gb = SelectionTotals().Gb;
        var dl = RbFresh.IsChecked == true && !string.IsNullOrWhiteSpace(TxtDownloadsDir.Text)
            ? TxtDownloadsDir.Text!.Trim() : installDir;
        var iRoot = Path.GetPathRoot(Path.GetFullPath(installDir)) ?? "";
        var dRoot = Path.GetPathRoot(Path.GetFullPath(dl)) ?? "";
        return string.Equals(iRoot, dRoot, StringComparison.OrdinalIgnoreCase)
            ? (iRoot, dRoot, gb * 2, 0)
            : (iRoot, dRoot, gb, gb);
    }

    private static double FreeGb(string root) => new DriveInfo(root).AvailableFreeSpace / 1_000_000_000.0;

    /// <summary>Null when every drive involved has the space; otherwise the message to show.</summary>
    private string? CheckSpace(string installDir)
    {
        try
        {
            if (_manifest is null) return null;
            var p = SpacePlan(installDir);
            if (FreeGb(p.Install) < p.InstallNeed)
                return $"Not enough space. {p.Install} has {FreeGb(p.Install):N0} GB free, this install needs about {p.InstallNeed:N0} GB.";
            if (p.DownloadsNeed > 0 && FreeGb(p.Downloads) < p.DownloadsNeed)
                return $"Not enough space for the downloads. {p.Downloads} has {FreeGb(p.Downloads):N0} GB free, they need about {p.DownloadsNeed:N0} GB.";
            return null;
        }
        catch { return "That folder path doesn't look valid. Try Browse."; }
    }

    private string SpaceSummary(string installDir)
    {
        try
        {
            var p = SpacePlan(installDir);
            return $"Good. {p.Install} has enough space (about {p.InstallNeed + p.DownloadsNeed:N0} GB needed).";
        }
        catch { return "That folder path doesn't look valid. Try Browse."; }
    }

    private void OnBackToSetup(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(1);

    /// <summary>The first problem with the Setup page, or null. Runs before moving on so the install never starts half-configured.</summary>
    private string? ValidateSetup()
    {
        var dir = (TxtInstallDir.Text ?? "").Trim();
        if (dir.Length == 0) return "Choose an install folder.";
        if (_manifest is null) return "The mod list hasn't loaded yet. Wait a moment, or check the Nexus sign-in.";
        try
        {
            if (!Path.IsPathRooted(dir) || Path.GetPathRoot(dir) is not { Length: > 0 } root || !Directory.Exists(root))
                return "The install folder isn't a valid path on this PC. Try Browse.";
            if (File.Exists(dir)) return "The install folder is a file. Choose a folder.";
            var dl = (TxtDownloadsDir.Text ?? "").Trim();
            if (RbFresh.IsChecked == true)
            {
                if (dl.Length == 0) return "Choose a downloads folder.";
                if (!Path.IsPathRooted(dl) || !Directory.Exists(Path.GetPathRoot(dl)!))
                    return "The downloads folder isn't a valid path on this PC. Try Browse.";
            }
            else
            {
                var reuse = (TxtReuseDir.Text ?? "").Trim();
                if (reuse.Length == 0 || !Directory.Exists(reuse)) return "The Vortex downloads folder doesn't exist. Point it at your Fallout 4 downloads folder.";
            }
        }
        catch { return "That folder path doesn't look valid. Try Browse."; }
        if (_resume is null && GameDir().Length == 0)
            return "Couldn't find Fallout 4. Browse to the folder that contains Fallout4.exe.";
        return CheckSpace(dir);
    }

    private void OnToNexus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        TxtSetupError.Text = ValidateSetup() ?? "";
        if (TxtSetupError.Text.Length > 0) return;
        ShowPage(2);
        _ = RefreshAuthAsync();
        _ = CheckVortexAsync();
    }

    private int _vortexMissing = -1; // -1 = not checked / not applicable

    /// <summary>The by-hand download card only matters when something actually has to be downloaded.</summary>
    private void UpdateManualCard() =>
        CardManual.IsVisible = _signedIn && !_authPremium && (RbFresh.IsChecked == true || _vortexMissing != 0);

    /// <summary>
    /// Vortex transfer: counts which of the chosen mods are already in the Vortex folder (file name/mod id + exact size;
    /// the install still md5-checks every one) so the user sees what, if anything, still has to be downloaded.
    /// </summary>
    private async Task CheckVortexAsync()
    {
        _vortexMissing = -1;
        CardVortex.IsVisible = RbVortex.IsChecked == true;
        var dir = (TxtReuseDir.Text ?? "").Trim();
        if (RbVortex.IsChecked != true || _manifest is null || !Directory.Exists(dir)) { UpdateManualCard(); return; }
        TxtVortexCheck.Text = "Checking your Vortex downloads…";
        var skip = AStoryWealth.Core.OptionalGroups.Excluded(_manifest.Mods, AStoryWealth.Core.OptionalGroups.Load(AddonId), SelectedGroups());
        var mods = _manifest.Mods.Where(m => m.Nexus is not null && !skip.Contains(m.InstallAs)).ToList();
        var (found, missing, missingBytes) = await Task.Run(async () =>
        {
            DownloadReuse.Refresh(dir);
            int f = 0, miss = 0; long bytes = 0;
            foreach (var m in mods)
            {
                if (await DownloadReuse.FindArchiveAsync(dir, m, _ => Task.FromResult((true, ""))) is not null) f++;
                else { miss++; bytes += m.Nexus?.FileSize ?? 0; }
            }
            return (f, miss, bytes);
        });
        _vortexMissing = missing;
        TxtVortexCheck.Text = missing == 0
            ? $"All {found:N0} mods were found in your Vortex folder. Nothing needs downloading."
            : $"{found:N0} of {found + missing:N0} mods were found in your Vortex folder. {missing:N0} ({missingBytes / 1_000_000_000.0:N1} GB) still need downloading"
              + (_signedIn && !_authPremium ? ", which you'll do by hand in your browser." : ".");
        UpdateManualCard();
    }

    // ---------- page 2: nexus ----------

    private async Task RefreshAuthAsync()
    {
        if (_apiKey == null)
        {
            TxtAuthDetail.Text = "Not signed in yet.";
            TxtSettingsAuth.Text = TxtAuthDetail.Text;
            TxtAuthNote.Text = "Paste your personal API key from Nexus (use Get my key, then Personal API Key). It's stored encrypted on this PC and only sent to Nexus.";
            _signedIn = false;
            BtnToInstall.IsEnabled = false;
            SetStatus("Not signed in");
            return;
        }
        BtnRefreshAuth.IsEnabled = false;
        TxtAuthDetail.Text = "Checking your Nexus account…";
        try
        {
            using var api = new AStoryWealth.NexusApi.NexusApiClient(_apiKey);
            var user = await api.ValidateUserAsync();
            _authPremium = user.IsPremium;
            TxtAuthDetail.Text = user.IsPremium
                ? $"Signed in as {user.Name}. Downloads will run automatically at full speed."
                : $"Signed in as {user.Name}. Free account: you'll download each mod yourself in your browser, one at a time. The installer opens the right page and does the rest.";
            TxtAuthNote.Text = "Ready to install.";
            _signedIn = true;
            UpdateManualCard();
            BtnToInstall.IsEnabled = _collectionPath != null;
            SetStatus($"Signed in: {user.Name}");
            _ = SyncCollectionAsync(); // pull the latest collection in the background
        }
        catch (Exception ex)
        {
            TxtAuthDetail.Text = "Nexus didn't accept the key, or couldn't be reached. Check the key and your internet connection, then try again.";
            InstallLog.Error("ui", $"auth check failed: {ex.Message}");
            _signedIn = false;
            BtnToInstall.IsEnabled = false;
            SetStatus("Not signed in");
        }
        if (_apiKey != null) _ = LoadBaseImagesAsync();
        TxtSettingsAuth.Text = TxtAuthDetail.Text;
        BtnRefreshAuth.IsEnabled = true;
    }

    private async void OnRefreshAuth(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await RefreshAuthAsync();

    private bool _threadsLoaded; // ignore ValueChanged fired by the constructor's own assignment
    private void OnThreadsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_threadsLoaded) { _threadsLoaded = true; return; }
        InstallState.SetThreads(new InstallState.ThreadSettings(
            (int)(NudDownloadThreads.Value ?? 1), (int)(NudExtractThreads.Value ?? 1)));
    }

    // ---------- collection sync (auto-download + update tracking) ----------

    private static string SyncCacheDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AStoryWealth", "Collections");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private bool _syncing;

    private async Task SyncCollectionAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try { await SyncCollectionCoreAsync(); }
        finally { _syncing = false; }
    }

    private async Task SyncCollectionCoreAsync()
    {
        TxtCollectionSummary.Text = "Fetching the latest version of A StoryWealth…";
        SetStatus("Fetching the latest version…");
        try
        {
            var sync = new CollectionSync(SyncCacheDir);
            var prog = new Progress<string>(s => { TxtCollectionSummary.Text = ToFriendly(s); SetStatus(ToFriendly(s)); });
            var result = await Task.Run(() => sync.EnsureLatestAsync(_apiKey!, prog));
            await LoadCollectionAsync(result.CollectionJsonPath);
            TxtCollectionSummary.Text = result.UpdateAvailable
                ? "Updated to the latest version"
                : result.WasCached ? "Up to date" : "";
            SetStatus("");
            RefreshInstallCheck();
        }
        catch (Exception ex)
        {
            InstallLog.Error("ui", $"collection sync failed: {ex.Message}");
            SetStatus("Couldn't fetch the latest version");
            var saved = new CollectionSync(SyncCacheDir).LoadState();
            if (saved is not null && File.Exists(saved.CollectionJsonPath))
            {
                await LoadCollectionAsync(saved.CollectionJsonPath);
                TxtCollectionSummary.Text = $"Couldn't check for a newer version. Using the saved copy (r{saved.Revision}).";
            }
            else
            {
                // Nexus only gives the collection file itself to Premium members.
                TxtCollectionSummary.Text = ex.Message.Contains("premium", StringComparison.OrdinalIgnoreCase)
                    ? "Free accounts can't fetch the mod list automatically. Open the collection page, use Manual download, then choose the file you get."
                    : ex.Message.Contains("7-Zip", StringComparison.OrdinalIgnoreCase)
                        ? "7-Zip is needed to unpack the mod list and wasn't found. Install it from 7-zip.org, then press Check again."
                        : "Couldn't reach Nexus. Check your internet connection and try again later.";
                PnlPickCollection.IsVisible = true;
            }
            RefreshInstallCheck();
        }
    }

    /// <summary>Base pack installed? Add-Ons page only exists once it is.</summary>
    private void RefreshInstallCheck()
    {
        var dir = InstallState.GetInstallDir();
        RefreshAddons();
        InstallLog.Info("ui", dir is null ? "install check: not installed" : $"install check: installed at {dir}");
    }

    private static string ToFriendly(string s)
    {
        // sync progress lines contain revision numbers/percent — keep them plain
        if (s.Contains("Downloading collection", StringComparison.Ordinal))
            return s.Contains('%') ? "Downloading the mod list…" : "Downloading the mod list…";
        if (s.Contains("already cached", StringComparison.Ordinal)) return "Up to date";
        if (s.Contains("found on disk", StringComparison.Ordinal)) return "Checking the mod list…";
        if (s.Contains("extracted", StringComparison.Ordinal)) return "Preparing the mod list…";
        if (s.Contains("archive downloaded", StringComparison.Ordinal)) return "Mod list downloaded";
        return s;
    }


    // Browser sign-in (Nexus SSO) needs a Nexus-verified application, so until then the user's personal API key is
    // the way in. It is kept DPAPI-encrypted for this Windows user (SecretStore); ASW_NEXUS_KEY still overrides it.
    private const string KeyName = "nexus-api-key";
    private const string KeyPageUrl = "https://www.nexusmods.com/settings/api-keys";

    private async void OnSaveKey(object? sender, RoutedEventArgs e)
    {
        var box = (sender as Control)?.Tag as string == "settings" ? TxtApiKeySettings : TxtApiKey;
        var key = (box.Text ?? "").Trim();
        if (key.Length == 0) { TxtAuthNote.Text = "Paste your Nexus personal API key first."; return; }
        try { SecretStore.Save(KeyName, key); }
        catch (Exception ex) { InstallLog.Error("ui", $"couldn't store the API key: {ex.Message}"); TxtAuthNote.Text = "Couldn't save the key on this PC."; return; }
        _apiKey = key;
        TxtApiKey.Text = TxtApiKeySettings.Text = "";
        _addonGrabbed = false;
        await RefreshAuthAsync();
    }

    private void OnForgetKey(object? sender, RoutedEventArgs e)
    {
        try { SecretStore.Delete(KeyName); } catch { }
        _apiKey = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY");
        _ = RefreshAuthAsync();
    }

    private void OnOpenKeyPage(object? sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(KeyPageUrl) { UseShellExecute = true });

    private void OnToInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_collectionPath == null) return;
        ClearInstallPage();
        ShowPage(3);
        _ = StartInstallAsync();
    }

    // ---------- page 3: install ----------

    /// <summary>Fresh start from Setup: empty log, counters and buttons.</summary>
    private void ClearInstallPage()
    {
        ClearLog();
        ResetRunState();
        BtnResume.IsVisible = BtnRetry.IsVisible = BtnInstallBack.IsVisible = false;
    }

    /// <summary>Per-run state that must not leak from one attempt into the next.</summary>
    private void ResetRunState()
    {
        TxtItem.ClearValue(TextBlock.ForegroundProperty);
        TxtItem.Text = "";
        TxtSpeed.Text = "";
        TxtCounts.Text = "";
        PbOverall.Value = 0;
    }

    /// <summary>Install page controls and the left nav follow whether a run is active.</summary>
    private void SetRunning(bool running)
    {
        BtnCancel.IsVisible = running;
        if (running) { BtnResume.IsVisible = BtnRetry.IsVisible = BtnInstallBack.IsVisible = BtnOpenPage.IsVisible = false; }
        else BtnOpenPage.IsVisible = false;
        Nav.IsEnabled = !running;
    }

    /// <summary>A run stopped without finishing: offer to continue it or go back to Setup.</summary>
    private void OfferContinue(bool retryFailedOnly)
    {
        TxtSpeed.Text = "";
        BtnRetry.IsVisible = retryFailedOnly;
        BtnResume.IsVisible = !retryFailedOnly;
        BtnInstallBack.IsVisible = true;
    }

    private void OnInstallBack(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        ApplyResumeGroups();
        ShowPage(1);
    }

    private void OnOpenModPage(object? sender, RoutedEventArgs e)
    {
        if (ManualDownloader.CurrentUrl is { } url) ManualDownloader.OpenPage(url);
    }

    private async void OnPickWatchDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose where your browser saves downloads", AllowMultiple = false });
        if (folders.Count > 0) TxtWatchDir.Text = folders[0].Path.LocalPath;
    }

    // ---------- page 9: optional content ----------

    private readonly Queue<OptionalJob> _jobQueue = new();
    private OptionalJob? _currentJob;
    private bool _runOk;
    private readonly List<(CheckBox Box, OptionalItem Item)> _optRows = [];
    private readonly HashSet<string> _optFolded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>collection.json of an installed add-on: the copy kept in the instance, else the app's cache.</summary>
    private string? AddonCollectionFile(InstalledAddon a)
    {
        if (InstallState.GetInstallDir() is { } dir)
        {
            var kept = Path.Combine(dir, ".asw", a.Id + ".collection.json");
            if (File.Exists(kept)) return kept;
        }
        var cached = Path.Combine(SyncCacheDir, "collections", a.Id, "r" + a.Revision.TrimStart('r'), "collection.json");
        return File.Exists(cached) ? cached : null;
    }

    private async void RefreshOptionalTab()
    {
        PanelOptTab.Children.Clear();
        _optRows.Clear();
        BtnOptInstall.IsEnabled = false;
        TxtOptSummary.Text = "";
        var dir = InstallState.GetInstallDir();
        if (dir is null || _collectionPath is null)
        {
            TxtOptEmpty.Text = "Install A StoryWealth first. Optional mods for it and for your add-ons show up here.";
            return;
        }
        TxtOptEmpty.Text = "Loading…";
        var baseJson = _collectionPath;
        var names = _liveAddons?.ToDictionary(c => c.Slug, c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<OptionalItem> items;
        try { items = await Task.Run(() => OptionalCatalog.Build(dir, baseJson, AddonCollectionFile, s => names.GetValueOrDefault(s) ?? AddonOptions.NameOf(s))); }
        catch (Exception ex)
        {
            InstallLog.Error("ui", $"optional list failed: {ex.Message}");
            TxtOptEmpty.Text = "Couldn't read the optional content list. The details are in the log.";
            return;
        }
        TxtOptEmpty.Text = items.Count == 0 ? "There is no optional content to choose from yet." : "";
        var grey = new SolidColorBrush(Color.Parse("#7A828E"));
        foreach (var src in items.GroupBy(i => i.SourceId))
        {
            // one drop-down per source; click the heading to fold it away (remembered while the app runs)
            var head = new TextBlock { Text = $"{src.First().SourceName}  ·  {src.Count()}", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            head.Classes.Add("h2");
            var body = new StackPanel { Margin = new Avalonia.Thickness(4, 4, 0, 4) };
            var sourceId = src.Key;
            var expander = new Expander
            {
                Header = head, Content = body, IsExpanded = !_optFolded.Contains(sourceId),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Margin = new Avalonia.Thickness(0, 8, 0, 0),
            };
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                if (expander.IsExpanded) _optFolded.Remove(sourceId); else _optFolded.Add(sourceId);
            };
            PanelOptTab.Children.Add(expander);
            foreach (var it in src)
            {
                var label = it.GroupId.Length > 0 ? $"{it.Name}  ·  {it.ModCount} mods, {SizeText(it.Bytes)}"
                    : it.Bytes > 0 ? $"{it.Name}  ·  {SizeText(it.Bytes)}" : it.Name;
                var box = new CheckBox { Content = label, IsChecked = it.State == OptionState.Installed, IsEnabled = it.State == OptionState.Available };
                box.IsCheckedChanged += (_, _) => UpdateOptSummary();
                var note = string.Join("  ", new[] { it.State == OptionState.Installed ? "Installed." : null, it.Reason.Length > 0 ? it.Reason + "." : null, it.Instruction }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var row = new StackPanel { Opacity = it.State == OptionState.NotApplicable ? 0.45 : 1 };
                if (it.State == OptionState.Installed && it.GroupId.Length == 0 && it.Md5.Length > 0)
                {
                    // an installed option can be taken out again (two clicks: Remove, then Sure?)
                    var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var rm = new Button { Content = "Remove", FontSize = 12, Padding = new Avalonia.Thickness(12, 3), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    var item = it;
                    rm.Click += (_, _) => RemoveOptionalAsync(item, rm);
                    Grid.SetColumn(rm, 1);
                    line.Children.Add(box);
                    line.Children.Add(rm);
                    row.Children.Add(line);
                }
                else row.Children.Add(box);
                if (note.Length > 0)
                    row.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = grey, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(28, -2, 0, 8) });
                body.Children.Add(row);
                _optRows.Add((box, it));
            }
        }
        UpdateOptSummary();
    }

    private void UpdateOptSummary()
    {
        var ticked = _optRows.Where(r => r.Box.IsChecked == true && r.Item.State == OptionState.Available).Select(r => r.Item).ToList();
        BtnOptInstall.IsEnabled = ticked.Count > 0 && !_installing;
        TxtOptSummary.Text = ticked.Count == 0 ? _optNote : $"{ticked.Count} selected  ·  {SizeText(ticked.Sum(i => i.Bytes))} to download";
    }

    private void OnOptInstall(object? sender, RoutedEventArgs e)
    {
        if (_installing || _collectionPath is null) return;
        if (InstallState.GetResume() is not null)
        {
            TxtOptSummary.Text = "An earlier install is unfinished. Open Modlist and resume it first.";
            return;
        }
        var ticked = _optRows.Where(r => r.Box.IsChecked == true && r.Item.State == OptionState.Available).Select(r => r.Item).ToList();
        var jobs = OptionalCatalog.Jobs(ticked, _collectionPath);
        if (jobs.Count == 0) return;
        foreach (var j in jobs) _jobQueue.Enqueue(j);
        TxtInstallDir.Text = InstallState.GetInstallDir() ?? InstallState.GetLastDir() ?? TxtInstallDir.Text;
        ClearInstallPage();
        ShowPage(3);
        _ = StartInstallAsync();
    }

    private async Task StartInstallAsync()
    {
        if (_installing) return; // re-entrancy guard: no double installs, _cts stays valid
        _installing = true;
        SetRunning(true);
        try
        {
            // Optional-tab installs queue one run per source (base list first, then each add-on); a failed run drops the rest.
            do
            {
                _runOk = false;
                if (_currentJob is null && _jobQueue.Count > 0) _currentJob = _jobQueue.Dequeue();
                await RunInstallAsync();
            }
            while (_runOk && _jobQueue.Count > 0);
            if (!_runOk) _jobQueue.Clear();
        }
        finally { _installing = false; SetRunning(false); }
    }

    private async Task RunInstallAsync()
    {
        var job = _currentJob;
        _currentJob = null;
        // Audit H1: refresh the checkpoint from disk — it advances during a run,
        // and Retry must pick up the runner's latest state, not a stale capture.
        // A new add-on or optional install starts fresh; only Resume/Retry of the same run picks up the checkpoint.
        _resume = job is null && !_addonInstall ? InstallState.GetResume() : null;
        var resuming = _resume is not null;
        if (resuming) { _resumeJsonPath = _resume!.JsonPath; _resumeAddonId = _resume.AddonId; }
        var dir = TxtInstallDir.Text!;
        var jobIsBase = job?.SourceId == OptionalCatalog.BaseId;
        var jsonPath = resuming ? _resumeJsonPath! : job is not null ? job.JsonPath : _addonInstall ? _addonJsonPath! : _collectionPath!;
        var installId = resuming ? _resumeAddonId! : job is not null ? (jobIsBase ? AddonId : job.SourceId) : _addonInstall ? _addonDetailCard!.Slug : AddonId;
        // add-on runs anchor on the installed base list
        var baseForAddon = !resuming && (job is not null ? !jobIsBase : _addonInstall) ? _collectionPath : null;
        _addonInstall = false;
        if (job is not null) InstallLog.Info("ui", $"optional content for {job.SourceName}: {job.Md5s.Count} mod(s), groups [{string.Join(", ", job.Groups)}]");
        ResetRunState();
        TxtPhase.Text = "Starting…";
        _cts = new CancellationTokenSource();
        var runner = new InstallRunner();
        var progress = new Progress<InstallRunner.ProgressInfo>(p =>
        {
            TxtPhase.Text = p.Phase switch
            {
                "plan" => "Reading the mod list…",
                "game" => "Copying game files…",
                "mo2" => "Setting up Mod Organizer 2…",
                "install" => "Installing mods…",
                "download" => p.Total == 0 ? $"Downloading {ShortModName(p.CurrentItem)}" : $"Downloading mod {p.Current + 1} of {p.Total}",
                "manual" => $"Your turn: download mod {p.Current + 1} of {p.Total} in your browser",
                "finalize" => "Finishing up…",
                "done" => "Done.",
                _ => p.Phase
            };
            TxtItem.Text = ShortModName(p.CurrentItem);
            TxtSpeed.Text = p.Speed ?? "";
            BtnOpenPage.IsVisible = p.Phase == "manual";
            PbOverall.IsIndeterminate = p.Total == 0;
            if (p.Total > 0) { PbOverall.Value = 100.0 * p.Current / p.Total; TxtCounts.Text = $"{p.Current}/{p.Total}"; }
        });
        try
        {
            var gamePath = resuming ? _resume!.GamePath : GameDir();
            var stockGame = resuming ? _resume!.StockGame : RbStockGame.IsChecked == true; // read on the UI thread — IsChecked is thread-affine
            // Older checkpoints stored the Stock Game destination as GamePath; if so, rediscover the real Steam source.
            if (stockGame && gamePath is not null && gamePath.TrimEnd('\\').EndsWith("Stock Game", StringComparison.OrdinalIgnoreCase))
                gamePath = GameDir();
            var fresh = RbFresh.IsChecked == true; // resume restores the saved radio, so Vortex reuse survives a resume
            var reuseDir = fresh ? null
                : (string.IsNullOrWhiteSpace(TxtReuseDir.Text) ? null : TxtReuseDir.Text.Trim());
            if (reuseDir is not null && !Directory.Exists(reuseDir)) reuseDir = null;
            var downloadsDir = string.IsNullOrWhiteSpace(TxtDownloadsDir.Text) ? null : TxtDownloadsDir.Text.Trim();
            InstallState.SetLastPath(dir, reuseDir, downloadsDir, stockGame); // remembered even if the install is cancelled
            // Read UI control values on the UI thread — Avalonia controls are
            // thread-affine and Task.Run below runs on a worker thread.
            var dlThreads = (int)(NudDownloadThreads.Value ?? 1);
            var exThreads = (int)(NudExtractThreads.Value ?? 1);
            // base-list job: the groups already installed plus the ones just ticked; anything else follows the Setup boxes
            var optionalGroups = jobIsBase
                ? (AddonRegistry.Load(dir).BaseGroups ?? AStoryWealth.Core.OptionalGroups.Defaults(AStoryWealth.Core.OptionalGroups.Load(AddonId)))
                    .Concat(job!.Groups).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : SelectedGroups();
            var watchDir = string.IsNullOrWhiteSpace(TxtWatchDir.Text) ? null : TxtWatchDir.Text.Trim();
            await Task.Run(() => runner.RunAsync(jsonPath, installId, _apiKey!, dir,
                gamePath ?? "", progress, _cts.Token, stockGame: stockGame, reuseDir: reuseDir,
                downloadsDir: downloadsDir, resume: _resume,
                downloadThreads: dlThreads, extractThreads: exThreads, optionalGroups: optionalGroups,
                manualWatchDir: watchDir, baseCollectionJsonPath: baseForAddon, optionalMods: job?.Md5s));
            _resume = null;
            ApplyResumeGroups();
            _installedDir = dir;
            InstallState.SetInstallDir(dir);
            ShowExistingInstall();
            TxtDone.Text = job is not null
                ? "The optional content you picked is installed.\nTo play, open Mod Organizer 2, pick F4SE in the drop-down at the top right and press Run."
                : "All done! A StoryWealth is installed.\nTo play, open Mod Organizer 2, pick F4SE in the drop-down at the top right and press Run. The game has to start through F4SE.";
            EnableNav(3);
            _runOk = true;
            if (_jobQueue.Count == 0) ShowPage(4);
        }
        catch (OperationCanceledException)
        {
            TxtPhase.Text = "Stopped. Your downloads so far are kept. Press Resume to continue.";
            PbOverall.IsIndeterminate = false;
            OfferContinue(false);
        }
        catch (InstallRunner.DownloadIncompleteException die)
        {
            var list = string.Join(", ", die.Failures.Keys.Take(5));
            if (die.Failures.Count > 5) list += $", and {die.Failures.Count - 5} more";
            TxtPhase.Text = $"{die.Failures.Count} downloads failed. Check your connection and press Retry. Only the failed ones run again.";
            TxtItem.Text = $"Failed: {list}";
            TxtItem.Foreground = Brushes.Salmon;
            PbOverall.IsIndeterminate = false;
            OfferContinue(true);
        }
        catch (Exception ex)
        {
            TxtPhase.Text = "Something went wrong. The log below has the details, and so does install.log in your install folder. Send that file if you need help.";
            TxtItem.Text = ex.Message;
            TxtItem.Foreground = Brushes.Salmon;
            PbOverall.IsIndeterminate = false;
            OfferContinue(false);
        }
    }

    private void RememberGameMode(bool stock)
    {
        var dir = (TxtInstallDir.Text ?? "").Trim();
        if (dir.Length == 0) return;
        var reuse = (TxtReuseDir.Text ?? "").Trim();
        var dl = (TxtDownloadsDir.Text ?? "").Trim();
        InstallState.SetLastPath(dir, reuse.Length > 0 ? reuse : null, dl.Length > 0 ? dl : null, stock);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        InstallLog.Warn("ui", "cancel requested");
        _cts?.Cancel();
    }

    private void OnRetry(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        InstallLog.Info("ui", "retrying / resuming install");
        _ = StartInstallAsync(); // _resume still holds the checkpoint — only failed downloads re-run
    }

    private string? _installedDir;

    /// <summary>Finds the local Fallout 4 Steam install (registry Steam path, then default). Empty string if not found.</summary>
    private static string FindFallout4Path()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                var guess = Path.GetFullPath(Path.Combine(steamPath.Replace('/', '\\'), "steamapps", "common", "Fallout 4"));
                if (File.Exists(Path.Combine(guess, "Fallout4.exe"))) return guess;
                // other libraries
                var vdf = Path.Combine(guess, "..", "..", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var line in File.ReadAllLines(vdf))
                        if (line.Contains("\"path\"") && line.Contains('"'))
                        {
                            var lib = line.Split('"')[^2].Replace("\\\\", "\\");
                            var f4 = Path.Combine(lib, "steamapps", "common", "Fallout 4");
                            if (File.Exists(Path.Combine(f4, "Fallout4.exe"))) return f4;
                        }
                }
            }
        }
        catch (Exception ex) { InstallLog.Warn("game", $"steam discovery failed: {ex.Message}"); }
        // Bethesda launcher and GOG leave their install path in the registry
        foreach (var (sub, name) in new[] { (@"SOFTWARE\WOW6432Node\Bethesda Softworks\Fallout4", "Installed Path"), (@"SOFTWARE\WOW6432Node\GOG.com\Games\1998527297", "path") })
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub);
                if (k?.GetValue(name) is string p && File.Exists(Path.Combine(p, "Fallout4.exe"))) return p.TrimEnd('\\');
            }
            catch { /* not installed that way */ }
        }
        return "";
    }

    /// <summary>The game folder from the Setup box if it holds Fallout4.exe, else whatever auto-detection finds.</summary>
    private string GameDir()
    {
        var typed = (TxtGameDir.Text ?? "").Trim();
        return typed.Length > 0 && File.Exists(Path.Combine(typed, "Fallout4.exe")) ? typed : FindFallout4Path();
    }

    private async void OnPickGameDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Choose your Fallout 4 folder (the one with Fallout4.exe)", AllowMultiple = false });
        if (folders.Count > 0) TxtGameDir.Text = folders[0].Path.LocalPath;
    }

    private void OnLaunchMo2(object? sender, RoutedEventArgs e)
    {
        var exe = _installedDir is null ? null : Path.Combine(_installedDir, "ModOrganizer.exe");
        if (exe is not null && File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _installedDir });
    }

    private void OnOpenFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_installedDir != null && Directory.Exists(_installedDir))
            Process.Start(new ProcessStartInfo(_installedDir) { UseShellExecute = true });
    }
}
