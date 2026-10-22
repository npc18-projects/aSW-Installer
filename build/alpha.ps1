<#
  Local "alpha" build: the working tree as it is right now, built into dist-alpha\ so it can be tried before anything
  goes out as a test build (beta) or a release.

    pwsh build/alpha.ps1                # tests, then dist-alpha\AStoryWealth-Installer-<version>-win-x64\
    pwsh build/alpha.ps1 -SkipTests     # quicker
    pwsh build/alpha.ps1 -Keep 5        # keep the 5 newest alpha folders (default 3)

  The version is the number in version.txt plus -alpha.g<commit>, with .dirty added when there are uncommitted changes
  (for example 0.5.0-alpha.g1a2b3c4.dirty). The build is made from a temporary copy of the tree, so a copy of the app
  that is already running (an earlier alpha, or one started from bin\) never blocks it. Nothing is zipped or signed, and an
  alpha never updates itself: its updater is off.

  An alpha shares the app's settings and install record in %LOCALAPPDATA%\AStoryWealth with the normal app, so it opens
  on the same install.
#>
#Requires -Version 7.0
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist-alpha'),
    [string]$SevenZipDir,
    [switch]$SkipTests,
    [int]$Keep = 3
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$base = ((Get-Content (Join-Path $root 'version.txt') -Raw).Trim() -split '-')[0]
$sha = (git -C $root rev-parse --short HEAD).Trim()
$dirty = if (git -C $root status --porcelain) { '.dirty' } else { '' }
$version = "$base-alpha.g$sha$dirty"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("asw-alpha-" + [guid]::NewGuid().ToString('N'))
try {
    robocopy $root $tmp /E /XD .git bin obj dist dist-gui dist-alpha samples /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the source failed (robocopy $LASTEXITCODE)." }
    Set-Content (Join-Path $tmp 'version.txt') $version -NoNewline

    # No -SevenZipDir and nothing on the usual paths: reuse the 7-Zip bundled into an earlier build of this tree.
    if (-not $SevenZipDir -and -not $env:ASW_7Z) {
        $prev = Get-ChildItem $root -Recurse -Filter 7z.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -notmatch '\\(obj|bin)\\' -and (Test-Path (Join-Path $_.DirectoryName '7z.dll')) } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($prev) { $SevenZipDir = $prev.DirectoryName }
    }
    $args2 = @{ Output = $Output; NoZip = $true }
    if ($SevenZipDir) { $args2.SevenZipDir = $SevenZipDir }
    if ($SkipTests) { $args2.SkipTests = $true }
    & (Join-Path $tmp 'build/publish.ps1') @args2
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# keep the newest few; one that is running can't be deleted, so it just stays
Get-ChildItem $Output -Directory | Where-Object Name -like 'AStoryWealth-Installer-*-alpha.*' |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $Keep |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

$outFull = (Resolve-Path $Output).Path
$built = Join-Path $outFull "AStoryWealth-Installer-$version-win-x64"

# dist-alpha\latest is the one path to open: it always holds the newest alpha. A copy that is running can't be replaced,
# so it is left alone (all or nothing, never half old and half new) and the new build stays in its own folder.
$latest = Join-Path $outFull 'latest'
$latestExe = Join-Path $latest 'AStoryWealth.Installer.exe'
$locked = $false
if (Test-Path $latestExe) {
    try { [IO.File]::Open($latestExe, 'Open', 'ReadWrite', 'None').Dispose() } catch { $locked = $true }
}
Write-Host ""
Write-Host "Alpha $version"
if ($locked) {
    Write-Warning "dist-alpha\latest is running, so it was not updated. Close it and run build/alpha.ps1 again, or open the new build directly:"
    Write-Host (Join-Path $built 'AStoryWealth.Installer.exe')
}
else {
    robocopy $built $latest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Updating dist-alpha\latest failed (robocopy $LASTEXITCODE)." }
    Set-Content (Join-Path $latest 'alpha-version.txt') $version
    Write-Host $latestExe
}
