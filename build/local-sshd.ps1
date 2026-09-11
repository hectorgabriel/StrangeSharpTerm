<#
.SYNOPSIS
    Start an unprivileged sshd on 127.0.0.1 for the integration test: the Windows
    counterpart of local-sshd.sh.

.DESCRIPTION
    Writes everything it needs into a work directory and prints that directory on
    stdout, as the shell version does: client key, known_hosts, sshd.log.

        $work = ./build/local-sshd.ps1 start
        ./build/local-sshd.ps1 stop $work
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop')][string]$Action = 'start',
    [string]$Work,
    [int]$Port = 22022
)

$ErrorActionPreference = 'Stop'
# Without this, an empty argument is dropped or passed as two quote characters,
# and ssh-keygen ends up encrypting the host key with a passphrase of `""`.
$PSNativeCommandArgumentPassing = 'Standard'

function Find-Sshd {
    $candidates = @(
        (Join-Path $env:SystemRoot 'System32\OpenSSH\sshd.exe'),
        'C:\Program Files\OpenSSH\sshd.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    # Not every image ships the server half; ask for it rather than give up.
    Write-Host 'installing the OpenSSH server capability...'
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'sshd is not available on this machine.'
}

# Windows sshd refuses to use key files that others can read, and StrictModes
# does not cover its own host key.
function Restrict-ToOwner([string]$Path) {
    icacls $Path /inheritance:r | Out-Null
    icacls $Path /grant:r "$($env:USERNAME):(R,W)" | Out-Null
    icacls $Path /grant:r 'SYSTEM:(R,W)' | Out-Null
}

if ($Action -eq 'stop') {
    if ($Work -and (Test-Path (Join-Path $Work 'sshd.pid'))) {
        $processId = Get-Content (Join-Path $Work 'sshd.pid')
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
    if ($Work -and (Test-Path $Work)) { Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue }
    exit 0
}

$sshd = Find-Sshd
$sftpServer = Join-Path (Split-Path $sshd) 'sftp-server.exe'
$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("st-sshd-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null

ssh-keygen -t ed25519 -f (Join-Path $Work 'host') -N '' -q
ssh-keygen -t ed25519 -f (Join-Path $Work 'client') -N '' -q
Copy-Item (Join-Path $Work 'client.pub') (Join-Path $Work 'authorized_keys')
Restrict-ToOwner (Join-Path $Work 'host')
Restrict-ToOwner (Join-Path $Work 'client')
Restrict-ToOwner (Join-Path $Work 'authorized_keys')

$config = Join-Path $Work 'sshd_config'
@"
Port $Port
ListenAddress 127.0.0.1
HostKey $Work\host
AuthorizedKeysFile $Work\authorized_keys
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
LogLevel VERBOSE
AllowTcpForwarding yes
Subsystem sftp $sftpServer
AllowUsers $env:USERNAME
"@ | Set-Content -Path $config -Encoding ascii

$log = Join-Path $Work 'sshd.log'
$process = Start-Process -FilePath $sshd -ArgumentList @('-f', $config, '-D', '-e') `
    -RedirectStandardError $log -RedirectStandardOutput (Join-Path $Work 'sshd.out') -PassThru -WindowStyle Hidden
$process.Id | Set-Content -Path (Join-Path $Work 'sshd.pid')

$listening = $false
foreach ($attempt in 1..40) {
    # A dead sshd will never start listening; say so now rather than in a minute.
    if ($process.HasExited) { break }
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect('127.0.0.1', $Port)
        $client.Close()
        $listening = $true
        break
    }
    catch { Start-Sleep -Milliseconds 250 }
}

if (-not $listening) {
    Write-Host 'sshd failed to start:'
    if (Test-Path $log) { Get-Content $log -Tail 20 | Write-Host }
    exit 1
}

Write-Output $Work
