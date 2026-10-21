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
        _apiKey = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY");
        InstallRunner.UiLogLine += OnLogLine;
        Closed += (_, _) => { _cts?.Cancel(); InstallRunner.UiLogLine -= OnLogLine; };
        RbFresh.IsCheckedChanged += (_, _) => OnSourceChanged();
        RbVortex.IsCheckedChanged += (_, _) => OnSourceChanged();
        TxtWatchDir.Text = ManualDownloader.DefaultWatchDir();
        var threads = InstallState.GetThreads();
        NudDownloadThreads.Value = threads.Download;
        NudExtractThreads.Value = threads.Extract;
        _resume = InstallState.GetResume();
        _ = RefreshAuthAsync();
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
            TxtDirStatus.Text = "Previous install was interrupted — installing again resumes where it left off.";
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
            var text = string.IsNullOrWhiteSpace(g.Description) ? g.Name : $"{g.Name} — {g.Description}";
            if (_manifest is not null)
            {
                var only = AStoryWealth.Core.OptionalGroups.Excluded(_manifest.Mods, groups, groups.Where(x => x.Id != g.Id).Select(x => x.Id).ToList());
                var bytes = _manifest.Mods.Where(m => only.Contains(m.InstallAs)).Sum(m => m.Nexus?.FileSize ?? 0);
                text += $"  ({only.Count} mods, {bytes / 1_000_000_000.0:N1} GB)";
            }
            box.Content = text;
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

    private void OnLogLine(string line)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        { Avalonia.Threading.Dispatcher.UIThread.Post(() => OnLogLine(line)); return; }
        TxtLog.Text += line + Environment.NewLine;
    }

    private void OnLogChanged(object? sender, TextChangedEventArgs e)
        => TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;

    private void ShowPage(int page)
    {
        Control[] pages = [PageModlist, PageSetup, PageNexus, PageInstall, PageDone, PageDetail, PageAddons, PageSettings, PageAddonDetail];
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == page;

        // left rail: Modlist(0) / Add-Ons(6) / Settings(7)
        Nav.SelectionChanged -= OnNavChanged;
        Nav.SelectedIndex = page is 6 or 8 ? 1 : page == 7 ? 2 : 0;
        Nav.SelectionChanged += OnNavChanged;

        // wizard step bar only during an install flow.
        WizBar.IsVisible = page is >= 1 and <= 4;
        Avalonia.Controls.Primitives.ToggleButton[] steps = [Wiz0, Wiz1, Wiz2, Wiz3];
        var step = page switch { 0 => 0, 1 => 1, 2 or 3 => 2, 4 => 3, _ => -1 };
        for (var i = 0; i < steps.Length; i++) steps[i].IsChecked = i == step;
        if (page == 6) { RefreshAddons(); StartAddonGrab(); }
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
        ShowPage(Nav.SelectedIndex switch { 1 => 6, 2 => 7, _ => 0 });
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
            BtnToSetup.IsEnabled = true;
            BtnDetailInstall.IsEnabled = true;
            EnableNav(1);
            SetStatus("");
        }
        catch (Exception ex)
        {
            TxtCollectionSummary.Text = "That file doesn't look like a valid collection. Details are in the log.";
            InstallLog.Error("ui", $"manifest load failed for {path}: {ex.Message}");
            BtnToSetup.IsEnabled = false;
        }
    }

    private void OnTabModlists(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { }

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
            var baseRev = new CollectionSync(SyncCacheDir).LoadState()?.Revision;
            cards = live.Select(c =>
            {
                var mf = AddonStore.Get(c.Slug);
                var pinned = mf?.Requires.RevisionNumber;
                var update = pinned is int p && baseRev is int b && p < b;
                return            new AddonCard(c.Slug, c.Name, c.Author, $"r{c.LatestRevision}", update, string.IsNullOrWhiteSpace(c.HeaderImageUrl) ? c.TileImageUrl : c.HeaderImageUrl);
            }).ToList();
        }
        else
        {
            cards = AddonStore.List(null)
                .Select(a => new AddonCard(Path.GetFileNameWithoutExtension(a.Path), a.Manifest.Name, a.Manifest.Author, a.Manifest.Version, a.UpdateAvailable, ""))
                .ToList();
        }
        cards = cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

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
        TxtAddonsEmpty.Text = cards.Count == 0
            ? "No add-ons are available yet."
            : rows.Count == 0 ? "No add-ons match this filter." : "";
        foreach (var a in rows) GridAddons.Children.Add(BuildAddonCard(a, installed));
    }

    private sealed record AddonCard(string Slug, string Name, string Author, string Version, bool UpdateAvailable, string HeaderImageUrl);

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
        TxtAddDetStatus.Text = "";
        BtnAddDetDownload.IsEnabled = _apiKey != null;
        BtnAddDetInstall.IsEnabled = false;
        _ = LoadImageAsync(ImgAddDet, c.HeaderImageUrl, "addon-" + c.Slug);
        ShowPage(8);
    }

    /// <summary>Display name without the "aSW <Addon> - " install-folder prefix.</summary>
    private static string ShortModName(string s)
    {
        if (s.StartsWith("aSW ", StringComparison.Ordinal))
            s = s["aSW ".Length..];
        var i = s.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? s[(i + 3)..] : s;
    }

    private void OnAddDetDownload(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_apiKey is null || _addonDetailCard is null) return;
        BtnAddDetDownload.IsEnabled = false;
        TxtAddDetStatus.Text = "Downloading archive…";
        _ = Task.Run(async () =>
        {
            try
            {
                var sync = new CollectionSync(SyncCacheDir);
                var addon = await sync.EnsureAddonJsonAsync(_apiKey!, _addonDetailCard.Slug, _addonDetailCard.Name);
                _addonJsonPath = addon.CollectionJsonPath;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    TxtAddDetStatus.Text = "Archive downloaded. Install when ready.";
                    BtnAddDetInstall.IsEnabled = _collectionPath != null;
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    TxtAddDetStatus.Text = "Download failed. The details are in the logs.";
                    BtnAddDetDownload.IsEnabled = true;
                    InstallLog.Info("addons", $"download {_addonDetailCard.Slug}: {ex.Message}");
                });
            }
        });
    }

    private void OnAddDetInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_addonJsonPath is null || _collectionPath is null) return;
        _addonInstall = true;
        TxtInstallDir.Text = InstallState.GetLastDir() ?? TxtInstallDir.Text;
        ClearInstallPage();
        ShowPage(3);
        _ = StartInstallAsync();
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
        if (c.UpdateAvailable)
            text.Children.Add(new TextBlock
            { Text = "Update available", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#F2C48D")) });
        if (!installed)
            text.Children.Add(new TextBlock
            { Text = "Install the base list first", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B8BEC9")) });

        var card = new Border
        {
            Classes = { "card" },
            Width = 420,
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
        if (!string.IsNullOrEmpty(filter)) rows = rows.Where(r => r.Size.Contains(filter, StringComparison.OrdinalIgnoreCase));
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
        ShowPage(1);
    }

    private void OnBackToModlist(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(0);

    private void OnToggleArchives(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ColArchives.IsVisible = !ColArchives.IsVisible;

    private void OnDetailInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ShowPage(1); // paths live on Setup now; the detail page is overview only

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
            TxtDownloadsDir.Text = Path.Combine(TxtInstallDir.Text.Length > 0 ? TxtInstallDir.Text : ".", "Downloads");
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
        if (_resume is null && FindFallout4Path().Length == 0)
            return RbStockGame.IsChecked == true
                ? "Couldn't find a Fallout 4 install to copy. Install the game through Steam once, then try again."
                : "Couldn't find your Fallout 4 install. Install the game through Steam once, then try again.";
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
            TxtAuthNote.Text = "Signing in lets the installer download mods for you. Your Nexus password is never shared with the installer.";
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
            TxtAuthNote.Text = "You're all set. Continue to install.";
            _signedIn = true;
            UpdateManualCard();
            BtnToInstall.IsEnabled = _collectionPath != null;
            SetStatus($"Signed in: {user.Name}");
            _ = SyncCollectionAsync(); // pull the latest collection in the background
        }
        catch (Exception ex)
        {
            TxtAuthDetail.Text = "Couldn't reach Nexus Mods. Check your internet connection and try again.";
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
            (int)NudDownloadThreads.Value, (int)NudExtractThreads.Value));
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
                TxtCollectionSummary.Text = $"Couldn't check for a newer version, so the saved copy (r{saved.Revision}) is being used.";
            }
            else
            {
                // Nexus only gives the collection file itself to Premium members.
                TxtCollectionSummary.Text = ex.Message.Contains("premium", StringComparison.OrdinalIgnoreCase)
                    ? "The installer can't fetch the mod list automatically with a free account. Open the collection page, download the collection (Manual download), then choose the file you got."
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
        TxtInstallCheck.Text = dir is null
            ? "A StoryWealth isn't installed yet. Install the base pack first, then add-on lists will appear here."
            : $"A StoryWealth is installed ({dir}). Add-on lists will appear here once the base pack is set up.";
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


    private async void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // SSO browser flow: opens nexusmods.com/sso, key arrives over websocket.
        // Blocked on a Nexus-issued application slug ("Application ID was invalid"
        // otherwise) — see skill asw-installer. Until then the machine-local
        // ASW_NEXUS_KEY serves as the testing-stage authorization.
        TxtAuthNote.Text = "Browser login is pending a Nexus-issued application ID (support@nexusmods.com). " +
                           "For now, this machine's testing authorization is used automatically.";
        if (_apiKey != null) await RefreshAuthAsync();
    }

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
        TxtLog.Text = "";
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

    private async Task StartInstallAsync()
    {
        if (_installing) return; // re-entrancy guard: no double installs, _cts stays valid
        _installing = true;
        SetRunning(true);
        try { await RunInstallAsync(); }
        finally { _installing = false; SetRunning(false); }
    }

    private async Task RunInstallAsync()
    {
        // Audit H1: refresh the checkpoint from disk — it advances during a run,
        // and Retry must pick up the runner's latest state, not a stale capture.
        _resume = InstallState.GetResume();
        var resuming = _resume is not null;
        if (resuming) { _resumeJsonPath = _resume!.JsonPath; _resumeAddonId = _resume.AddonId; }
        var dir = TxtInstallDir.Text!;
        var jsonPath = resuming ? _resumeJsonPath! : _addonInstall ? _addonJsonPath! : _collectionPath!;
        var installId = resuming ? _resumeAddonId! : _addonInstall ? _addonDetailCard!.Slug : AddonId;
        _addonInstall = false;
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
            var gamePath = resuming ? _resume!.GamePath : FindFallout4Path();
            var stockGame = resuming ? _resume!.StockGame : RbStockGame.IsChecked == true; // read on the UI thread — IsChecked is thread-affine
            // Older checkpoints stored the Stock Game destination as GamePath; if so, rediscover the real Steam source.
            if (stockGame && gamePath is not null && gamePath.TrimEnd('\\').EndsWith("Stock Game", StringComparison.OrdinalIgnoreCase))
                gamePath = FindFallout4Path();
            var fresh = RbFresh.IsChecked == true; // resume restores the saved radio, so Vortex reuse survives a resume
            var reuseDir = fresh ? null
                : (string.IsNullOrWhiteSpace(TxtReuseDir.Text) ? null : TxtReuseDir.Text.Trim());
            if (reuseDir is not null && !Directory.Exists(reuseDir)) reuseDir = null;
            var downloadsDir = string.IsNullOrWhiteSpace(TxtDownloadsDir.Text) ? null : TxtDownloadsDir.Text.Trim();
            InstallState.SetLastPath(dir, reuseDir, downloadsDir, stockGame); // remembered even if the install is cancelled
            // Read UI control values on the UI thread — Avalonia controls are
            // thread-affine and Task.Run below runs on a worker thread.
            var dlThreads = (int)NudDownloadThreads.Value;
            var exThreads = (int)NudExtractThreads.Value;
            var optionalGroups = SelectedGroups();
            var watchDir = string.IsNullOrWhiteSpace(TxtWatchDir.Text) ? null : TxtWatchDir.Text.Trim();
            await Task.Run(() => runner.RunAsync(jsonPath, installId, _apiKey!, dir,
                gamePath, progress, _cts.Token, stockGame: stockGame, reuseDir: reuseDir,
                downloadsDir: downloadsDir, resume: _resume,
                downloadThreads: dlThreads, extractThreads: exThreads, optionalGroups: optionalGroups,
                manualWatchDir: watchDir));
            _resume = null;
            ApplyResumeGroups();
            _installedDir = dir;
            InstallState.SetInstallDir(dir);
            TxtDone.Text = $"All done! A StoryWealth is installed.\nTo play, open the folder below and run Mod Organizer 2, just like starting the game normally.";
            EnableNav(3);
            ShowPage(4);
        }
        catch (OperationCanceledException)
        {
            TxtPhase.Text = "Stopped. Everything downloaded so far is kept. Press Resume to pick up where it left off.";
            PbOverall.IsIndeterminate = false;
            OfferContinue(false);
        }
        catch (InstallRunner.DownloadIncompleteException die)
        {
            var list = string.Join(", ", die.Failures.Keys.Take(5));
            if (die.Failures.Count > 5) list += $", and {die.Failures.Count - 5} more";
            TxtPhase.Text = $"{die.Failures.Count} mod(s) couldn't be downloaded. Everything else downloaded fine. Check your connection, then press Retry — only the failed downloads will run again.";
            TxtItem.Text = $"Failed: {list}";
            TxtItem.Foreground = Brushes.Salmon;
            PbOverall.IsIndeterminate = false;
            OfferContinue(true);
        }
        catch (Exception ex)
        {
            TxtPhase.Text = "Something went wrong. The details are in the log below, and in install.log next to your install. That file is what you'd send us if you need help.";
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
        var fallback = @"D:\Games\Steam\steamapps\common\Fallout 4";
        return File.Exists(Path.Combine(fallback, "Fallout4.exe")) ? fallback : "";
    }

    private void OnOpenFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_installedDir != null && Directory.Exists(_installedDir))
            Process.Start(new ProcessStartInfo(_installedDir) { UseShellExecute = true });
    }
}
