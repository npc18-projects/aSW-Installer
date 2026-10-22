<#
  Builds a self-contained Windows release of the installer.

    pwsh build/publish.ps1                 # -> dist/AStoryWealth-Installer-<version>-win-x64/ and a .zip next to it
    pwsh build/publish.ps1 -SkipTests      # skip the test run
    pwsh build/publish.ps1 -Output D:\out  # another output folder

  The test suite runs first and a failing test stops the build. Nothing is signed; sign dist\...\AStoryWealth.Installer.exe
  yourself if you have a certificate. 7-Zip is not bundled (the installer finds 7z.exe on PATH, in Program Files, next to the
  exe, or through ASW_7Z); copy 7z.exe and 7z.dll next to the exe if you want a build that needs nothing installed.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist'),
    [switch]$SkipTests
)
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
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') $dir

    $zip = "$dir.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "Built $dir"
    Write-Host "Zip   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
finally { Pop-Location }
