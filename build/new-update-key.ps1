<#
  Makes the signing key pair for in-app updates. Run it once.

    pwsh build/new-update-key.ps1 -OutPrivate D:\secrets\asw-update-private-key.pem

  The private key goes to the file you name. Keep it somewhere safe and never commit it: whoever has it can sign
  an update that every installed copy will accept. The public key is printed. Paste it into
  src/Core/Data/update.json as "PublicKey", next to "Repo".
#>
param(
    [Parameter(Mandatory = $true)][string]$OutPrivate
)
$ErrorActionPreference = 'Stop'
$OutPrivate = $OutPrivate.Trim('<', '>', '"', ' ')                       # a pasted <placeholder> still works
if ((Test-Path $OutPrivate -PathType Container) -or -not [IO.Path]::HasExtension($OutPrivate)) { $OutPrivate = Join-Path $OutPrivate 'asw-update-private-key.pem' }   # a folder (or a name with no extension) means: put the default file name inside it
$parent = Split-Path $OutPrivate -Parent
if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
if (Test-Path $OutPrivate) { throw "$OutPrivate already exists. Pick another name, or delete it if you really want a new key." }
$ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
Set-Content -Path $OutPrivate -Value $ec.ExportPkcs8PrivateKeyPem() -Encoding ascii
$pub = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
Write-Host "Private key saved to $OutPrivate"
Write-Host ""
Write-Host "Public key (put this in src/Core/Data/update.json as PublicKey):"
Write-Output $pub
