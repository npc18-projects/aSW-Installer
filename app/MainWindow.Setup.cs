namespace AStoryWealth.Installer;

public partial class MainWindow
{
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

    private string? _nexusName;   // only used to hide it in the report

    /// <summary>Shown when something unexpected went wrong in a button handler: the app carries on instead of closing.</summary>
    public void ShowUnexpectedError(Exception ex)
    {
        InstallLog.Error("ui", $"unexpected error: {ex}");
        SetStatus("Something went wrong. The details are in the log.");
    }

    private async Task RefreshAuthAsync()
    {
        if (_cred == null)
        {
            _nexusName = null;
            TxtAuthDetail.Text = "Not signed in yet.";
            TxtSettingsAuth.Text = TxtAuthDetail.Text;
            TxtAuthNote.Text = "Sign in to your Nexus Mods account to download the mods.";
            _signedIn = false;
            BtnToInstall.IsEnabled = false;
            SetStatus("Not signed in");
            UpdateSignInButtons();
            return;
        }
        BtnRefreshAuth.IsEnabled = false;
        TxtAuthDetail.Text = "Checking your Nexus account…";
        var credNow = _cred;
        try
        {
            using var api = new AStoryWealth.NexusApi.NexusApiClient(credNow);
            var user = await api.ValidateUserAsync();
            if (!ReferenceEquals(credNow, _cred)) { BtnRefreshAuth.IsEnabled = true; return; }   // signed out or in again while we asked
            _nexusName = user.Name;
            _authPremium = user.IsPremium;
            TxtAuthDetail.Text = user.IsPremium
                ? $"Signed in as {user.Name}. Downloads will run automatically at full speed."
                : $"Signed in as {user.Name}. Free account: you'll download each mod yourself in your browser, one at a time. The installer opens the right page and does the rest.";
            TxtAuthNote.Text = "Ready to install.";
            _signedIn = true;
            if (Mo2Launcher.IsInstalled(InstallState.GetInstallDir())) StartAddonGrab();   // so the Add-Ons dot is right from the start
            UpdateManualCard();
            BtnToInstall.IsEnabled = _collectionPath != null;
            SetStatus($"Signed in: {user.Name}");
            _ = SyncCollectionAsync(); // pull the latest collection in the background
        }
        catch (Exception ex)
        {
            TxtAuthDetail.Text = ex is NexusSignedOutException
                ? "Your Nexus sign-in has ended. Sign in again."
                : "Couldn't reach Nexus, or it didn't accept the sign-in. Check your internet connection and press Check again, or sign in again.";
            if (ex is NexusSignedOutException) { _cred = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY") is { Length: > 0 } devKey ? new ApiKeyCredential(devKey) : null; }
            InstallLog.Error("ui", $"auth check failed: {ex.Message}");
            if (ex is not NexusSignedOutException && _manifest is null) _ = LoadSavedCollectionAsync();   // offline: the saved mod list still works
            _signedIn = false;
            BtnToInstall.IsEnabled = false;
            SetStatus("Not signed in");
        }
        if (_cred != null) _ = LoadBaseImagesAsync();
        TxtSettingsAuth.Text = TxtAuthDetail.Text;
        BtnRefreshAuth.IsEnabled = true;
        UpdateSignInButtons();
    }

    private async void OnRefreshAuth(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await RefreshAuthAsync();

    private bool _threadsLoaded; // ignore ValueChanged fired by the constructor's own assignment
    private void OnThreadsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_threadsLoaded) { _threadsLoaded = true; return; }
        InstallState.SetThreads(new InstallState.ThreadSettings(
            (int)(NudDownloadThreads.Value ?? 1), (int)(NudExtractThreads.Value ?? 1)));
    }
}
