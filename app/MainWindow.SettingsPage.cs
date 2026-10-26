namespace AStoryWealth.Installer;

public partial class MainWindow
{
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
            RbStockGame.IsChecked, _signedIn ? _authPremium : null, InstallState.GetResume() is not null, null, _nexusName, _cred?.Secrets.ToList());
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
        UpdateHomeTab();
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
        ChkUpdateTest.IsChecked = settings.IncludeTestBuilds;
        if (_version.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
        {
            TxtUpdateStatus.Text = $"You're on version {_version}, a local alpha build. It doesn't update itself.";
            ChkUpdateAuto.IsEnabled = ChkUpdateTest.IsEnabled = BtnUpdateCheck.IsEnabled = false;
            return;
        }
        if (!UpdateCfg.Enabled)
        {
            TxtUpdateStatus.Text = $"You're on version {_version}. Updates aren't set up in this build.";
            ChkUpdateAuto.IsEnabled = ChkUpdateTest.IsEnabled = BtnUpdateCheck.IsEnabled = false;
            return;
        }
        TxtUpdateStatus.Text = $"You're on version {_version}.";
        _ = Task.Run(() => { try { foreach (var d in Directory.GetDirectories(GitHubUpdater.DefaultStagingRoot())) Directory.Delete(d, true); } catch { } });   // leftovers of an earlier update
        if (settings.CheckAtStartup) _ = Task.Run(async () => { await Task.Delay(4000); await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CheckForUpdateAsync(false)); });
    }

    private void OnUpdateAutoChanged(object? sender, RoutedEventArgs e) =>
        new UpdateSettings { CheckAtStartup = ChkUpdateAuto.IsChecked == true, IncludeTestBuilds = ChkUpdateTest.IsChecked == true }.Save();

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
            var info = await _updater.CheckAsync(_version, ChkUpdateTest.IsChecked == true);
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
            if (_installing || _removing || _signInCts is not null)   // never restart in the middle of an install, a removal or a sign-in
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
}
