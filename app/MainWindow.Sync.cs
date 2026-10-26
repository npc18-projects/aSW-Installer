namespace AStoryWealth.Installer;

public partial class MainWindow
{
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

    /// <summary>Loads the mod list kept from the last successful sync, for when Nexus can't be reached.</summary>
    private async Task LoadSavedCollectionAsync()
    {
        var saved = new CollectionSync(SyncCacheDir).LoadState();
        if (saved is null || !File.Exists(saved.CollectionJsonPath)) return;
        await LoadCollectionAsync(saved.CollectionJsonPath);
        TxtCollectionSummary.Text = $"Couldn't check for a newer version. Using the saved copy (r{saved.Revision}).";
        RefreshInstallCheck();
    }

    private async Task SyncCollectionCoreAsync()
    {
        TxtCollectionSummary.Text = "Fetching the latest version of A StoryWealth…";
        SetStatus("Fetching the latest version…");
        try
        {
            var sync = new CollectionSync(SyncCacheDir);
            var prog = new Progress<string>(s => { TxtCollectionSummary.Text = ToFriendly(s); SetStatus(ToFriendly(s)); });
            var result = await Task.Run(() => sync.EnsureLatestAsync(_cred!, prog));
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


    // Sign-in is Nexus OAuth (PKCE): the browser opens, the user allows the app, and Nexus answers on 127.0.0.1.
    // The tokens are kept DPAPI-encrypted for this Windows user. ASW_NEXUS_KEY (a personal key) is for developers only.
    private async void OnSignIn(object? sender, RoutedEventArgs e)
    {
        if (_signInCts is not null) { _signInCts.Cancel(); return; }   // the button reads "Cancel sign-in" while we wait
        var cts = _signInCts = new CancellationTokenSource();
        SetSignInWaiting(true);
        string? message = null;
        try
        {
            await _session.SignInAsync(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), cts.Token);
            _cred = _session;
            _addonGrabbed = false;
        }
        catch (OperationCanceledException) { message = "Sign-in cancelled."; }
        catch (TimeoutException tex) { message = tex.Message; }
        catch (Exception ex)
        {
            InstallLog.Warn("auth", $"sign-in failed: {ex.Message}");
            message = ex.Message;
        }
        finally { _signInCts = null; cts.Dispose(); SetSignInWaiting(false); }
        await RefreshAuthAsync();
        if (message is not null) TxtAuthNote.Text = message;
    }

    private void SetSignInWaiting(bool waiting)
    {
        BtnSignIn.Content = BtnSettingsSignIn.Content = waiting ? "Cancel sign-in" : "Sign in to Nexus";
        if (waiting) TxtAuthDetail.Text = TxtSettingsAuth.Text = "Finish signing in in your browser. The installer carries on when you allow it.";
    }

    private async void OnSignOut(object? sender, RoutedEventArgs e)
    {
        await _session.SignOutAsync();
        _cred = Environment.GetEnvironmentVariable("ASW_NEXUS_KEY") is { Length: > 0 } devKey ? new ApiKeyCredential(devKey) : null;
        _addonGrabbed = false;
        _liveAddons = null;
        await RefreshAuthAsync();
    }

    /// <summary>Sign in shows when signed out, Sign out when signed in (on both the Settings page and the Nexus page).</summary>
    private void UpdateSignInButtons()
    {
        BtnSignIn.IsVisible = BtnSettingsSignIn.IsVisible = !_signedIn;
        BtnSignOut.IsVisible = BtnSettingsSignOut.IsVisible = _signedIn;
    }

    private void OnToInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_collectionPath == null) return;
        ClearInstallPage();
        ShowPage(3);
        _ = StartInstallAsync();
    }
}
