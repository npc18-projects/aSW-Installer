namespace AStoryWealth.Installer;

public partial class MainWindow
{
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

    private int _optRun;
    private bool _removing;   // a removal is running: no automatic restart

    private async void RefreshOptionalTab()
    {
        var run = ++_optRun;
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
        if (run != _optRun) return;   // a newer refresh has taken over
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
                    var rm = new Button { Content = "Remove", Classes = { "danger" }, FontSize = 12, Padding = new Avalonia.Thickness(12, 3), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
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
        BtnOptInstall.Content = ticked.Count > 0 ? $"Install {ticked.Count} selected" : "Install selected";
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
        var jobs = OptionalCatalog.Jobs(ticked);
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
        _runWatch = Stopwatch.StartNew();
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

    private Stopwatch _runWatch = new();

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
            await Task.Run(() => runner.RunAsync(jsonPath, installId, _cred!, dir,
                gamePath ?? "", progress, _cts.Token, stockGame: stockGame, reuseDir: reuseDir,
                downloadsDir: downloadsDir, resume: _resume,
                downloadThreads: dlThreads, extractThreads: exThreads, optionalGroups: optionalGroups,
                manualWatchDir: watchDir, baseCollectionJsonPath: baseForAddon, optionalMods: job?.Md5s, gameResolution: MainDisplay()));
            _resume = null;
            ApplyResumeGroups();
            _installedDir = dir;
            InstallState.SetInstallDir(dir);
            ShowExistingInstall();
            TxtDone.Text = job is not null
                ? "The optional content you picked is installed.\nPlay from the Home tab with Launch with F4SE."
                : "All done! A StoryWealth is installed.\nPlay from the Home tab with Launch with F4SE. The game has to start through F4SE.";
            try
            {
                var sum = InstallHealth.Summary(dir);
                var took = _runWatch.Elapsed;
                var tookText = took.TotalHours >= 1 ? $"{(int)took.TotalHours} h {took.Minutes} min" : took.TotalMinutes >= 1 ? $"{(int)took.TotalMinutes} min" : "under a minute";
                TxtDoneSummary.Text = $"{sum.Mods:N0} mods in your install ({sum.EnabledMods:N0} enabled)\n{sum.Plugins:N0} plugins ({sum.EnabledPlugins:N0} enabled)\n"
                    + $"{sum.AddOns} add-on{(sum.AddOns == 1 ? "" : "s")} installed\nThis run took {tookText}\nInstalled in {dir}\nLog: {Path.Combine(dir, "install.log")}";
            }
            catch { TxtDoneSummary.Text = $"Installed in {dir}"; }
            EnableNav();
            UpdateHomeTab();
            _ = CheckBaseUpdateAsync();
            _runOk = true;
            if (_jobQueue.Count == 0) ShowPage(4);
        }
        catch (OperationCanceledException)
        {
            TxtPhase.Text = "Stopped. Your downloads so far are kept. Press Resume to continue.";
            PbOverall.IsIndeterminate = false;
            OfferContinue(false);
        }
        catch (NexusSignedOutException)
        {
            TxtPhase.Text = "Your Nexus sign-in has ended. Sign in again, then press Resume. Your downloads so far are kept.";
            TxtItem.Text = "";
            PbOverall.IsIndeterminate = false;
            OfferContinue(false);
            _ = RefreshAuthAsync();
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
