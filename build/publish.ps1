<#
  Builds a self-contained Windows release of the installer.

    pwsh build/publish.ps1                 # dist/AStoryWealth-Installer-<version>-win-x64/ and a .zip next to it
    pwsh build/publish.ps1 -SkipTests      # skip the tests
    pwsh build/publish.ps1 -Output D:\out  # different output folder
    pwsh build/publish.ps1 -Loose          # old layout: the exe plus 220+ separate files

  The tests run first. If one fails, the build stops. Nothing is signed, so sign
  dist\...\AStoryWealth.Installer.exe yourself if you have a certificate.

  The app is one exe with the .NET runtime and Avalonia inside it. A few native parts unpack to a
  temp folder the first time it starts.

  7-Zip gets bundled: 7z.exe, 7z.dll and its licence are copied next to the exe, where the
  installer looks first. The script finds them in -SevenZipDir, then the folder of ASW_7Z,
  then Program Files\7-Zip, then the folder of 7z.exe on PATH. They aren't kept in the repo.
  If 7-Zip isn't found the build stops. Pass -NoSevenZip to build without it.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    [string]$SevenZipDir,
    [switch]$NoSevenZip,
    [switch]$Loose,
    [switch]$SkipTests
)

function Find-SevenZipDir {
    $candidates = @()
    if ($SevenZipDir) { $candidates += $SevenZipDir }
    if ($env:ASW_7Z) { $candidates += (Split-Path $env:ASW_7Z -Parent) }
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) { if ($pf) { $candidates += (Join-Path $pf '7-Zip') } }
    $onPath = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += (Split-Path $onPath.Source -Parent) }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c '7z.exe')) -and (Test-Path (Join-Path $c '7z.dll'))) { return (Resolve-Path $c).Path }
    }
    return $null
}

$ErrorActionPreference = 'Stop'
$single = if ($Loose) { 'false' } else { 'true' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet run --project src/Tests
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
    }
    $version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
    $name = "AStoryWealth-Installer-$version-win-x64"
    $dir = Join-Path $Output $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    dotnet publish app/AStoryWealth.Installer.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=$single -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false -o $dir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    # Files next to the exe that nobody needs: native debug symbols and the crash-dump helper.
    Get-ChildItem $dir -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -eq 'createdump.exe' } | Remove-Item -Force

    # The shipped data is embedded in the exe. A file in <exe folder>\order\ overrides it (docs/formats.md),
    # so none is copied here. A stale override would hide newer embedded data after an in-place update.
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dir

    if (-not $NoSevenZip) {
        $sz = Find-SevenZipDir
        if (-not $sz) { throw '7-Zip not found. Install it, or pass -SevenZipDir <folder with 7z.exe and 7z.dll>, or -NoSevenZip.' }
        Copy-Item (Join-Path $sz '7z.exe'), (Join-Path $sz '7z.dll') $dir
        foreach ($doc in 'License.txt', 'readme.txt') { if (Test-Path (Join-Path $sz $doc)) { Copy-Item (Join-Path $sz $doc) (Join-Path $dir "7-Zip-$doc") } }
        $ver = (Get-Item (Join-Path $dir '7z.exe')).VersionInfo.FileVersion
        $hash = (Get-FileHash (Join-Path $dir '7z.exe')).Hash
        Write-Host "Bundled 7-Zip $ver from $sz (7z.exe sha256 $hash)"
    }

    $zip = "$dir.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "Built $dir ($((Get-ChildItem $dir -File | Measure-Object).Count) files)"
    Write-Host "Zip   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally { Pop-Location }
