<#
  Builds a self-contained Windows release of the installer.

    pwsh build/publish.ps1                 # -> dist/AStoryWealth-Installer-<version>-win-x64/ and a .zip next to it
    pwsh build/publish.ps1 -SkipTests      # skip the test run
    pwsh build/publish.ps1 -Output D:\out  # another output folder

  The test suite runs first and a failing test stops the build. Nothing is signed; sign dist\...\AStoryWealth.Installer.exe
  yourself if you have a certificate.

  7-Zip is bundled: 7z.exe, 7z.dll and its licence are copied next to the exe, where the installer looks first. They are taken
  from -SevenZipDir, else the folder of ASW_7Z, else Program Files\7-Zip, else the folder of 7z.exe on PATH. The binaries are
  not kept in the repository. The build stops if 7-Zip cannot be found; pass -NoSevenZip to build without it.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    [string]$SevenZipDir,
    [switch]$NoSevenZip,
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
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet run --project src/Tests
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
    }
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $version = $props.Project.PropertyGroup.Version | Select-Object -First 1
    $name = "AStoryWealth-Installer-$version-win-x64"
    $dir = Join-Path $Output $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    dotnet publish app/AStoryWealth.Installer.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o $dir
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    # The shipped metadata is embedded in the exe. An override file in <exe folder>\order\ wins over it (docs/formats.md),
    # so none is copied here: a stale override would hide newer embedded data after an in-place update.
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
    Write-Host "Built $dir"
    Write-Host "Zip   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally { Pop-Location }
