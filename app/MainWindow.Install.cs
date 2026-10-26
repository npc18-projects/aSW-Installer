namespace AStoryWealth.Installer;

public partial class MainWindow
{
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
}
