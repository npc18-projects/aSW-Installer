namespace AStoryWealth.Installer;

public partial class MainWindow
{
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
            string[] names = ["modlist", "setup", "nexus", "install", "done", "detail", "addons", "settings", "addon-detail", "optional", "home"];
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
                if (i == 4)
                {
                    TxtDone.Text = "All done! A StoryWealth is installed.\nPlay from the Home tab with Launch with F4SE.";
                    TxtDoneSummary.Text = "1,129 mods in your install (1,129 enabled)\n1,024 plugins (1,023 enabled)\n2 add-ons installed\nThis run took 38 min\nInstalled in D:\\Games\\Modlists\\Fallout 4\\A Storywealth\nLog: D:\\Games\\Modlists\\Fallout 4\\A Storywealth\\install.log";
                }
                if (i == 5) OnOpenDetail(null, new RoutedEventArgs());
                ShowPage(i);
                if (i == 10) await Task.Delay(12000);   // Home reads the whole install: health check and folder size
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
}
