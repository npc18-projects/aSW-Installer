namespace AStoryWealth.Installer;

public partial class MainWindow
{
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
            FillDetail();
            _ = CheckBaseUpdateAsync();
            TxtCollectionSummary.Text = "";
            PnlPickCollection.IsVisible = false;
            BtnToInstall.IsEnabled = _signedIn;
            BtnDetailInstall.IsEnabled = true;
            EnableNav();
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
        _addonUpdates = cards.Any(c => c.UpdateAvailable);
        UpdateNavBadges();

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
        var listing = _cred != null && _liveAddons is null && _addonGrabbed; // still fetching the list
        TxtAddonsEmpty.Text = cards.Count == 0
            ? (listing ? "" : "No add-ons are available yet.")
            : rows.Count == 0 ? "No add-ons match this filter." : "";
        foreach (var a in rows) GridAddons.Children.Add(BuildAddonCard(a, installed));
        ResizeAddonCards();
    }

    private sealed record AddonCard(string Slug, string Name, string Author, string Version, bool UpdateAvailable, string HeaderImageUrl, string? InstalledRev = null,
        NexusApiClient.CollectionStats? Stats = null);

    /// <summary>Cards share the page width: as many columns as fit at about 380 px, stretched to fill.</summary>
    private void OnAddonsSizeChanged(object? sender, SizeChangedEventArgs e) => ResizeAddonCards();

    private void ResizeAddonCards()
    {
        var avail = PageAddons.Bounds.Width - 72 - 20;   // page margins and the scroll bar
        if (avail < 300) return;
        const double gap = 20, wanted = 380;
        var cols = Math.Max(1, (int)Math.Floor((avail + gap) / (wanted + gap)));
        var width = Math.Floor((avail - gap * (cols - 1)) / cols);
        foreach (var c in GridAddons.Children.OfType<Border>()) c.Width = width;
    }

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
            // the base page shows the mod count and sizes in its own row, so only the Nexus numbers are listed here
            ? string.Join("\n", facts[0], $"{facts[1]}   ·   {facts[2]}", string.Join("   ·   ", facts.Skip(5)))
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
        ShowChangelog(CardAddDetChanges, TxtAddDetChangesTitle, TxtAddDetChanges, c.Stats);
        BtnAddDetRemove.IsVisible = c.InstalledRev is not null;
        BtnAddDetRemove.Content = "Remove";
        _addonJsonPath = null;           // belongs to whichever add-on was last fetched
        PnlAddDetManual.IsVisible = false;
        var baseInstalled = InstallState.GetInstallDir() is not null;
        TxtAddDetStatus.Text = !baseInstalled ? "Install the base list first; add-ons are placed on top of it."
            : c.UpdateAvailable ? $"Installed ({c.InstalledRev}). A newer revision is available."
            : c.InstalledRev is not null ? $"Installed ({c.InstalledRev}). Installing again re-applies it." : "";
        BtnAddDetInstall.Content = c.UpdateAvailable ? "Update" : c.InstalledRev is not null ? "Reinstall" : "Install";
        BtnAddDetInstall.IsEnabled = baseInstalled && _cred != null && _collectionPath != null;
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
        if (_addonDetailCard is not { } card || _collectionPath is null || _cred is null) return;
        if (InstallState.GetInstallDir() is null) { TxtAddDetStatus.Text = "Install the base list first."; return; }
        BtnAddDetInstall.IsEnabled = false;
        PnlAddDetManual.IsVisible = false;
        if (_addonJsonPath is null || _addonJsonSlug != card.Slug)
        {
            TxtAddDetStatus.Text = "Fetching the add-on list…";
            try
            {
                var sync = new CollectionSync(SyncCacheDir);
                var addon = await Task.Run(() => sync.EnsureAddonJsonAsync(_cred!, card.Slug, card.Name));
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
        if (_cred is null) return;
        try
        {
            using var api = new NexusApiClient(_cred);
            var info = await api.GetCollectionInfoAsync(CollectionSync.BaseSlug);
            if (info.Stats is { } bs)
            {
                TxtBaseStats.Text = StatsLine(bs, withSize: false);
                TxtBaseStatsDetail.Text = StatsDetail(bs, " (everything, including optional files)", stacked: true);
                ShowChangelog(CardHomeChanges, TxtHomeChangesTitle, TxtHomeChanges, bs);
                TxtHomeBannerSub.Text = (bs.Revision > 0 ? $"Revision {bs.Revision}   ·   " : "") + StatsLine(bs, withSize: false);
            }
            await LoadImageAsync(ImgBaseCard, info.HeaderImageUrl, "base");
            await LoadImageAsync(ImgDetailBanner, info.HeaderImageUrl, "base");
            await LoadImageAsync(ImgHomeBanner, info.HeaderImageUrl, "base");
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

        var panel = new Panel { Children = { banner, text } };
        if (c.InstalledRev is not null)
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(c.UpdateAvailable ? "#F2C48D" : "#3E9B54")),
                CornerRadius = new Avalonia.CornerRadius(12),
                Padding = new Avalonia.Thickness(10, 4),
                Margin = new Avalonia.Thickness(0, 10, 10, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = c.UpdateAvailable ? "Installed · update" : "✓ Installed",
                    FontSize = 12, FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(c.UpdateAvailable ? "#2B2113" : "#FFFFFF"))
                }
            });
        var card = new Border
        {
            Classes = { "card" },
            Width = 380,
            Padding = new Avalonia.Thickness(0),
            Tag = c.Slug,
            Child = panel
        };
        if (c.InstalledRev is not null)
        {
            card.BorderBrush = new SolidColorBrush(Color.Parse(c.UpdateAvailable ? "#F2C48D" : "#3E9B54"));
            card.BorderThickness = new Avalonia.Thickness(2);
        }
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
        FillDetail();
        ShowPage(5);
    }

    /// <summary>Builds the archive list shown on the A StoryWealth page from the loaded collection.</summary>
    private void FillDetail()
    {
        if (_manifest is null) return;
        _archiveRows = _manifest.Mods
            .Select(m =>
            {
                var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.InstallAs : m.DisplayName;
                return new ArchiveRow(name, m.Nexus?.FileSize is long b && b > 0 ? SizeText(b) : "?",
                    m.Nexus?.FileSize ?? 0);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RefreshArchives();
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        <= 0 => "unknown",
        < 1_000 => "<1 KB",
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

    private void OnDoneHome(object? sender, RoutedEventArgs e) => ShowPage(10);

    private void OnDetailInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OnToSetup(sender, e); // fills the install/downloads defaults, then opens Setup
}
