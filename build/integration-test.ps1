<#
.SYNOPSIS
    The Windows half of the integration gate: the same assertions as
    integration-test.sh, against a real sshd on loopback.

.DESCRIPTION
    Windows is the reason this project exists, so the acceptance test has to run
    here too rather than being taken on trust from macOS. The ssh-agent section
    is the one that matters most: on Windows the agent is a named pipe rather
    than a Unix socket, and SshNet.Agent is the only third-party dependency in
    the credential path.
#>
[CmdletBinding()]
param([int]$Port = 22022, [int]$ForwardPort = 18080)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$failures = 0

function Check([string]$Name, [string]$Actual, [string]$Expected) {
    if ($Actual -eq $Expected) {
        Write-Host "  ok    $Name ($Actual)"
    }
    else {
        Write-Host "  FAIL  $Name`: expected $Expected, got $Actual"
        $script:failures++
    }
}

Write-Host 'building stctl...'
dotnet build "$root/src/stctl/stctl.csproj" -c Release --nologo | Out-Null
$stctl = Join-Path $root 'src/stctl/bin/Release/net10.0/stctl.exe'

Write-Host "starting sshd on 127.0.0.1:$Port ..."
$work = & "$root/build/local-sshd.ps1" start -Port $Port
if ($LASTEXITCODE -ne 0) { exit 1 }

try {
    $target = "$env:USERNAME@127.0.0.1:$Port"
    $knownHosts = Join-Path $work 'known_hosts'
    $clientKey = Join-Path $work 'client'
    $log = Join-Path $work 'sshd.log'

    function Run { & $stctl --key $clientKey --known-hosts $knownHosts @args 2>$null }
    function Auths {
        if (-not (Test-Path $log)) { return 0 }
        @(Select-String -Path $log -Pattern 'Accepted publickey' -ErrorAction SilentlyContinue).Count
    }

    Write-Host "`nhost keys:"
    Run exec $target true | Out-Null
    Check 'an unknown host key is refused' $(if ($LASTEXITCODE -eq 0) { 'connected' } else { 'refused' }) 'refused'
    Check 'nothing was recorded for a refused key' `
        $(if ((Test-Path $knownHosts) -and (Get-Item $knownHosts).Length -gt 0) { 'recorded' } else { 'empty' }) 'empty'

    & $stctl --key $clientKey --known-hosts $knownHosts --insecure exec $target true | Out-Null
    ssh-keygen -F "[127.0.0.1]:$Port" -f $knownHosts | Out-Null
    Check "an accepted key is recorded in OpenSSH's own format" `
        $(if ($LASTEXITCODE -eq 0) { 'found' } else { 'missing' }) 'found'

    Run exec $target true | Out-Null
    Check 'a recorded key needs no further trust' `
        $(if ($LASTEXITCODE -eq 0) { 'connected' } else { 'refused' }) 'connected'

    # Swap in a different key of the same type: what an interception looks like.
    $clientBlob = (Get-Content "$clientKey.pub").Split(' ')[1]
    $changed = Join-Path $work 'known_hosts.changed'
    (Get-Content $knownHosts) | ForEach-Object {
        $fields = $_.Split(' '); "$($fields[0]) $($fields[1]) $clientBlob"
    } | Set-Content -Path $changed
    & $stctl --key $clientKey --known-hosts $changed exec $target true 2>$null | Out-Null
    Check 'a changed host key is refused' $(if ($LASTEXITCODE -eq 0) { 'connected' } else { 'refused' }) 'refused'
    & $stctl --key $clientKey --known-hosts $changed --insecure exec $target true 2>$null | Out-Null
    Check 'and refused even when told to accept new keys' `
        $(if ($LASTEXITCODE -eq 0) { 'connected' } else { 'refused' }) 'refused'

    Write-Host "`nmultiplexing:"
    Check 'remote command output' ((Run exec $target echo multiplexed) -join '').Trim() 'multiplexed'
    $before = Auths
    Run exec --repeat 3 $target true | Out-Null
    Check 'three execs on one connection cost one authentication' ((Auths) - $before) '1'

    Write-Host "`ntunnels:"
    $forwardOutput = (Run forward --check $target -L "${ForwardPort}:127.0.0.1:$Port") -join "`n"
    $banner = if ($forwardOutput -match 'banner=(SSH-2\.0-)') { $Matches[1] } else { '' }
    Check 'traffic flows through the forward' $banner 'SSH-2.0-'
    $released = if ($forwardOutput -match 'released=(\w+)') { $Matches[1] } else { '' }
    Check 'the forward released its port' $released 'yes'

    Write-Host "`nsftp:"
    $sandbox = Join-Path $work 'remote'
    New-Item -ItemType Directory -Path $sandbox | Out-Null
    $payload = Join-Path $work 'payload.bin'
    $bytes = [byte[]]::new(700 * 1024)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [System.IO.File]::WriteAllBytes($payload, $bytes)

    # sftp-server on Windows speaks POSIX-looking paths.
    $remotePath = ($sandbox -replace '\\', '/') + '/uploaded.bin'
    Run sftp put $target $payload $remotePath | Out-Null
    Check 'upload lands on the server' `
        $(if (Test-Path (Join-Path $sandbox 'uploaded.bin')) { 'yes' } else { 'no' }) 'yes'

    $downloaded = Join-Path $work 'downloaded.bin'
    Run sftp get $target $remotePath $downloaded | Out-Null
    $expected = (Get-FileHash $payload -Algorithm SHA256).Hash
    $actual = if (Test-Path $downloaded) { (Get-FileHash $downloaded -Algorithm SHA256).Hash } else { 'missing' }
    Check '700 KiB round-trips by SHA-256' $actual $expected

    Write-Host "`nfailure classification:"
    $authError = (& $stctl --key $clientKey --known-hosts $knownHosts exec "nosuchuser@127.0.0.1:$Port" true 2>&1 1>$null) -join ' '
    Check 'a bad user reports authentication, not a mystery' `
        $(if ($authError -match 'Authentication failed') { 'authentication' } else { $authError }) 'authentication'
    $refusedError = (& $stctl --key $clientKey --known-hosts $knownHosts exec "$env:USERNAME@127.0.0.1:$($Port + 1)" true 2>&1 1>$null) -join ' '
    Check 'a refused port says so' `
        $(if ($refusedError -match 'refused') { 'refused' } else { $refusedError }) 'refused'

    Write-Host "`nssh-agent (a named pipe here, not a socket):"
    Start-Service ssh-agent -ErrorAction SilentlyContinue
    ssh-add $clientKey 2>$null | Out-Null
    $agentOutput = (& $stctl --known-hosts $knownHosts exec $target echo agent-ok 2>$null) -join ''
    Check 'authenticates with an agent-held key' $agentOutput.Trim() 'agent-ok'
    ssh-add -d $clientKey 2>$null | Out-Null
}
finally {
    & "$root/build/local-sshd.ps1" stop -Work $work | Out-Null
}

Write-Host ''
if ($failures -eq 0) { Write-Host 'all checks passed' } else { Write-Host "$failures check(s) failed" }
exit $failures
