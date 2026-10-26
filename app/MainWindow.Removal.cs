namespace AStoryWealth.Installer;

public partial class MainWindow
{
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
        : _manifest is null ? "The mod list hasn't loaded yet. Sign in to Nexus (or reconnect) first, so shared mods can be told apart."
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
        RemovalResult res;
        _removing = true;
        try { res = await Task.Run(() => Removal.RemoveAddon(dir, c.Slug, names)); _removing = false; }
        catch (Exception ex)
        {
            _removing = false;
            InstallLog.Error("ui", $"removing {c.Slug} failed: {ex.Message}");
            BtnAddDetRemove.IsEnabled = true;
            TxtAddDetStatus.Text = $"Couldn't finish removing it: {ex.Message} Close anything using those files and try again.";
            return;
        }
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
        RemovalResult res;
        _removing = true;
        try { res = await Task.Run(() => Removal.RemoveOptional(dir, it.SourceId == OptionalCatalog.BaseId ? null : it.SourceId, it.InstallAs, it.Md5, required)); _removing = false; }
        catch (Exception ex)
        {
            _removing = false;
            InstallLog.Error("ui", $"removing {it.Name} failed: {ex.Message}");
            _optNote = $"Couldn't remove {it.Name}: {ex.Message}";
            UpdateOptSummary();
            return;
        }
        _optNote = res.Refused ?? $"Removed {it.Name} ({SizeText(res.BytesFreed)}).";
        RefreshOptionalTab();
    }
}
