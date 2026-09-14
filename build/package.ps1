# Builds a distributable StrangeSharpTerm for Windows.
#
#   ./build/package.ps1                      # unsigned zip, for yourself
#   ./build/package.ps1 -Certificate CERT.pfx -Password (Read-Host -AsSecureString)
#   ./build/package.ps1 -Thumbprint ABC123    # a certificate already in the store
#   ./build/package.ps1 -Arch win-arm64
#
# Signing here needs an Authenticode certificate, which is a second signing
# identity and a second annual renewal from a different vendor than Apple's --
# budget for it. Without one the app runs perfectly and SmartScreen warns the
# first people who download it, which is the Windows counterpart of Gatekeeper
# refusing an ad-hoc bundle. See docs/adr/0008.
#
# An MSIX would be the other option and is deliberately not taken yet: it needs
# the same certificate *and* a packaging identity, and a plain zip is the thing
# that can be produced and tested today.
[CmdletBinding()]
param(
    [string]$Arch = "win-x64",
    [string]$Certificate,
    [securestring]$Password,
    [string]$Thumbprint,
    [string]$Version = $(if ($env:STRANGESHARPTERM_VERSION) { $env:STRANGESHARPTERM_VERSION } else { "0.1.0" })
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Say($message) { Write-Host "`n== $message" }
function Die($message) { Write-Error $message; exit 1 }

$appName = "StrangeSharpTerm"
$out = Join-Path $root "artifacts/$Arch"
$stage = Join-Path $out $appName

Say "publishing $Arch"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# No debug symbols in a shipped build: they are not useful without the build
# that made them.
dotnet publish src/StrangeSharpTerm.App/StrangeSharpTerm.App.csproj `
    -c Release -r $Arch --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false `
    -o $stage --nologo
if ($LASTEXITCODE -ne 0) { Die "publish failed" }

$exe = Join-Path $stage "$appName.exe"
if (-not (Test-Path $exe)) { Die "no $appName.exe in the published output" }

# ------------------------------------------------------------------ signing
#
# Every native binary, not only the executable. SmartScreen judges what the user
# launched, but an unsigned DLL beside a signed exe is the kind of thing that
# fails an enterprise policy long after it shipped -- and it costs nothing to do
# now.
$identity = $null
if ($Thumbprint) {
    $identity = Get-ChildItem -Path "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $identity) { Die "no certificate with thumbprint $Thumbprint in the current user's store" }
} elseif ($Certificate) {
    if (-not (Test-Path $Certificate)) { Die "no certificate file at $Certificate" }
    $identity = Get-PfxCertificate -FilePath $Certificate -Password $Password
}

if ($identity) {
    Say "signing with $($identity.Subject)"
    $signable = Get-ChildItem -Path $stage -Recurse -Include *.exe, *.dll |
        Where-Object { $_.Name -notlike "*.resources.dll" }

    # A timestamp is what keeps a signature valid after the certificate expires.
    # Without it everything shipped stops verifying on renewal day.
    $signed = Set-AuthenticodeSignature -FilePath $signable.FullName -Certificate $identity `
        -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256

    $failed = $signed | Where-Object { $_.Status -ne "Valid" }
    if ($failed) {
        $failed | ForEach-Object { Write-Host "$($_.Status): $($_.Path)" }
        Die "$($failed.Count) files did not sign"
    }
    Say "signed $($signed.Count) files"
} else {
    Say "not signed"
    Write-Host "No certificate given. The app runs, and SmartScreen will warn whoever downloads it."
}

# --------------------------------------------------------------------- zip
Say "building the archive"
$zip = Join-Path $out "$appName-$Arch.zip"
Compress-Archive -Path $stage -DestinationPath $zip -Force

Say "checking it starts"
# --version loads the runtime and exits without opening a window. A WinExe has no
# console here, so the exit code is the answer rather than the output.
$launch = Start-Process -FilePath $exe -ArgumentList "--version" -PassThru -Wait -WindowStyle Hidden
if ($launch.ExitCode -ne 0) {
    Die "the published app exited with $($launch.ExitCode) instead of starting"
}

Say "done"
Write-Output $zip
