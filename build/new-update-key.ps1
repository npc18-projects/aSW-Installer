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
if (Test-Path $OutPrivate) { throw "$OutPrivate already exists. Pick another name, or delete it if you really want a new key." }
$ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
Set-Content -Path $OutPrivate -Value $ec.ExportPkcs8PrivateKeyPem() -Encoding ascii
$pub = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
Write-Host "Private key saved to $OutPrivate"
Write-Host ""
Write-Host "Public key (put this in src/Core/Data/update.json as PublicKey):"
Write-Output $pub
